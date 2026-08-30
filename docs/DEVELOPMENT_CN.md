# SwiftOnlineMusicPlayerSW2 开发者文档

[English](DEVELOPMENT.md) | 简体中文 | [返回 README](../README_CN.md)

本文面向插件开发者和服务器运维人员，说明项目边界、构建产物、运行配置、资源部署、原生 GameData 维护与验证流程。

## 1. 项目边界

SwiftOnlineMusicPlayerSW2 由服务器插件与客户端 HUD 资源共同组成：

| 产物 | 默认位置 | 用途 |
| --- | --- | --- |
| 插件发布目录 | `build/publish/SwiftOnlineMusicPlayerSW2/` | SwiftlyS2 服务器插件与 GameData |
| 插件压缩包 | `SwiftOnlineMusicPlayerSW2.zip` | `dotnet publish` 后生成的分发包 |
| HUD VPK | `dist/swift_online_music_player.vpk` | Panorama 布局、样式和图标 |
| Workshop Addon | [Online Music Player（3792571203）](https://steamcommunity.com/sharedfiles/filedetails/?id=3792571203) | 正式服客户端 HUD 资源 |
| 安装报告 | `build/install-backups/<timestamp>/install-report.json` | 本地测试安装的备份与目标记录 |

服务器插件、Audio 插件和 Workshop Addon 是三个独立产物：部署服务器插件不会自动安装 Audio，也不会把 Panorama 资源发送给客户端。正式服使用 SwiftlyS2 AddonsManager 分发并挂载 Workshop Addon；本地开发才使用仓库生成的 override VPK。

## 2. 运行时架构

### 2.1 播放链路

1. 玩家通过命令或 HUD 点击发起操作。
2. 插件验证曲目 URL，并请求 SwiftlyS2 Audio 解码音频源。
3. 每位玩家使用独立的 Audio channel controller。
4. Audio 通过 CS2 VoIP 通道投递音频。
5. 插件根据播放游标更新 HUD 时间、进度、状态和歌词。

解码后的同一 URL 音源可在服务器内缓存复用，但播放控制器和会话状态始终逐玩家隔离。

### 2.2 HUD 链路

1. 插件动态创建一个 `custom_hud_layout` 实体。
2. 实体加载 `panorama/layout/custom_game/online_music_player_custom_hud.xml`。
3. 原生桥逐玩家写入 dialog variable 与 CSS class。
4. 静态 Button ID 经 `CustomHudClickedReceiver` 回传给插件。
5. 插件把点击事件映射为播放、切歌、音量、搜索结果或输入捕获操作。

该 HUD 不包含 VJS、HTML 或 Panorama Audio 面板，符合 `CCSCustomHudLayout` 的受限资源模型。

### 2.3 输入与会话生命周期

- `!music` 只显示播放器，不立即捕获鼠标。
- 玩家按下并松开鼠标右键后，插件启用当前玩家的输入捕获。
- 点击底部返回瞄准区域会释放输入捕获，但播放器保持显示。
- 点击 `X` 关闭播放器并释放输入捕获，音乐继续播放。
- `!music_stop` 停止并重置玩家自己的频道。
- 玩家断线、切换会话或插件卸载时，插件会停止相关音频并清理 HUD、歌词和输入捕获状态。

## 3. 环境要求

### 3.1 编译环境

- Windows PowerShell 7 或 Windows PowerShell 5.1
- .NET 10 SDK
- 能够还原 NuGet 包的网络环境

### 3.2 运行环境

- Windows CS2 Dedicated Server
- SwiftlyS2 `1.4.6-beta.8` 或兼容版本
- SwiftlyS2 Audio 插件；项目引用 Audio API `2.0.0`
- 正式服安装 [SwiftlyS2 AddonsManager](https://github.com/SwiftlyS2-Plugins/AddonsManager) 并配置 Workshop `3792571203`
- 本地测试环境在客户端与服务器挂载同一版 HUD VPK

若希望 Audio 处理更多媒体格式或流协议，请在服务器安装 FFmpeg，并按 Audio 插件文档启用 `UseFFMpeg`。

### 3.3 HUD 构建工具

- CS2 `resourcecompiler.exe`
- [VPKEdit](https://github.com/craftablescience/VPKEdit) CLI

## 4. 仓库结构

```text
SwiftOnlineMusicPlayerSW2/
├─ src/                         C# 插件、搜索和歌词实现
├─ hud/
│  ├─ layout/                  Panorama VXML
│  ├─ styles/                  Panorama VCSS
│  └─ icons/                   128×128 RGBA 图标源文件
├─ resources/gamedata/         Windows 原生桥签名
├─ tools/                       HUD 构建、签名校验和诊断脚本
├─ docs/                        开发文档与专项分析
├─ build_and_deploy.ps1         仅发布并部署服务器插件
├─ install_local_test.ps1       完整本地客户端/服务器测试安装
├─ TESTING_CN.md                实机测试清单
└─ SwiftOnlineMusicPlayerSW2.csproj
```

## 5. 构建服务器插件

在项目根目录执行：

```powershell
dotnet restore .\SwiftOnlineMusicPlayerSW2.csproj
dotnet publish .\SwiftOnlineMusicPlayerSW2.csproj -c Release
```

`dotnet publish` 会生成：

- `build/publish/SwiftOnlineMusicPlayerSW2/`
- `SwiftOnlineMusicPlayerSW2.zip`

只把插件发布到现有服务器：

```powershell
.\build_and_deploy.ps1 -ServerRoot "F:\csgoserver_win\cs2"
```

目标目录：

```text
<ServerRoot>\game\csgo\addons\swiftlys2\plugins\SwiftOnlineMusicPlayerSW2\
```

该脚本只处理本插件，不安装 Audio、不复制 HUD VPK，也不修改 `gameinfo.gi`。

## 6. 构建 HUD VPK

`tools/build_hud_resources.ps1` 提供四种动作：

| Action | 行为 |
| --- | --- |
| `Validate` | 校验 XML、CSS、图标和资源引用，不调用 CS2 编译器 |
| `Compile` | 使用 ResourceCompiler 生成编译资源 |
| `Pack` | 使用已存在的编译资源生成 VPK |
| `Build` | 依次完成校验、编译和打包 |

源码验证：

```powershell
.\tools\build_hud_resources.ps1 -Action Validate
```

完整构建：

```powershell
.\tools\build_hud_resources.ps1 -Action Build `
  -Cs2Root "F:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive" `
  -VpkEditCli "F:\path\to\vpkeditcli.exe"
```

默认 addon 名称为 `swift_online_music_player`，输出为：

```text
dist/swift_online_music_player.vpk
```

构建脚本会为 `hud/icons/*.png` 生成 VTEX 描述，再交给 ResourceCompiler 编译。不要直接把未编译 VXML/VCSS 放进运行时 VPK。

## 7. 运行配置

配置文件位于：

```text
<ServerRoot>\game\csgo\addons\swiftlys2\configs\plugins\SwiftOnlineMusicPlayerSW2\config.jsonc
```

默认模型：

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

### 7.1 通用播放字段

| 字段 | 说明 |
| --- | --- |
| `DefaultVolume` | 新会话默认音量，规范化到 `0.0–1.0` |
| `AutoAdvance` | 已知时长曲目结束后是否自动播放下一首 |
| `AutoPlayFirstSearchResult` | 搜索成功后是否自动加载第一条，同时保留结果抽屉 |
| `Tracks` | 管理员静态曲库，最多 64 首 |
| `DurationSeconds` | HUD 计时与自动下一首依据；未知时长或直播填 `0` |

静态 `Url` 必须是解码器可读取的 HTTP/HTTPS 直接媒体或流地址。网页链接不能直接播放。

### 7.2 搜索字段

| 字段 | 说明 |
| --- | --- |
| `Enabled` | 是否启用在线搜索 |
| `KuwoEnabled` | 是否启用酷我主搜索路径 |
| `KuwoQualityIndex` | 酷我候选音质索引 |
| `ResultLimit` | 结果数量，限制为 `1–10` |
| `TimeoutSeconds` | 单次请求超时，限制为 `3–30` 秒 |
| `CooldownSeconds` | 每位玩家的搜索冷却，限制为 `0–60` 秒 |
| `AllowedAudioHostSuffixes` | 可播放音频主机后缀白名单 |

建议在生产环境保持白名单尽量小。留空会接受任意通过公网地址校验的 HTTP(S) 音频主机。

### 7.3 歌词字段

| 字段 | 说明 |
| --- | --- |
| `Enabled` | 服务器是否启用歌词功能 |
| `VisibleByDefault` | 新玩家会话是否默认显示歌词 |
| `TimeoutSeconds` | 歌词请求超时，限制为 `3–30` 秒 |
| `TimingOffsetSeconds` | 歌词校时，正值提前、负值延后，限制为 `-5–5` 秒 |

搜索结果中带有受支持 `Source` 与 `SourceId` 的曲目会请求歌词。普通静态 URL 没有来源标识时不会自动请求歌词。歌词失败不会阻断音频播放。

### 7.4 网络保护

搜索与歌词请求均实施：

- 仅接受 HTTP/HTTPS。
- 禁止 HTTP 重定向。
- DNS 解析后拒绝本地、环回、链路本地和其他非公网目标。
- 单次响应上限 512 KiB。
- 独立请求超时。
- 搜索结果的最终音频 URL 额外执行主机后缀白名单校验。

这些检查降低误配置与 SSRF 风险，但不能替代服务器级出口访问控制。

## 8. 部署与资源挂载

### 8.1 完整本地测试安装

准备：

- 已生成的插件发布目录
- `dist/swift_online_music_player.vpk`
- `build/dependencies/Audio-v1.0.6.zip`
- 与 GameData 快照匹配的 `server.dll`
- 已停止的 CS2 客户端和服务器进程

执行：

```powershell
.\install_local_test.ps1 `
  -ServerRoot "F:\csgoserver_win\cs2" `
  -ClientRoot "F:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive"
```

脚本会：

1. 校验输入目录、发布产物、Audio 包和 `server.dll` 哈希。
2. 备份现有插件、Audio、服务器/客户端 VPK 和 `gameinfo.gi`。
3. 安装 Audio 与本插件。
4. 将 VPK 复制到服务器与客户端的 `game/csgo/overrides/`。
5. 向双方 `gameinfo.gi` 添加：

   ```text
   Game csgo/overrides/swift_online_music_player.vpk
   ```

6. 写出 `install-report.json`，便于定位备份与恢复目标。

更完整的实机顺序见 [TESTING_CN.md](../TESTING_CN.md)。

### 8.2 正式服：Workshop + AddonsManager

HUD 资源已经发布到 Steam 创意工坊：

- 名称：[Online Music Player](https://steamcommunity.com/sharedfiles/filedetails/?id=3792571203)
- Workshop ID：`3792571203`

正式服务器使用上游 [SwiftlyS2 AddonsManager](https://github.com/SwiftlyS2-Plugins/AddonsManager) 下载并挂载该资源，同时让连接的玩家客户端取得 Workshop Addon。AddonsManager 不会安装 SwiftOnlineMusicPlayerSW2 或 SwiftlyS2 Audio，这两个服务器插件仍需单独部署。

1. 按 AddonsManager 上游 README 安装插件并启动服务器一次。
2. 打开配置文件：

   ```text
   <ServerRoot>\game\csgo\addons\swiftlys2\configs\plugins\AddonsManager\config.jsonc
   ```

3. 将本项目 Workshop ID 加入 `Main.Addons`。保留服务器已有的其他 ID：

   ```jsonc
   {
     "Main": {
       "Addons": [
         "3792571203"
       ]
     }
   }
   ```

4. 重启服务器或按当前 AddonsManager 版本的方式重载插件。
5. 可在服务器控制台请求立即下载并检查挂载路径：

   ```text
   sw_downloadaddon 3792571203
   sw_searchpath
   ```

`sw_downloadaddon` 和 `sw_searchpath` 来自 AddonsManager 上游命令接口。执行 `!music` 前，应先确认下载成功，且 `sw_searchpath` 已列出对应的 Workshop/VPK 搜索路径。

不要把 8.1 节的本地 `gameinfo.gi` override 当作正式服分发方案；正式服以 Workshop `3792571203` 和 AddonsManager 配置为准。

### 8.3 正式服验证

1. 确认 SwiftOnlineMusicPlayerSW2、Audio 和 AddonsManager 均正常加载。
2. 确认 AddonsManager 已下载 Workshop `3792571203`，`sw_searchpath` 能看到相应资源路径。
3. 使用没有本地 override VPK 的客户端连接服务器，确认客户端能够取得资源。
4. 输入 `!music`，验证播放器、搜索抽屉、鼠标交互和同步歌词。
5. Workshop 更新后重新进行下载、挂载和干净客户端验证，避免服务器插件与 HUD 资源版本不一致。

## 9. GameData 与原生桥维护

签名文件：

```text
resources/gamedata/signatures.jsonc
```

当前 Windows 快照记录：

```text
server.dll SHA-256:
9e5749d77dcb68883477feae751a3f28068d119ec145edcb0e4d48d15b538d36

last unique-pattern validation:
2026-08-29
```

| 符号 | 已验证文件偏移 |
| --- | --- |
| `SetDialogVariableStringForPlayer` | `0x8A3090` |
| `SetHasClassForPlayer` | `0x8A33C0` |
| `SetInputCaptureEnabled` | `0x8A3450` |
| `CustomHudClickedReceiver` | `0x259420` |

本地复查：

```powershell
python .\tools\validate_gamedata_signatures.py `
  "F:\path\to\server.dll" `
  ".\resources\gamedata\signatures.jsonc"
```

每次 CS2 更新后都必须重新确认：

1. 客户端与服务器 `server.dll` 是否来自同一构建。
2. SHA-256 是否仍与受支持快照一致。
3. 每条模式是否唯一命中。
4. 函数调用约定、参数和返回类型是否仍兼容。
5. HUD dialog variable、class 写入和点击回调是否通过实机验证。

模式唯一命中只证明字节定位成立，不证明 ABI 一定没有变化。Linux pattern 当前为空，不能视为受支持平台。

## 10. 搜索与歌词实现

### 10.1 搜索

默认顺序：

1. 请求酷我 OIAPI。
2. 对候选名称、歌手、可播放 URL 和主机进行验证。
3. 若没有可播放结果，回退到网易云兼容 Meting 接口。
4. 把结果同时发送到聊天与 HUD 抽屉。
5. 根据 `AutoPlayFirstSearchResult` 决定是否自动播放第一条。

实现借鉴 MusicSquare 的“搜索元数据 → 获取直接音频 URL → 播放器消费”流程，但没有嵌入或抓取 MusicSquare 网页，也不复制第三方 token。详见 [MusicSquare 接口分析](MUSICSQUARE_ANALYSIS.md)。

### 10.2 歌词

- 酷我曲目读取 JSON 时间轴歌词。
- 网易云曲目读取 LRC。
- 歌词被规范化为按秒排序的行列表，并按来源和曲目 ID 缓存。
- 播放 tick 根据玩家实际游标选择当前行和下一行。
- 暂停时歌词停留；恢复后继续同步。
- 关闭播放器卡片不隐藏正在播放的歌词；停止、切歌、断线和卸载会立即清理。

HudText 只作为实现机制参考。本项目使用自己的固定 Label、dialog variable、class、布局和 VPK，不依赖 HudText DLL 或独立 addon。详见 [HudText 歌词机制分析](HUDTEXT_LYRICS_ANALYSIS.md)。

## 11. 验证流程

提交代码前至少执行：

```powershell
dotnet build .\SwiftOnlineMusicPlayerSW2.csproj -c Release --no-restore
.\tools\build_hud_resources.ps1 -Action Validate
git diff --check
```

涉及 GameData、资源编译或安装脚本时，还应执行：

- `dotnet publish`
- `build_hud_resources.ps1 -Action Build`
- `validate_gamedata_signatures.py`
- [TESTING_CN.md](../TESTING_CN.md) 中的单人、双人、断线重连与输入捕获测试

重点回归项：

- 玩家断线后音频、歌词和输入捕获均被清理。
- 同一服务器上的两位玩家互不影响播放、音量和搜索结果。
- 打开 HUD 不捕获鼠标；右键进入交互；底部文字恢复瞄准。
- 关闭 HUD 后音乐继续；`!music_stop` 后彻底停止。
- 搜索失败、歌词失败和 Audio 缺失时均显示真实错误状态。
- 16:9 与 4:3 分辨率下播放器和歌词位置可用。

## 12. 常见问题

| 现象 | 检查项 |
| --- | --- |
| `!music` 有日志但没有界面 | 客户端是否挂载 VPK；路径是否为已编译 `custom_game` 资源；服务器是否创建 HUD 实体 |
| 有界面但按钮无效 | GameData 是否匹配当前 `server.dll`；点击接收器是否唯一命中；输入捕获是否已启用 |
| 有指针但无法恢复瞄准 | 点击播放器底部返回瞄准区域；确认 VPK 与插件版本一致 |
| 显示加载但没有声音 | Audio 插件是否加载；URL 是否为直链；语音设置、FFmpeg 与服务器出口网络是否正常 |
| 搜索始终失败 | 第三方端点、超时、公网 DNS 校验与音频主机白名单 |
| 有音乐但没有歌词 | `Lyrics.Enabled`、玩家 `!music_lyrics` 状态、曲目 `Source/SourceId` 与歌词端点 |
| 更新 CS2 后崩溃或 HUD 失效 | 立即停用旧 GameData，并重新验证哈希、签名和 ABI |
| 两端效果不一致 | 确认客户端与服务器挂载的是同一版 VPK，清理旧覆盖资源后重启 |

可使用 `tools/collect_test_diagnostics.ps1` 收集测试诊断；具体参数和输出见 [测试指南](../TESTING_CN.md)。

## 13. 第三方服务与内容合规

- 只播放你有权在目标服务器公开传输的音频。
- 第三方 API 的可用性、响应格式和授权条款可能随时变化。
- MusicSquare 的源码许可证不授予任何歌曲、平台目录或公开播放权。
- 本项目不分发 JOOX token、付费/VIP 内容绕过逻辑或第三方媒体文件。
- Audio 插件必须按其自身许可证单独安装和分发。

## 14. 作者与许可证

**作者：niceday_zhu**

Copyright © 2026 niceday_zhu. 本项目以 [MIT License](../LICENSE) 发布。MIT License 仅覆盖本项目自身代码和文档；第三方依赖、服务、素材以及音乐内容仍受各自许可证和条款约束。
