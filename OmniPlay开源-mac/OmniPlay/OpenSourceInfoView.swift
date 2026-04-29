import SwiftUI

struct OpenSourceSummaryRow: Identifiable {
    let id: String
    let title: String
    let value: String

    init(title: String, value: String) {
        self.id = title
        self.title = title
        self.value = value
    }
}

struct OpenSourceComponentNotice: Identifiable {
    let id: String
    let name: String
    let version: String
    let license: String
    let role: String
    let integration: String
    let sourceURL: URL
    let note: String

    init(
        name: String,
        version: String,
        license: String,
        role: String,
        integration: String,
        sourceURL: URL,
        note: String
    ) {
        self.id = name
        self.name = name
        self.version = version
        self.license = license
        self.role = role
        self.integration = integration
        self.sourceURL = sourceURL
        self.note = note
    }
}

struct OpenSourceLicenseDocument: Identifiable, Hashable {
    let id: String
    let title: String
    let summary: String
    let bundleResourceName: String
    let bundleExtension: String
    let sourceDescription: String
    let sourceURL: URL

    init(
        id: String,
        title: String,
        summary: String,
        bundleResourceName: String,
        bundleExtension: String = "txt",
        sourceDescription: String,
        sourceURL: URL
    ) {
        self.id = id
        self.title = title
        self.summary = summary
        self.bundleResourceName = bundleResourceName
        self.bundleExtension = bundleExtension
        self.sourceDescription = sourceDescription
        self.sourceURL = sourceURL
    }
}

struct OpenSourceDependencyNotice: Identifiable {
    let id: String
    let category: String
    let name: String
    let version: String
    let license: String
    let role: String
    let linkedThrough: String
    let buildSourceURL: URL
    let upstreamURL: URL
    let note: String
    let licenseDocumentIDs: [String]

    init(
        category: String,
        name: String,
        version: String,
        license: String,
        role: String,
        linkedThrough: String,
        buildSourceURL: URL,
        upstreamURL: URL,
        note: String,
        licenseDocumentIDs: [String] = []
    ) {
        self.id = name
        self.category = category
        self.name = name
        self.version = version
        self.license = license
        self.role = role
        self.linkedThrough = linkedThrough
        self.buildSourceURL = buildSourceURL
        self.upstreamURL = upstreamURL
        self.note = note
        self.licenseDocumentIDs = licenseDocumentIDs
    }
}

enum OpenSourceCatalog {
    static let summaryRows: [OpenSourceSummaryRow] = [
        OpenSourceSummaryRow(title: "播放器封装", value: "MPVKit-GPL"),
        OpenSourceSummaryRow(title: "播放内核", value: "libmpv"),
        OpenSourceSummaryRow(title: "媒体解码", value: "FFmpeg / Libav"),
        OpenSourceSummaryRow(title: "视频输出", value: "gpu-next / Metal"),
        OpenSourceSummaryRow(title: "硬件解码", value: "VideoToolbox"),
        OpenSourceSummaryRow(title: "数据库", value: "GRDB / SQLite")
    ]

    static let components: [OpenSourceComponentNotice] = [
        OpenSourceComponentNotice(
            name: "MPVKit-GPL",
            version: "SwiftPM 远程依赖，当前工程固定到 revision 613c0ccc",
            license: "包级 LICENSE 为 LGPL v3.0；当前工程链接 MPVKit-GPL 产品后按 GPL 栈处理",
            role: "Swift 层播放器封装，负责桥接 libmpv 与预编译媒体框架。",
            integration: "当前工程直接链接的产品名就是 MPVKit-GPL，MPVKit 的 Package.swift 同时区分了 LGPL 的 MPVKit 和 GPL 的 MPVKit-GPL 两个产品。",
            sourceURL: URL(string: "https://github.com/mpvkit/MPVKit")!,
            note: "当前页已内置 MPVKit 包级 LGPL v3.0 正文，同时另外内置 GNU GPL v3.0 供 MPVKit-GPL / FFmpeg-GPL 产品链参考。"
        ),
        OpenSourceComponentNotice(
            name: "libmpv",
            version: "随工程附带 Libmpv.xcframework，当前构建未内嵌更细的版本号",
            license: "当前通过 MPVKit-GPL 产品链接入，分发上按 GPL 栈风险处理",
            role: "播放核心，负责加载媒体、轨道选择、渲染链路和播放控制。",
            integration: "由 MPVKit 驱动，当前初始化配置为 vo=gpu-next、gpu-api=metal。",
            sourceURL: URL(string: "https://github.com/mpv-player/mpv")!,
            note: "如果后续要迁移到 LGPL-only 方案，需要重新确认编译参数和依赖组合，而不是只改界面说明。"
        ),
        OpenSourceComponentNotice(
            name: "FFmpeg / Libav",
            version: "随工程附带 Libavcodec / format / filter / util / swresample / swscale / device.xcframework",
            license: "当前产品链对应 _FFmpeg-GPL；本仓库迁移文档另外标记现有构建存在 GPL + nonfree 风险",
            role: "负责解封装、解码、滤镜、音视频处理和部分网络 I/O。",
            integration: "作为 libmpv 的底层媒体栈随当前工程一并存在。",
            sourceURL: URL(string: "https://ffmpeg.org")!,
            note: "此页只展示当前使用情况，不替代对应源码、构建脚本和许可证随附要求。"
        ),
        OpenSourceComponentNotice(
            name: "GRDB",
            version: "本地 Swift Package，源码已随工程附带",
            license: "MIT",
            role: "应用数据库访问层，负责持久化媒体库、播放记录和本地元数据。",
            integration: "当前工程通过本地 Swift Package 链接 GRDB。",
            sourceURL: URL(string: "https://github.com/groue/GRDB.swift")!,
            note: "MIT 相对宽松，但分发时仍应保留原版权声明和许可文本。"
        ),
        OpenSourceComponentNotice(
            name: "SQLite",
            version: "由系统库提供",
            license: "Public Domain",
            role: "底层数据库引擎，由 GRDB 进行封装和访问。",
            integration: "通常随 macOS 系统提供，不单独作为 App 私有框架打包。",
            sourceURL: URL(string: "https://www.sqlite.org")!,
            note: "这里单独列出是为了让数据库栈更完整，便于用户确认实际采用的底层引擎。"
        )
    ]

