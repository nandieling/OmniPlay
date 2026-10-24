# 2026-10-15 内封字幕后台转成 sidecar（v1.34）

## 为什么走这条路

直出模式（`vo=mediacodec_embed`）由 MediaCodec 直接上屏，mpv 的字幕与 OSD 不参与合成，所以内封字幕看不见；兼容模式能画字幕，但 4K HDR 要在电视 GPU 上跑色调映射，1.33 降过一轮负载仍然吃力。Android 侧的覆盖层（`SubtitleCue` 文本层 + `PgsSubtitleView` 位图层，由 `subtitleOverlayUpdater` 按播放进度驱动）一直都在，只是独立版从没喂过它——服务端模式的 `api.subtitleUrl(track, ticket)` 才有现成的字幕接口。

缺的就是「本机把内封字幕抽出来」。抽出来之后：

- 直出模式能看字幕（读文本 sidecar 走覆盖层）；
- 兼容模式也少一路 mpv 字幕渲染；
- 产物是标准 SRT / ASS / .sup，别的播放器也认。

## 整轨一次顺序读，成本挪到后台

Matroska 把字幕块和音视频块交错写在同一条字节流里，`Cues` 只索引到簇。要覆盖到第 80 分钟就得把那段字节翻过去：MKV 没有「读一小段就拿到全部字幕」这回事。播放时按窗口现抽意味着正在放的那段要边播边重读，网络源上等于跟视频流抢带宽；PGS 一条字幕就是一帧位图（100~500KB），更不适合现抽。

结论：既然整轨成本是一次顺序读，就把它整个挪到后台跑，产物贴着视频放，播放只读那个小文件。这就是用户拍的「后台全量批量转换，转好的文本字幕存回 NAS，播放时直接调用」，并且按用户要求 PGS 也一起转。

## 代码

- `subtitle/SeekableSource.java`（新）：`size()` / `readAt(offset, length)` / `close()`。三个实现分别在 `SubtitleSidecarService` 里：本地 `RandomAccessFile`、WebDAV 一次 Range 请求（长度靠 `Range: bytes=0-0` 的 `Content-Range` 拿）、SMB 用一个 `SmbRandomAccessFile` 句柄从头用到尾。
- `subtitle/MatroskaSubtitleExtractor.java`（新）：自己写的 EBML 遍历 + 簇顺序走。每簇一次分块（1MiB）读，负载按声明长度跳过，所以整轨扫一遍就是顺序读一遍；支持从任意字节偏移续跑。`tracks()` 只读文件头，开销几十 KB。时间码按容器 `TimecodeScale` 换算；交织（laced）块跳过并计数。三种输出：`S_TEXT/UTF8` / `S_TEXT/WEBVTT` → SRT；`S_TEXT/ASS` → ASS（带容器里的脚本头，Matroska 的 ASS 块不带 `Dialogue:` 前缀、时间轴按块时间重算）；`S_HDMV/PGS` / `S_DVBSUB` → 原样拼 `.sup`。
- `local/SubtitleSidecarService.java`（新）：`Access`（`open` / `write` / `writeFile` / `exists` / `read` / `playbackUrl`）对接本地、WebDAV、SMB 三种媒体源；`sidecarPathFor` 定 `目录/片名.语言.格式`；`probe` 探能转的轨；`convert` 抽一条轨并写回。文本轨每个窗口（45 秒）整份重写，续跑时先 `readExisting` 把盘上那份解析回来当种子（只认自家写的格式，不需要通用字幕解析器）；PGS 先追加到 `<cacheDir>/sidecars/` 的临时文件，跑完一次性流式 PUT。写入失败按 HTTP 状态报「媒体源不接受写入（HTTP nnn），共享可能是只读的」。
- `local/Http.java`：请求体改成 `Body` 抽象（内存字节或本机文件），新增 `putFile`，`.sup` 几百 MB 不进内存。
- `local/SmbClient.java`：补 `write` / `writeFile` / `exists` / `length` / `readAll` / `readAt`（`SmbRandomAccessFile`）。
- 数据库升到 6：`subtitleSidecar(videoFileId, trackNumber, relativePath, language, format, status, cueCount, byteOffset, coveredSeconds, error, updatedAt)`，主键 `(videoFileId, trackNumber)`；`recordSidecar` / `sidecarRows` / `sidecarCandidates`（`LEFT JOIN` 挑没转过的，没看过优先）/ `forgetSidecars`。表由 `onUpgrade` 末尾统一的 `onCreate` 补出来，升级不搬数据。
- `local/TvSettings.java`：`subtitleSidecarMode()` = `off` / `unseen`（默认）/ `all`。
- `local/LocalEngine.java`：扫描刮削收尾调 `runSubtitleSidecarQueue(3)`；`transcodeSidecar` 探轨 → 挑轨 → 转 → 记 `done` / `partial` / `unsupported` / `failed`；`pickSubtitleTrack` 按用户字幕语言偏好 → 容器默认轨 → 第一条；`readSidecar` 给播放侧读产物，超过 64MB 直接报「太大了，这条先不用它」。
- `MainActivity`：设置里「内封字幕转档」三段开关（切换即排一小批）；字幕菜单列已转好的（「已存媒体源 · 中文（SRT · 612 条 · 还在后台转）」）并给「后台转档这条的内封字幕」；`selectSubtitleSidecar` 先 `playerView.setSubtitleTrack("no")` 关掉 mpv 那条内封轨，再把 sidecar 读进覆盖层（文本走 `parseSubtitleCues`，`.sup` 走 `PgsSubtitleParser` + `PgsSubtitleView`）。

