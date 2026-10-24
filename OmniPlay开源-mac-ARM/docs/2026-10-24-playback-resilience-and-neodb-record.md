# 播放韧性与 NeoDB 记录（1.9 基准记录）

日期：2026-10-24　范围：mac ARM（功能基准）

本轮围绕「看得顺眼、退得干净、切得回来、记得住」四件事，改动集中在主题落点、SMB 媒体源、蓝光包呈现、数据库并发与播放器生命周期。x64 镜像见 [x64 同步记录](../../OmniPlay开源-mac-x64/2026-10-24-sync-with-arm-1.9.md)。

## 1. 主题与玻璃落点

- 「主题风格 / 主题颜色」两维保留，配色重画一套；各页面与弹窗的渐变底统一改为「左浅右深、上浅下深」，并全部跟随当前配色。
- 首页「继续播放」「所有影视」标题与海报左边缘对齐；液态玻璃下这两处标题加玻璃条，且玻璃条最左边与最左侧海报的左边线对齐。
- 海报下的年份居中，已看/未看按钮左移贴近年份。
- 播放页：左上角片名不加玻璃条；播放按钮玻璃条改固定版式高度（图标 56pt + 上下 3pt）并填主题色，与详情页按钮一致；去掉磨砂；控制条整体上移避开字幕；音轨/字幕菜单随控制条一起 3 秒自动收起。

## 2. NeoDB 改叫「记录」

- 详情页按钮固定叫「记录」，不随播放进度改名；弹窗「完成」改成与已播/未看同款胶囊。
- 看完自动补「已看」并同步；弹窗评论框加高约 4 倍。
- 新增 `mediaMark.remoteUrl` 列（迁移 v13，`healSchemaGaps` 自动补列），弹窗状态条右侧新增「NeoDB网页」按钮直接跳条目页；推送与导入时都会记下这个地址。
- 新增 `MediaMark.hasPendingLocalChange`：界面只在真的存过又没同步时才提示「有改动还没同步」，从没落库的条目不再误报。
- 保存后的提示改成本次真实结果（`AfterEditSyncOutcome`）；遇到另一轮同步在跑会等它结束再补推，不再出现「推送完成但还是脏」。
- 设置页：「外观与主题」改名「主题」并删掉说明文字；TMDB 网关与 API 输入框左对齐、去掉冗余说明与图标、API 默认打码并加显示/隐藏开关；「NeoDB同步」改名「NeoDB记录」，账户与令牌输入框统一左对齐，浏览器授权按钮改名「保存授权码」。

## 3. SMB 媒体源

- 新增 `SMBSourceManager`：直连 `mount_smbfs`，共享枚举走 `smbclient`，挂载点用 `kNetFSMountURLSync`，添加与挂载流程与 WebDAV 一致（先连接、再挑共享、按共享建源），并接入自动扫描。
- 手动添加入口并入「局域网媒体源」，SMB 排在 WebDAV 之后；「已挂载媒体源」的操作按钮统一成检测按钮那种填充样式，开启/关闭移到检测前面。

## 4. 蓝光包只算一个版本

`MediaNameParser.bluRayPackageKey(relativePath:)` 以 `…/BDMV/STREAM/…` 的包目录为键，详情页把一个光盘的多个 `*.m2ts` 合成一行（副标题标注「蓝光 N 段」），进度按包内各文件合并计算。多段连播逻辑不变。

## 5. 数据库与退出

- `DatabaseQueue` 换成 `DatabasePool`（读不阻塞写），并把若干 `DispatchQueue.global(qos: .background)` 提到 `.userInitiated`，消掉 GRDB 连接上的优先级反转告警。
- ⌘Q 退出时播放进度改走同步写接口在当前调用栈内落盘，不再交给协程；iCloud 换库前后补 WAL 清理与 checkpoint。

## 6. 全屏与尺寸切换

mpv 在这条「裸 CAMetalLayer、无系统窗口」的嵌入路径上只在视频输出创建时读一次图层尺寸，所以窗口尺寸变化要真正销毁重建视频输出。本轮补上：重建前后暂停播放，等输出尺寸连续两次对上图层再自动续播；重建过的补一次原地精确 `seek`，把音视频管线一起冲干净，避免续播后音画错位。所有等待都在控制队列上异步进行，不再 `Thread.sleep` 堵主线程。

## 7. 起播提速

