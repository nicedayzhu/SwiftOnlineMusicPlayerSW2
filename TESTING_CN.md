# SwiftOnlineMusicPlayerSW2 本机测试清单

## 已安装位置

- 服务器插件：`F:\csgoserver_win\cs2\game\csgo\addons\swiftlys2\plugins\SwiftOnlineMusicPlayerSW2`
- Audio v1.0.6：`F:\csgoserver_win\cs2\game\csgo\addons\swiftlys2\plugins\Audio`
- 服务器 HUD：`F:\csgoserver_win\cs2\game\csgo\overrides\swift_online_music_player.vpk`
- 客户端 HUD：`F:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\overrides\swift_online_music_player.vpk`
- 两侧 `gameinfo.gi` 都已加入：`Game csgo/overrides/swift_online_music_player.vpk`

第一次测试前必须完全退出并重新启动 CS2 客户端；Panorama 资源和 `gameinfo.gi` 不应依赖热刷新。

## 一、启动与依赖检查

1. 使用现有 `F:\csgoserver_win\start_server-cs2.bat` 启动测试服务器。
2. 客户端控制台连接：`connect 127.0.0.1:29015`。
3. 服务器控制台输入 `sw`，确认 SwiftlyS2 正常响应。
4. 进入服务器后发送 `!music_status`。

预期结果：

```text
audio=ready, hud=ready, search=kuwo-primary+netease-fallback
```

首次正常启动后应生成：

```text
addons/swiftlys2/configs/plugins/Audio/config.jsonc
addons/swiftlys2/configs/plugins/SwiftOnlineMusicPlayerSW2/config.jsonc
```

Audio 测试阶段保持 `UseFFMpeg: false`，先使用随插件提供的 `pcmdecoder.dll`。

## 二、静态直链播放

1. 输入 `!music`，应出现播放器卡片并捕获鼠标。
2. 点击播放，默认 SoundHelix 测试曲应在数秒内开始。
3. 依次测试暂停、继续、音量减、音量加、上一首、下一首。
4. 点击爱心，确认当前曲目的收藏高亮可切换，切歌后状态按曲目独立；重新连接后无需保留。
5. 输入 `!music_close` 或点击 X；UI 应关闭，但音乐继续。
6. 再次输入 `!music`；UI 应恢复且显示当前状态。
7. 输入 `!music_stop`；音乐应停止，进度归零。

同时观察：按钮 Hover 是否正常、进度是否每秒推进、播放状态下左上角 5 根绿色音浪是否以不同节奏持续波动、暂停后音浪是否停止、音量文本是否为 0%–100%、关闭后鼠标是否立即恢复。

## 三、在线搜索播放

依次测试：

```text
!music_search 周杰伦 稻香
!music_pick 2
!music_library
```

预期行为：

1. HUD 状态先变成 `SEARCHING / KUWO PRIMARY · NETEASE FALLBACK`，并展开搜索抽屉。
2. 搜索完成后聊天区应逐条显示编号、曲名、歌手和来源；HUD 显示字体清晰的可点击候选曲目行。
3. 默认 `AutoPlayFirstSearchResult: true` 时应立即加载并播放第 1 条，同时保留搜索抽屉供玩家改选。
4. 点击任一曲目行应立即收起抽屉并加载该曲；超过 5 条时测试抽屉翻页按钮。
5. `!music_pick 2` 仍可播放第 2 条；主播放器左右按钮在本次候选中切换。
6. `!music_library` 返回 SoundHelix 静态曲库并隐藏搜索入口。
7. 5 秒内连续搜索应提示冷却，而不是重复请求接口。

随后将配置改为 `"AutoPlayFirstSearchResult": false` 并等待配置重载，再搜索一次。此时 HUD 应进入 `RESULTS READY`，保留列表且不自动播放，直到点击结果或使用 `!music_pick`。

搜索结果时长未知，所以 HUD 显示 `LIVE` 属于当前设计，不代表一定是直播流。

## 四、双玩家隔离

若可以打开两个客户端：

1. 玩家 A 搜索并播放歌曲，音量调到 20%。
2. 玩家 B 打开播放器并播放静态曲库，音量调到 100%。
3. A 暂停、切歌、关闭 UI，确认 B 不受影响。
4. B 断线，确认 A 仍播放；服务器日志不应出现遗留 channel 或输入捕获异常。

## 五、画面与清理

- 测试 16:9 和 4:3。
- 点击 X、`!music_close`、`!music_stop` 后验证鼠标状态。
- 换图一次，重新输入 `!music`。
- 测试插件热重载或服务器重启后再次打开。
- 退出服务器后确认客户端没有残留 HUD。

## 六、无声时先检查

1. `!music_status` 中 `audio` 是否为 `ready`。
2. CS2 设置中是否启用了语音，语音音量是否非零；此插件通过逐玩家 VoIP 通道传输音乐。
3. 是否把服务器或相关语音来源静音。
4. 先测默认 SoundHelix。若它能播放而搜索歌曲不能播放，问题通常是搜索接口/CDN URL，而不是 Audio 插件。
5. 若所有 URL 都失败，再检查 Audio 配置和 `pcmdecoder.dll`；不要在未安装 FFmpeg 时启用 `UseFFMpeg`。

## 七、日志采集

测试后在 PowerShell 运行：

```powershell
$logRoot = "F:\csgoserver_win\cs2\game\csgo\addons\swiftlys2\logs\managed"
$latest = Get-ChildItem -LiteralPath $logRoot -Filter "*.log" |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 1
Get-Content -LiteralPath $latest.FullName -Tail 300 |
  Select-String "SwiftOnlineMusicPlayer|Audio|Exception|Error|Warning"
```

若 HUD 完全不出现，同时提供客户端控制台中包含以下关键词的行：

```text
custom_hud_layout
online_music_player
disallowed
vxml
vcss
```

也可以在停止服务器后直接运行一键收集脚本：

```powershell
.\tools\collect_test_diagnostics.ps1
```

它只读取本项目、Audio、两条 VPK 挂载和最新 SwiftlyS2 日志，不读取 `server.cfg`、登录令牌或其他服务器秘密。输出写入 `build/test-diagnostics-<时间>.txt`。

## 反馈模板

```text
1. !music_status 输出：
2. HUD 是否出现/是否可点击：
3. 默认 SoundHelix 是否有声：
4. !music_search 的聊天输出：
5. 搜索结果是否有声：
6. 暂停/继续/音量/切歌结果：
7. 关闭 UI 后音乐和鼠标状态：
8. 画面比例与异常截图：
9. 服务器日志相关片段：
10. 客户端控制台错误：
```

## 构建验证

- `server.dll` SHA-256：`9E5749D77DCB68883477FEAE751A3F28068D119EC145EDCB0E4D48D15B538D36`
- 四条 Custom HUD 原生签名均唯一命中。
- Audio v1.0.6 官方 ZIP SHA-256：`CF719B1AE4784202D7673BF2D55B172A0F7A0E8502D84C7BE968AC14D157F4FF`
- 当前实机安装的服务器/客户端/项目音浪修正版 VPK SHA-256：`ED743ECB0AA3E600B24260EFA9EE7FD6BC14E5CA42E7F583E9EB657EFE95113E`。
- 本次实机安装的插件 DLL SHA-256：`BBFAB3289AD4C582A11108301BBA35BA8A41B0D6119F8B289F2534BAF5AD980C`。

原始 `gameinfo.gi` 备份位于：

```text
F:\cs2dev\SkinTools\res\SwiftOnlineMusicPlayerSW2\build\install-backups\20260829-225908
```
