# OmniPlay 字幕转档（瘦身版）

只做一件事：把媒体库里视频**内封的字幕**抽成贴着视频的 sidecar 写回原目录，

     Movies/大电影.2019.1080p.mkv
    -> Movies/大电影.2019.1080p.zh.srt   （或 .zh.ass / .zh.sup）

安卓 TV 端播放时直接读这个小文件，走现成的字幕覆盖层，于是**高性能直出**模式下也能看字幕：
视频整条流直接进硬件解码器，字幕只是一份几十 KB 的文本（PGS 是几百 KB 一段的位图），
跟解码无关，不会给 4K 播放增加负担。

## 跟 docker 全功能版的关系

`OmniPlay-docker` 是完整的媒体服务器：刮削、海报、用户、Web 前端、播放接口、字幕缓存，
镜像里要装 .NET 运行时、Node 构建产物、字体、VAAPI 驱动。

这一份只保留「扫库 -> 抽字幕 -> 写回」这一条链路，页面也只有一张任务表：

| | OmniPlay-docker | OmniPlay-字幕转档 |
| --- | --- | --- |
| 刮削 / 海报 / 媒体库管理 | 有 | 无 |
| 抽内封字幕 | 有，但写进自己的缓存目录 | 有，写回视频旁边 |
| 字幕任务中心 | 有 | 有（这一份就是全部） |
| 运行时 | .NET 10 + Node 构建 | Python 3 标准库 + ffmpeg |
| 镜像体积 | 数百 MB | 约 90 MB |

两边可以同时跑：全功能版管刮削和播放接口，这一份专门把字幕落成文件。
产物文件名跟安卓 TV 端 v1.34 的约定一致（`片名.语言.格式`，语言码同样把 `chi`/`zho` 收成 `zh`），
谁写的都一样。

## 目录里有什么

    sidecar.py        服务本体，只用 Python 3 标准库 + ffmpeg / ffprobe
    bluray.py         蓝光盘读取：MPLS 选正片、PMT 认位图字幕、按 TS 包抽 PGS
    web/index.html    自带的管理页面，无框架、无外链、无构建步骤
    Dockerfile        alpine + python3 + ffmpeg（另装 libbluray / 7z / xorriso 兜底 ISO）
    Dockerfile.noapk  备用镜像：基镜自带 python3，构建不联网，ffmpeg 从外面挂
    compose.yml       部署模板（媒体库这一份挂载必须可写）
    run-local.sh      不开容器，本机直接跑
    selftest.py       离线自检：造几个真片源跑一遍，核对产物和接口
    synology/         群晖套件（SPK）版：info/conf/脚本、默认配置、打包和本机空跑脚本

## 部署

    mkdir -p state          # 进度表放这儿，DSM 上的 docker 不会替你建这个目录
    docker compose up -d --build
    # 浏览器打开 http://NAS地址:45733/

漏建的话会报 `Bind mount failed: '.../state' does not exist`，建好再 up 就行。

`compose.yml` 里最关键的一行是媒体库挂载**不能带 `:ro`**——转出来的字幕要写回原目录。
只读挂载也能起服务，但每条任务都会记成「媒体源是只读挂载，写不进去」。

### 构建时拉不到 apk 包

日志里有 `ERROR: unable to select packages`（后面跟着 `ffmpeg (no such package)` 那三条）
就是构建容器抓不到 apk 源。Dockerfile 会自己挨个试：`ALPINE_MIRROR` 传进来的、阿里云、
清华、中科大、官方 CDN，第一个通用哪个。它是**覆写** `/etc/apk/repositories` 而不是替换
字符串——从国内加速站拉下来的 alpine 基镜里这个文件往往已经被改成站点自己的源了，
照字符串换会一声不响地什么也没换。

（仓库路径取的是 alpine 的分支号，`VERSION_ID` 是 `3.20.10` 也照样拼 `v3.20`，apk 仓库只按
分支发。）五个源全失败，按这个顺序往下试：

1. 确认构建走的是宿主机网络。`compose.yml` 里 `build.network: host` 已经打开（群晖默认
   `ip_forward=0`，bridge 里的构建容器出不去，症状就是五个源齐刷刷 5 秒报
   `temporary error`）。手动构建同理：

       sudo docker build --network=host -t omniplay-sidecar:latest .
       sudo docker compose up -d

   顺手确认一下：`cat /proc/sys/net/ipv4/ip_forward` 读回 0 就是这台机器的转发没开。

2. 确认宿主机自己通不通。两条都回 `HTTP/... 200` 就说明问题在容器网络，不在源：

       curl -sSI -m 10 https://mirrors.aliyun.com/alpine/v3.20/main/x86_64/APKINDEX.tar.gz | head -1
       curl -sSI -m 10 https://mirrors.tuna.tsinghua.edu.cn/alpine/v3.20/main/x86_64/APKINDEX.tar.gz | head -1