    static let licenseDocuments: [OpenSourceLicenseDocument] = [
        OpenSourceLicenseDocument(
            id: "gpl3",
            title: "GNU GPL v3.0",
            summary: "用于当前 MPVKit-GPL / FFmpeg-GPL 产品链的主要许可证正文。",
            bundleResourceName: "GNU-GPL-3.0",
            sourceDescription: "来自 GNU 官方许可证文本。",
            sourceURL: URL(string: "https://www.gnu.org/licenses/gpl-3.0.txt")!
        ),
        OpenSourceLicenseDocument(
            id: "lgpl3",
            title: "GNU LGPL v3.0",
            summary: "MPVKit 包级 LICENSE 原文；用于说明 MPVKit 源码与非 GPL 产品时的基础许可证。",
            bundleResourceName: "GNU-LGPL-3.0",
            sourceDescription: "来自当前 Xcode SwiftPM 缓存中的 MPVKit/LICENSE。",
            sourceURL: URL(string: "https://github.com/mpvkit/MPVKit/blob/main/LICENSE")!
        ),
        OpenSourceLicenseDocument(
            id: "lgpl21",
            title: "GNU LGPL v2.1",
            summary: "用于 GnuTLS、FriBidi、libbluray、libplacebo 等依赖常见的 LGPL v2.1 正文。",
            bundleResourceName: "GNU-LGPL-2.1",
            sourceDescription: "来自 GNU 官方旧版许可证文本。",
            sourceURL: URL(string: "https://www.gnu.org/licenses/old-licenses/lgpl-2.1.txt")!
        ),
        OpenSourceLicenseDocument(
            id: "apache2",
            title: "Apache License 2.0",
            summary: "用于 OpenSSL 3.x、MoltenVK、shaderc 等依赖常见的 Apache 2.0 正文。",
            bundleResourceName: "Apache-2.0",
            sourceDescription: "来自 Apache Software Foundation 官方许可证文本。",
            sourceURL: URL(string: "https://www.apache.org/licenses/LICENSE-2.0.txt")!
        ),
        OpenSourceLicenseDocument(
            id: "grdb_mit",
            title: "MIT License (GRDB)",
            summary: "GRDB 本地 Swift Package 附带的 MIT 许可证全文。",
            bundleResourceName: "GRDB-MIT",
            sourceDescription: "来自工程内 GRDB.swift-master/LICENSE。",
            sourceURL: URL(string: "https://github.com/groue/GRDB.swift/blob/master/LICENSE")!
        )
    ]

    static let dependencyCategories: [String] = [
        "播放核心",
        "FFmpeg 组件",
        "字幕与文本",
        "网络与安全",
        "渲染与色彩",
        "补充解码器"
    ]

