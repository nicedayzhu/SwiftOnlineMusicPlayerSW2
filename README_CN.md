# SwiftOnlineMusicPlayerSW2

这是一个独立的 SwiftlyS2 项目：玩家输入 `!music` 后，在 CS2 游戏内打开一张可点击的在线音乐播放器卡片。界面基于用户提供的 Uiverse 音乐播放器重新设计，并转换为 CS2 `CCSCustomHudLayout` 允许的 VXML/VCSS。

## 技术路线

项目把界面与音频明确分层：

1. 客户端 VPK 提供已编译的 VXML/VCSS 音乐播放器。
2. 服务器创建 `custom_hud_layout`，逐玩家写入曲名、歌手、时间、状态、进度和音量 CSS 类，并接收静态 Button ID。
3. [SwiftlyS2-Plugins/Audio](https://github.com/SwiftlyS2-Plugins/Audio) 从直接音频 URL 解码音源，通过 CS2 VoIP 音频通道逐玩家投递。
4. 每位玩家使用独立 Audio channel，因此暂停、切歌和音量不会影响其他玩家。

不能让这个 Custom HUD 自己播放 URL：当前 CS2 的 `CCSCustomHudLayout` 校验器禁止 VJS、HTML 和 Audio 面板。SwiftlyS2 原生 `SoundEvent` 也只播放客户端已有的声音事件，不接受任意在线音频。因此，Audio 插件的 URL 解码 + VoIP 是当前路线中功能最完整、对客户端侵入最小的办法。

## 已实现

- `!music` 打开播放器，`!music_close` 只关闭 UI，音乐继续播放。
- 在线 URL 加载、播放、暂停、继续、上一首、下一首。
- 服务器侧在线搜索：`!music_search <歌名/歌手>` 以酷我为主、网易云为回退，并在 HUD 内展开可点击的候选曲目菜单；每页 5 条，最多支持配置允许的 10 条结果。
- `!music_pick <序号>` 精确选择搜索结果，`!music_library` 返回管理员静态曲库；结果与冷却时间均逐玩家隔离。
- 逐玩家 0%–100% 六档音量。
- 爱心按钮可标记当前连接期间的逐玩家收藏状态；它不会伪装成已持久化的服务器曲库修改。
- 曲名、歌手、当前时间、总时长、加载/暂停/错误状态。
- 20 段进度显示；`DurationSeconds = 0` 时显示 LIVE 动画。
- 配置文件热重载；URL 只允许 HTTP/HTTPS，最多 64 首。
- 搜索请求带有 3–30 秒超时、0–60 秒冷却、512 KiB 响应上限、禁用 HTTP 重定向、公网 DNS 检查和音频主机后缀白名单。
- 相同 URL 的解码结果在服务器内缓存并由玩家共享，播放游标仍然逐玩家独立。
- 玩家断线、插件卸载、HUD 关闭时的音频与鼠标捕获清理。
- Audio 插件缺失、URL 解码失败、GameData 失效时均显示明确状态，不会假装播放成功。

## 依赖

- SwiftlyS2 `1.4.6-beta.8` 或兼容版本。
- SwiftlyS2 Audio 插件及其 API `2.0.0`。
- Windows CS2 服务器。当前 Custom HUD 原生桥签名仅提供 Windows 快照。
- 客户端和服务器都必须挂载本项目生成的 HUD VPK。
- 若希望 FFmpeg 处理更多格式或流协议，需要在服务器 PATH 中安装 FFmpeg，并在 Audio 插件的 `config.jsonc` 中启用 `UseFFMpeg`。

Audio 插件本体必须单独下载并作为 SwiftlyS2 插件安装；本项目只引用它的 API 包，不复制它的 GPL 源码或二进制。

## 音源配置

首次加载后，SwiftlyS2 会在本插件配置目录创建 `config.jsonc`。默认模型相当于：

```jsonc
{
  "MusicPlayer": {
    "DefaultVolume": 0.65,
    "AutoAdvance": true,
    "MusicSquareSearch": {
      "Enabled": true,
      "SearchEndpoint": "https://api.qijieya.cn/meting/",
      "ResultLimit": 5,
      "TimeoutSeconds": 10,
      "CooldownSeconds": 5,
      "AllowedAudioHostSuffixes": [
        "api.qijieya.cn",
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

要求：

- `Url` 必须是解码器可以直接读取的 HTTP/HTTPS 媒体地址。
- YouTube、Spotify、网易云等网页链接不是音频流地址，不能直接填入；如需这类来源，应在服务器外部合法解析/转码成受控的直链或自建流媒体端点。
- `DurationSeconds` 用于 HUD 计时和自动下一首。直播流或未知时长填 `0`。
- `MusicSquareSearch` 是可选的服务器侧搜索适配器。默认仅启用 MusicSquare 当前使用的网易云 qijieya Meting 路径；管理员可关闭或换成协议兼容的自建端点。
- `AllowedAudioHostSuffixes` 应保持尽量小。留空会接受任意公网 HTTP(S) 音频主机，不建议在生产服这样配置。
- 只使用你有权公开播放的音频，并遵守所在地区的版权、表演权和平台条款。

## MusicSquare 采用范围

本项目没有嵌入或抓取 MusicSquare 网页，也没有复制其播放器实现。采用的是它公开源码中可观察到的“搜索元数据 → 获取直接音频 URL → 交给播放器”适配思路，并独立实现了 C# 服务端客户端。

- 当前以酷我搜索为主：服务端对候选进行匹配、URL 公网校验和音频主机白名单校验；酷我无可播放结果时再回退到网易云 qijieya Meting 路径。
- 搜索结果只在聊天区发送数量摘要，完整曲名、歌手、来源与时长改由 HUD 曲目抽屉承载，减少战斗中的聊天刷屏。
- 暂不接入 QQ：需要第二次详情请求，付费/VIP 结果不保证可播放。
- 暂不接入 JOOX：MusicSquare 前端包含第三方 token，本项目不会复制或分发该凭据。
- MusicSquare 的 Apache-2.0 许可证只覆盖它的源码，不授予任何歌曲、平台目录或公开演播权。其在线演示也明确声明音乐版权归平台和原作者所有。

更完整的接口、风险和扩展分析见 [`docs/MUSICSQUARE_ANALYSIS.md`](docs/MUSICSQUARE_ANALYSIS.md)。

## 命令

- `!music`：打开/重新打开播放器。
- `!music_close`：关闭播放器 UI，不停止后台音乐。
- `!music_stop`：停止并重置自己的音乐频道。
- `!music_status`：显示 Audio、HUD、曲库和个人会话状态。
- `!music_search <歌名或歌手>`：搜索在线候选并展开 HUD 曲目菜单，点击整行即可播放。
- `!music_pick <1-N>`：播放最近一次搜索的第 N 条结果。
- `!music_library`：退出搜索结果集并返回静态曲库。

## 构建插件

```powershell
dotnet restore .\SwiftOnlineMusicPlayerSW2.csproj
dotnet publish .\SwiftOnlineMusicPlayerSW2.csproj -c Release
```

发布目录：

```text
build/publish/SwiftOnlineMusicPlayerSW2/
```

也可以部署到服务器：

```powershell
.\build_and_deploy.ps1 -ServerRoot "F:\csgoserver_win\cs2"
```

此脚本只部署服务器插件，不会安装 Audio 依赖、挂载 VPK 或修改 `gameinfo.gi`。

本机完整测试安装可使用：

```powershell
.\install_local_test.ps1 `
  -ServerRoot "F:\csgoserver_win\cs2" `
  -ClientRoot "F:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive"
```

该脚本会校验当前 `server.dll`、备份已有目标、安装官方 Audio 发布包与本插件、复制服务器/客户端 VPK，并在两侧 `gameinfo.gi` 中追加独立 SearchPath。完整实机步骤见 [`TESTING_CN.md`](TESTING_CN.md)。

## 验证与构建 HUD VPK

只做本地源码验证，不写入 CS2 目录：

```powershell
.\tools\build_hud_resources.ps1 -Action Validate
```

编译并打包：

```powershell
.\tools\build_hud_resources.ps1 -Action Build `
  -Cs2Root "F:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive" `
  -VpkEditCli "F:\cs2dev\SkinTools\VPKEdit-Windows-Standalone-msvc-Release\vpkeditcli.exe"
```

输出：

```text
dist/swift_online_music_player.vpk
```

VPK 的分发/挂载方式取决于服务器现有资源方案。服务器插件本身不能让未安装资源的客户端显示 HUD。

## 版本锁与实机验证

`resources/gamedata/signatures.jsonc` 已在当前测试客户端和服务器的 `server.dll` 上重新验证：

```text
SHA-256: 9e5749d77dcb68883477feae751a3f28068d119ec145edcb0e4d48d15b538d36
unique-pattern validation: 2026-08-29
```

四条签名分别唯一命中，文件偏移为 `0x8A3090`、`0x8A33C0`、`0x8A3450` 和 `0x259420`。可运行 `python tools/validate_gamedata_signatures.py <server.dll> resources/gamedata/signatures.jsonc` 复查。CS2 更新后必须重新核对哈希、唯一命中和 ABI。最终还需要在两名客户端上验证：各自切歌/音量隔离、语音设置对音乐的影响、关闭 UI 后继续播放、断线/换图/热重载清理，以及 16:9 与 4:3 布局。

## 参考

- [SwiftlyS2](https://github.com/swiftly-solution/swiftlys2)
- [SwiftlyS2 Audio](https://github.com/SwiftlyS2-Plugins/Audio)
- [Audio API: IAudioApi](https://github.com/SwiftlyS2-Plugins/Audio/blob/main/AudioApi/IAudioApi.cs)
- [Audio API: IAudioChannelController](https://github.com/SwiftlyS2-Plugins/Audio/blob/main/AudioApi/IAudioChannelController.cs)
- [SwiftlyS2 Sound Events](https://swiftlys2.net/docs/development/soundevents/)
- [Uiverse music player reference](https://uiverse.io/bociKond/serious-robin-34)
- [MusicSquare source](https://github.com/CharlesPikachu/musicsquare)
- [MusicSquare live demo](https://charlespikachu.github.io/musicsquare/)