## 顺带修的两个 bug

1. `PgsSubtitleParser` 段头读成 `'P' 'G'` + 4 字节 + 4 字节 + 类型 + 长度 = 13 字节。规范是 `'P' 'J'`（`0x50 0x4A`）+ 5 字节 PTS + 类型 + 16 位长度 = 10 字节，PTS 按 MPEG 时间戳布局 `'1000' + pts_ext(3) + '1' | pts[32..18] + '1' | pts[17..3] + '1'` 解，丢掉低 3 位（1/8 帧精度，对 90kHz 来说是 0.03 毫秒）。以前独立版没有本地 PGS 来源，这条路没被走过，所以一直没暴露。
2. 新抽取器自己的：EBML 元素 ID 连长度标记位一起才是 ID 本身，`Vint.decode` 为数据 vint 把标记位剥了，`0x18538067` 变成 `0x08538067`，所有 ID 常量都对不上、整份结构读空。`Vint` 现在同时给 `value`（数据用）和 `raw`（ID 比对用）。这条是夹具当场抓出来的。

## 边界与已知问题

- 只处理 MKV / WEBM。TS / M2TS / 蓝光 ISO 的 PGS 是 MPEG-TS 描述符 + PES，MP4 内封字幕是 `tx3g` / `wvtt`，都不在这套 EBML 读取器里；这类文件记一条 `unsupported`（「没有能转的字幕轨（目前支持 MKV 里的 SRT / ASS / PGS）。」），不会反复重试。
- 每条视频只转一条轨；多语言并列、sidecar 跟随视频改名、按目录/按语言的转档策略、专门的进度 UI 都还没做。
- `.sup` 的调色板段（PDS）与对象段（ODS）字段偏移沿用了覆盖层解析器现有的读法（对象序号在第 4 个字节、对象数在第 11 个字节）。PDS 与规范一致；ODS / 显示段（WDS）与常见实现差一个字节，得拿一张真原盘的 PGS 核对再定，这一版的位图字幕显示效果因此留作真机核对项。文本 sidecar 不受影响。
- 只读共享（WebDAV 禁 PUT、SMB 只读挂载）转档必然失败，只能回兼容模式看内封字幕。

## 验证