播放页不再等 CAMetalLayer 绑定完才下发 `loadfile`：图层未就绪时先以 `vid=no` 打开，容器探测与音频缓冲跟建窗口并行，`setDrawable` 成功后补回视频轨。实测（NAS 探针）mpv 打开 20GB 的 `*.m2ts` 含断点 seek 约 0.45s、SMB 上的 `bd://` 冷读约 1s，这一段现在与建窗口重叠。另加两条计时日志：`start playback … 准备耗时=`（准备阶段）与 `起播计时：loadfile→拿到时长=`（引擎阶段）。

## 8. 意外退出：CoreAudio 设备热插拔崩溃

现象：六份崩溃报告（10-21 至 10-24，含用户提供的 `意外退出日志.rtf`）签名完全一致——`EXC_BAD_ACCESS / SIGSEGV`、`KERN_INVALID_ADDRESS at 0x0`，崩溃线程是 `HALC_ProxyNotification Call Listener Queue`，栈为 `mp_msg_va ← mp_msg ← hotplug_cb ← HALObject::PropertiesChanged ← HALSystem::ObjectsPublishedAndDied`。崩溃当下 mpv 主线程停在 `mp_idle`，音频输出早就不在了。

原因在 libmpv 的 `ao_coreaudio`：它把 `hotplug_cb` 连同一个 `struct ao *` 上下文注册到 CoreAudio 系统对象的「设备列表变化」「默认输出变化」两个属性监听上，而 `init()` 和 `hotplug_init()` 各自注册一次、`uninit()` 与 `hotplug_uninit()` 各只减一次计数，计数器一失衡监听就留在系统里；再加上 macOS 把这类通知 `dispatch async` 到 HAL 自己的队列（用户日志第 9 帧就是 `HALC_ProxyNotifications::_SendPropertiesChanged` 里的 async），音频输出拆完之后排队的回调照样会跑，于是拿着已释放的 `ao` 去读 `ao->log`，直接解空指针。上游 master 目前没有修。

复现：`/tmp/mpvprobe` 里的独立 libmpv 探针，按现在的选项起播 → `stop` → 用 `AudioHardwareCreateAggregateDevice` 造一个聚合设备再销毁，制造一次真实的设备发布/消失事件，必现 SIGSEGV，逐帧对得上。

处置：mpv 选项改为 `ao=avfoundation,coreaudio`，正常走 AVFoundation 输出，coreaudio 只留作兜底。同一探针的对照矩阵：coreaudio 参与的三种配置（不设 `ao`、`ao=coreaudio`、钉死 `audio-device=coreaudio/…`）无论播放中还是 `stop` 之后都会被设备事件打崩；强制 avfoundation 后 12 轮设备事件（其中 12 次顺带切换系统默认输出）全部存活，播放位置正常推进。x64 版同步了同一行改动。

## 9. x64 首页滚动卡顿、回弹

x64（Intel）机器首页上下滑不跟手还会回弹，四处叠加造成：

- 海报玻璃垫在 macOS 26 之前只能拿背景模糊顶，一屏十几张各糊一次；Intel 又没有系统折射，等于每帧重做十几次 backdrop blur。现在重复元件在拿不到系统折射的系统上直接走平涂染色底（`AppGlassStyle.nativeRefractionAvailable`），玻璃观感靠染色与描边维持。
- 滚动相位只在 macOS 15+ 有回调，macOS 14 整套降级完全不生效。补了 AppKit 兜底：盯最外层 `NSScrollView` 的 contentView 偏移变化，同样能触发降级并抑制 hover 抖动。
- 相位回落原先立刻翻转，滚轮每轻推一下都报一次 idle，整墙跟着反复重排。改成停下 0.25s 再回落。
- `displayedMovies` 与 `categoryFolderGroups` 原先是计算属性，body 每求值一次就全库 filter + `localizedStandardCompare` 重排（实测 325 条标题按名称排一次 1.2ms，分类视图还要再分桶+排序一次），而滚动相位翻转就会重估 body。改成输入不变就复用（`rebuildWallCaches()`，在媒体库载入、搜索词、排序方式与方向、分类模式、分类排序方向、库条数变化处重算）。
- 每张海报的投影是一次离屏模糊，滚动中先撤掉，手停下再回来。

## 10. 验证

- `xcodebuild -configuration Debug test -only-testing:OmniPlayTests` 全绿（新增 `hasPendingLocalChange`、`remoteUrl` 落库与 `itemPageURL` 三个用例）。
- 独立 libmpv 探针实测起播与 seek 行为，见上面第 6、7 节数据。
- 热插拔崩溃用同一套探针复现并验证修复，见第 8 节。
