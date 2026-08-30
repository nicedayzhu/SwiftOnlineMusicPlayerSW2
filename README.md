# SwiftOnlineMusicPlayerSW2

An independent SwiftlyS2 project that opens a clickable online music player inside CS2. The Uiverse-inspired card is implemented as a validated `CCSCustomHudLayout`; online audio is decoded by [SwiftlyS2-Plugins/Audio](https://github.com/SwiftlyS2-Plugins/Audio) and delivered per player through CS2 VoIP.

See [README_CN.md](README_CN.md) for the complete architecture, configuration, build, deployment, and validation guide.

Quick commands:

- `!music` — open the player.
- `!music_search <song or artist>` — search Kuwo with Netease fallback and open the clickable in-HUD result drawer.
- `!music_pick <1-N>` — play a result from the player's latest search.
- `!music_library` — return to the administrator-configured static library.
- `!music_close` — close the UI while playback continues.
- `!music_stop` — stop/reset the player's private channel.
- `!music_lyrics [on|off]` — toggle synchronized lyrics for the player; omitting the argument toggles the current setting.
- `!music_status` — show dependency and session status.
- The heart button toggles a per-player favorite marker for the current connection; it is intentionally not persisted as a server library edit.
- Search results are echoed to chat and the first result plays automatically by default. Set `MusicPlayer.AutoPlayFirstSearchResult` to `false` to require an explicit HUD/command selection.
- Kuwo and Netease search tracks load synchronized lyrics into a non-interactive two-line overlay. Lyrics follow pause/resume, continue when the player card is closed, and clear on stop, track change, disconnect, or unload.
- The lyrics layer adopts HudText's fixed-label plus per-player dialog-variable/class mechanism, but it is compiled into this project's existing VPK. No HudText plugin or additional addon is required.
- Opening the player keeps aiming active. Press and release right-click once to enter pointer interaction mode; click the footer action to return to aiming while the player remains visible, or use X to close it.

Important constraints:

- Install the SwiftlyS2 Audio plugin separately; this project references API package `2.0.0` only.
- Track URLs must be direct HTTP/HTTPS media or stream URLs recognized by the decoder.
- Online search is server-side and optional. The default adapter queries Kuwo first and independently implements the Netease/qijieya fallback request shape currently used by MusicSquare; it does not embed or scrape the MusicSquare site.
- The result drawer exposes five clickable rows per page and supports the configured maximum of ten results without using Panorama JavaScript.
- HUD icon sources are checked-in 128×128 RGBA PNG files; the build writes VTEX descriptors and compiles them to the `.vtex_c` resources used by Panorama.
- Third-party catalog APIs have no uptime guarantee. The Apache-2.0 license for MusicSquare source does not grant music, platform, or public-performance rights.
- Both client and server must mount the generated HUD VPK.
- The native Custom HUD bridge is locked to the documented Windows `server.dll` snapshot and must be reverified after CS2 updates.
- Use only audio you are licensed or otherwise authorized to stream.