    static let transitiveDependencies: [OpenSourceDependencyNotice] = [
        OpenSourceDependencyNotice(
            category: "播放核心",
            name: "Libmpv-GPL",
            version: "mpv v0.41.0 / MPVKit 0.41.0 GPL bundle",
            license: "GNU GPL v3.0 product chain",
            role: "实际播放核心，负责媒体加载、播放状态、轨道控制和渲染调度。",
            linkedThrough: "MPVKit-GPL -> _MPVKit-GPL -> Libmpv-GPL",
            buildSourceURL: URL(string: "https://github.com/mpvkit/MPVKit/releases/download/0.41.0/Libmpv-GPL.xcframework.zip")!,
            upstreamURL: URL(string: "https://github.com/mpv-player/mpv")!,
            note: "仓库文档已将当前 libmpv / FFmpeg 组合按 GPL 风险栈处理。",
            licenseDocumentIDs: ["gpl3"]
        ),
        OpenSourceDependencyNotice(
            category: "播放核心",
            name: "Libbluray",
            version: "1.4.0",
            license: "LGPL-2.1-or-later",
            role: "蓝光目录、播放列表和导航支持。",
            linkedThrough: "MPVKit-GPL -> _MPVKit-GPL -> Libbluray",
            buildSourceURL: URL(string: "https://github.com/mpvkit/libbluray-build/releases/download/1.4.0/Libbluray.xcframework.zip")!,
            upstreamURL: URL(string: "https://www.videolan.org/developers/libbluray.html")!,
            note: "用于蓝光路径和菜单/播放列表相关能力。",
            licenseDocumentIDs: ["lgpl21"]
        ),
        OpenSourceDependencyNotice(
            category: "播放核心",
            name: "Libluajit",
            version: "2.1.0-xcode",
            license: "MIT",
            role: "mpv 在 macOS 下的脚本运行时。",
            linkedThrough: "MPVKit-GPL -> _MPVKit-GPL -> Libluajit",
            buildSourceURL: URL(string: "https://github.com/mpvkit/libluajit-build/releases/download/2.1.0-xcode/Libluajit.xcframework.zip")!,
            upstreamURL: URL(string: "https://github.com/LuaJIT/LuaJIT")!,
            note: "当前未内置 LuaJIT 专属正文，可从上游 COPYRIGHT / README 查看。"
        ),
        OpenSourceDependencyNotice(
            category: "FFmpeg 组件",
            name: "Libavcodec-GPL",
            version: "FFmpeg n8.0.1 / MPVKit 0.41.0 GPL bundle",
            license: "GNU GPL v3.0 product chain",
            role: "音视频编解码核心库。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> Libavcodec-GPL",
            buildSourceURL: URL(string: "https://github.com/mpvkit/MPVKit/releases/download/0.41.0/Libavcodec-GPL.xcframework.zip")!,
            upstreamURL: URL(string: "https://github.com/FFmpeg/FFmpeg")!,
            note: "本仓库迁移文档另外指出当前 FFmpeg 构建含 GPL + nonfree 风险。",
            licenseDocumentIDs: ["gpl3"]
        ),
        OpenSourceDependencyNotice(
            category: "FFmpeg 组件",
            name: "Libavdevice-GPL",
            version: "FFmpeg n8.0.1 / MPVKit 0.41.0 GPL bundle",
            license: "GNU GPL v3.0 product chain",
            role: "采集设备和设备层 I/O 支持。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> Libavdevice-GPL",
            buildSourceURL: URL(string: "https://github.com/mpvkit/MPVKit/releases/download/0.41.0/Libavdevice-GPL.xcframework.zip")!,
            upstreamURL: URL(string: "https://github.com/FFmpeg/FFmpeg")!,
            note: "按当前 GPL bundle 链路处理。",
            licenseDocumentIDs: ["gpl3"]
        ),
        OpenSourceDependencyNotice(
            category: "FFmpeg 组件",
            name: "Libavfilter-GPL",
            version: "FFmpeg n8.0.1 / MPVKit 0.41.0 GPL bundle",
            license: "GNU GPL v3.0 product chain",
            role: "音视频滤镜链、转场和处理图元。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> Libavfilter-GPL",
            buildSourceURL: URL(string: "https://github.com/mpvkit/MPVKit/releases/download/0.41.0/Libavfilter-GPL.xcframework.zip")!,
            upstreamURL: URL(string: "https://github.com/FFmpeg/FFmpeg")!,
            note: "按当前 GPL bundle 链路处理。",
            licenseDocumentIDs: ["gpl3"]
        ),
        OpenSourceDependencyNotice(
            category: "FFmpeg 组件",
            name: "Libavformat-GPL",
            version: "FFmpeg n8.0.1 / MPVKit 0.41.0 GPL bundle",
            license: "GNU GPL v3.0 product chain",
            role: "封装、解封装和协议层入口。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> Libavformat-GPL",
            buildSourceURL: URL(string: "https://github.com/mpvkit/MPVKit/releases/download/0.41.0/Libavformat-GPL.xcframework.zip")!,
            upstreamURL: URL(string: "https://github.com/FFmpeg/FFmpeg")!,
            note: "按当前 GPL bundle 链路处理。",
            licenseDocumentIDs: ["gpl3"]
        ),
        OpenSourceDependencyNotice(
            category: "FFmpeg 组件",
            name: "Libavutil-GPL",
            version: "FFmpeg n8.0.1 / MPVKit 0.41.0 GPL bundle",
            license: "GNU GPL v3.0 product chain",
            role: "FFmpeg 公共工具、缓冲区、时间基和像素/采样描述。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> Libavutil-GPL",
            buildSourceURL: URL(string: "https://github.com/mpvkit/MPVKit/releases/download/0.41.0/Libavutil-GPL.xcframework.zip")!,
            upstreamURL: URL(string: "https://github.com/FFmpeg/FFmpeg")!,
            note: "按当前 GPL bundle 链路处理。",
            licenseDocumentIDs: ["gpl3"]
        ),
        OpenSourceDependencyNotice(
            category: "FFmpeg 组件",
            name: "Libswresample-GPL",
            version: "FFmpeg n8.0.1 / MPVKit 0.41.0 GPL bundle",
            license: "GNU GPL v3.0 product chain",
            role: "音频重采样和格式转换。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> Libswresample-GPL",
            buildSourceURL: URL(string: "https://github.com/mpvkit/MPVKit/releases/download/0.41.0/Libswresample-GPL.xcframework.zip")!,
            upstreamURL: URL(string: "https://github.com/FFmpeg/FFmpeg")!,
            note: "按当前 GPL bundle 链路处理。",
            licenseDocumentIDs: ["gpl3"]
        ),
        OpenSourceDependencyNotice(
            category: "FFmpeg 组件",
            name: "Libswscale-GPL",
            version: "FFmpeg n8.0.1 / MPVKit 0.41.0 GPL bundle",
            license: "GNU GPL v3.0 product chain",
            role: "图像缩放、色彩空间和像素格式转换。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> Libswscale-GPL",
            buildSourceURL: URL(string: "https://github.com/mpvkit/MPVKit/releases/download/0.41.0/Libswscale-GPL.xcframework.zip")!,
            upstreamURL: URL(string: "https://github.com/FFmpeg/FFmpeg")!,
            note: "按当前 GPL bundle 链路处理。",
            licenseDocumentIDs: ["gpl3"]
        ),
        OpenSourceDependencyNotice(
            category: "字幕与文本",
            name: "Libass",
            version: "0.17.4",
            license: "ISC",
            role: "ASS / SSA 字幕渲染核心。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> Libass",
            buildSourceURL: URL(string: "https://github.com/mpvkit/libass-build/releases/download/0.17.4/Libass.xcframework.zip")!,
            upstreamURL: URL(string: "https://github.com/libass/libass")!,
            note: "当前未内置 ISC 正文，请以上游仓库 LICENSE 为准。"
        ),
        OpenSourceDependencyNotice(
            category: "字幕与文本",
            name: "Libfreetype",
            version: "0.17.4 build line",
            license: "FreeType License / GPLv2 dual license",
            role: "字幕和字体轮廓栅格化。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> Libfreetype",
            buildSourceURL: URL(string: "https://github.com/mpvkit/libass-build/releases/download/0.17.4/Libfreetype.xcframework.zip")!,
            upstreamURL: URL(string: "https://freetype.org")!,
            note: "当前未内置 FreeType 专属正文，应以上游发布包中的 FTL/GPL 说明为准。"
        ),
        OpenSourceDependencyNotice(
            category: "字幕与文本",
            name: "Libfribidi",
            version: "0.17.4 build line",
            license: "LGPL-2.1-or-later",
            role: "双向文本排版和阿拉伯/希伯来等脚本处理。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> Libfribidi",
            buildSourceURL: URL(string: "https://github.com/mpvkit/libass-build/releases/download/0.17.4/Libfribidi.xcframework.zip")!,
            upstreamURL: URL(string: "https://github.com/fribidi/fribidi")!,
            note: "GitHub 上游项目页面明确标注为 LGPL-2.1 或更高版本。",
            licenseDocumentIDs: ["lgpl21"]
        ),
        OpenSourceDependencyNotice(
            category: "字幕与文本",
            name: "Libharfbuzz",
            version: "0.17.4 build line",
            license: "Old MIT",
            role: "复杂文本 shaping 和字形布局。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> Libharfbuzz",
            buildSourceURL: URL(string: "https://github.com/mpvkit/libass-build/releases/download/0.17.4/Libharfbuzz.xcframework.zip")!,
            upstreamURL: URL(string: "https://github.com/harfbuzz/harfbuzz")!,
            note: "当前未内置 HarfBuzz 专属 Old MIT 正文，请以上游仓库为准。"
        ),
        OpenSourceDependencyNotice(
            category: "字幕与文本",
            name: "Libunibreak",
            version: "0.17.4 build line",
            license: "zlib",
            role: "换行和文本分段规则支持。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> Libunibreak",
            buildSourceURL: URL(string: "https://github.com/mpvkit/libass-build/releases/download/0.17.4/Libunibreak.xcframework.zip")!,
            upstreamURL: URL(string: "https://github.com/adah1972/libunibreak")!,
            note: "当前未内置 zlib 正文，请查看上游仓库或发布包。"
        ),
        OpenSourceDependencyNotice(
            category: "字幕与文本",
            name: "Libuchardet",
            version: "0.0.8-xcode",
            license: "MPL-1.1 OR GPL-2.0-or-later OR LGPL-2.1-or-later",
            role: "文本编码探测，常用于外挂字幕/文本文件编码识别。",
            linkedThrough: "MPVKit-GPL -> _MPVKit-GPL -> Libuchardet",
            buildSourceURL: URL(string: "https://github.com/mpvkit/libuchardet-build/releases/download/0.0.8-xcode/Libuchardet.xcframework.zip")!,
            upstreamURL: URL(string: "https://www.freedesktop.org/wiki/Software/uchardet/")!,
            note: "uchardet 上游公开声明为 MPL/GPL/LGPL 三选一；当前未内置其专属 COPYING。",
            licenseDocumentIDs: ["lgpl21"]
        ),
        OpenSourceDependencyNotice(
            category: "网络与安全",
            name: "Libssl",
            version: "3.3.5",
            license: "Apache-2.0",
            role: "TLS/SSL 协议实现。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> Libssl",
            buildSourceURL: URL(string: "https://github.com/mpvkit/openssl-build/releases/download/3.3.5/Libssl.xcframework.zip")!,
            upstreamURL: URL(string: "https://github.com/openssl/openssl")!,
            note: "OpenSSL 3.x 采用 Apache 2.0 许可证。",
            licenseDocumentIDs: ["apache2"]
        ),
        OpenSourceDependencyNotice(
            category: "网络与安全",
            name: "Libcrypto",
            version: "3.3.5",
            license: "Apache-2.0",
            role: "加密原语、证书和摘要算法实现。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> Libcrypto",
            buildSourceURL: URL(string: "https://github.com/mpvkit/openssl-build/releases/download/3.3.5/Libcrypto.xcframework.zip")!,
            upstreamURL: URL(string: "https://github.com/openssl/openssl")!,
            note: "OpenSSL 3.x 采用 Apache 2.0 许可证。",
            licenseDocumentIDs: ["apache2"]
        ),
        OpenSourceDependencyNotice(
            category: "网络与安全",
            name: "Libsmbclient",
            version: "4.15.13-2512",
            license: "GNU GPL v3.x family",
            role: "SMB/CIFS 协议访问支持。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> Libsmbclient",
            buildSourceURL: URL(string: "https://github.com/mpvkit/libsmbclient-build/releases/download/4.15.13-2512/Libsmbclient.xcframework.zip")!,
            upstreamURL: URL(string: "https://www.samba.org")!,
            note: "MPVKit README 也将启用 Samba 协议列为 GPL 版本的主要区别之一。",
            licenseDocumentIDs: ["gpl3"]
        ),
        OpenSourceDependencyNotice(
            category: "网络与安全",
            name: "gmp",
            version: "3.8.11 build line",
            license: "LGPL-3.0-or-later / GPL-2.0-or-later dual license",
            role: "大整数运算，供 GnuTLS/Nettle 依赖链使用。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> gmp",
            buildSourceURL: URL(string: "https://github.com/mpvkit/gnutls-build/releases/download/3.8.11/gmp.xcframework.zip")!,
            upstreamURL: URL(string: "https://gmplib.org")!,
            note: "当前未单独内置 GMP 专属版权声明，但可参考 LGPL v3 正文。",
            licenseDocumentIDs: ["lgpl3"]
        ),
        OpenSourceDependencyNotice(
            category: "网络与安全",
            name: "nettle",
            version: "3.8.11 build line",
            license: "LGPL-3.0-or-later",
            role: "对称/非对称密码学基础库。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> nettle",
            buildSourceURL: URL(string: "https://github.com/mpvkit/gnutls-build/releases/download/3.8.11/nettle.xcframework.zip")!,
            upstreamURL: URL(string: "https://www.gnutls.org/software/nettle/")!,
            note: "供 GnuTLS 链路使用。",
            licenseDocumentIDs: ["lgpl3"]
        ),
        OpenSourceDependencyNotice(
            category: "网络与安全",
            name: "hogweed",
            version: "3.8.11 build line",
            license: "LGPL-3.0-or-later",
            role: "Nettle 的公钥密码学配套库。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> hogweed",
            buildSourceURL: URL(string: "https://github.com/mpvkit/gnutls-build/releases/download/3.8.11/hogweed.xcframework.zip")!,
            upstreamURL: URL(string: "https://www.gnutls.org/software/nettle/")!,
            note: "与 Nettle 一起构成 GnuTLS 的常见密码学依赖。",
            licenseDocumentIDs: ["lgpl3"]
        ),
        OpenSourceDependencyNotice(
            category: "网络与安全",
            name: "gnutls",
            version: "3.8.11",
            license: "LGPL-2.1-or-later",
            role: "TLS 协议实现，供网络访问能力使用。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> gnutls",
            buildSourceURL: URL(string: "https://github.com/mpvkit/gnutls-build/releases/download/3.8.11/gnutls.xcframework.zip")!,
            upstreamURL: URL(string: "https://www.gnutls.org")!,
            note: "当前已内置 LGPL v2.1 正文以便在 App 内查看。",
            licenseDocumentIDs: ["lgpl21"]
        ),
        OpenSourceDependencyNotice(
            category: "渲染与色彩",
            name: "MoltenVK",
            version: "1.4.1",
            license: "Apache-2.0",
            role: "把 Vulkan 能力映射到 Apple Metal。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> MoltenVK",
            buildSourceURL: URL(string: "https://github.com/mpvkit/moltenvk-build/releases/download/1.4.1/MoltenVK.xcframework.zip")!,
            upstreamURL: URL(string: "https://github.com/KhronosGroup/MoltenVK")!,
            note: "当前播放器配置为 gpu-next + metal，MoltenVK 仍处在该依赖图中。",
            licenseDocumentIDs: ["apache2"]
        ),
        OpenSourceDependencyNotice(
            category: "渲染与色彩",
            name: "Libshaderc_combined",
            version: "2025.5.0",
            license: "Apache-2.0",
            role: "着色器编译和 SPIR-V 工具链聚合库。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> Libshaderc_combined",
            buildSourceURL: URL(string: "https://github.com/mpvkit/libshaderc-build/releases/download/2025.5.0/Libshaderc_combined.xcframework.zip")!,
            upstreamURL: URL(string: "https://github.com/google/shaderc")!,
            note: "通常用于 Vulkan / shader 相关编译路径。",
            licenseDocumentIDs: ["apache2"]
        ),
        OpenSourceDependencyNotice(
            category: "渲染与色彩",
            name: "lcms2",
            version: "2.17.0",
            license: "MIT",
            role: "ICC 颜色管理和色彩转换。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> lcms2",
            buildSourceURL: URL(string: "https://github.com/mpvkit/lcms2-build/releases/download/2.17.0/lcms2.xcframework.zip")!,
            upstreamURL: URL(string: "https://github.com/mm2/Little-CMS")!,
            note: "GitHub 上游仓库页面标注为 MIT 许可证；当前未内置专属正文。"
        ),
        OpenSourceDependencyNotice(
            category: "渲染与色彩",
            name: "Libplacebo",
            version: "7.351.0-2512",
            license: "LGPL-2.1-or-later",
            role: "GPU 视频渲染、色调映射、缩放和色彩处理。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> Libplacebo",
            buildSourceURL: URL(string: "https://github.com/mpvkit/libplacebo-build/releases/download/7.351.0-2512/Libplacebo.xcframework.zip")!,
            upstreamURL: URL(string: "https://github.com/haasn/libplacebo")!,
            note: "上游 README 明确标注为 LGPL v2.1 或更高版本。",
            licenseDocumentIDs: ["lgpl21"]
        ),
        OpenSourceDependencyNotice(
            category: "渲染与色彩",
            name: "Libdovi",
            version: "3.3.2",
            license: "MIT",
            role: "Dolby Vision 元数据解析与相关处理。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> Libdovi",
            buildSourceURL: URL(string: "https://github.com/mpvkit/libdovi-build/releases/download/3.3.2/Libdovi.xcframework.zip")!,
            upstreamURL: URL(string: "https://github.com/quietvoid/dovi_tool")!,
            note: "libdovi 所在上游仓库页面标注为 MIT；当前未内置专属正文。"
        ),
        OpenSourceDependencyNotice(
            category: "补充解码器",
            name: "Libdav1d",
            version: "1.5.2-xcode",
            license: "BSD-2-Clause",
            role: "AV1 软件解码器。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> Libdav1d",
            buildSourceURL: URL(string: "https://github.com/mpvkit/libdav1d-build/releases/download/1.5.2-xcode/Libdav1d.xcframework.zip")!,
            upstreamURL: URL(string: "https://code.videolan.org/videolan/dav1d")!,
            note: "GitHub / VideoLAN 镜像页面标注为 BSD-2-Clause；当前未内置专属正文。"
        ),
        OpenSourceDependencyNotice(
            category: "补充解码器",
            name: "Libuavs3d",
            version: "1.2.1-xcode",
            license: "BSD-3-Clause",
            role: "AVS3 视频解码器。",
            linkedThrough: "MPVKit-GPL -> _FFmpeg-GPL -> Libuavs3d",
            buildSourceURL: URL(string: "https://github.com/mpvkit/libuavs3d-build/releases/download/1.2.1-xcode/Libuavs3d.xcframework.zip")!,
            upstreamURL: URL(string: "https://github.com/uavs3/uavs3d")!,
            note: "公开包管理元数据与上游镜像说明均将 uavs3d 标记为 BSD-3-Clause；当前未内置专属正文。"
        )
    ]

