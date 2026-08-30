# SwiftOnlineMusicPlayerSW2 Development Guide

English | [简体中文](DEVELOPMENT_CN.md) | [Back to README](../README.md)

This guide is intended for plugin developers and server operators. It documents project boundaries, build artifacts, runtime configuration, resource deployment, native GameData maintenance, and validation.

## 1. Project scope

SwiftOnlineMusicPlayerSW2 consists of a server plugin and client-facing HUD resources:

| Artifact | Default location | Purpose |
| --- | --- | --- |
| Plugin publish directory | `build/publish/SwiftOnlineMusicPlayerSW2/` | SwiftlyS2 plugin and GameData |
| Plugin archive | `SwiftOnlineMusicPlayerSW2.zip` | Distribution archive produced after `dotnet publish` |
| HUD VPK | `dist/swift_online_music_player.vpk` | Panorama layout, styles, and icons |
| Workshop addon | [Online Music Player (3792571203)](https://steamcommunity.com/sharedfiles/filedetails/?id=3792571203) | Production client HUD resources |
| Installation report | `build/install-backups/<timestamp>/install-report.json` | Backup and target record for local test installs |

The server plugin, Audio plugin, and Workshop addon are separate artifacts. Deploying the server plugin does not install Audio or send Panorama resources to clients. Production uses SwiftlyS2 AddonsManager to distribute and mount the Workshop addon; local development uses the generated override VPK.

## 2. Runtime architecture

### 2.1 Playback path

1. A player issues a command or clicks a HUD control.
2. The plugin validates the track URL and asks SwiftlyS2 Audio to decode the source.
3. Every player owns an independent Audio channel controller.
4. Audio sends the decoded stream through CS2 VoIP.
5. The plugin updates HUD time, progress, state, and lyrics from the playback cursor.

Decoded data for the same URL may be cached server-wide, while playback controllers and session state remain isolated per player.

### 2.2 HUD path

1. The plugin creates a `custom_hud_layout` entity.
2. The entity loads `panorama/layout/custom_game/online_music_player_custom_hud.xml`.
3. The native bridge writes per-player dialog variables and CSS classes.
4. Static Button IDs return through `CustomHudClickedReceiver`.
5. The plugin maps click IDs to playback, track, volume, search-result, and input-capture actions.

The HUD contains no VJS, HTML, or Panorama Audio panel and stays within the restricted `CCSCustomHudLayout` resource model.

### 2.3 Input and session lifecycle

- `!music` displays the player without immediately capturing the mouse.
- Pressing and releasing right-click enables input capture for that player.
- Clicking the footer return-to-aim action releases capture while keeping the player visible.
- Clicking `X` closes the player and releases capture; playback continues.
- `!music_stop` stops and resets only the issuing player's channel.
- Disconnect, session replacement, and plugin unload stop the relevant audio and clear HUD, lyrics, and capture state.

## 3. Requirements

### 3.1 Build environment

- Windows PowerShell 7 or Windows PowerShell 5.1
- .NET 10 SDK
- Network access for NuGet restore

### 3.2 Runtime environment

- Windows CS2 Dedicated Server
- SwiftlyS2 `1.4.6-beta.8` or a compatible version
- SwiftlyS2 Audio; this project references Audio API `2.0.0`
- [SwiftlyS2 AddonsManager](https://github.com/SwiftlyS2-Plugins/AddonsManager) configured with Workshop `3792571203` in production
- The same HUD VPK mounted on server and client for local testing

For additional media formats or stream protocols, install FFmpeg on the server and enable `UseFFMpeg` according to the Audio plugin documentation.

### 3.3 HUD toolchain

- CS2 `resourcecompiler.exe`
- [VPKEdit](https://github.com/craftablescience/VPKEdit) CLI

## 4. Repository layout

```text
SwiftOnlineMusicPlayerSW2/
├─ src/                         C# plugin, search, and lyrics code
├─ hud/
│  ├─ layout/                  Panorama VXML
│  ├─ styles/                  Panorama VCSS
│  └─ icons/                   128×128 RGBA icon sources
├─ resources/gamedata/         Windows native-bridge signatures
├─ tools/                       HUD build, signature validation, diagnostics
├─ docs/                        Development and integration documents
├─ build_and_deploy.ps1         Server-plugin-only deployment
├─ install_local_test.ps1       Complete local client/server test install
├─ TESTING_CN.md                In-game test checklist in Chinese
└─ SwiftOnlineMusicPlayerSW2.csproj
```

## 5. Build the server plugin

Run from the repository root:

```powershell
dotnet restore .\SwiftOnlineMusicPlayerSW2.csproj
dotnet publish .\SwiftOnlineMusicPlayerSW2.csproj -c Release
```

`dotnet publish` produces:

- `build/publish/SwiftOnlineMusicPlayerSW2/`
- `SwiftOnlineMusicPlayerSW2.zip`

To deploy only the plugin to an existing server:

```powershell
.\build_and_deploy.ps1 -ServerRoot "F:\csgoserver_win\cs2"
```

Target:

```text
<ServerRoot>\game\csgo\addons\swiftlys2\plugins\SwiftOnlineMusicPlayerSW2\
```

This script intentionally does not install Audio, copy the HUD VPK, or edit `gameinfo.gi`.

## 6. Build the HUD VPK

`tools/build_hud_resources.ps1` supports four actions:

| Action | Behavior |
| --- | --- |
| `Validate` | Validate XML, CSS, icons, and references without invoking CS2 tools |
| `Compile` | Compile resources with ResourceCompiler |
| `Pack` | Package existing compiled resources into a VPK |
| `Build` | Validate, compile, and package |

Source validation:

```powershell
.\tools\build_hud_resources.ps1 -Action Validate
```

Full build:

```powershell
.\tools\build_hud_resources.ps1 -Action Build `
  -Cs2Root "F:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive" `
  -VpkEditCli "F:\path\to\vpkeditcli.exe"
```

The default addon name is `swift_online_music_player`. Output:

```text
dist/swift_online_music_player.vpk
```

The script creates VTEX descriptors for `hud/icons/*.png` before invoking ResourceCompiler. Do not put uncompiled VXML/VCSS directly into the runtime VPK.

## 7. Runtime configuration

Configuration path:

```text
<ServerRoot>\game\csgo\addons\swiftlys2\configs\plugins\SwiftOnlineMusicPlayerSW2\config.jsonc
```

Default model:

```jsonc
{
  "MusicPlayer": {
    "DefaultVolume": 0.65,
    "AutoAdvance": true,
    "AutoPlayFirstSearchResult": true,
    "Lyrics": {
      "Enabled": true,
      "VisibleByDefault": true,
      "KuwoEndpoint": "https://www.kuwo.cn/openapi/v1/www/lyric/getlyric",
      "NeteaseEndpoint": "https://api.qijieya.cn/meting/",
      "TimeoutSeconds": 8,
      "TimingOffsetSeconds": 0.0
    },
    "MusicSquareSearch": {
      "Enabled": true,
      "KuwoEnabled": true,
      "KuwoSearchEndpoint": "https://oiapi.net/api/Kuwo",
      "KuwoQualityIndex": 6,
      "SearchEndpoint": "https://api.qijieya.cn/meting/",
      "ResultLimit": 5,
      "TimeoutSeconds": 10,
      "CooldownSeconds": 5,
      "AllowedAudioHostSuffixes": [
        "api.qijieya.cn",
        "kuwo.cn",
        "music.126.net",
        "music.163.com"
      ]
    },
    "Tracks": [
      {
        "Title": "SoundHelix Song 1",
        "Artist": "SoundHelix",
        "Url": "https://www.soundhelix.com/examples/mp3/SoundHelix-Song-1.mp3",
        "DurationSeconds": 373
      }
    ]
  }
}
```

### 7.1 Playback options

| Field | Description |
| --- | --- |
| `DefaultVolume` | New-session volume, normalized to `0.0–1.0` |
| `AutoAdvance` | Advance after a track with a known duration finishes |
| `AutoPlayFirstSearchResult` | Load the first result automatically while retaining the drawer |
| `Tracks` | Administrator-defined static library, limited to 64 tracks |
| `DurationSeconds` | HUD timing and auto-advance duration; use `0` for unknown/live streams |

A static `Url` must be a direct HTTP/HTTPS media or stream URL understood by the decoder. A catalog web page is not playable media.

### 7.2 Search options

| Field | Description |
| --- | --- |
| `Enabled` | Enable online search |
| `KuwoEnabled` | Enable the primary Kuwo provider |
| `KuwoQualityIndex` | Requested Kuwo quality index |
| `ResultLimit` | Result count, normalized to `1–10` |
| `TimeoutSeconds` | Request timeout, normalized to `3–30` seconds |
| `CooldownSeconds` | Per-player cooldown, normalized to `0–60` seconds |
| `AllowedAudioHostSuffixes` | Allowed suffixes for resolved audio hosts |

Keep the production allowlist as small as practical. An empty list accepts any HTTP(S) host that passes public-address validation.

### 7.3 Lyrics options

| Field | Description |
| --- | --- |
| `Enabled` | Enable lyrics server-wide |
| `VisibleByDefault` | Default visibility for new player sessions |
| `TimeoutSeconds` | Lyrics timeout, normalized to `3–30` seconds |
| `TimingOffsetSeconds` | Positive values advance lyrics and negative values delay them; normalized to `-5–5` seconds |

Tracks with a supported `Source` and `SourceId` request lyrics. Plain static URLs have no source identity and do not trigger an automatic request. Lyrics failure never blocks playback.

### 7.4 Network protections

Search and lyrics requests:

- Accept HTTP/HTTPS only.
- Disable HTTP redirects.
- Reject loopback, link-local, private, and other non-public DNS results.
- Limit responses to 512 KiB.
- Apply independent timeouts.
- Validate final search-result audio URLs against the configured host-suffix allowlist.

These controls reduce misconfiguration and SSRF exposure but do not replace server-level egress controls.

## 8. Deployment and resource mounting

### 8.1 Complete local test install

Required inputs:

- Published plugin directory
- `dist/swift_online_music_player.vpk`
- `build/dependencies/Audio-v1.0.6.zip`
- A `server.dll` matching the supported GameData snapshot
- Stopped CS2 client and server processes

Run:

```powershell
.\install_local_test.ps1 `
  -ServerRoot "F:\csgoserver_win\cs2" `
  -ClientRoot "F:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive"
```

The script:

1. Validates roots, artifacts, Audio archive, and the `server.dll` hash.
2. Backs up the plugin, Audio, server/client VPKs, and `gameinfo.gi` files.
3. Installs Audio and this plugin.
4. Copies the VPK to `game/csgo/overrides/` on server and client.
5. Adds this SearchPath to both `gameinfo.gi` files:

   ```text
   Game csgo/overrides/swift_online_music_player.vpk
   ```

6. Writes `install-report.json` with backup and target details.

See [TESTING_CN.md](../TESTING_CN.md) for the full in-game sequence.

### 8.2 Production: Workshop + AddonsManager

The HUD resource is published on the Steam Workshop:

- Name: [Online Music Player](https://steamcommunity.com/sharedfiles/filedetails/?id=3792571203)
- Workshop ID: `3792571203`

Production servers use the upstream [SwiftlyS2 AddonsManager](https://github.com/SwiftlyS2-Plugins/AddonsManager) to download and mount the resource and make the Workshop addon available to connecting clients. AddonsManager does not install SwiftOnlineMusicPlayerSW2 or SwiftlyS2 Audio; deploy both server plugins separately.

1. Install AddonsManager according to its upstream README and start the server once.
2. Open:

   ```text
   <ServerRoot>\game\csgo\addons\swiftlys2\configs\plugins\AddonsManager\config.jsonc
   ```

3. Add this Workshop ID to `Main.Addons` while preserving existing IDs:

   ```jsonc
   {
     "Main": {
       "Addons": [
         "3792571203"
       ]
     }
   }
   ```

4. Restart the server or reload AddonsManager using the procedure supported by the installed version.
5. The server console can request a download and inspect mounted search paths:

   ```text
   sw_downloadaddon 3792571203
   sw_searchpath
   ```

`sw_downloadaddon` and `sw_searchpath` are part of the upstream AddonsManager command interface. Before using `!music`, confirm the download completed and `sw_searchpath` lists the relevant Workshop/VPK resource path.

Do not treat the local `gameinfo.gi` override in section 8.1 as production distribution. Production uses Workshop `3792571203` and AddonsManager.

### 8.3 Production verification

1. Confirm SwiftOnlineMusicPlayerSW2, Audio, and AddonsManager all load successfully.
2. Confirm AddonsManager downloaded Workshop `3792571203` and `sw_searchpath` exposes its resource path.
3. Connect with a client that has no local override VPK and confirm it obtains the addon.
4. Run `!music` and validate the player, result drawer, mouse interaction, and synchronized lyrics.
5. After a Workshop update, repeat download, mount, and clean-client verification to prevent plugin/HUD version skew.

## 9. GameData maintenance

Signature file:

```text
resources/gamedata/signatures.jsonc
```

Current recorded Windows snapshot:

```text
server.dll SHA-256:
9e5749d77dcb68883477feae751a3f28068d119ec145edcb0e4d48d15b538d36

last unique-pattern validation:
2026-08-29
```

| Symbol | Validated file offset |
| --- | --- |
| `SetDialogVariableStringForPlayer` | `0x8A3090` |
| `SetHasClassForPlayer` | `0x8A33C0` |
| `SetInputCaptureEnabled` | `0x8A3450` |
| `CustomHudClickedReceiver` | `0x259420` |

Validate a local binary:

```powershell
python .\tools\validate_gamedata_signatures.py `
  "F:\path\to\server.dll" `
  ".\resources\gamedata\signatures.jsonc"
```

After every CS2 update:

1. Confirm that client and server `server.dll` files are from the expected build.
2. Compare SHA-256 with the supported snapshot.
3. Require each pattern to match exactly once.
4. Recheck calling convention, parameters, and return types.
5. Validate dialog variables, CSS classes, input capture, and click callbacks in game.

A unique byte-pattern match proves location only; it does not prove an unchanged ABI. Linux patterns are currently empty and Linux is not a supported target.

## 10. Search and lyrics internals

### 10.1 Search

Default flow:

1. Query the Kuwo OIAPI.
2. Validate candidate metadata, playable URL, and host.
3. Fall back to the Netease-compatible Meting endpoint if no playable Kuwo result remains.
4. Send results to both chat and the HUD drawer.
5. Apply `AutoPlayFirstSearchResult`.

The implementation adopts MusicSquare's metadata-to-direct-media adapter concept. It does not embed or scrape the MusicSquare page and does not copy third-party tokens. See [MusicSquare integration analysis](MUSICSQUARE_ANALYSIS.md).

### 10.2 Lyrics

- Kuwo tracks use JSON timeline lyrics.
- Netease tracks use LRC.
- Parsed lines are normalized by timestamp and cached by provider and track ID.
- Each playback tick selects the current and next lines from the player's real cursor.
- Pause freezes lyrics; resume keeps synchronization.
- Closing the player card leaves active lyrics visible; stop, track change, disconnect, and unload clear them.

HudText is an implementation reference only. This project owns its labels, dialog variables, classes, layout, and VPK and does not depend on a HudText DLL or addon. See [HudText lyrics analysis](HUDTEXT_LYRICS_ANALYSIS.md).

## 11. Validation

Minimum checks before committing:

```powershell
dotnet build .\SwiftOnlineMusicPlayerSW2.csproj -c Release --no-restore
.\tools\build_hud_resources.ps1 -Action Validate
git diff --check
```

For GameData, resource, or installer changes, also run:

- `dotnet publish`
- `build_hud_resources.ps1 -Action Build`
- `validate_gamedata_signatures.py`
- The single-player, two-player, reconnect, and input-capture cases in [TESTING_CN.md](../TESTING_CN.md)

Critical regressions:

- Disconnect clears audio, lyrics, and capture state.
- Two players do not share playback control, volume, or search results.
- HUD open preserves aiming; right-click enters interaction; the footer restores aiming.
- Closing the HUD keeps playback; `!music_stop` stops it completely.
- Search, lyrics, and Audio dependency failures report real error state.
- Player and lyrics remain usable at 16:9 and 4:3.

## 12. Troubleshooting

| Symptom | Check |
| --- | --- |
| `!music` logs success but no HUD appears | Client VPK mount, compiled `custom_game` path, and HUD entity creation |
| HUD appears but controls do nothing | GameData version, unique click-receiver match, and input-capture state |
| Cursor cannot return to aiming | Click the footer return-to-aim action and verify plugin/VPK versions match |
| Loading state but no sound | Audio plugin, direct media URL, voice settings, FFmpeg, and server egress |
| Search always fails | Provider availability, timeout, public DNS validation, and audio-host allowlist |
| Playback works but lyrics do not | `Lyrics.Enabled`, player `!music_lyrics` state, `Source/SourceId`, and lyrics endpoint |
| Crash or HUD failure after a CS2 update | Disable stale GameData and revalidate hash, patterns, and ABI |
| Client/server visuals differ | Mount the same VPK version on both sides, remove stale overrides, and restart |

Use `tools/collect_test_diagnostics.ps1` to collect diagnostics; parameters and outputs are documented in [TESTING_CN.md](../TESTING_CN.md).

## 13. Third-party services and content

- Stream only audio you are authorized to transmit on the target server.
- Third-party API availability, response formats, and terms may change.
- MusicSquare's source license does not grant rights to music, platform catalogs, or public performance.
- This project does not distribute JOOX tokens, paid-content bypasses, or third-party media.
- Install and distribute the Audio plugin under its own license.

## 14. Author and license

**Author: niceday_zhu**

Copyright © 2026 niceday_zhu. This project is released under the [MIT License](../LICENSE). The MIT License covers this project's own code and documentation only; third-party dependencies, services, assets, and music content remain subject to their own licenses and terms.
