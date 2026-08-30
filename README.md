# SwiftOnlineMusicPlayerSW2

English | [简体中文](README_CN.md)

SwiftOnlineMusicPlayerSW2 is a per-player online music player for Counter-Strike 2 and SwiftlyS2. It combines online audio playback, server-side music search, synchronized lyrics, and an interactive `CCSCustomHudLayout` interface in one project.

![SwiftOnlineMusicPlayerSW2 in game](docs/assets/swift-online-music-player-preview.png)

## Highlights

- Independent playback channel, progress, volume, search results, and lyrics state for every player.
- Play, pause, resume, stop, previous, next, and six volume levels from 0% to 100%.
- Server-side search with Kuwo as the primary provider and a Netease-compatible fallback.
- Clickable in-HUD result drawer with five rows per page and up to ten results.
- Synchronized Kuwo JSON and Netease LRC lyrics.
- Aiming remains active when the player opens; pressing and releasing right-click enters pointer interaction mode.
- Closing the player card does not stop playback. Stop, track change, disconnect, and plugin unload clean up their associated state.
- Hot-reloadable configuration with server-side URL, DNS, response-size, timeout, and audio-host validation.
- Lyrics are integrated into this project's existing HUD and VPK. HudText is not required as a plugin or additional addon.

## Architecture

| Component | Responsibility | Runtime |
| --- | --- | --- |
| SwiftlyS2 plugin | Sessions, commands, search, lyrics, audio channels, and HUD state synchronization | CS2 server |
| SwiftlyS2 Audio | Decode online media and deliver it to selected players through CS2 VoIP | CS2 server |
| `swift_online_music_player.vpk` | Compiled Panorama layout, styles, and icons | Server and clients |
| Custom HUD native bridge | Write per-player dialog variables/classes and receive HUD click events | Windows `server.dll` |

`CCSCustomHudLayout` does not allow arbitrary VJS, HTML, or Audio panels. Online media is therefore decoded on the server by the Audio plugin while the HUD remains a presentation and interaction layer.

## Quick start

### Prerequisites

- .NET 10 SDK
- SwiftlyS2 `1.4.6-beta.8` or a compatible version
- A separately installed [SwiftlyS2 Audio](https://github.com/SwiftlyS2-Plugins/Audio) plugin
- Windows CS2 server
- CS2 ResourceCompiler and VPKEdit CLI when building the HUD VPK

### Build the server plugin

```powershell
dotnet restore .\SwiftOnlineMusicPlayerSW2.csproj
dotnet publish .\SwiftOnlineMusicPlayerSW2.csproj -c Release
```

Published files are written to:

```text
build/publish/SwiftOnlineMusicPlayerSW2/
```

### Build the HUD resources

Validate the sources without writing to a CS2 installation:

```powershell
.\tools\build_hud_resources.ps1 -Action Validate
```

Compile and package the VPK:

```powershell
.\tools\build_hud_resources.ps1 -Action Build `
  -Cs2Root "F:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive" `
  -VpkEditCli "F:\path\to\vpkeditcli.exe"
```

Output:

```text
dist/swift_online_music_player.vpk
```

### Install a complete local test environment

```powershell
.\install_local_test.ps1 `
  -ServerRoot "F:\csgoserver_win\cs2" `
  -ClientRoot "F:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive"
```

The script verifies `server.dll`, backs up existing targets, installs Audio and this plugin, copies the client/server VPK, and adds a dedicated SearchPath to both `gameinfo.gi` files. Stop the CS2 client and server before running it.

For server-plugin-only deployment:

```powershell
.\build_and_deploy.ps1 -ServerRoot "F:\csgoserver_win\cs2"
```

This script does not install Audio, copy the VPK, or modify `gameinfo.gi`.

See the [development guide](docs/DEVELOPMENT.md) for configuration, deployment contracts, GameData maintenance, and troubleshooting.

## Player commands

| Command | Description |
| --- | --- |
| `!music` | Open or reopen the player |
| `!music_close` | Close the UI while playback continues |
| `!music_stop` | Stop and reset the player's music channel |
| `!music_lyrics [on\|off]` | Show, hide, or toggle synchronized lyrics |
| `!music_status` | Show dependency, HUD, library, and session status |
| `!music_search <song or artist>` | Search online and expand the HUD result drawer |
| `!music_pick <1-N>` | Play a result from the player's latest search |
| `!music_library` | Leave search results and return to the static library |

## Interaction model

1. `!music` opens the player without immediately capturing the mouse, so aiming still works.
2. Press and release right-click to enter pointer interaction mode.
3. Click the footer return-to-aim text to release the cursor while keeping the player visible.
4. Click the `X` in the top-right corner to close the player; current playback continues.

## Documentation

- [Development guide](docs/DEVELOPMENT.md): architecture, build, configuration, deployment, GameData, and troubleshooting
- [Chinese testing guide](TESTING_CN.md): local installation, in-game validation matrix, and diagnostics
- [MusicSquare integration analysis](docs/MUSICSQUARE_ANALYSIS.md): provider scope, risks, and extension guidance
- [HudText lyrics analysis](docs/HUDTEXT_LYRICS_ANALYSIS.md): synchronized-lyrics data flow and lifecycle
- [中文 README](README_CN.md)
- [中文开发者文档](docs/DEVELOPMENT_CN.md)

## Status and limitations

- The Custom HUD native bridge currently maintains Windows signatures only; Linux GameData is not provided.
- Both the client and server must mount the generated HUD VPK.
- Search and lyrics depend on third-party services whose availability is outside this project's control.
- Web pages are not direct audio streams. YouTube, Spotify, or music-catalog page URLs cannot be used as static track URLs.
- Favorites are connection-scoped UI state and are not persisted to the server library.
- No public Workshop item is provided by this repository. Production resource distribution is deployment-specific.
- Every CS2 update requires revalidation of the `server.dll` hash, unique GameData matches, and native function ABI.

## Acknowledgements

- [SwiftlyS2](https://github.com/swiftly-solution/swiftlys2)
- [SwiftlyS2 Audio](https://github.com/SwiftlyS2-Plugins/Audio)
- [HudText](https://github.com/T3Marius/HudText)
- [MusicSquare](https://github.com/CharlesPikachu/musicsquare)
- [Uiverse music player reference](https://uiverse.io/bociKond/serious-robin-34)

This project adopts ideas from HudText's Custom HUD update mechanism and MusicSquare's search-adapter flow. It does not bundle their plugins, addons, credentials, or third-party music content. Each dependency, service, catalog, and asset remains subject to its own license and terms.

## License

This project is released under the [MIT License](LICENSE). Music rights, public-performance rights, and licenses for third-party services, dependencies, and assets are outside its scope and remain subject to their own terms.