    static var appBuildDescription: String {
        let shortVersion = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.0"
        let buildVersion = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "1"
        return "觅影 \(shortVersion) (\(buildVersion))"
    }

    static let noticeText = "此页用于展示当前构建采用的播放器栈和主要开源组件，方便用户确认播放器内核、数据库和媒体解码链路。当前已内置 GNU GPL v3.0、GNU LGPL v3.0、GNU LGPL v2.1、Apache 2.0 与 GRDB 的 MIT 正文，但它仍然只是信息展示页，不替代各协议要求的源码提供、许可证随附和再分发义务。"

    static func licenseDocument(id: String) -> OpenSourceLicenseDocument? {
        licenseDocuments.first(where: { $0.id == id })
    }

    static func licenseDocuments(for ids: [String]) -> [OpenSourceLicenseDocument] {
        ids.compactMap { id in
            licenseDocuments.first(where: { $0.id == id })
        }
    }

    static func dependencies(in category: String) -> [OpenSourceDependencyNotice] {
        transitiveDependencies.filter { $0.category == category }
    }
}

struct OpenSourceInfoSheet: View {
    @Environment(\.dismiss) private var dismiss

    let theme: AppTheme
    @State private var selectedLicenseDocument: OpenSourceLicenseDocument?
    @State private var selectedDependency: OpenSourceDependencyNotice?

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                VStack(alignment: .leading, spacing: 4) {
                    Text("播放器内核与开源组件")
                        .font(.title2.bold())
                    Text(OpenSourceCatalog.appBuildDescription)
                        .font(.subheadline)
                        .foregroundColor(theme.textSecondary)
                }

