# 本地 exFAT 片源「卡顿 + 进度乱跳」定位与进度条稳定

日期：2026-11-20

## 现象

挂载本地文件夹 `/Volumes/1T/电影/后会友期.I.Miss.You.S01.2026.2160p.MGTV.WEB-DL.H265.AAC-ADWeb`
后播放出现卡顿和进度异常跳转。同一部剧下载到 NAS（`/Volumes/电视剧/` 同一目录）播放没有问题。

## 结论

**不是播放器的问题，也不是片源本身的问题，是这批文件拷到 exFAT 外置盘时丢了数据。**
五个 MKV 的内容里有 6.6%~24.1% 是整片整片的零字节，其中四个连尾部的 seek 索引（Cues）都被写成了零。
同样大小、同样字节 offsets 的 NAS 原件这些位置都是有内容的，其它没丢块的区域两边字节完全一致。

## 证据

盘和文件系统：`/Volumes/1T` 是 exFAT，经 FSKit 挂载（`mount` 里带 `fskit`）。顺序读约 973MB/s，
成片只需 1.5MB/s 左右，盘和带宽都不是瓶颈；五个文件都是 HEVC Main 8-bit 3840x2160@25、约 12.2Mbps，
2160p@25 的 8-bit HEVC 走 VideoToolbox 没有解码压力。

全文件按 64KB 整块扫零块（`>=64KB` 连续全零算一段）：

| 文件 | 大小 | 全零 | 段数 |
| --- | --- | --- | --- |
| S01E09 | 7716MB | 509MB (6.6%) | 343 |
| S01E10 | 7227MB | 828MB (11.5%) | 444 |
| 加更 S01E04 Plus | 3386MB | 732MB (21.6%) | 201 |
| 加更 S01E05 Plus | 3404MB | 819MB (24.1%) | 361 |
| 抢鲜 S01E05 Preview | 3093MB | 578MB (18.7%) | 266 |

交叉验证：

- 每个文件在本地全零的段里等距抽 25 处，去 NAS 同名同大小文件的同一 offset 读，125 处**全部**是有效内容；
  另外在未丢块的 offset 上两边字节逐字节相同。
- 同盘对照：`Ideology...H264/01.mkv`(2.7GB)、`02.mkv`(3.3GB)、`张家界.zip` 前 3GB 全零块均为 0，
  说明这块盘和 FSKit exFAT 驱动平时是好的。
- 复现验证：把 NAS 的 抢鲜E05Preview(3.09GB) 重新拷到同一块 exFAT 盘，新文件全零块 0、Cues 完好，
  说明现在写这条路是通的，坏掉的是 11-20 17:23~18:11 那一次拷贝。
- `mkvmerge` 重混 加更E05Plus 成功，报大量 `Error in the Matroska file structure ... Resyncing to the next
  level 1 element`（最后一处 `Resync failed`），输出的干净文件全零块 0、ffmpeg 零报错。

卡顿和进度乱跳的直接原因就一条：seek 索引没了。ffmpeg 从 70% 处定位取 30 帧：

| 文件 | 定位耗时 |
| --- | --- |
| 本地 S01E09 | 6.11s |
| 本地 S01E10 | 6.01s |
| 本地 加更E04Plus | 3.33s |
| 本地 加更E05Plus | 3.63s（重混后的同一文件 0.065s） |
| NAS 原件 / 重拷副本 | 0.03s~0.07s |

SeekHead 里四条 `SeekPos` 都还在，但 Cues、Tags 指向的那段读回来是全零 —— 头部写进去了、尾部没写进去，
是典型的「大文件写丢块」，不是下载截断（下载截断文件尺寸会短）。

## 改动

`MPVPlayerManager.swift`

- 新增「进度条稳定」：mpv 报 `seeking` 期间冻住上屏位置，别把没落定的 `time-pos` 发给 UI；
  定位结束（或冻结超过 8 秒）后第一次无条件接受；平时比正常推进量级（>1.5s）大得多的跳变，
  要连续两轮站得住才上屏。自己发起的 seek 会置 `acceptNextTimePos`，不会被稳定逻辑压掉。
- 定位耗时 >=1.5s 记一行日志，提示这个文件多半没有可用索引。
- 换片时 `resetTimelineStability(seededPosition:)` 从续播位置重新记账。

`ContainerIntegrityProbe.swift`（新）

- 加载本地 MKV/WebM 时在后台队列做一次容器快检：顺 SeekHead 找到尾部 Cues，按 `SeekPos`
  去读那 16 个字节，确认元素 ID 真是 Cues；再等距抽 16 个 2MB 窗口（外加文件尾巴）数 64KB 全零块。
- 结论写进日志：索引缺失、全零占比、以及「建议跟源文件重新拷贝一次并校验」。阈值取 2%，
  正常编码器留下的 Void 填充到不了这个量级。每个文件只多读约 34MB，不跟播放抢带宽。

两份工程（`OmniPlay开源-mac-x64` / `OmniPlay开源-mac-ARM`）同步，Release 均 **BUILD SUCCEEDED**。

## 遗留

- 容器体检结论目前只进控制台日志（`print`），播放页还没有非阻断提示条。要做的话接在
  `PlayerScreen` 的控制层上，文案已经有了。
- 定位提速没有 mpv 侧开关：`mpv --list-options` 里 Matroska 只有 `demuxer-mkv-crop-compat`
  `demuxer-mkv-probe-start-time` `demuxer-mkv-probe-video-duration` `demuxer-mkv-subtitle-preroll*`，
  没有索引预扫这类选项，只能靠文件自身有索引。重复定位靠之前 ISO 修复里加的 256MiB demuxer 缓存兜着。
- 这批坏文件在本地救不回来（零字节是真写进盘里的），只能从 NAS 重新拷或重新下载，并做校验。
  exFAT 外置盘放片源建议拷完必校验，或者换成 APFS。
