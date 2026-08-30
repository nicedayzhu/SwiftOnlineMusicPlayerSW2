using System.Collections.Concurrent;
using AudioApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.EntitySystem;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace SwiftOnlineMusicPlayerSW2;

[PluginMetadata(
    Id = "SwiftOnlineMusicPlayerSW2",
    Version = "0.3.6",
    Name = "Swift Online Music Player",
    Author = "SkinTools",
    Description = "Per-player online music playback and MusicSquare-inspired search with a CCSCustomHudLayout controller.",
    MinimumAPIVersion = "1.2.0"
)]
public sealed class SwiftOnlineMusicPlayerPlugin(ISwiftlyCore core) : BasePlugin(core)
{
    private const string DesignerName = "custom_hud_layout";
    private const string TargetName = "swift_online_music_player_custom_hud";
    private const string LayoutResource = "panorama/layout/custom_game/online_music_player_custom_hud.xml";
    private const string DialogPanelId = "music_dialog";
    private const string HiddenClass = "MusicHudHidden";
    private const string InteractiveClass = "MusicHudInteractive";
    private const string RuntimeConfigFileName = "config.jsonc";
    private const string RuntimeConfigSectionName = "MusicPlayer";
    private const int ProgressStepCount = 20;
    private const int VolumeStepCount = 5;
    private const int SearchRowsPerPage = 5;
    private static readonly string[] TrackVariantMarkers =
    [
        "live", "remix", "demo", "cover", "伴奏", "翻唱", "柔情版", "3d", "环绕",
        "montagem", "童声", "儿歌", "女声", "男声", "男生", "女生", "吉他版", "钢琴版",
        "现场", "原唱", "dj", "mix", "speed", "slowed", "低音", "加速", "变调"
    ];

    private readonly Dictionary<int, PlayerSession> _sessions = [];
    private readonly HashSet<int> _openSlots = [];
    private readonly HashSet<int> _inputCapturedSlots = [];
    private readonly HashSet<int> _mouse2HeldSlots = [];
    private readonly HashSet<int> _mouse2CapturePendingSlots = [];
    private readonly ConcurrentDictionary<string, Lazy<Task<IAudioSource>>> _sourceCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly MusicSquareSearchProvider _searchProvider = new();

    private MusicPlayerConfig _config = MusicPlayerConfig.Normalize(null);
    private IAudioApi? _audioApi;
    private CCSCustomHudLayout? _layoutEntity;
    private CustomHudNativeBridge? _nativeHud;
    private IDisposable? _configReloadSubscription;
    private CancellationTokenSource? _hudRefreshTimer;
    private bool _unloading;

    private ILogger<SwiftOnlineMusicPlayerPlugin> Logger =>
        Core.LoggerFactory.CreateLogger<SwiftOnlineMusicPlayerPlugin>();