3. 宿主机也不通，就换 `Dockerfile.noapk`。基镜自带 python3，构建过程一次外部请求都不发，
   ffmpeg 从群晖套件目录挂进来：compose 里把 `build.dockerfile` 改掉、解开那行挂载，再
   `sudo docker build -f Dockerfile.noapk -t omniplay-sidecar:latest .`。

4. 再不行就不折腾构建了：换台机器 build 完 `docker save` 导进来，或者直接装下面那个群晖套件。

想跑一次就退出（丢给 cron 或 systemd timer 用）：

    python3 sidecar.py --roots /volume1/media --db /volume1/homes/sidecar/state.db --once

## 群晖套件版

不想在 NAS 上跑容器，就装成群晖套件：套件中心 -> 手动安装 -> 选
`dist/OmniPlaySidecar_1.0-1_noarch.spk`（套件来源要选「所有发行者」）。
装完先改配置再启动：

    sudo vi /var/packages/OmniPlaySidecar/var/sidecar.conf     # 至少把 SIDECAR_ROOTS 改对
    sudo synopkg start OmniPlaySidecar
    # 浏览器打开 http://群晖地址:45733/

套件不自带 ffmpeg：DSM 本身没这个可执行文件。装了社区包 ffmpeg8（或 ffmpeg6 / ffmpeg4）
会自动找到，装的是别的路径就在配置里写 `SIDECAR_FFMPEG`。没找到 ffmpeg 的时候服务照样起、
页面照样开，只是每条任务记一行失败，装好之后重启套件就会开始跑。
套件以 root 运行，否则写不进媒体库目录。

- 程序在 `/var/packages/OmniPlaySidecar/target`，配置、进度表、日志都在 `.../var` 下
  （`sidecar.conf` / `state.db` / `sidecar.log`）。
- 升级只换 `target`，配置和已完成的进度都留着；卸载只删自己的状态表，
  写进媒体库的那些 `.zh.srt` 一律不动。
- 改完代码重新打包：`./synology/build-spk.sh`，版本号读自 `sidecar.py` 里的 `VERSION`，
  产物在 `dist/`。
- 想在电脑上先验一遍脚本：`./synology/dryrun.sh /路径/媒体库`，它按群晖的目录结构把
  「装 -> 起 -> 看页面 -> 停」走一遍，不用等机器。
- 说明白：包结构照 DSM7 的规范写（`info` + `conf` 的 systemd 单元 + `scripts/` 三件套），
  脚本链路在电脑上跑通了，但没在真 NAS 上装过，DSM 的安装向导和权限细节要在机器上过一遍才算数。
  装不上就退回上面 Docker 那一节。

## 配置

命令行参数和环境变量等价，环境变量名就是参数名大写加 `SIDECAR_` 前缀。

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| `--roots` | `/media` | 媒体库根目录，多个用逗号或分号分隔 |
| `--db` | `/var/lib/sidecar/state.db` | 状态表，记进度、失败原因、重试次数 |
| `--langs` | `zh` | 希望转的字幕语言，按优先级排；命中第一条就只转这一条 |
| `--fallback` | `first` | 没命中偏好语言时：`first` 转默认轨/第一条，`none` 不转 |
| `--port` / `--host` | `45733` / `0.0.0.0` | 管理页面和接口 |
| `--token` | 空 | 设了以后所有接口都要带 `Authorization: Bearer` |
| `--interval` | `900` | 空闲重扫间隔（秒），`0` 表示只在启动时扫一次 |
| `--sleep` | `2` | 两条任务之间的间歇，别把 NAS 占满 |
| `--nice` | `10` | ffmpeg 的进程优先级 |
| `--max-out-mb` | `512` | 产物超过这个大小就丢掉这条 |
| `--min-height` | `0` | 低于这个高度的视频不转（`1080` = 只转 4K），`0` = 全都转 |
| `--min-free-gb` | `500` | 媒体库所在盘剩余低于这个容量就停手，`0` = 不限制 |
| `--autostart` | 关 | 容器一起来就开始扫和转；默认是先停着，等页面上点「开始转档」 |
| `--once` | 关 | 扫一遍、把队列跑干净就退出 |
| `--no-serve` | 关 | 不起 HTTP，只在后台跑队列 |
| `--ffmpeg` / `--ffprobe` | 找 PATH | 指定可执行文件 |

## 接口

