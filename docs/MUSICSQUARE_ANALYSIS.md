# MusicSquare integration analysis

Verified against the upstream `main` branch and live demo on 2026-08-29.

## Decision

Use MusicSquare as a reference for provider adapters, not as an embedded player or a production backend. The CS2 client cannot run arbitrary HTML/VJS inside the validated `CCSCustomHudLayout`, while SwiftlyS2 Audio needs a direct media URL on the server. The useful boundary is therefore:

```text
player chat command
  -> SwiftlyS2 server search adapter
  -> direct public HTTP(S) audio URL
  -> SwiftlyS2 Audio DecodeFromUrlAsync
  -> private per-player VoIP channel
```

The implementation currently supports the least complex upstream path only: Netease search through the qijieya Meting-compatible endpoint. Search results are stored per player, the first result starts automatically, and the existing HUD controls move through the stored result set.

## What upstream currently does

MusicSquare is a static HTML application licensed under Apache-2.0. Its browser code calls unrelated third-party APIs directly:

| Source | Search | Playback URL resolution | Decision |
| --- | --- | --- | --- |
| Netease | `api.qijieya.cn/meting/?type=search...&server=netease` | Search records already include `url`; fallback uses `type=url&id=...` | Implemented |
| QQ | `tang.api.s01s.cn/music_open_api.php` | Second call with `mid`, then chooses one of several quality URLs | Deferred |
| Kuwo | `oiapi.net/api/Kuwo` | Second call tied to result number `n` | Deferred |
| JOOX | `apicx.asia/api/joox_music` | Second call plus probing several quality links | Rejected for now because upstream embeds a third-party token |

There is no single MusicSquare API and no server-side stability contract. Copying the website would not remove these dependencies.

## Implementation boundaries

- `MusicSquareSearchProvider` is an independent C# implementation of the observable request/response shape. No upstream JavaScript, CSS, artwork, or credentials are copied.
- The endpoint is administrator-configurable and can be disabled without affecting the static playlist.
- Only HTTP(S) URLs are accepted. The search endpoint must resolve exclusively to public IP addresses.
- Search responses are capped at 512 KiB and JSON depth 16. Redirects are disabled for the metadata request.
- Audio URLs are restricted to configured host suffixes. The defaults cover qijieya and common NetEase music hosts.
- Each player has a cooldown, generation token, private result list, and private Audio channel. Late responses cannot replace a newer search or a stopped session.
- Search results have unknown duration and display as `LIVE`; the Audio API currently supplies playback but this project does not derive remote duration metadata.

## Operational and legal risks

- Any third-party endpoint can change fields, enforce rate limits, disappear, add bot protection, or return a URL that the server decoder cannot read.
- CDN URLs may be short-lived, geo-restricted, require headers/cookies, or redirect. Successful search does not guarantee successful decode.
- Allowing arbitrary host suffixes increases server-side request forgery exposure because the Audio plugin performs the final URL request and may follow redirects outside this adapter's control.
- Apache-2.0 covers MusicSquare source code only. It does not license music recordings, compositions, platform catalog access, retransmission, or public performance.
- The live demo states that it is for learning/demo purposes and that music copyrights belong to the platforms and original authors. A public CS2 server operator must independently confirm the rights and platform terms for every source used.

## Production recommendation

Keep the adapter disabled if the server operator cannot accept those dependencies and rights obligations. For a durable production deployment, point `SearchEndpoint` at an administrator-controlled HTTPS service that returns the same small JSON array (`name`, `artist`, `url`) and only exposes licensed audio from an allowlisted CDN. That keeps the CS2/SwiftlyS2 plugin unchanged while moving catalog authentication, rate limiting, auditing, expiring URLs, and rights enforcement to a service designed for them.

## Upstream references

- <https://github.com/CharlesPikachu/musicsquare>
- <https://github.com/CharlesPikachu/musicsquare/blob/main/index.html>
- <https://github.com/CharlesPikachu/musicsquare/blob/main/LICENSE>
- <https://charlespikachu.github.io/musicsquare/>
