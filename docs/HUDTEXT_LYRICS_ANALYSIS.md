# HudText 歌词机制移植说明

## 采用的机制

HudText 的关键并不是独立文字渲染器，而是 `CCSCustomHudLayout` 的原生逐玩家状态接口：

1. VXML 预先声明固定 `Label`，文本绑定静态 dialog variable。
2. 服务端使用 `SetDialogVariableStringForPlayer` 更新某个玩家看到的文本。
3. 服务端使用 `SetHasClassForPlayer` 控制同一玩家看到的可见性和样式。
4. 所有玩家共享一个 HUD 实体，但文本、CSS class 和显示状态逐玩家隔离。

音乐播放器已经使用同一种 Custom HUD 桥接方式，因此没有引入 HudText 的 DLL、共享接口、64 槽管理器或 addon。现有 `online_music_player_custom_hud.xml` 新增固定的 `#music_lyrics` 面板及当前/下一句两个 Label，资源仍编译进 `swift_online_music_player.vpk`。

## 数据流

```text
酷我/网易搜索结果 (Source + SourceId)
        |
        +-- Kuwo --> kuwo.cn lyric/getlyric --> JSON lrclist
        |
        +-- Netease --> qijieya meting type=lrc --> LRC 文本
        |
        v
MusicLyricsProvider
  - 公网 DNS 校验
  - 禁止重定向
  - 独立超时与 512 KiB 上限
  - 时间戳与 `[offset]` 解析、排序、去重、缓存
        |
        v
PlayerSession.Lyrics
        |
        +-- GetElapsed = 已播放时间 + 本次继续后的时间
        +-- 0.20 秒轻量检查，仅换行时写 HUD
        |
        v
#music_lyrics 的 lyric-current / lyric-next + MusicLyricsVisible
```

歌词请求和音频解码并行。歌词接口失败、超时或没有结果时只隐藏歌词，不会延迟或终止音乐。相同平台和歌曲 ID 的歌词任务在服务器内共享缓存，最多保留 128 个条目。

## 生命周期约束

- 播放：按当前播放时间选择最后一个不晚于当前时间的歌词行。
- 暂停：`StartedAt` 清空，歌词停在当前行并降亮。
- 恢复：从 `ElapsedBeforeResume` 接续，不计算暂停时长。
- 关闭播放器 UI：只隐藏 `#music_dialog`，歌词与后台音乐继续。
- 切歌、停止、搜索开始、解码失败：立即隐藏并清空旧歌词。
- 断线：停止逐玩家 Audio channel，同时清空该 slot 的歌词 class 和 dialog variable，避免下一个占用该 slot 的玩家继承状态。
- 插件卸载：停止两个刷新定时器、清理所有会话和缓存，再移除 HUD 实体。

## 未采用的部分

- 不复制 HudText 的 64 个通用文本槽。
- 不依赖 HudText API 或 SwiftlyS2 插件。
- 不增加第二个 `CCSCustomHudLayout` 实体。
- 不增加客户端脚本、粒子、HTML 或 Panorama Audio 面板。
- 不要求客户端下载或挂载第二个 addon/VPK。

## 当前边界

- 自动歌词只支持带有效 `SourceId` 的酷我和网易云在线搜索曲目。
- 静态曲库的任意直链没有可靠的曲目平台 ID，因此默认不猜测歌词。
- 歌词平台属于第三方在线依赖，不能保证所有歌曲、翻唱、现场版或地区都返回匹配歌词。
- `Lyrics.TimingOffsetSeconds` 可在 `-5` 到 `5` 秒间调整服务器整体校时；它不会修改音频游标。
