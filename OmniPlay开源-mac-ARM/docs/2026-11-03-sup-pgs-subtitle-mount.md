# 直接打开视频时挂载同名 `.sup`（蓝光 PGS 位图字幕）

日期：2026-11-03 ｜ 影响工程：`OmniPlay开源-mac-ARM`（x64 同步）｜ 版本：1.10

## 现象

直接打开 `/Volumes/1T/00000.m2ts`，同目录同名的 `00000.sup` 不会自动挂上；在播放页手动
「挂载字幕」选同一个 `.sup` 也一样，字幕列表里始终什么都没有。

## 两层原因

1. **`.sup` 根本挂不上。** MPVKit 自带的 ffmpeg 是白名单编译（`--disable-demuxers
   --enable-demuxer=...`），里面没有 `sup` 解封装器，`sub-add x.sup` 直接静默失败。
   但 `pgssub` 解码器和 `mpegts` 解封装器都在名单内，所以由
   `PGSSubtitleWrapper` 把 `.sup` 里的 PGS 段重新装进一个只含字幕流的 MPEG-TS 交给 mpv。
2. **包出来的 TS mpv 认得、却不画。** 这才是这一轮真正修的坑：PES 头写的是
   `PTS_DTS_flags = 11`（PTS 和 DTS 都带），却只补了 5 字节的 PTS，
   `PES_header_data_length` 也按 5 字节填。解码侧按声明去读 DTS，会把负载的头 5 字节
   当成 DTS 吞掉，`pgssub` 收到的段流从此错位，一个显示组都出不来。
   `ffprobe` 只看 PTS，所以看起来一切正常，最难查。

## 修法

`PGSSubtitleWrapper.pesPackets` 的 PES 头改成和实际写出去的时间戳一致：
`0x84`（带 data_alignment_indicator）+ `0x80`（只带 PTS）+ `0x05`（5 字节头），
与 ffmpeg 自己封装 PGS 时写的那份字节完全一致。

## 怎么验的

拿应用自带的那份 libmpv 起一个最小播放窗口（1280x720 灰底视频 + 待测字幕轨），
同一时刻各抓两帧「字幕可见 / 字幕隐藏」，逐像素比差：

| 字幕 | t=144.9s | t=200s | t=300s |
| --- | --- | --- | --- |
| 修复前的产物 | 0.00% | — | 0.00% |
| 修复后的产物 | 0.63% | 0.59% | 2.08% |

对照：同一条字幕用系统 ffmpeg 封成 TS 喂给 mpv，能画出 1.08% 的差异，说明 mpv 的 PGS
链路本身没问题；文本字幕（`.srt`）同机 0.78%，说明抓帧比对这套办法是有效的。
产物里 6928 个 PES 包全部符合 `0x84 / 0x80 / 0x05`，mpv 侧
`track-list` 报 `codec=hdmv_pgs_subtitle`、`selected=true`、`decoder=pgssub`。

## 留下的一句话

以后动这个文件的 PES/TS 头，别只验「ffprobe 能不能读出字幕流」——它对 PES 声明和实际
字节不一致是免疫的，必须像上面这样让 mpv 真画一帧出来对差。