页面用的就是这几个接口，直接 curl 也一样。带令牌时加 `-H "Authorization: Bearer 令牌"`。

    GET  /                      管理页面
    GET  /health                版本、挂载与磁盘剩余、libbluray、各状态计数、当前策略
    GET  /api/jobs              任务列表：`page` / `per_page` / `status` / `q` / `res`
                                （`res` = `uhd` `fhd` `sdp` `unknown`）
    GET  /api/status?file=相对路径      单个文件的结论，外加这个文件里有哪些字幕轨
    GET  /api/tree?path=相对目录&q=关键字  文件夹视图：列这一层的子目录和影片，
                                每个影片带上它旁边已经写好的字幕和转档状态
    POST /api/jobs   {"files":["Movies/A.mkv"]}   点名立刻转，路径可绝对可相对
    POST /api/scan   马上重扫一遍媒体库（暂停状态只排队，不开工）；带
                                {"path":"剧集/某剧"} 只扫这一层往下
    POST /api/folder/run {"path":"剧集/某剧"}  把这个目录下没转的和转失败的全推进队列
    POST /api/run    {"action":"start"} / {"action":"pause"}   开始 / 暂停
    POST /api/policy {"min_height":2160,"min_free_gb":20}   改策略，存进状态表

## 转档策略

- **只转一条**：一个视频在一次任务里只挑一条字幕——按 `--langs` 的顺序取第一条命中的，
  都没命中就取容器标了默认的轨（`--fallback first`）。国/粤都被归一成 `zh`，输出路径会撞车，
  所以同一语言只留第一条。
- **主动扫库认这些**：`.mkv` / `.webm` / `.mp4` / `.m4v` 单文件，**蓝光文件夹**（下面有
  `BDMV/` 的整盘算一条任务，不再往 `BDMV/STREAM` 里钻），以及 `.iso` / `.bin` 镜像。
  散落的 `.m2ts` / `.ts` 还是只认 TV 端点名，避免把整盘的分卷一条条码进队列。
- **蓝光盘整盘一条任务**：正片由 `BDMV/PLAYLIST` 里最长的那条播放列表决定，字幕取
  「MPLS 声明的语言 ∩ PMT 实际有的 PID」的交集。电视端进库的是 `BDMV/STREAM/NNNNN.m2ts`，
  它只在自己的目录里找外挂字幕，所以产物必须贴着分卷写：`00001.zh.sup`、`00002.zh.sup`。
  正片跨几个分卷就写几份，进度按「写好的分卷 / 总分卷」算；某个分卷本来就没有位图字幕，
  不会硬写一个空文件充数。
- **已有外挂字幕就跳过**：同目录下已经有 `片名.任意语言.srt|ass|sup`（不管是这份服务写的、
  TV 端写的还是人手放的），就不再动它。这一眼判断只用目录列举，不读文件内容。
- **文件没变就不重做**：状态表记了每条的大小和 mtime，重扫时先看这个；换片子、重命名会重做。
- **写回是原子的**：先写 `.名字.part` 再 `rename`，中途被打断不会留下半个字幕糊在播放器里。
- **格式**：`subrip`/`srt`/`webvtt`/`mov_text` 收成 `.srt`，`ass`/`ssa` 收成 `.ass`
  （带样式头，字体、描边、位置都不丢），`hdmv_pgs_subtitle` 原样封成 `.sup`。
  DVB / DVD 字幕这类位图轨判成「转不了」——它们要 OCR，这份服务不做 OCR。

## 和安卓 TV 端怎么配合

两边写的是同一套文件名（`片名.语言.srt` / `.ass` / `.sup`，语言码归一规则也一样），
所以谁写的都一样，电视端不需要知道这台服务的存在：

1. 这台服务在后台扫库、抽字幕、写回媒体库目录，用的都是媒体库账号自己的权限。
2. 电视端播放或后台转档时会先在同目录找一遍现成的 sidecar，找到就直接登记进字幕菜单，
   并且不再自己重抽一遍（省的就是那一次全文件读）。这一步是 1.35 加的。
3. 蓝光 ISO 与 `.m2ts` 分卷只有电视端知道哪条是正片，所以这两种容器一直由电视端
   自己抽；这台服务对它们要么不主动扫，要么要求 ffmpeg 带 libbluray。

想让电视端把片源点名交给这台服务，就 `POST /api/jobs`（带 `Authorization: Bearer`）。
接口是现成的，电视端目前还没接这个开关——ISO 那部分留在本机跑，够用。

## 页面怎么用

页面顶部有「转档队列 / 文件夹」两个页签。队列是扁平的任务表（按状态筛、翻页），**文件夹**
那一侧照媒体库本来的样子走：一层一层点进去，每个影片一行，右边直接列出它旁边已经写好的
字幕（语言、格式、大小）和这条的转档状态，行尾一个「转 / 重转」。点进蓝光盘会看到
`BDMV/STREAM` 里的分卷，整盘的进度挂在页面底部一条，不会被拆成一堆看不懂的任务。
右上角「扫描这一层」只扫当前目录往下，「转这一层」把这个目录下没转的和转失败的全推进队列。