`build/patches/mkmkv.py` 现造夹具：视频轨 + `S_TEXT/UTF8`(chi) / `S_TEXT/ASS`(eng) / `S_HDMV/PGS`(zho) 三条字幕轨 + 三个簇（0 / 60s / 120s）+ 一条 BlockGroup 带 `BlockDuration` + 一条 laced 块 + 一个非 MKV 文件。

`build/harness/SidecarCheck.java` 30 项全绿（`zsh build/tvtools/harness.sh`，WebDAV 那半截要允许本机绑端口）：探轨、语言归一、SRT 三条时间轴、ASS 补前缀并保脚本头、`.sup` 能被覆盖层解析器读回两张画面且按 PTS 定时（3.0s / 62.0s）、laced 块不入库、中途收工再续跑不冲掉前两句、非 MKV 不转；再用 JDK 自带 `HttpServer` 假 WebDAV 验 Range 探轨、PUT 文本产物、流式 PUT `.sup`、HEAD 认已存在。`NameYearCheck` / `DoubanParseCheck` 一起跑，仍然全绿。

`assembleRelease` 通过，出 `dist/OmniPlay-TV-v1.34-arm64.apk`（versionCode 45）。

模拟器（Android TV 14 / arm64，假 WebDAV + 假 TMDB 夹具）装的是覆盖安装的 release 包，走的是 v5→v6 迁移：启动直接进海报墙（`topResumedActivity=com.omniplay.tv/.MainActivity`，日志有「刮削中刷新：就地更新 9 张海报」），整轮扫描刮削跑完没有 `no such table` / `SQLiteException` / `FATAL`；设置 → 刮削与播放偏好里「内封字幕转档」三段开关渲染与其余分段控件一致、能聚焦能选中，点「全部」触发一次队列也没报错（夹具的 `.mkv` 是 1000 字节的假数据，探轨必然为空，只留下 `unsupported` 账，所以看不到转档产物）。

## 一部片子要多久

`build/patches/mkbigmkv.py` 造了个 3.05GB 的夹具（12288 个 256KB 视频块 + 1024 个 48KB PGS 包），`build/harness/SidecarBench.java` 计时：整轨顺序扫一遍 3.12GB 用 1.2~1.8 秒，也就是抽取器本身能跑到 1.7~2.5 GB/s（Apple Silicon、文件已在页缓存）。CPU 不是瓶颈，电视上慢一个数量级也无所谓，因为**真正的时间是「把整部片顺序读一遍」**：

| 顺序读带宽 | 25GB 原盘 | 50GB 原盘 |
| --- | --- | --- |
| 40 MB/s（千兆 NAS 常见） | 约 11 分钟 | 约 21 分钟 |
| 70 MB/s（千兆打满） | 约 6 分钟 | 约 12 分钟 |
| 110 MB/s（2.5G 网口） | 约 4 分钟 | 约 8 分钟 |
| 本地 SATA / NVMe | 1~3 分钟 | 3~6 分钟 |

写回那一步很小：1080p 一条 PGS 轨约 1000~1500 帧，每帧一个不超过 64KB 的对象段，整轨 30~150MB，一次性流式 PUT 在千兆内网 1~3 秒；文本轨产物只有几十 KB。默认「仅未看」+ 每轮只排三个文件，所以前台播放不受影响，代价是一部片子从「开始转」到「有字幕」要等十几分钟到几十分钟。

PGS 要变成**文字**还得 OCR（本版本没做，只是把位图段搬成 `.sup`）：每帧 200~400ms 的轻模型 × 1200 帧 ≈ 4~8 分钟纯算；在 NAS 或 PC 上跑现成工具（pgsrip + Tesseract、Subtitle Edit）2 小时片 3~10 分钟一部，适合整夜批量。

还没验的：字幕菜单里选 sidecar、PGS 上屏、转档期间的首页日志。夹具文件起播就 `loading failed (-13)`，播放页进不去，这一段留给真机。
