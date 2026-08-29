# SwiftOnlineMusicPlayerSW2

An independent SwiftlyS2 project that opens a clickable online music player inside CS2. The Uiverse-inspired card is implemented as a validated `CCSCustomHudLayout`; online audio is decoded by [SwiftlyS2-Plugins/Audio](https://github.com/SwiftlyS2-Plugins/Audio) and delivered per player through CS2 VoIP.

See [README_CN.md](README_CN.md) for the complete architecture, configuration, build, deployment, and validation guide.

Quick commands:

- `!music` — open the player.
- `!music_search <song or artist>` — search Netease through the configured MusicSquare-compatible endpoint and play result 1.
- `!music_pick <1-N>` — play a result from the player's latest search.
- `!music_library` — return to the administrator-configured static library.
- `!music_close` — close the UI while playback continues.
- `!music_stop` — stop/reset the player's private channel.
- `!music_status` — show dependency and session status.

Important constraints:

- Install the SwiftlyS2 Audio plugin separately; this project references API package `2.0.0` only.
- Track URLs must be direct HTTP/HTTPS media or stream URLs recognized by the decoder.
- Online search is server-side and optional. The default adapter independently implements the Netease/qijieya request shape currently used by MusicSquare; it does not embed or scrape the MusicSquare site.
- Third-party catalog APIs have no uptime guarantee. The Apache-2.0 license for MusicSquare source does not grant music, platform, or public-performance rights.
- Both client and server must mount the generated HUD VPK.
- The native Custom HUD bridge is locked to the documented Windows `server.dll` snapshot and must be reverified after CS2 updates.
- Use only audio you are licensed or otherwise authorized to stream.