- 容器起来默认是**停着**的：不扫也不转，页面上点「开始转档」才动手（点下去顺手就扫一遍，
  不用等下一个间隔）。「暂停」随时能按，队列原样留着，之后「继续转档」接着跑。想让它一起来
  就干活，起服务时带 `--autostart`，或者在容器环境里加 `SIDECAR_AUTOSTART=1`。
  `--once` 那种跑一遍就退的用法不受这个闸门管。
- 控制条在列表上方：运行状态、开始 / 暂停、只转多大分辨率、剩余空间低于多少 GB 停手
  （数字随便填，默认 500）。状态灯有三种——正在跑（绿）、已暂停（黄）、空间不够（红），
  下面每个媒体库一张磁盘水位条，跌破阈值整块变红并给一行提示，腾出空间后自己继续。
  扫库进行中会写「正在扫描媒体库…」（接口里是 `scanning`），大库等起来心里有数。
- 顶上那排计数格就是筛选项：点「转不了」列表只留转不了的，点「跳过」只留跳过的，
  「全部」回到整表。一格对一个状态，格子里的数字和点出来的条数一定一致。
- 列表按更新时间倒序，翻页、每页条数、状态、分辨率、文件名都在服务端筛，
  几万条也不会一次全拉进浏览器。筛完是空的会说「这一类下头暂时没有任务」，
  不会让你以为是整个库空了。
- 「跳过」这一类有两种原因，行里的状态条会写清楚：`已有字幕` 是原目录旁边已经躺着
  一条外挂字幕，`按策略不转` 是分辨率没到阈值（比如 1080p 交给兼容模式）。
- 「只转 1080p 及以上」这类阈值只对还没干过活的片源生效：已经转好的不动，
  之前按策略跳过的那批在下次扫描时重新判定。改过的策略存在状态表里，重启不丢。
- 正在填的那两个框不会被 3 秒一次的刷新覆盖（否则数字刚敲上去就被打回已保存的值）；
  改过还没保存时「保存策略」会亮成「保存策略（未保存）」，回车等同于点它。

## 自检

    python3 selftest.py        # 需要本机有 ffmpeg，会临时起两个 HTTP 服务

覆盖：文本轨抽成 SRT 且时间轴正确、已有外挂字幕跳过、没字幕判成转不了、只读目录报只读、
重复扫描不重做、`/health` 与页面与任务接口、路径越界被挡、令牌鉴权。蓝光盘那一段现场造
一张结构齐全的小盘（`index.bdmv` + MPLS + 带 PAT/PMT/PGS 的 `.m2ts`），验「整盘只排一条
任务」「字幕贴着分卷写成 `00001.zh.sup`」「解出来的段数对得上」「跨分卷各按自己的 PMT
取字幕」「没字幕的分卷不硬写空文件」「盘里不留下临时文件」；文件夹视图那一段验
`/api/tree` 的目录/影片/字幕三样、整盘进度、`q=` 搜索和 `../..` 越界。
PGS 那一条**本机造不出真片**（手搓的 TS 里 ffmpeg 不认那条轨），自检会打一行「跳过」——
拿真蓝光的 `.m2ts` 在 NAS 上验一次。

## 已知边界

- **ISO**：ffmpeg 要带 libbluray 才读得了蓝光镜像，`GET /health` 的 `bluray` 字段就是结论。
  没有 libbluray 时退一步用机器上的 `isoinfo` / `xorriso` / `7z` / `bsdtar` 把 `BDMV` 结构和
  播放列表点名的那几个分卷拆到镜像旁边的临时目录，再走蓝光盘那套（跑完删掉，`isoTool`
  字段会写用的是哪个）。两个都没有就记「不支持」，不会报错。镜像里的字幕文件名只能贴着
  镜像写（`片名.zh.sup`），电视端要不要用它取决于它进库的是镜像本身还是里面的分卷。
- **PGS 不做 OCR**：只把位图轨原样搬到 `.sup`，由电视端的覆盖层渲染。
- **蓝光盘的 `.sup` 方言**：盘里 `.m2ts` 的每条 PES 只装一个 PGS 段，净荷里没有 `PG` 同步字，
  写 `.sup` 时要按蓝光方言补上 `PG` + 段长(2) + 类型(1) + PTS(8)；ffmpeg dump 出来的那种
  （`PG` + PTS(4) + PTS(4) + 类型 + 段长）原样搬，别补两遍。
- **多分卷的字幕 PID**：每个 clip 有自己的 PMT，同一轨字幕在第一个分卷是 4700、在后面的
  分卷可能是 4701。所以抽每个分卷前都按它自己的 PMT 再认一次，取的是盘上选定那条的**位次**。
- **产物大小**：一条 4K 双语 PGS 轨全片能有 100~300 MB，超过 `--max-out-mb` 会整条丢掉。
  电视端读 `.sup` 也有一份 64 MB 的上限，双语 PGS 想稳，优先转文本轨那一条。