                Spacer()

                Button(action: { dismiss() }) {
                    Image(systemName: "xmark.circle.fill")
                        .font(.title2)
                        .foregroundColor(.secondary)
                }
                .buttonStyle(.plain)
            }
            .padding()

            Divider()

            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    OpenSourcePanel(theme: theme) {
                        Text(OpenSourceCatalog.noticeText)
                            .font(.body)
                            .foregroundColor(theme.textPrimary)
                            .fixedSize(horizontal: false, vertical: true)
                    }

                    OpenSourcePanel(title: "当前内核摘要", theme: theme) {
                        VStack(spacing: 10) {
                            ForEach(OpenSourceCatalog.summaryRows) { row in
                                SettingsValueRow(title: row.title, value: row.value)
                            }
                        }
                    }

                    OpenSourcePanel(title: "已内置的许可证全文", theme: theme) {
                        VStack(alignment: .leading, spacing: 12) {
                            Text("当前构建内置了 GNU GPL v3.0、GNU LGPL v3.0、GNU LGPL v2.1、Apache 2.0 和 GRDB 的 MIT 正文，便于在 App 内直接查看。")
                                .font(.caption)
                                .foregroundColor(theme.textSecondary)

                            ForEach(OpenSourceCatalog.licenseDocuments) { document in
                                OpenSourceLicenseDocumentRow(document: document, theme: theme) {
                                    selectedLicenseDocument = document
                                }
                            }
                        }
                    }

                    OpenSourcePanel(title: "按当前 MPVKit-GPL 链路展开的传递依赖", theme: theme) {
                        VStack(alignment: .leading, spacing: 14) {
                            Text("当前 macOS 构建按 MPVKit-GPL 产品链展开后，共整理出 \(OpenSourceCatalog.transitiveDependencies.count) 个直接或间接打包进播放器栈的二级依赖。下面按功能分组，每个条目都可打开单独详情页。")
                                .font(.caption)
                                .foregroundColor(theme.textSecondary)

                            ForEach(OpenSourceCatalog.dependencyCategories, id: \.self) { category in
                                let dependencies = OpenSourceCatalog.dependencies(in: category)
                                if !dependencies.isEmpty {
                                    VStack(alignment: .leading, spacing: 10) {
                                        Text(category)
                                            .font(.subheadline.weight(.semibold))
                                            .foregroundColor(theme.textPrimary)

                                        ForEach(dependencies) { dependency in
                                            OpenSourceDependencyRow(dependency: dependency, theme: theme) {
                                                selectedDependency = dependency
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    ForEach(OpenSourceCatalog.components) { component in
                        OpenSourceComponentCard(component: component, theme: theme)
                    }
                }
                .padding(20)
            }
            .background(theme.background)
        }
        .frame(width: 720, height: 700)
        .background(theme.background)
        .sheet(item: $selectedLicenseDocument) { document in
            OpenSourceLicenseTextSheet(document: document, theme: theme)
        }
        .sheet(item: $selectedDependency) { dependency in
            OpenSourceDependencyDetailSheet(dependency: dependency, theme: theme)
        }
    }
}

private struct OpenSourceComponentCard: View {
    let component: OpenSourceComponentNotice
    let theme: AppTheme

    var body: some View {
        OpenSourcePanel(title: component.name, theme: theme) {
            VStack(alignment: .leading, spacing: 10) {
                SettingsValueRow(title: "许可证", value: component.license)
                SettingsValueRow(title: "版本/来源", value: component.version)
                SettingsValueRow(title: "作用", value: component.role)
                SettingsValueRow(title: "集成方式", value: component.integration)

                Link(destination: component.sourceURL) {
                    Label("上游源码 / 许可证信息", systemImage: "link")
                        .font(.subheadline.weight(.medium))
                }

                Text(component.note)
                    .font(.caption)
                    .foregroundColor(theme.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

private struct OpenSourceDependencyRow: View {
    let dependency: OpenSourceDependencyNotice
    let theme: AppTheme
    let openAction: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                Text(dependency.name)
                    .font(.subheadline.weight(.semibold))
                    .foregroundColor(theme.textPrimary)

                Text("\(dependency.version)  ·  \(dependency.license)")
                    .font(.caption)
                    .foregroundColor(theme.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)

                Text(dependency.linkedThrough)
                    .font(.caption2)
                    .foregroundColor(theme.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Spacer(minLength: 12)

            Button("详情", action: openAction)
                .buttonStyle(.bordered)
                .tint(theme.accent)
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .fill(theme.background)
        )
        .overlay(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .stroke(theme.accent.opacity(0.08), lineWidth: 1)
        )
    }
}

private struct OpenSourceLicenseDocumentRow: View {
    let document: OpenSourceLicenseDocument
    let theme: AppTheme
    let openAction: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .top) {
                VStack(alignment: .leading, spacing: 4) {
                    Text(document.title)
                        .font(.subheadline.weight(.semibold))
                        .foregroundColor(theme.textPrimary)
                    Text(document.summary)
                        .font(.caption)
                        .foregroundColor(theme.textSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                }

                Spacer(minLength: 12)

                Button("查看全文", action: openAction)
                    .buttonStyle(.bordered)
                    .tint(theme.accent)
            }

            Link(destination: document.sourceURL) {
                Label(document.sourceDescription, systemImage: "link")
                    .font(.caption)
            }
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .fill(theme.background)
        )
        .overlay(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .stroke(theme.accent.opacity(0.08), lineWidth: 1)
        )
    }
}

private struct OpenSourceDependencyDetailSheet: View {
    @Environment(\.dismiss) private var dismiss

    let dependency: OpenSourceDependencyNotice
    let theme: AppTheme

    @State private var selectedLicenseDocument: OpenSourceLicenseDocument?

    private var bundledDocuments: [OpenSourceLicenseDocument] {
        OpenSourceCatalog.licenseDocuments(for: dependency.licenseDocumentIDs)
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                VStack(alignment: .leading, spacing: 4) {
                    Text(dependency.name)
                        .font(.title3.bold())
                    Text(dependency.category)
                        .font(.caption)
                        .foregroundColor(theme.textSecondary)
                }

                Spacer()

                Button(action: { dismiss() }) {
                    Image(systemName: "xmark.circle.fill")
                        .font(.title2)
                        .foregroundColor(.secondary)
                }
                .buttonStyle(.plain)
            }
            .padding()

            Divider()

            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    OpenSourcePanel(title: "条目摘要", theme: theme) {
                        VStack(alignment: .leading, spacing: 10) {
                            SettingsValueRow(title: "版本", value: dependency.version)
                            SettingsValueRow(title: "许可证", value: dependency.license)
                            SettingsValueRow(title: "作用", value: dependency.role)
                            SettingsValueRow(title: "引入链路", value: dependency.linkedThrough)

                            Text(dependency.note)
                                .font(.caption)
                                .foregroundColor(theme.textSecondary)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                    }

                    OpenSourcePanel(title: "来源链接", theme: theme) {
                        VStack(alignment: .leading, spacing: 10) {
                            Link(destination: dependency.buildSourceURL) {
                                Label("MPVKit 打包来源 / 发布资产", systemImage: "shippingbox")
                                    .font(.subheadline)
                            }

                            Link(destination: dependency.upstreamURL) {
                                Label("上游项目 / 许可证主页", systemImage: "link")
                                    .font(.subheadline)
                            }
                        }
                    }

                    OpenSourcePanel(title: "可在 App 内查看的许可证正文", theme: theme) {
                        if bundledDocuments.isEmpty {
                            Text("当前没有为该条目内置专属或通用许可证正文，请优先查看上游项目页中的 LICENSE / COPYING。")
                                .font(.caption)
                                .foregroundColor(theme.textSecondary)
                        } else {
                            VStack(alignment: .leading, spacing: 10) {
                                ForEach(bundledDocuments) { document in
                                    OpenSourceLicenseDocumentRow(document: document, theme: theme) {
                                        selectedLicenseDocument = document
                                    }
                                }
                            }
                        }
                    }
                }
                .padding(20)
            }
            .background(theme.background)
        }
        .frame(width: 760, height: 760)
        .background(theme.background)
        .sheet(item: $selectedLicenseDocument) { document in
            OpenSourceLicenseTextSheet(document: document, theme: theme)
        }
    }
}

private struct OpenSourceLicenseTextSheet: View {
    @Environment(\.dismiss) private var dismiss

    let document: OpenSourceLicenseDocument
    let theme: AppTheme

    @State private var licenseText = ""
    @State private var loadError = ""

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                VStack(alignment: .leading, spacing: 4) {
                    Text(document.title)
                        .font(.title3.bold())
                    Text(document.summary)
                        .font(.caption)
                        .foregroundColor(theme.textSecondary)
                }

                Spacer()

                Button(action: { dismiss() }) {
                    Image(systemName: "xmark.circle.fill")
                        .font(.title2)
                        .foregroundColor(.secondary)
                }
                .buttonStyle(.plain)
            }
            .padding()

            Divider()

            VStack(alignment: .leading, spacing: 10) {
                Link(destination: document.sourceURL) {
                    Label("许可证来源", systemImage: "link")
                        .font(.subheadline)
                }

                if !loadError.isEmpty {
                    Text(loadError)
                        .font(.caption)
                        .foregroundColor(.red)
                }

                ScrollView {
                    Text(licenseText.isEmpty ? "正在加载许可证全文..." : licenseText)
                        .font(.system(.footnote, design: .monospaced))
                        .foregroundColor(theme.textPrimary)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .textSelection(.enabled)
                        .padding(14)
                        .background(
                            RoundedRectangle(cornerRadius: 12, style: .continuous)
                                .fill(theme.surface)
                        )
                }
            }
            .padding()
        }
        .frame(width: 760, height: 760)
        .background(theme.background)
        .task(id: document.id) {
            loadLicenseText()
        }
    }

    private func loadLicenseText() {
        if let url = Bundle.main.url(forResource: document.bundleResourceName, withExtension: document.bundleExtension, subdirectory: "OpenSourceLicenses")
            ?? Bundle.main.url(forResource: document.bundleResourceName, withExtension: document.bundleExtension) {
            do {
                licenseText = try String(contentsOf: url, encoding: .utf8)
                loadError = ""
            } catch {
                licenseText = ""
                loadError = "许可证资源已打包，但读取失败：\(error.localizedDescription)"
            }
        } else {
            licenseText = ""
            loadError = "未在当前 App 包中找到 \(document.bundleResourceName).\(document.bundleExtension)。如果你是从旧构建启动，重新编译后再打开此页。"
        }
    }
}

private struct OpenSourcePanel<Content: View>: View {
    var title: String?
    let theme: AppTheme
    @ViewBuilder var content: Content

    init(title: String? = nil, theme: AppTheme, @ViewBuilder content: () -> Content) {
        self.title = title
        self.theme = theme
        self.content = content()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if let title {
                Text(title)
                    .font(.headline)
                    .foregroundColor(theme.textPrimary)
            }

            content
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(theme.surface)
        )
        .overlay(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .stroke(theme.accent.opacity(0.12), lineWidth: 1)
        )
    }
}

struct SettingsValueRow: View {
    let title: String
    let value: String

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Text(title)
                .foregroundColor(.secondary)
                .frame(width: 88, alignment: .leading)

            Text(value)
                .frame(maxWidth: .infinity, alignment: .leading)
                .multilineTextAlignment(.leading)
                .textSelection(.enabled)
        }
        .font(.subheadline)
    }
}