    public override void Load(bool hotReload)
    {
        _unloading = false;
        InitializeRuntimeConfiguration();

        try
        {
            _nativeHud = CustomHudNativeBridge.Create(Core.GameData, Core.Memory);
            _nativeHud.HookCustomHudClicks(OnNativeCustomHudClicked, exception =>
                Logger.LogError(exception, "[SwiftOnlineMusicPlayer] Custom HUD click bridge callback failed."));
        }
        catch (Exception exception)
        {
            _nativeHud?.Dispose();
            _nativeHud = null;
            Logger.LogError(
                exception,
                "[SwiftOnlineMusicPlayer] Native Custom HUD bridge is unavailable. Verify server.dll and GameData signatures.");
        }

        Core.Event.OnClientDisconnected += OnClientDisconnected;
        Core.GameHooks.Controller.ProcessUsercmds.Pre += OnProcessUsercmdsPre;
        _hudRefreshTimer = Core.Scheduler.RepeatBySeconds(1f, UpdateOpenHuds);

        Logger.LogInformation(
            "[SwiftOnlineMusicPlayer] Loaded (hotReload={HotReload}, tracks={TrackCount}, search={SearchEnabled}). Players can use !music.",
            hotReload,
            _config.Tracks.Count,
            _config.MusicSquareSearch.Enabled);
    }

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        try
        {
            _audioApi = interfaceManager.GetSharedInterface<IAudioApi>("audio");
            _sourceCache.Clear();
            foreach (var session in _sessions.Values)
            {
                session.Generation++;
                session.State = PlaybackState.Idle;
                session.Error = string.Empty;
                session.ElapsedBeforeResume = TimeSpan.Zero;
                session.StartedAt = null;
                session.PendingTrack = null;
                session.ActiveTrack = null;
                session.Channel = _audioApi.UseChannel(ChannelId(session.PlayerSlot));
                session.Channel.SetVolume(session.PlayerSlot, VolumeFromStep(session.VolumeStep));
            }

            UpdateOpenHuds();
            Logger.LogInformation("[SwiftOnlineMusicPlayer] SwiftlyS2 Audio shared interface is ready.");
        }
        catch (Exception exception)
        {
            _audioApi = null;
            foreach (var session in _sessions.Values)
            {
                session.Channel = null;
                session.State = PlaybackState.Error;
                session.Error = "Audio extension is offline";
                session.Generation++;
            }

            Logger.LogWarning(
                exception,
                "[SwiftOnlineMusicPlayer] SwiftlyS2 Audio shared interface was not found. Install and load the Audio plugin.");
        }
    }

    public override void Unload()
    {
        _unloading = true;
        _configReloadSubscription?.Dispose();
        _configReloadSubscription = null;
        _hudRefreshTimer?.Cancel();
        _hudRefreshTimer?.Dispose();
        _hudRefreshTimer = null;
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
        Core.GameHooks.Controller.ProcessUsercmds.Pre -= OnProcessUsercmdsPre;

        foreach (var playerSlot in _sessions.Keys.ToArray())
        {
            StopAndRemoveSession(playerSlot, releaseHud: true);
        }

        ClearLayout("plugin unload");
        _nativeHud?.Dispose();
        _nativeHud = null;
        _audioApi = null;
        _sourceCache.Clear();
        _searchProvider.Dispose();
        Logger.LogInformation("[SwiftOnlineMusicPlayer] Unloaded.");
    }

    [Command("music", registerRaw: true, helpText: "Open the online music player.")]
    public void OpenCommand(ICommandContext context)
    {
        var player = context.Sender;
        if (player?.IsValid != true || player.IsFakeClient || player.Controller?.IsValid != true)
        {
            context.Reply("[Music] This command must be used by a connected player.");
            return;
        }

        if (_nativeHud is null)
        {
            context.Reply("[Music] Custom HUD bridge unavailable; check the server log and GameData build hash.");
            return;
        }

        try
        {
            EnsureLayout();
            OpenHud(player.Slot);
            context.Reply(_audioApi is null
                ? "[Music] Player opened, but the SwiftlyS2 Audio plugin is not available. Right-click to interact."
                : "[Music] Player opened. Right-click to interact; click the footer action to return to aiming.");
        }
        catch (Exception exception)
        {
            context.Reply($"[Music] Could not open the player: {exception.Message}");
            Logger.LogError(exception, "[SwiftOnlineMusicPlayer] Failed to open the HUD for slot {PlayerSlot}.", player.Slot);
        }
    }

    [Command("music_close", registerRaw: true, helpText: "Close the music UI without stopping playback.")]
    public void CloseCommand(ICommandContext context)
    {
        var player = context.Sender;
        if (player?.IsValid != true)
        {
            context.Reply("[Music] This command must be used by a connected player.");
            return;
        }

        context.Reply(CloseHud(player.Slot)
            ? "[Music] Player UI closed; playback continues in the background."
            : "[Music] Your player UI was not open.");
    }

    [Command("music_stop", registerRaw: true, helpText: "Stop and reset your online music channel.")]
    public void StopCommand(ICommandContext context)
    {
        var player = context.Sender;
        if (player?.IsValid != true)
        {
            context.Reply("[Music] This command must be used by a connected player.");
            return;
        }

        if (!_sessions.TryGetValue(player.Slot, out var session))
        {
            context.Reply("[Music] Nothing is playing.");
            return;
        }

        ResetPlayback(session);
        if (_openSlots.Contains(player.Slot))
        {
            RenderHud(session, forceClasses: true);
        }

        context.Reply("[Music] Playback stopped and reset.");
    }

    [Command("music_status", registerRaw: true, helpText: "Show music player dependency and session status.")]
    public void StatusCommand(ICommandContext context)
    {
        var state = context.Sender is { IsValid: true } player && _sessions.TryGetValue(player.Slot, out var session)
            ? session.State.ToString().ToLowerInvariant()
            : "none";
        context.Reply(
            $"[Music] audio={(_audioApi is null ? "offline" : "ready")}, hud={(_nativeHud is null ? "offline" : "ready")}, search={(_config.MusicSquareSearch.Enabled ? "kuwo-primary+netease-fallback" : "disabled")}, tracks={_config.Tracks.Count}, your-state={state}.");
    }

    [Command("music_search", registerRaw: true, helpText: "Search the MusicSquare-compatible providers and open the clickable result list.")]
    public void SearchCommand(ICommandContext context)
    {
        var player = context.Sender;
        if (player?.IsValid != true || player.IsFakeClient)
        {
            context.Reply("[Music] This command must be used by a connected player.");
            return;
        }

        if (!_config.MusicSquareSearch.Enabled)
        {
            context.Reply("[Music] Online search is disabled by the server administrator.");
            return;
        }

        if (_audioApi is null)
        {
            context.Reply("[Music] SwiftlyS2 Audio is offline, so search results cannot be played.");
            return;
        }

        var query = SanitizeSearchQuery(string.Join(" ", context.Args));
        if (query.Length is < 1 or > 80)
        {
            context.Reply("[Music] Usage: !music_search <song or artist> (1-80 characters).");
            return;
        }

        var session = GetOrCreateSession(player.Slot);
        var now = DateTimeOffset.UtcNow;
        if (session.NextSearchAllowedAt > now)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling((session.NextSearchAllowedAt - now).TotalSeconds));
            context.Reply($"[Music] Please wait {seconds}s before searching again.");
            return;
        }

        session.NextSearchAllowedAt = now.AddSeconds(_config.MusicSquareSearch.CooldownSeconds);
        session.Generation++;
        session.SearchResults.Clear();
        session.SearchIndex = 0;
        session.SearchPage = 0;
        session.SearchMenuOpen = true;
        session.LastSearchQuery = query;
        session.PendingTrack = new MusicTrackConfig
        {
            Title = SanitizeStatus(query, "Searching"),
            Artist = "Searching Kuwo, with Netease fallback",
            DurationSeconds = 0,
            Source = "Search"
        };
        session.ActiveTrack = null;
        session.State = PlaybackState.Searching;
        session.Error = string.Empty;
        session.ElapsedBeforeResume = TimeSpan.Zero;
        session.StartedAt = null;
        session.Channel?.Stop(session.PlayerSlot);

        if (_nativeHud is not null)
        {
            try
            {
                EnsureLayout();
                OpenHud(player.Slot);
            }
            catch (Exception exception)
            {
                Logger.LogWarning(exception, "[SwiftOnlineMusicPlayer] Search started, but the HUD could not be opened for slot {PlayerSlot}.", player.Slot);
            }
        }

        RenderHudIfOpen(session, forceClasses: true);
        context.Reply($"[Music] Searching Kuwo/Netease for \"{SanitizeStatus(query, "music")}\"...");
        var generation = session.Generation;
        var searchConfig = _config.MusicSquareSearch;
        _ = SearchAndPlayAsync(player.Slot, generation, query, searchConfig);
    }

    [Command("music_pick", registerRaw: true, helpText: "Play a numbered result from your latest online search.")]
    public void PickSearchResultCommand(ICommandContext context)
    {
        var player = context.Sender;
        if (player?.IsValid != true)
        {
            context.Reply("[Music] This command must be used by a connected player.");
            return;
        }

        if (!_sessions.TryGetValue(player.Slot, out var session) || session.SearchResults.Count == 0)
        {
            context.Reply("[Music] No search results are stored. Use !music_search <song> first.");
            return;
        }

        if (context.Args.Length != 1 ||
            !int.TryParse(context.Args[0], out var selection) ||
            selection < 1 || selection > session.SearchResults.Count)
        {
            context.Reply($"[Music] Usage: !music_pick <1-{session.SearchResults.Count}>.");
            return;
        }

        BeginLoadSearchTrack(session, selection - 1);
        session.SearchMenuOpen = false;
        RenderHudIfOpen(session, forceClasses: false);
        context.Reply($"[Music] Loading result {selection}: {CurrentTrack(session)?.Title}.");
    }

    [Command("music_library", registerRaw: true, helpText: "Return to the administrator-configured static music library.")]
    public void LibraryCommand(ICommandContext context)
    {
        var player = context.Sender;
        if (player?.IsValid != true)
        {
            context.Reply("[Music] This command must be used by a connected player.");
            return;
        }

        var session = GetOrCreateSession(player.Slot);
        session.SearchResults.Clear();
        session.SearchIndex = 0;
        session.SearchPage = 0;
        session.SearchMenuOpen = false;
        session.LastSearchQuery = string.Empty;
        session.PendingTrack = null;
        if (_config.Tracks.Count == 0)
        {
            ResetPlayback(session);
            session.State = PlaybackState.Error;
            session.Error = "No tracks configured";
            context.Reply("[Music] The static library is empty.");
        }
        else
        {
            BeginLoadTrack(session, session.TrackIndex);
            context.Reply("[Music] Returned to the server music library.");
        }

        RenderHudIfOpen(session, forceClasses: true);
    }

    private async Task SearchAndPlayAsync(
        int playerSlot,
        int generation,
        string query,
        MusicSquareSearchConfig searchConfig)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(searchConfig.TimeoutSeconds));
        try
        {
            var results = await _searchProvider
                .SearchAsync(query, searchConfig, timeout.Token)
                .ConfigureAwait(false);
            if (_unloading)
            {
                return;
            }

            Core.Scheduler.NextWorldUpdate(() =>
                CompleteSearch(playerSlot, generation, query, results));
        }
        catch (Exception exception)
        {
            if (_unloading)
            {
                return;
            }

            Core.Scheduler.NextWorldUpdate(() =>
                FailSearch(playerSlot, generation, query, exception));
        }
    }

    private void CompleteSearch(
        int playerSlot,
        int generation,
        string query,
        IReadOnlyList<MusicTrackConfig> results)
    {
        if (!_sessions.TryGetValue(playerSlot, out var session) || session.Generation != generation)
        {
            return;
        }

        session.PendingTrack = null;
        session.SearchResults.Clear();
        session.SearchResults.AddRange(results);
        session.SearchIndex = 0;
        session.SearchPage = 0;
        session.SearchMenuOpen = true;
        var player = FindPlayer(playerSlot);
        if (results.Count == 0)
        {
            session.State = PlaybackState.Error;
            session.Error = "No playable public result";
            player?.SendChat($"[Music] No playable result was returned for \"{SanitizeStatus(query, "music")}\".");
            RenderHudIfOpen(session, forceClasses: true);
            return;
        }

        session.Error = string.Empty;
        var searchAction = _config.AutoPlayFirstSearchResult
            ? "Playing #1 automatically; use the HUD or !music_pick <number> to choose another track."
            : "Choose a track in the HUD or use !music_pick <number>.";
        player?.SendChat($"[Music] Found {results.Count} result(s). {searchAction}");
        for (var index = 0; index < results.Count; index++)
        {
            var result = results[index];
            var variant = LooksLikeVariant(result.Title, result.Artist) ? " · variant" : string.Empty;
            player?.SendChat(
                $"[Music] {index + 1}. {result.Title} — {result.Artist} [{result.Source}]{variant}");
        }

        if (_config.AutoPlayFirstSearchResult)
        {
            BeginLoadSearchTrack(session, 0);
        }
        else
        {
            session.State = PlaybackState.Browsing;
        }

        RenderHudIfOpen(session, forceClasses: true);
    }

    private void FailSearch(int playerSlot, int generation, string query, Exception exception)
    {
        if (!_sessions.TryGetValue(playerSlot, out var session) || session.Generation != generation)
        {
            return;
        }

        session.PendingTrack = null;
        session.SearchMenuOpen = true;
        session.State = PlaybackState.Error;
        session.Error = exception is OperationCanceledException
            ? "Search timed out"
            : SanitizeStatus(exception.Message, "Search request failed");
        FindPlayer(playerSlot)?.SendChat(
            $"[Music] Search failed for \"{SanitizeStatus(query, "music")}\": {session.Error}.");
        Logger.LogWarning(
            exception,
            "[SwiftOnlineMusicPlayer] MusicSquare-compatible search failed for slot {PlayerSlot}, query {Query}.",
            playerSlot,
            SanitizeStatus(query, "music"));
        RenderHudIfOpen(session, forceClasses: true);
    }

    private SwiftlyS2.Shared.Players.IPlayer? FindPlayer(int playerSlot) =>
        Core.PlayerManager.GetAllPlayers().FirstOrDefault(player =>
            player.IsValid && !player.IsFakeClient && player.Slot == playerSlot);

    private void RenderHudIfOpen(PlayerSession session, bool forceClasses)
    {
        if (_openSlots.Contains(session.PlayerSlot))
        {
            RenderHud(session, forceClasses);
        }
    }

    private void InitializeRuntimeConfiguration()
    {
        _ = Core.Configuration
            .InitializeJsonWithModel<MusicPlayerConfig>(RuntimeConfigFileName, RuntimeConfigSectionName)
            .Configure(builder =>
                builder.AddJsonFile(RuntimeConfigFileName, optional: false, reloadOnChange: true));

        ReloadRuntimeConfiguration();
        _configReloadSubscription?.Dispose();
        _configReloadSubscription = ChangeToken.OnChange(
            () => Core.Configuration.Manager.GetReloadToken(),
            () => Core.Scheduler.NextWorldUpdate(ReloadRuntimeConfiguration));
    }

    private void ReloadRuntimeConfiguration()
    {
        try
        {
            var configured = Core.Configuration.Manager
                .GetSection(RuntimeConfigSectionName)
                .Get<MusicPlayerConfig>();
            var normalized = MusicPlayerConfig.Normalize(configured);

            foreach (var session in _sessions.Values)
            {
                ResetPlayback(session);
                session.SearchResults.Clear();
                session.SearchIndex = 0;
                session.SearchPage = 0;
                session.SearchMenuOpen = false;
                session.LastSearchQuery = string.Empty;
                session.PendingTrack = null;
                session.ActiveTrack = null;
                session.TrackIndex = normalized.Tracks.Count == 0
                    ? 0
                    : Math.Clamp(session.TrackIndex, 0, normalized.Tracks.Count - 1);
                session.VolumeStep = VolumeToStep(normalized.DefaultVolume);
                session.Channel?.SetVolume(session.PlayerSlot, VolumeFromStep(session.VolumeStep));
            }

            _config = normalized;
            _sourceCache.Clear();
            UpdateOpenHuds();
            Logger.LogInformation(
                "[SwiftOnlineMusicPlayer] Config loaded from {Path}: tracks={TrackCount}, autoAdvance={AutoAdvance}, autoPlayFirstSearchResult={AutoPlayFirstSearchResult}, defaultVolume={DefaultVolume:F2}, musicSquareSearch={SearchEnabled}.",
                Core.Configuration.GetConfigPath(RuntimeConfigFileName),
                normalized.Tracks.Count,
                normalized.AutoAdvance,
                normalized.AutoPlayFirstSearchResult,
                normalized.DefaultVolume,
                normalized.MusicSquareSearch.Enabled);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                exception,
                "[SwiftOnlineMusicPlayer] Config reload failed at {Path}; keeping the previous validated config.",
                Core.Configuration.GetConfigPath(RuntimeConfigFileName));
        }
    }

    private void EnsureLayout()
    {
        if (_layoutEntity is { IsValid: true })
        {
            return;
        }

        _openSlots.Clear();
        _inputCapturedSlots.Clear();
        _mouse2HeldSlots.Clear();
        _mouse2CapturePendingSlots.Clear();
        foreach (var session in _sessions.Values)
        {
            session.ResetRenderedClasses();
        }

        using var keyValues = new CEntityKeyValues();
        keyValues.SetString("targetname", TargetName);
        keyValues.SetString("layout", LayoutResource);
        var entity = Core.EntitySystem.CreateEntityByDesignerName<CCSCustomHudLayout>(DesignerName, -1);
        entity.DispatchSpawn(keyValues);
        _layoutEntity = entity;
        Logger.LogInformation(
            "[SwiftOnlineMusicPlayer] Spawned Custom HUD entity #{EntityIndex}: {LayoutResource}.",
            entity.Index,
            LayoutResource);
    }

    private void OpenHud(int playerSlot)
    {
        if (!TryGetLayoutAddress(out var layoutAddress) || _nativeHud is null)
        {
            return;
        }

        var session = GetOrCreateSession(playerSlot);
        _openSlots.Add(playerSlot);
        _mouse2HeldSlots.Remove(playerSlot);
        _mouse2CapturePendingSlots.Remove(playerSlot);
        _nativeHud.SetHasClassForPlayer(layoutAddress, playerSlot, DialogPanelId, HiddenClass, false);
        SetHudInteraction(playerSlot, enabled: false);
        session.ResetRenderedClasses();
        RenderHud(session, forceClasses: true);
    }

    private bool CloseHud(int playerSlot)
    {
        if (!_openSlots.Contains(playerSlot))
        {
            return false;
        }

        if (TryGetLayoutAddress(out var layoutAddress) && _nativeHud is not null)
        {
            _nativeHud.SetInputCaptureEnabled(layoutAddress, playerSlot, false);
            _nativeHud.SetHasClassForPlayer(layoutAddress, playerSlot, DialogPanelId, InteractiveClass, false);
            _nativeHud.SetHasClassForPlayer(layoutAddress, playerSlot, DialogPanelId, HiddenClass, true);
        }

        ForgetHudState(playerSlot);
        return true;
    }

    private void OnProcessUsercmdsPre(ref ProcessUsercmdsPreContext context)
    {
        var playerSlot = context.Params.Player.Slot;
        if (!_openSlots.Contains(playerSlot))
        {
            _mouse2HeldSlots.Remove(playerSlot);
            _mouse2CapturePendingSlots.Remove(playerSlot);
            return;
        }

        var wasMouse2Down = _mouse2HeldSlots.Contains(playerSlot);
        var inputCaptured = _inputCapturedSlots.Contains(playerSlot);
        var enableInteractionAfterRelease = false;
        var mouse2Mask = (ulong)GameButtonFlags.Mouse2;
        foreach (var userCmd in context.Params.Usercmds)
        {
            var command = userCmd.CSGOUserCmd;
            var buttons = command.Base.ButtonsPb;
            var isMouse2Down = (buttons.Buttonstate1 & mouse2Mask) != 0;

            if (!inputCaptured)
            {
                if (isMouse2Down && !wasMouse2Down)
                {
                    // Wait for this click to be released before enabling Panorama
                    // capture. Otherwise the release can be swallowed, leaving the
                    // next right-click indistinguishable from the first held click.
                    _mouse2CapturePendingSlots.Add(playerSlot);
                }
                else if (!isMouse2Down && wasMouse2Down &&
                         _mouse2CapturePendingSlots.Remove(playerSlot))
                {
                    enableInteractionAfterRelease = true;
                }
            }

            wasMouse2Down = isMouse2Down;
            buttons.Buttonstate1 &= ~mouse2Mask;
            buttons.Buttonstate2 &= ~mouse2Mask;
            buttons.Buttonstate3 &= ~mouse2Mask;
            command.Attack2StartHistoryIndex = -1;
        }

        if (wasMouse2Down)
        {
            _mouse2HeldSlots.Add(playerSlot);
        }
        else
        {
            _mouse2HeldSlots.Remove(playerSlot);
        }

        if (enableInteractionAfterRelease)
        {
            Core.Scheduler.NextWorldUpdate(() => SetHudInteraction(playerSlot, enabled: true));
        }
    }

    private bool SetHudInteraction(int playerSlot, bool enabled)
    {
        if (!_openSlots.Contains(playerSlot) ||
            !TryGetLayoutAddress(out var layoutAddress) ||
            _nativeHud is null)
        {
            _inputCapturedSlots.Remove(playerSlot);
            _mouse2CapturePendingSlots.Remove(playerSlot);
            return false;
        }

        _mouse2CapturePendingSlots.Remove(playerSlot);
        _nativeHud.SetInputCaptureEnabled(layoutAddress, playerSlot, enabled);
        _nativeHud.SetHasClassForPlayer(layoutAddress, playerSlot, DialogPanelId, InteractiveClass, enabled);
        SetDialogValue(
            layoutAddress,
            playerSlot,
            "interaction-hint",
            enabled ? "CLICK TO RETURN TO AIM" : "RIGHT CLICK TO INTERACT");

        if (enabled)
        {
            _inputCapturedSlots.Add(playerSlot);
        }
        else
        {
            _inputCapturedSlots.Remove(playerSlot);
        }

        return true;
    }

    private void ForgetHudState(int playerSlot)
    {
        _openSlots.Remove(playerSlot);
        _inputCapturedSlots.Remove(playerSlot);
        _mouse2HeldSlots.Remove(playerSlot);
        _mouse2CapturePendingSlots.Remove(playerSlot);
    }

    private PlayerSession GetOrCreateSession(int playerSlot)
    {
        if (_sessions.TryGetValue(playerSlot, out var existing))
        {
            if (existing.Channel is null && _audioApi is not null)
            {
                existing.Channel = _audioApi.UseChannel(ChannelId(playerSlot));
                existing.Channel.SetVolume(playerSlot, VolumeFromStep(existing.VolumeStep));
            }

            return existing;
        }

        var session = new PlayerSession(playerSlot)
        {
            VolumeStep = VolumeToStep(_config.DefaultVolume),
            Channel = _audioApi?.UseChannel(ChannelId(playerSlot))
        };
        session.Channel?.SetVolume(playerSlot, VolumeFromStep(session.VolumeStep));
        _sessions[playerSlot] = session;
        return session;
    }

    private void OnNativeCustomHudClicked(nint playerControllerAddress, nint layoutAddress, string buttonId)
    {
        Core.Scheduler.NextWorldUpdate(() =>
            ProcessNativeCustomHudClick(playerControllerAddress, layoutAddress, buttonId));
    }

    private void ProcessNativeCustomHudClick(nint playerControllerAddress, nint layoutAddress, string buttonId)
    {
        if (!TryGetLayoutAddress(out var expectedLayoutAddress) || layoutAddress != expectedLayoutAddress)
        {
            return;
        }

        var player = Core.PlayerManager.GetAllPlayers().FirstOrDefault(candidate =>
            candidate.IsValid &&
            candidate.Controller is { IsValid: true } controller &&
            controller.Address == playerControllerAddress);
        if (player is null ||
            !_openSlots.Contains(player.Slot) ||
            !_inputCapturedSlots.Contains(player.Slot))
        {
            return;
        }

        var session = GetOrCreateSession(player.Slot);
        switch (buttonId)
        {
            case "music_player_prev":
                SelectRelativeTrack(session, -1);
                break;
            case "music_player_play_pause":
                TogglePlayback(session);
                break;
            case "music_player_next":
                SelectRelativeTrack(session, 1);
                break;
            case "music_player_volume_down":
                AdjustVolume(session, -1);
                break;
            case "music_player_volume_up":
                AdjustVolume(session, 1);
                break;
            case "music_player_favorite":
                ToggleFavorite(session);
                break;
            case "music_player_search_toggle":
                ToggleSearchMenu(session);
                break;
            case "music_player_results_prev":
                ChangeSearchPage(session, -1);
                break;
            case "music_player_results_next":
                ChangeSearchPage(session, 1);
                break;
            case "music_player_result_1":
                SelectSearchRow(session, 0);
                break;
            case "music_player_result_2":
                SelectSearchRow(session, 1);
                break;
            case "music_player_result_3":
                SelectSearchRow(session, 2);
                break;
            case "music_player_result_4":
                SelectSearchRow(session, 3);
                break;
            case "music_player_result_5":
                SelectSearchRow(session, 4);
                break;
            case "music_player_close":
                _ = CloseHud(player.Slot);
                return;
            case "music_player_return_to_aim":
                _ = SetHudInteraction(player.Slot, enabled: false);
                return;
            default:
                return;
        }

        RenderHud(session, forceClasses: false);
        Logger.LogInformation(
            "[SwiftOnlineMusicPlayer] HUD action: slot={PlayerSlot}, button={ButtonId}, state={State}, track={TrackIndex}.",
            player.Slot,
            buttonId,
            session.State,
            session.TrackIndex);
    }

    private void TogglePlayback(PlayerSession session)
    {
        if (!EnsureAudioChannel(session))
        {
            return;
        }

        switch (session.State)
        {
            case PlaybackState.Searching:
            case PlaybackState.Loading:
                return;
            case PlaybackState.Playing:
                session.ElapsedBeforeResume = GetElapsed(session);
                session.StartedAt = null;
                session.Channel!.Pause(session.PlayerSlot);
                session.State = PlaybackState.Paused;
                session.Error = string.Empty;
                break;
            case PlaybackState.Paused:
                session.Channel!.Resume(session.PlayerSlot);
                session.StartedAt = DateTimeOffset.UtcNow;
                session.State = PlaybackState.Playing;
                session.Error = string.Empty;
                break;
            default:
                if (session.SearchResults.Count > 0)
                {
                    BeginLoadSearchTrack(session, session.SearchIndex);
                    session.SearchMenuOpen = false;
                }
                else
                {
                    BeginLoadTrack(session, session.TrackIndex);
                }
                break;
        }
    }

    private void SelectRelativeTrack(PlayerSession session, int delta)
    {
        if (session.SearchResults.Count > 0)
        {
            var searchIndex = (session.SearchIndex + delta) % session.SearchResults.Count;
            if (searchIndex < 0)
            {
                searchIndex += session.SearchResults.Count;
            }

            BeginLoadSearchTrack(session, searchIndex);
            return;
        }

        if (_config.Tracks.Count == 0)
        {
            session.State = PlaybackState.Error;
            session.Error = "No tracks configured";
            return;
        }

        var index = (session.TrackIndex + delta) % _config.Tracks.Count;
        if (index < 0)
        {
            index += _config.Tracks.Count;
        }

        BeginLoadTrack(session, index);
    }

    private static void ToggleSearchMenu(PlayerSession session)
    {
        if (session.SearchResults.Count == 0 && string.IsNullOrEmpty(session.LastSearchQuery))
        {
            return;
        }

        session.SearchMenuOpen = !session.SearchMenuOpen;
    }

    private static void ChangeSearchPage(PlayerSession session, int delta)
    {
        var pageCount = SearchPageCount(session.SearchResults.Count);
        if (pageCount <= 1)
        {
            session.SearchPage = 0;
            return;
        }

        var nextPage = (session.SearchPage + delta) % pageCount;
        session.SearchPage = nextPage < 0 ? nextPage + pageCount : nextPage;
    }

    private void SelectSearchRow(PlayerSession session, int rowIndex)
    {
        var searchIndex = session.SearchPage * SearchRowsPerPage + rowIndex;
        if (searchIndex < 0 || searchIndex >= session.SearchResults.Count)
        {
            return;
        }

        BeginLoadSearchTrack(session, searchIndex);
        session.SearchMenuOpen = false;
    }

    private void ToggleFavorite(PlayerSession session)
    {
        var track = CurrentTrack(session);
        if (track is null || session.State == PlaybackState.Searching)
        {
            return;
        }

        var key = TrackKey(track);
        if (!session.FavoriteTrackKeys.Remove(key))
        {
            session.FavoriteTrackKeys.Add(key);
        }
    }

    private void BeginLoadTrack(PlayerSession session, int trackIndex)
    {
        if (_config.Tracks.Count == 0)
        {
            session.State = PlaybackState.Error;
            session.Error = "No tracks configured";
            return;
        }

        session.TrackIndex = Math.Clamp(trackIndex, 0, _config.Tracks.Count - 1);
        session.SearchResults.Clear();
        session.SearchIndex = 0;
        session.SearchPage = 0;
        session.SearchMenuOpen = false;
        session.LastSearchQuery = string.Empty;
        session.PendingTrack = null;
        BeginLoadResolvedTrack(session, _config.Tracks[session.TrackIndex]);
    }

    private void BeginLoadSearchTrack(PlayerSession session, int searchIndex)
    {
        if (session.SearchResults.Count == 0)
        {
            session.State = PlaybackState.Error;
            session.Error = "No search results stored";
            return;
        }

        session.SearchIndex = Math.Clamp(searchIndex, 0, session.SearchResults.Count - 1);
        session.SearchPage = session.SearchIndex / SearchRowsPerPage;
        session.PendingTrack = null;
        BeginLoadResolvedTrack(session, session.SearchResults[session.SearchIndex]);
    }

    private void BeginLoadResolvedTrack(PlayerSession session, MusicTrackConfig track)
    {
        if (!EnsureAudioChannel(session))
        {
            return;
        }

        session.Generation++;
        session.State = PlaybackState.Loading;
        session.Error = string.Empty;
        session.ElapsedBeforeResume = TimeSpan.Zero;
        session.StartedAt = null;
        session.PendingTrack = track;
        session.ActiveTrack = null;
        session.Channel!.Stop(session.PlayerSlot);

        var generation = session.Generation;
        var api = _audioApi!;
        _ = DecodeAndStartAsync(session.PlayerSlot, generation, track, api);
    }

    private async Task DecodeAndStartAsync(
        int playerSlot,
        int generation,
        MusicTrackConfig track,
        IAudioApi api)
    {
        try
        {
            var sourceFactory = _sourceCache.GetOrAdd(
                track.Url,
                url => new Lazy<Task<IAudioSource>>(
                    () => api.DecodeFromUrlAsync(new Uri(url)),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            var source = await sourceFactory.Value.ConfigureAwait(false);
            if (_unloading)
            {
                return;
            }

            Core.Scheduler.NextWorldUpdate(() =>
                CompleteTrackLoad(playerSlot, generation, track, source));
        }
        catch (Exception exception)
        {
            _sourceCache.TryRemove(track.Url, out _);
            if (_unloading)
            {
                return;
            }

            Core.Scheduler.NextWorldUpdate(() =>
                FailTrackLoad(playerSlot, generation, track, exception));
        }
    }

    private void CompleteTrackLoad(
        int playerSlot,
        int generation,
        MusicTrackConfig track,
        IAudioSource source)
    {
        if (_unloading ||
            !_sessions.TryGetValue(playerSlot, out var session) ||
            session.Generation != generation ||
            !EnsureAudioChannel(session))
        {
            return;
        }

        session.Channel!.Stop(playerSlot);
        session.Channel.SetSource(source);
        session.Channel.SetVolume(playerSlot, VolumeFromStep(session.VolumeStep));
        session.Channel.Play(playerSlot);
        session.ActiveTrack = track;
        session.PendingTrack = null;
        session.State = PlaybackState.Playing;
        session.Error = string.Empty;
        session.ElapsedBeforeResume = TimeSpan.Zero;
        session.StartedAt = DateTimeOffset.UtcNow;
        Logger.LogInformation(
            "[SwiftOnlineMusicPlayer] Playing verified track for slot {PlayerSlot}: {Title} / {Artist} [{Source}:{SourceId}].",
            playerSlot,
            track.Title,
            track.Artist,
            track.Source,
            track.SourceId);

        if (_openSlots.Contains(playerSlot))
        {
            RenderHud(session, forceClasses: false);
        }
    }

    private void FailTrackLoad(
        int playerSlot,
        int generation,
        MusicTrackConfig track,
        Exception exception)
    {
        if (!_sessions.TryGetValue(playerSlot, out var session) || session.Generation != generation)
        {
            return;
        }

        session.State = PlaybackState.Error;
        session.Error = SanitizeStatus(exception.Message, "Stream could not be decoded");
        session.StartedAt = null;
        session.PendingTrack = null;
        session.ActiveTrack = null;
        Logger.LogWarning(
            exception,
            "[SwiftOnlineMusicPlayer] Online source decode failed for slot {PlayerSlot}: {Title} / {Artist} [{Source}:{SourceId}].",
            playerSlot,
            track.Title,
            track.Artist,
            track.Source,
            track.SourceId);

        if (_openSlots.Contains(playerSlot))
        {
            RenderHud(session, forceClasses: false);
        }
    }

    private bool EnsureAudioChannel(PlayerSession session)
    {
        if (_audioApi is null)
        {
            session.State = PlaybackState.Error;
            session.Error = "Audio extension is offline";
            return false;
        }

        session.Channel ??= _audioApi.UseChannel(ChannelId(session.PlayerSlot));
        return true;
    }

    private void AdjustVolume(PlayerSession session, int delta)
    {
        session.VolumeStep = Math.Clamp(session.VolumeStep + delta, 0, VolumeStepCount);
        if (EnsureAudioChannel(session))
        {
            session.Channel!.SetVolume(session.PlayerSlot, VolumeFromStep(session.VolumeStep));
        }
    }

    private void UpdateOpenHuds()
    {
        if (!TryGetLayoutAddress(out _))
        {
            _openSlots.Clear();
            _inputCapturedSlots.Clear();
            _mouse2HeldSlots.Clear();
            _mouse2CapturePendingSlots.Clear();
            return;
        }

        foreach (var playerSlot in _openSlots.ToArray())
        {
            if (!_sessions.TryGetValue(playerSlot, out var session))
            {
                ForgetHudState(playerSlot);
                continue;
            }

            var track = CurrentTrack(session);
            if (session.State == PlaybackState.Playing &&
                track is { DurationSeconds: > 0 } &&
                GetElapsed(session).TotalSeconds >= track.DurationSeconds)
            {
                if (_config.AutoAdvance && _config.Tracks.Count > 0)
                {
                    SelectRelativeTrack(session, 1);
                }
                else
                {
                    ResetPlayback(session);
                }
            }

            RenderHud(session, forceClasses: false);
        }
    }

    private void RenderHud(PlayerSession session, bool forceClasses)
    {
        if (!TryGetLayoutAddress(out var layoutAddress) || _nativeHud is null)
        {
            return;
        }

        var track = CurrentTrack(session);
        var elapsed = GetElapsed(session);
        var progressStep = 0;
        if (track is { DurationSeconds: > 0 })
        {
            elapsed = TimeSpan.FromSeconds(Math.Min(elapsed.TotalSeconds, track.DurationSeconds));
            progressStep = Math.Clamp(
                (int)Math.Floor(elapsed.TotalSeconds / track.DurationSeconds * ProgressStepCount),
                0,
                ProgressStepCount);
        }

        var searchPageCount = SearchPageCount(session.SearchResults.Count);
        session.SearchPage = Math.Clamp(session.SearchPage, 0, searchPageCount - 1);
        var searchPageStart = session.SearchPage * SearchRowsPerPage;
        var searchPageItems = Math.Clamp(
            session.SearchResults.Count - searchPageStart,
            0,
            SearchRowsPerPage);
        var searchSelection = session.SearchIndex >= searchPageStart &&
                              session.SearchIndex < searchPageStart + searchPageItems
            ? session.SearchIndex - searchPageStart + 1
            : 0;
        var searchAvailable = !string.IsNullOrEmpty(session.LastSearchQuery);
        var hasTrack = track is not null && session.State != PlaybackState.Searching;
        var inputCaptured = _inputCapturedSlots.Contains(session.PlayerSlot);

        SetDialogValue(layoutAddress, session.PlayerSlot, "track-title", track?.Title ?? "No tracks configured");
        SetDialogValue(layoutAddress, session.PlayerSlot, "artist-name", track?.Artist ?? "Edit config.jsonc");
        SetDialogValue(layoutAddress, session.PlayerSlot, "source-name", track?.Source ?? "Server library");
        SetDialogValue(layoutAddress, session.PlayerSlot, "current-time", FormatTime(elapsed));
        SetDialogValue(layoutAddress, session.PlayerSlot, "duration", track is { DurationSeconds: > 0 }
            ? FormatTime(TimeSpan.FromSeconds(track.DurationSeconds))
            : "LIVE");
        SetDialogValue(layoutAddress, session.PlayerSlot, "play-action", session.State switch
        {
            PlaybackState.Playing => "PAUSE",
            PlaybackState.Searching => "WAIT",
            PlaybackState.Loading => "WAIT",
            _ => "PLAY"
        });
        SetDialogValue(layoutAddress, session.PlayerSlot, "volume-text", $"VOL {session.VolumeStep * 20}%");
        SetDialogValue(layoutAddress, session.PlayerSlot, "status", StatusText(session));
        SetDialogValue(layoutAddress, session.PlayerSlot, "search-hint-kicker", "DISCOVER");
        SetDialogValue(layoutAddress, session.PlayerSlot, "search-hint-text", "USE !MUSIC_SEARCH <SONG OR ARTIST>");
        SetDialogValue(layoutAddress, session.PlayerSlot, "search-heading", "SEARCH RESULTS");
        SetDialogValue(layoutAddress, session.PlayerSlot, "search-query", session.LastSearchQuery);
        SetDialogValue(layoutAddress, session.PlayerSlot, "search-page", session.State == PlaybackState.Searching
            ? "SEARCHING"
            : session.SearchResults.Count == 0
                ? "0 RESULTS"
                : $"{session.SearchPage + 1} / {searchPageCount}");
        SetDialogValue(layoutAddress, session.PlayerSlot, "results-label", session.State == PlaybackState.Searching
            ? "SEARCHING"
            : $"{session.SearchResults.Count} RESULT{(session.SearchResults.Count == 1 ? string.Empty : "S")}");
        SetDialogValue(layoutAddress, session.PlayerSlot, "results-hint", session.SearchMenuOpen
            ? "CLICK TO COLLAPSE"
            : "CLICK TO BROWSE");
        SetDialogValue(layoutAddress, session.PlayerSlot, "results-chevron", session.SearchMenuOpen ? "-" : "+");
        SetDialogValue(layoutAddress, session.PlayerSlot, "search-empty-title", session.State == PlaybackState.Searching
            ? "SEARCHING ONLINE PROVIDERS"
            : "NO PLAYABLE RESULTS");
        SetDialogValue(layoutAddress, session.PlayerSlot, "search-empty-hint", session.State == PlaybackState.Searching
            ? "KUWO PRIMARY / NETEASE FALLBACK"
            : "TRY A MORE SPECIFIC SONG OR ARTIST");
        SetDialogValue(layoutAddress, session.PlayerSlot, "search-drawer-hint", "CLICK A TRACK TO PLAY / USE THE ARROWS TO CHANGE PAGE");
        SetDialogValue(
            layoutAddress,
            session.PlayerSlot,
            "interaction-hint",
            inputCaptured ? "CLICK TO RETURN TO AIM" : "RIGHT CLICK TO INTERACT");

        for (var row = 0; row < SearchRowsPerPage; row++)
        {
            var resultIndex = searchPageStart + row;
            var result = resultIndex < session.SearchResults.Count
                ? session.SearchResults[resultIndex]
                : null;
            var variableIndex = row + 1;
            SetDialogValue(
                layoutAddress,
                session.PlayerSlot,
                $"search-result-{variableIndex}-index",
                result is null ? string.Empty : (resultIndex + 1).ToString("00"));
            SetDialogValue(
                layoutAddress,
                session.PlayerSlot,
                $"search-result-{variableIndex}-title",
                result?.Title ?? string.Empty);
            SetDialogValue(
                layoutAddress,
                session.PlayerSlot,
                $"search-result-{variableIndex}-meta",
                result is null
                    ? string.Empty
                    : $"{result.Artist} / {result.Source} / {(result.DurationSeconds > 0 ? FormatTime(TimeSpan.FromSeconds(result.DurationSeconds)) : "LIVE")}");
        }

        if (forceClasses)
        {
            foreach (var step in Enumerable.Range(0, ProgressStepCount + 1))
            {
                SetBooleanClass(layoutAddress, session.PlayerSlot, $"Progress{step}", false);
            }
            foreach (var step in Enumerable.Range(0, VolumeStepCount + 1))
            {
                SetBooleanClass(layoutAddress, session.PlayerSlot, $"Volume{step}", false);
            }
            foreach (var step in Enumerable.Range(0, SearchRowsPerPage + 1))
            {
                SetBooleanClass(layoutAddress, session.PlayerSlot, $"SearchItems{step}", false);
                SetBooleanClass(layoutAddress, session.PlayerSlot, $"SearchSelection{step}", false);
            }
        }

        RenderExclusiveClass(
            layoutAddress,
            session.PlayerSlot,
            $"Progress{progressStep}",
            forceClasses ? null : session.LastProgressClass);
        session.LastProgressClass = $"Progress{progressStep}";

        RenderExclusiveClass(
            layoutAddress,
            session.PlayerSlot,
            $"Volume{session.VolumeStep}",
            forceClasses ? null : session.LastVolumeClass);
        session.LastVolumeClass = $"Volume{session.VolumeStep}";

        RenderExclusiveClass(
            layoutAddress,
            session.PlayerSlot,
            $"SearchItems{searchPageItems}",
            forceClasses ? null : session.LastSearchItemsClass);
        session.LastSearchItemsClass = $"SearchItems{searchPageItems}";

        RenderExclusiveClass(
            layoutAddress,
            session.PlayerSlot,
            $"SearchSelection{searchSelection}",
            forceClasses ? null : session.LastSearchSelectionClass);
        session.LastSearchSelectionClass = $"SearchSelection{searchSelection}";

        RenderStateClasses(layoutAddress, session, forceClasses);
        SetBooleanClass(layoutAddress, session.PlayerSlot, "MusicHudLive", track?.DurationSeconds is not > 0);
        SetBooleanClass(layoutAddress, session.PlayerSlot, "MusicHudSearchAvailable", searchAvailable);
        SetBooleanClass(layoutAddress, session.PlayerSlot, "MusicHudSearchOpen", searchAvailable && session.SearchMenuOpen);
        SetBooleanClass(layoutAddress, session.PlayerSlot, "MusicHudMultiPage", searchPageCount > 1);
        SetBooleanClass(layoutAddress, session.PlayerSlot, "MusicHudHasTrack", hasTrack);
        SetBooleanClass(layoutAddress, session.PlayerSlot, InteractiveClass, inputCaptured);
        SetBooleanClass(
            layoutAddress,
            session.PlayerSlot,
            "MusicHudFavorite",
            hasTrack && session.FavoriteTrackKeys.Contains(TrackKey(track!)));
    }

    private void RenderStateClasses(nint layoutAddress, PlayerSession session, bool force)
    {
        var nextStateClass = session.State switch
        {
            PlaybackState.Playing => "MusicHudPlaying",
            PlaybackState.Searching => "MusicHudLoading",
            PlaybackState.Loading => "MusicHudLoading",
            PlaybackState.Browsing => "MusicHudBrowsing",
            PlaybackState.Error => "MusicHudError",
            PlaybackState.Paused => "MusicHudPaused",
            _ => "MusicHudIdle"
        };

        if (!force && session.LastStateClass == nextStateClass)
        {
            return;
        }

        if (!string.IsNullOrEmpty(session.LastStateClass))
        {
            SetBooleanClass(layoutAddress, session.PlayerSlot, session.LastStateClass, false);
        }
        else
        {
            foreach (var className in new[]
                     {
                         "MusicHudPlaying", "MusicHudLoading", "MusicHudBrowsing", "MusicHudError", "MusicHudPaused", "MusicHudIdle"
                     })
            {
                SetBooleanClass(layoutAddress, session.PlayerSlot, className, false);
            }
        }

        SetBooleanClass(layoutAddress, session.PlayerSlot, nextStateClass, true);
        session.LastStateClass = nextStateClass;
    }

    private void RenderExclusiveClass(nint layoutAddress, int playerSlot, string nextClass, string? previousClass)
    {
        if (previousClass == nextClass)
        {
            return;
        }

        if (!string.IsNullOrEmpty(previousClass))
        {
            SetBooleanClass(layoutAddress, playerSlot, previousClass, false);
        }
        SetBooleanClass(layoutAddress, playerSlot, nextClass, true);
    }

    private void SetDialogValue(nint layoutAddress, int playerSlot, string variableName, string value) =>
        _nativeHud!.SetDialogVariableStringForPlayer(
            layoutAddress,
            playerSlot,
            DialogPanelId,
            variableName,
            value);

    private void SetBooleanClass(nint layoutAddress, int playerSlot, string className, bool enabled) =>
        _nativeHud!.SetHasClassForPlayer(layoutAddress, playerSlot, DialogPanelId, className, enabled);

    private MusicTrackConfig? CurrentTrack(PlayerSession session)
    {
        if (session.PendingTrack is not null)
        {
            return session.PendingTrack;
        }

        if (session.ActiveTrack is not null)
        {
            return session.ActiveTrack;
        }

        if (session.SearchResults.Count > 0)
        {
            session.SearchIndex = Math.Clamp(session.SearchIndex, 0, session.SearchResults.Count - 1);
            return session.SearchResults[session.SearchIndex];
        }

        if (_config.Tracks.Count == 0)
        {
            return null;
        }

        session.TrackIndex = Math.Clamp(session.TrackIndex, 0, _config.Tracks.Count - 1);
        return _config.Tracks[session.TrackIndex];
    }

    private static TimeSpan GetElapsed(PlayerSession session) =>
        session.ElapsedBeforeResume +
        (session.StartedAt is { } started ? DateTimeOffset.UtcNow - started : TimeSpan.Zero);

    private static int SearchPageCount(int resultCount) =>
        Math.Max(1, (Math.Max(0, resultCount) + SearchRowsPerPage - 1) / SearchRowsPerPage);

    private static string TrackKey(MusicTrackConfig track) =>
        $"{track.Source}\n{track.SourceId}\n{track.Url}";

    private static string FormatTime(TimeSpan time)
    {
        var totalSeconds = Math.Max(0, (int)Math.Floor(time.TotalSeconds));
        return $"{totalSeconds / 60}:{totalSeconds % 60:00}";
    }

    private static string StatusText(PlayerSession session) => session.State switch
    {
        PlaybackState.Playing => "ONLINE STREAM / PLAYING",
        PlaybackState.Paused => "PAUSED / CLICK PLAY TO RESUME",
        PlaybackState.Browsing => "RESULTS READY / CLICK A TRACK TO PLAY",
        PlaybackState.Searching => "SEARCHING / KUWO PRIMARY · NETEASE FALLBACK",
        PlaybackState.Loading => "VERIFYING / DECODING SELECTED AUDIO",
        PlaybackState.Error => $"ERROR / {session.Error}",
        _ => "READY / SELECT PLAY"
    };

    private static string SanitizeStatus(string? value, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(value)
            ? fallback
            : new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return text.Length <= 96 ? text : text[..96];
    }

    private static bool LooksLikeVariant(string title, string artist)
    {
        var text = $"{title} {artist}".ToLowerInvariant();
        return TrackVariantMarkers.Any(marker =>
            text.Contains(marker, StringComparison.Ordinal));
    }

    private static string SanitizeSearchQuery(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
    }

    private static string ChannelId(int playerSlot) => $"swift-online-music-player-{playerSlot}";
    private static float VolumeFromStep(int step) => Math.Clamp(step, 0, VolumeStepCount) / (float)VolumeStepCount;
    private static int VolumeToStep(float volume) => Math.Clamp((int)Math.Round(volume * VolumeStepCount), 0, VolumeStepCount);

    private void ResetPlayback(PlayerSession session)
    {
        session.Generation++;
        try
        {
            // Audio 1.0.6 Reset only rewinds the cursor; Stop also pauses the
            // per-slot stream so a later occupant of this slot cannot inherit it.
            session.Channel?.Stop(session.PlayerSlot);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "[SwiftOnlineMusicPlayer] Failed to stop audio for slot {PlayerSlot}.", session.PlayerSlot);
        }

        session.State = PlaybackState.Idle;
        session.Error = string.Empty;
        session.ElapsedBeforeResume = TimeSpan.Zero;
        session.StartedAt = null;
        session.PendingTrack = null;
        session.ActiveTrack = null;
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event) =>
        StopAndRemoveSession(@event.PlayerId, releaseHud: false);

    private void StopAndRemoveSession(int playerSlot, bool releaseHud)
    {
        if (releaseHud)
        {
            _ = CloseHud(playerSlot);
        }
        else
        {
            ForgetHudState(playerSlot);
        }

        if (!_sessions.Remove(playerSlot, out var session))
        {
            return;
        }

        ResetPlayback(session);
    }

    private bool TryGetLayoutAddress(out nint layoutAddress)
    {
        if (_layoutEntity is { IsValid: true } layout)
        {
            layoutAddress = layout.Address;
            return true;
        }

        layoutAddress = nint.Zero;
        return false;
    }

    private bool ClearLayout(string reason)
    {
        var entity = _layoutEntity;
        _layoutEntity = null;

        if (entity is { IsValid: true } && _nativeHud is not null)
        {
            foreach (var playerSlot in _openSlots.ToArray())
            {
                try
                {
                    _nativeHud.SetInputCaptureEnabled(entity.Address, playerSlot, false);
                }
                catch (Exception exception)
                {
                    Logger.LogWarning(exception, "[SwiftOnlineMusicPlayer] Input release failed for slot {PlayerSlot}.", playerSlot);
                }
            }
        }

        _openSlots.Clear();
        _inputCapturedSlots.Clear();
        _mouse2HeldSlots.Clear();
        _mouse2CapturePendingSlots.Clear();
        if (entity is not { IsValid: true })
        {
            return false;
        }

        try
        {
            entity.AcceptInput("Kill", string.Empty);
            Logger.LogInformation(
                "[SwiftOnlineMusicPlayer] Requested Custom HUD entity #{EntityIndex} removal ({Reason}).",
                entity.Index,
                reason);
            return true;
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "[SwiftOnlineMusicPlayer] Failed to remove HUD entity ({Reason}).", reason);
            return false;
        }
    }

    private enum PlaybackState
    {
        Idle,
        Searching,
        Browsing,
        Loading,
        Playing,
        Paused,
        Error
    }

    private sealed class PlayerSession(int playerSlot)
    {
        public int PlayerSlot { get; } = playerSlot;
        public IAudioChannelController? Channel { get; set; }
        public int TrackIndex { get; set; }
        public int VolumeStep { get; set; }
        public int Generation { get; set; }
        public int SearchIndex { get; set; }
        public int SearchPage { get; set; }
        public bool SearchMenuOpen { get; set; }
        public string LastSearchQuery { get; set; } = string.Empty;
        public PlaybackState State { get; set; }
        public string Error { get; set; } = string.Empty;
        public List<MusicTrackConfig> SearchResults { get; } = [];
        public HashSet<string> FavoriteTrackKeys { get; } = new(StringComparer.Ordinal);
        public MusicTrackConfig? PendingTrack { get; set; }
        public MusicTrackConfig? ActiveTrack { get; set; }
        public DateTimeOffset NextSearchAllowedAt { get; set; }
        public TimeSpan ElapsedBeforeResume { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public string? LastProgressClass { get; set; }
        public string? LastVolumeClass { get; set; }
        public string? LastStateClass { get; set; }
        public string? LastSearchItemsClass { get; set; }
        public string? LastSearchSelectionClass { get; set; }

        public void ResetRenderedClasses()
        {
            LastProgressClass = null;
            LastVolumeClass = null;
            LastStateClass = null;
            LastSearchItemsClass = null;
            LastSearchSelectionClass = null;
        }
    }
}
