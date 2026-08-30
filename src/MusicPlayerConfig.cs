namespace SwiftOnlineMusicPlayerSW2;

public sealed class MusicPlayerConfig
{
    public float DefaultVolume { get; set; } = 0.65f;
    public bool AutoAdvance { get; set; } = true;
    public bool AutoPlayFirstSearchResult { get; set; } = true;
    public MusicSquareSearchConfig MusicSquareSearch { get; set; } = new();
    public LyricsConfig Lyrics { get; set; } = new();
    public List<MusicTrackConfig> Tracks { get; set; } =
    [
        new()
        {
            Title = "SoundHelix Song 1",
            Artist = "SoundHelix",
            Url = "https://www.soundhelix.com/examples/mp3/SoundHelix-Song-1.mp3",
            DurationSeconds = 373,
            Source = "Server library"
        },
        new()
        {
            Title = "SoundHelix Song 2",
            Artist = "SoundHelix",
            Url = "https://www.soundhelix.com/examples/mp3/SoundHelix-Song-2.mp3",
            DurationSeconds = 426,
            Source = "Server library"
        }
    ];

    internal static MusicPlayerConfig Normalize(MusicPlayerConfig? source)
    {
        source ??= new MusicPlayerConfig();
        var tracks = new List<MusicTrackConfig>();

        foreach (var candidate in source.Tracks ?? [])
        {
            var urlText = (candidate.Url ?? string.Empty).Trim();
            if (urlText.Length == 0 || urlText.Length > 2048 ||
                !Uri.TryCreate(urlText, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                continue;
            }

            tracks.Add(new MusicTrackConfig
            {
                Title = NormalizeText(candidate.Title, "Untitled", 80),
                Artist = NormalizeText(candidate.Artist, "Unknown artist", 80),
                Url = uri.AbsoluteUri,
                DurationSeconds = Math.Clamp(candidate.DurationSeconds, 0, 86_400),
                Source = NormalizeText(candidate.Source, "Server library", 32),
                SourceId = NormalizeText(candidate.SourceId, string.Empty, 96)
            });

            if (tracks.Count >= 64)
            {
                break;
            }
        }

        return new MusicPlayerConfig
        {
            DefaultVolume = Math.Clamp(source.DefaultVolume, 0f, 1f),
            AutoAdvance = source.AutoAdvance,
            AutoPlayFirstSearchResult = source.AutoPlayFirstSearchResult,
            MusicSquareSearch = MusicSquareSearchConfig.Normalize(source.MusicSquareSearch),
            Lyrics = LyricsConfig.Normalize(source.Lyrics),
            Tracks = tracks
        };
    }

    private static string NormalizeText(string? value, string fallback, int maxLength)
    {
        var text = string.IsNullOrWhiteSpace(value)
            ? fallback
            : new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}

public sealed class LyricsConfig
{
    public bool Enabled { get; set; } = true;
    public bool VisibleByDefault { get; set; } = true;
    public string KuwoEndpoint { get; set; } = "https://www.kuwo.cn/openapi/v1/www/lyric/getlyric";
    public string NeteaseEndpoint { get; set; } = "https://api.qijieya.cn/meting/";
    public int TimeoutSeconds { get; set; } = 8;
    public float TimingOffsetSeconds { get; set; }

    internal static LyricsConfig Normalize(LyricsConfig? source)
    {
        source ??= new LyricsConfig();
        var kuwoEndpoint = NormalizeEndpoint(source.KuwoEndpoint);
        var neteaseEndpoint = NormalizeEndpoint(source.NeteaseEndpoint);
        return new LyricsConfig
        {
            Enabled = source.Enabled && (kuwoEndpoint is not null || neteaseEndpoint is not null),
            VisibleByDefault = source.VisibleByDefault,
            KuwoEndpoint = kuwoEndpoint?.AbsoluteUri ?? string.Empty,
            NeteaseEndpoint = neteaseEndpoint?.AbsoluteUri ?? string.Empty,
            TimeoutSeconds = Math.Clamp(source.TimeoutSeconds, 3, 30),
            TimingOffsetSeconds = Math.Clamp(source.TimingOffsetSeconds, -5f, 5f)
        };
    }

    private static Uri? NormalizeEndpoint(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length is > 0 and <= 2048 &&
               Uri.TryCreate(text, UriKind.Absolute, out var endpoint) &&
               (endpoint.Scheme == Uri.UriSchemeHttps || endpoint.Scheme == Uri.UriSchemeHttp)
            ? endpoint
            : null;
    }
}

public sealed class MusicSquareSearchConfig
{
    public bool Enabled { get; set; } = true;
    public bool KuwoEnabled { get; set; } = true;
    public string KuwoSearchEndpoint { get; set; } = "https://oiapi.net/api/Kuwo";
    public int KuwoQualityIndex { get; set; } = 6;
    public string SearchEndpoint { get; set; } = "https://api.qijieya.cn/meting/";
    public int ResultLimit { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 10;
    public int CooldownSeconds { get; set; } = 5;
    public List<string> AllowedAudioHostSuffixes { get; set; } =
    [
        "api.qijieya.cn",
        "kuwo.cn",
        "music.126.net",
        "music.163.com"
    ];

    internal static MusicSquareSearchConfig Normalize(MusicSquareSearchConfig? source)
    {
        source ??= new MusicSquareSearchConfig();
        var endpointText = (source.SearchEndpoint ?? string.Empty).Trim();
        Uri? endpoint = null;
        var endpointIsValid = endpointText.Length is > 0 and <= 2048 &&
                              Uri.TryCreate(endpointText, UriKind.Absolute, out endpoint) &&
                              (endpoint.Scheme == Uri.UriSchemeHttps || endpoint.Scheme == Uri.UriSchemeHttp);
        var kuwoEndpointText = (source.KuwoSearchEndpoint ?? string.Empty).Trim();
        Uri? kuwoEndpoint = null;
        var kuwoEndpointIsValid = kuwoEndpointText.Length is > 0 and <= 2048 &&
                                  Uri.TryCreate(kuwoEndpointText, UriKind.Absolute, out kuwoEndpoint) &&
                                  (kuwoEndpoint.Scheme == Uri.UriSchemeHttps || kuwoEndpoint.Scheme == Uri.UriSchemeHttp);
        var allowedHosts = (source.AllowedAudioHostSuffixes ?? [])
            .Select(host => (host ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant())
            .Where(host => host.Length is > 0 and <= 253 &&
                           host.All(character => char.IsLetterOrDigit(character) || character is '.' or '-'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToList();
        if (source.KuwoEnabled && kuwoEndpointIsValid &&
            !allowedHosts.Contains("kuwo.cn", StringComparer.OrdinalIgnoreCase))
        {
            // Config files from 0.2.x do not contain this host yet. Preserve the
            // user's allowlist while adding the minimum host needed by Kuwo URLs.
            allowedHosts.Add("kuwo.cn");
        }

        return new MusicSquareSearchConfig
        {
            Enabled = source.Enabled && (endpointIsValid || (source.KuwoEnabled && kuwoEndpointIsValid)),
            KuwoEnabled = source.KuwoEnabled && kuwoEndpointIsValid,
            KuwoSearchEndpoint = kuwoEndpointIsValid ? kuwoEndpoint!.AbsoluteUri : string.Empty,
            KuwoQualityIndex = Math.Clamp(source.KuwoQualityIndex, 1, 6),
            SearchEndpoint = endpointIsValid ? endpoint!.AbsoluteUri : string.Empty,
            ResultLimit = Math.Clamp(source.ResultLimit, 1, 10),
            TimeoutSeconds = Math.Clamp(source.TimeoutSeconds, 3, 30),
            CooldownSeconds = Math.Clamp(source.CooldownSeconds, 0, 60),
            AllowedAudioHostSuffixes = allowedHosts
        };
    }
}

public sealed class MusicTrackConfig
{
    public string Title { get; set; } = "Untitled";
    public string Artist { get; set; } = "Unknown artist";
    public string Url { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }
    public string Source { get; set; } = "Server library";
    public string SourceId { get; set; } = string.Empty;
}
