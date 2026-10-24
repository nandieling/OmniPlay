import SwiftUI
import GRDB
import AppKit

struct SettingsView: View {
    @Environment(\.dismiss) var dismiss
    private let focusTMDBApi: Bool
    
    // 绑定全局持久化设置
    @AppStorage("keepLocalPosters") var keepLocalPosters = true
    @AppStorage("autoScanOnStartup") var autoScanOnStartup = true
    @AppStorage("autoCheckUpdatesOnStartup") var autoCheckUpdatesOnStartup = true
    @AppStorage("enableFastTooltip") var enableFastTooltip = true
    @AppStorage("showMediaSourceRealPath") var showMediaSourceRealPath = true
    @AppStorage("removeWebDAVCredentialsOnDelete") var removeWebDAVCredentialsOnDelete = false
    @AppStorage("enableLocalMetadataImport") var enableLocalMetadataImport = false
    @AppStorage("enableLocalMetadataExport") var enableLocalMetadataExport = false
    @AppStorage("tmdbApiKey") var tmdbApiKey = ""
    @AppStorage("tmdbProxyURL") var tmdbProxyURL = ""
    
    @AppStorage("appLanguage") var appLanguage = "zh-Hans"
    @AppStorage("defaultAudio") var defaultAudio = "auto"
    @AppStorage("defaultSub") var defaultSub = "chi"
    @AppStorage("playbackQualityMode") var playbackQualityMode = "balanced"
    @AppStorage("playBluRayExtras") var playBluRayExtras = false
    
    @AppStorage(AppThemeResolver.colorKey) var appTheme = ThemeType.appleLight.rawValue
    @AppStorage(AppThemeResolver.styleKey) var appThemeStyle = ThemeStyle.classic.rawValue
    var theme: AppTheme { AppThemeResolver.theme(colorRaw: appTheme, styleRaw: appThemeStyle) }
    
    @ObservedObject var cacheManager = OfflineCacheManager.shared
    
    @State private var isValidatingAPI = false
    @State private var apiValidationMessage = ""
    @State private var apiValidationColor: Color = .primary
    @State private var isShowingOpenSourceInfo = false
    @State private var isCheckingForUpdate = false
    @State private var updateStatusMessage = ""
    @State private var updateStatusColor: Color = .secondary
    @State private var isTMDBApiKeyVisible = false
    @FocusState private var isTMDBApiFocused: Bool

    private let githubRepositoryURL = "https://github.com/nandieling/OmniPlay"
    private let githubLatestReleaseAPI = "https://api.github.com/repos/nandieling/OmniPlay/releases/latest"

    init(focusTMDBApi: Bool = false) {
        self.focusTMDBApi = focusTMDBApi
    }
    
    var body: some View {
        VStack(spacing: 0) {
            Form {
                // 标题挪进 Form 首行，与下面的分组内容共用同一条左缘
                HStack {
                    Text("偏好设置").font(.title2.bold()).foregroundColor(theme.textPrimary)
                    Spacer()
                    Button(action: { dismiss() }) {
                        Image(systemName: "xmark.circle.fill").font(.title2).foregroundColor(theme.textSecondary)
                    }.buttonStyle(.plain)
                }
                .padding(.vertical, 4)

                Section(header: settingsHeader("软件更新")) {
                    HStack(spacing: 8) {
                        Text("当前版本")
                        Text(currentAppVersion)
                            .foregroundColor(theme.textSecondary)
                        Spacer(minLength: 0)
                    }

                    Toggle("启动时自动检查更新", isOn: $autoCheckUpdatesOnStartup)

                    HStack(spacing: 10) {
                        Button(action: { checkForUpdates(install: false) }) {
                            if isCheckingForUpdate {
                                ProgressView().controlSize(.small).frame(width: 52)
                            } else {
                                Text("检查更新")
                            }
                        }
                        .disabled(isCheckingForUpdate)

                        Button("直接更新") {
                            checkForUpdates(install: true)
                        }
                        .disabled(isCheckingForUpdate)

                        Button("打开 GitHub 仓库") {
                            openExternalURL(githubRepositoryURL)
                        }
                    }
                    .buttonStyle(.bordered)
                    .tint(theme.accent)

                    if !updateStatusMessage.isEmpty {
                        Text(updateStatusMessage)
                            .font(.caption)
                            .foregroundColor(updateStatusColor)
                    }
                }

                Section(header: settingsHeader("基础设置")) {
                    Toggle("启动时自动扫描并同步库", isOn: $autoScanOnStartup)
                    Toggle("删除文件夹时保留本地缓存海报", isOn: $keepLocalPosters)
                    Toggle("启用快速悬停提示 (Tooltip)", isOn: $enableFastTooltip)
                    Toggle("媒体源显示真实路径", isOn: $showMediaSourceRealPath)
                    Toggle("移除 WebDAV 源时同时删除保存的登录凭据", isOn: $removeWebDAVCredentialsOnDelete)
                        .help("若开启，删除 WebDAV 源会连同钥匙串里的账号密码一并清理。关闭则保留以便下次自动填充。")

                    VideoDefaultHandlerRow(theme: theme)
                }

                Section(header: settingsHeader("本地刮削文件").padding(.top, 10)) {
                    Toggle("读取本地 NFO、海报和剧照", isOn: $enableLocalMetadataImport)
                    Toggle("刮削完成后保存 NFO、海报和剧照到本地", isOn: $enableLocalMetadataExport)
                    Text("默认关闭。仅对本地文件夹源生效，支持 movie.nfo、tvshow.nfo、同名 .nfo、poster/fanart 与同名 -thumb 图片。")
                        .font(.caption)
                        .foregroundColor(theme.textSecondary)
                }
                
                Section(header: settingsHeader("离线缓存").padding(.top, 10)) {
                    HStack {
                        Text("保存位置")
                        Spacer()
                        
                        if let dir = cacheManager.cacheDirectory {
                            Text(dir.path)
                                .foregroundColor(theme.textSecondary)
                                .lineLimit(1)
                                .truncationMode(.middle)
                                .frame(maxWidth: 200, alignment: .trailing)
                        } else {
                            Text("未设置").foregroundColor(theme.dangerColor)
                        }
                        
                        Button("更改目录") {
                            cacheManager.selectCacheDirectory()
                        }
                        .buttonStyle(.bordered)
                        .tint(theme.accent)
                    }
                    Text("设置后，您可以在影视详情页将 NAS 视频缓存至此目录，方便离线观看。")
                        .font(.caption).foregroundColor(theme.textSecondary)
                }

                Section(header: settingsHeader("主题").padding(.top, 10)) {
                    Picker("主题风格", selection: $appThemeStyle) {
                        ForEach(ThemeStyle.allCases) { style in
                            Text(style.displayName).tag(style.rawValue)
                        }
                    }

                    Picker("主题颜色", selection: $appTheme) {
                        ForEach(ThemeType.allCases) { themeItem in
                            HStack {
                                Circle()
                                    .fill(themeItem.colors.accent)
                                    .frame(width: 12, height: 12)
                                Text(themeItem.displayName)
                            }.tag(themeItem.rawValue)
                        }
                    }

                    if theme.isLiquidGlass {
                        // 玻璃是透明的，直接把生效中的染色与光斑摊开给用户看
                        HStack(spacing: 6) {
                            Text("玻璃染色与光斑")
                                .font(.caption)
                                .foregroundColor(theme.textSecondary)
                            ForEach(Array(glassSwatchColors.enumerated()), id: \.offset) { _, color in
                                RoundedRectangle(cornerRadius: 3, style: .continuous)
                                    .fill(color)
                                    .frame(width: 22, height: 12)
                            }
                            Spacer(minLength: 0)
                        }
                    }

                }
                
                Section(header: settingsHeader("语言与播放偏好").padding(.top, 10)) {
                    Picker("软件与刮削语言", selection: $appLanguage) {
                        Text("简体中文").tag("zh-Hans")
                        Text("English").tag("en")
                    }.help("更改此项将改变 TMDB 刮削获取的海报和简介语言。")
                    
                    Picker("默认首选音轨", selection: $defaultAudio) {
                        Text("智能匹配 (制片国家语言)").tag("auto")
                        Text("中文").tag("chi")
                        Text("英语").tag("eng")
                        Text("日语").tag("jpn")
                    }
                    
                    Picker("默认首选字幕", selection: $defaultSub) {
                        Text("中文优先 (简/繁)").tag("chi")
                        Text("英语优先").tag("eng")
                        Text("关闭字幕").tag("no")
                    }

                    Picker("播放画质模式", selection: $playbackQualityMode) {
                        Text("流畅优先").tag("smooth")
                        Text("平衡 (推荐)").tag("balanced")
                        Text("画质优先").tag("quality")
                    }
                    .help("切换后新打开的播放窗口会生效。")

                    Toggle("播放蓝光花絮", isOn: $playBluRayExtras)
                        .help("默认关闭。关闭时蓝光 BDMV 会按 STREAM 文件大小自动选择主电影；开启后会按文件编号播放包含花絮在内的所有 STREAM 视频。")
                }
                
                Section(header: settingsHeader("刮削服务 (TMDB)").padding(.top, 10)) {
                    Text("TMDB 代理网关地址")
                        .font(.subheadline.bold())
                        .foregroundColor(theme.textPrimary)
                        .frame(maxWidth: .infinity, alignment: .leading)

                    // 用 NSTextField 版输入框 + Spacer 钉住左缘，与下面的 API 输入框、验证按钮左对齐
                    HStack(spacing: 0) {
                        PlainSingleLineField(text: $tmdbProxyURL)
                            .frame(width: 482)
                        Spacer(minLength: 0)
                    }

                    Text("无法直连官方 API 时可填写反向代理地址，支持域名或带路径前缀的地址，例如 https://proxy.example.com/tmdb。")
                        .font(.caption)
                        .foregroundColor(theme.textSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                        .frame(maxWidth: .infinity, alignment: .leading)

                    Text("TMDB API")
                        .font(.subheadline.bold())
                        .foregroundColor(theme.textPrimary)
                        .frame(maxWidth: .infinity, alignment: .leading)

                    Text("请填写自己的 TMDB 凭据，以启用刮削、海报和剧照获取。")
                        .font(.caption)
                        .foregroundColor(theme.textSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                        .frame(maxWidth: .infinity, alignment: .leading)

                    HStack(alignment: .center, spacing: 8) {
                        ZStack {
                            RoundedRectangle(cornerRadius: 5, style: .continuous)
                                .fill(theme.surface)
                            RoundedRectangle(cornerRadius: 5, style: .continuous)
                                .stroke(theme.textSecondary.opacity(0.3), lineWidth: 0.8)
                            SingleLineAPITextField(
                                text: $tmdbApiKey,
                                placeholder: "支持 API 读写令牌和 API 密钥",
                                isSecure: !isTMDBApiKeyVisible,
                                isFocused: Binding(
                                    get: { isTMDBApiFocused },
                                    set: { isTMDBApiFocused = $0 }
                                )
                            )
                            .padding(.leading, 8)
                            .padding(.trailing, 28)
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                        }
                        .frame(width: 410, height: 28)
                        .clipShape(RoundedRectangle(cornerRadius: 5, style: .continuous))
                        .overlay(alignment: .trailing) {
                            Button(action: { isTMDBApiKeyVisible.toggle() }) {
                                Image(systemName: isTMDBApiKeyVisible ? "eye.slash" : "eye")
                                    .font(.system(size: 12, weight: .semibold))
                                    .foregroundColor(theme.textSecondary)
                                    .frame(width: 28, height: 28)
                                    .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                            .help(isTMDBApiKeyVisible ? "隐藏密钥" : "显示密钥")
                        }

                        Button(action: validateTMDBApi) {
                            if isValidatingAPI {
                                ProgressView()
                                    .controlSize(.small)
                                    .frame(width: 44, height: 28)
                            } else {
                                Text("验证")
                                    .frame(width: 44, height: 28)
                            }
                        }
                        .buttonStyle(.plain)
                        .frame(width: 64, height: 28)
                        .background(
                            RoundedRectangle(cornerRadius: 5, style: .continuous)
                                .fill(theme.surface)
                        )
                        .overlay(
                            RoundedRectangle(cornerRadius: 5, style: .continuous)
                                .stroke(theme.textSecondary.opacity(0.3), lineWidth: 0.8)
                        )
                        .foregroundColor(trimmedTMDBApiKey.isEmpty || isValidatingAPI ? theme.textSecondary.opacity(0.55) : theme.textPrimary)
                        .contentShape(RoundedRectangle(cornerRadius: 5, style: .continuous))
                        .disabled(trimmedTMDBApiKey.isEmpty || isValidatingAPI)
                        Spacer(minLength: 0)
                    }
                    .frame(height: 28)

                    Link("请往TMDB官网注册获取api：https://www.themoviedb.org", destination: URL(string: "https://www.themoviedb.org")!)
                        .font(.caption)
                        .foregroundColor(theme.accent)
                        .frame(maxWidth: .infinity, alignment: .leading)

                    if trimmedTMDBApiKey.isEmpty {
                        Text("当前未配置任何 TMDB 凭据。TMDB 刮削、海报与剧照获取将不可用。")
                            .font(.caption)
                            .foregroundColor(theme.dangerColor)
                            .fixedSize(horizontal: false, vertical: true)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }

                    if !apiValidationMessage.isEmpty {
                        Text(apiValidationMessage)
                            .font(.caption)
                            .foregroundColor(apiValidationColor)
                            .fixedSize(horizontal: false, vertical: true)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.top, 2)
                            .transition(.opacity)
                    }
                }

                NeodbSettingsSection(theme: theme)

                Section(header: settingsHeader("播放器内核与开源组件").padding(.top, 10)) {
                    ForEach(OpenSourceCatalog.summaryRows) { row in
                        SettingsValueRow(title: row.title, value: row.value, theme: theme)
                    }

                    Text("此处显示当前构建实际采用的播放器栈和主要三方组件，便于用户确认采用的内核、解码链路和数据库栈。仅展示信息不替代开源协议要求的源码提供、许可证随附和再分发义务。")
                        .font(.caption)
                        .foregroundColor(theme.textSecondary)

                    Button("查看开源组件详情与许可证线索") {
                        isShowingOpenSourceInfo = true
                    }
                    .buttonStyle(.bordered)
                    .tint(theme.accent)
                }
            }
            .padding()
            .formStyle(.grouped)
            .scrollContentBackground(.hidden)   // 让主题底透出，别被系统分组底色盖住
            .tint(theme.accent)
            .foregroundStyle(theme.textPrimary)
        }
        .frame(width: 620, height: 760)
        .background(theme.backdropLayer)
        .preferredColorScheme(theme.preferredScheme)
        .environment(\.locale, .init(identifier: appLanguage))
        .sheet(isPresented: $isShowingOpenSourceInfo) {
            OpenSourceInfoSheet(theme: theme)
        }
        .onAppear {
            restoreAutomaticUpdateStatusIfAvailable()
            guard focusTMDBApi else { return }
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) {
                isTMDBApiFocused = true
            }
        }
        .onChange(of: appLanguage) { oldValue, newValue in
            UserDefaults.standard.set([newValue], forKey: "AppleLanguages")
            UserDefaults.standard.synchronize()
        }
    }

    /// 玻璃风格下真正生效的染色与光斑颜色，给设置页一个所见即所得的预览
    private var glassSwatchColors: [Color] {
        guard let glass = theme.glass else { return [] }
        return [glass.tint] + glass.glows.map { $0.color.opacity($0.opacity) }
    }

    /// 分节标题统一取主题主文字色，几套配色和玻璃底下都不脱节
    private func settingsHeader(_ title: String) -> some View {
        Text(title).font(.headline).foregroundColor(theme.textPrimary)
    }

    private var trimmedTMDBApiKey: String {
        tmdbApiKey.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private var trimmedTMDBProxyURL: String {
        tmdbProxyURL.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private var currentAppVersion: String {
        let info = Bundle.main.infoDictionary
        return (info?["CFBundleShortVersionString"] as? String)
            ?? (info?["CFBundleVersion"] as? String)
            ?? "dev"
    }

    private func restoreAutomaticUpdateStatusIfAvailable() {
        guard updateStatusMessage.isEmpty,
              let message = MacAppUpdateChecker.cachedAutomaticUpdateMessage(currentVersion: currentAppVersion) else {
            return
        }
        updateStatusMessage = message
        updateStatusColor = theme.warningColor
    }
    
    private func validateTMDBApi() {
        let customKey = trimmedTMDBApiKey
        guard !customKey.isEmpty else { return }

        isValidatingAPI = true
        apiValidationMessage = trimmedTMDBProxyURL.isEmpty ? "正在连接 TMDB 服务器..." : "正在通过自定义网关连接 TMDB..."
        apiValidationColor = theme.textSecondary
        
        Task {
            // 与扫描前的连通性检测走同一条后台路径，主线程只等结果
            let result = await Task.detached(priority: .utility) {
                await TMDBService.probeConnection(
                    credential: TMDBService.credentialSource(customAPIInput: customKey),
                    customProxyURL: trimmedTMDBProxyURL
                )
            }.value
            await MainActor.run {
                if result.isConnected {
                    apiValidationMessage = "✅ 验证成功！API 状态正常。"
                    apiValidationColor = theme.successColor
                } else {
                    apiValidationMessage = "❌ 验证失败：\(result.message)"
                    apiValidationColor = theme.dangerColor
                }
                isValidatingAPI = false
            }
        }
    }

    private func checkForUpdates(install: Bool) {
        isCheckingForUpdate = true
        updateStatusMessage = install ? "正在获取最新版本..." : "正在检查 GitHub 最新版本..."
        updateStatusColor = theme.textSecondary

        Task {
            do {
                let release = try await fetchLatestRelease()
                guard !release.isEmpty else {
                    await MainActor.run {
                        isCheckingForUpdate = false
                    }
                    return
                }
                let tagName = (release["tag_name"] as? String) ?? ""
                let htmlURL = (release["html_url"] as? String) ?? "\(githubRepositoryURL)/releases/latest"
                let isNewer = isVersion(tagName, newerThan: currentAppVersion)

                if !install {
                    await MainActor.run {
                        updateStatusMessage = isNewer
                            ? "发现新版本 \(tagName)，可点击“直接更新”。"
                            : "当前已是最新版本（\(currentAppVersion)）。"
                        updateStatusColor = isNewer ? theme.warningColor : theme.successColor
                        isCheckingForUpdate = false
                    }
                    return
                }

                guard isNewer else {
                    await MainActor.run {
                        updateStatusMessage = "当前已是最新版本（\(currentAppVersion)）。"
                        updateStatusColor = theme.successColor
                        isCheckingForUpdate = false
                    }
                    return
                }

                guard let asset = preferredUpdateAsset(from: release["assets"] as? [[String: Any]]) else {
                    await MainActor.run {
                        updateStatusMessage = "未找到适合当前 Mac 的安装包，已打开 GitHub Release 页面。"
                        updateStatusColor = theme.warningColor
                        isCheckingForUpdate = false
                        openExternalURL(htmlURL)
                    }
                    return
                }

                let fileURL = try await downloadUpdateAsset(asset)
                await MainActor.run {
                    updateStatusMessage = "已下载 \(fileURL.lastPathComponent)，正在打开安装包。"
                    updateStatusColor = theme.successColor
                    isCheckingForUpdate = false
                    NSWorkspace.shared.open(fileURL)
                }
            } catch {
                await MainActor.run {
                    updateStatusMessage = "检查更新失败：\(error.localizedDescription)"
                    updateStatusColor = theme.dangerColor
                    isCheckingForUpdate = false
                }
            }
        }
    }

    private func fetchLatestRelease() async throws -> [String: Any] {
        guard let url = URL(string: githubLatestReleaseAPI) else { return [:] }
        var request = URLRequest(url: url)
        request.setValue("OmniPlay", forHTTPHeaderField: "User-Agent")
        let (data, response) = try await URLSession.shared.data(for: request)
        if let http = response as? HTTPURLResponse, http.statusCode == 404 {
            await MainActor.run {
                updateStatusMessage = "GitHub 仓库暂未发布 Release，已打开仓库页面。"
                updateStatusColor = theme.warningColor
                openExternalURL(githubRepositoryURL)
            }
            return [:]
        }
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw NSError(domain: "OmniPlayUpdate", code: 1, userInfo: [NSLocalizedDescriptionKey: "GitHub 返回内容无法解析。"])
        }
        return object
    }

    private func preferredUpdateAsset(from assets: [[String: Any]]?) -> (name: String, url: URL)? {
        guard let assets else { return nil }
        let candidates = assets.compactMap { asset -> (name: String, url: URL, score: Int)? in
            guard let name = asset["name"] as? String,
                  let urlString = asset["browser_download_url"] as? String,
                  let url = URL(string: urlString) else { return nil }
            let lower = name.lowercased()
            guard lower.hasSuffix(".dmg") || lower.hasSuffix(".zip") || lower.hasSuffix(".pkg") else { return nil }
            var score = 0
            if lower.contains("mac") || lower.contains("darwin") || lower.contains("osx") { score += 3 }
            if lower.contains("universal") { score += 2 }
            #if arch(arm64)
            if lower.contains("arm64") || lower.contains("aarch64") || lower.contains("apple") { score += 4 }
            if lower.contains("x64") || lower.contains("x86_64") || lower.contains("intel") { score -= 3 }
            #else
            if lower.contains("x64") || lower.contains("x86_64") || lower.contains("intel") { score += 4 }
            if lower.contains("arm64") || lower.contains("aarch64") { score -= 3 }
            #endif
            return (name, url, score)
        }
        return candidates.max { $0.score < $1.score }.map { ($0.name, $0.url) }
    }

    private func downloadUpdateAsset(_ asset: (name: String, url: URL)) async throws -> URL {
        let (temporaryURL, _) = try await URLSession.shared.download(from: asset.url)
        let downloadDirectory = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        let destination = downloadDirectory.appendingPathComponent(asset.name)
        try? FileManager.default.removeItem(at: destination)
        try FileManager.default.moveItem(at: temporaryURL, to: destination)
        return destination
    }

    private func isVersion(_ candidate: String, newerThan current: String) -> Bool {
        let lhs = versionParts(candidate)
        let rhs = versionParts(current)
        guard !lhs.isEmpty else { return false }
        let count = max(lhs.count, rhs.count)
        for index in 0..<count {
            let left = index < lhs.count ? lhs[index] : 0
            let right = index < rhs.count ? rhs[index] : 0
            if left != right { return left > right }
        }
        return false
    }

    private func versionParts(_ value: String) -> [Int] {
        value.lowercased()
            .trimmingCharacters(in: CharacterSet(charactersIn: "v "))
            .split { !$0.isNumber }
            .compactMap { Int($0) }
    }

    private func openExternalURL(_ value: String) {
        guard let url = URL(string: value) else { return }
        NSWorkspace.shared.open(url)
    }
}

// 视频文件默认打开方式：显示当前接管情况，一键把常见容器交给觅影。
private struct VideoDefaultHandlerRow: View {
    let theme: AppTheme

    @State private var notOwned: [String] = []
    @State private var result = ""
    @State private var isWorking = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                Text("视频文件默认打开方式")
                Spacer(minLength: 8)
                Text(notOwned.isEmpty ? "已是默认" : "未设为默认")
                    .font(.caption)
                    .foregroundColor(notOwned.isEmpty ? theme.accent : theme.textSecondary)
                Button(action: apply) {
                    if isWorking {
                        ProgressView().controlSize(.small).frame(width: 56, height: 20)
                    } else {
                        Text("设为默认").frame(width: 56)
                    }
                }
                .buttonStyle(.bordered)
                .tint(theme.accent)
                .disabled(isWorking)
            }

            Text(caption)
                .font(.caption)
                .foregroundColor(theme.textSecondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .onAppear(perform: refresh)
    }

    private var caption: String {
        if !result.isEmpty { return result }
        if notOwned.isEmpty {
            return "双击视频文件会用觅影播放。可在访达的显示简介里单独改回其他播放器。"
        }
        return "一键把 mp4、mkv、mov、avi、rmvb、flv、webm、m2ts、ts、m4v、wmv 和蓝光 ISO 的默认打开应用设为觅影，不动其他文件类型。"
    }

    private func refresh() {
        notOwned = DirectFilePlaybackManager.shared.extensionsNotOwnedYet()
    }

    private func apply() {
        guard !isWorking else { return }
        isWorking = true
        Task {
            let report = await DirectFilePlaybackManager.shared.setAsDefaultVideoPlayer()
            await MainActor.run {
                isWorking = false
                if report.failed.isEmpty {
                    result = "已把全部视频类型设为觅影默认打开。"
                } else if report.succeeded.isEmpty {
                    result = "系统没有接受这次的默认应用设置，请在访达里对某个视频文件用显示简介手动改一次默认打开方式。"
                } else {
                    result = "已接管 \(report.succeeded.count) 种，未接管：\(report.failed.joined(separator: "、"))。"
                }
                refresh()
            }
        }
    }
}

private struct SingleLineAPITextField: NSViewRepresentable {
    @Binding var text: String
    let placeholder: String
    /// 是否用星号隐藏密钥内容
    let isSecure: Bool
    @Binding var isFocused: Bool

    func makeNSView(context: Context) -> NSTextField {
        let textField = FocusableTextField()
        textField.cell = VerticallyCenteredTextFieldCell(textCell: "")
        textField.delegate = context.coordinator
        textField.isEditable = true
        textField.isSelectable = true
        textField.isEnabled = true
        textField.refusesFirstResponder = false
        textField.isBordered = false
        textField.isBezeled = false
        textField.drawsBackground = false
        textField.focusRingType = .none
        textField.font = .systemFont(ofSize: 13)
        textField.placeholderString = placeholder
        textField.lineBreakMode = .byClipping
        if let cell = textField.cell {
            cell.wraps = false
            cell.isScrollable = true
            cell.usesSingleLineMode = true
            cell.lineBreakMode = .byClipping
        }
        textField.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        textField.setContentHuggingPriority(.defaultLow, for: .horizontal)
        return textField
    }

    func updateNSView(_ nsView: NSTextField, context: Context) {
        if nsView.stringValue != text {
            nsView.stringValue = text
        }
        if nsView.placeholderString != placeholder {
            nsView.placeholderString = placeholder
        }
        // 隐藏/显示密钥靠更换 cell 类型实现：系统没有可直接改写的 secure 开关
        if (nsView.cell is NSSecureTextFieldCell) != isSecure {
            let editorIsEditing = nsView.currentEditor() != nil
            let cell: NSTextFieldCell = isSecure
                ? VerticallyCenteredSecureTextFieldCell(textCell: text)
                : VerticallyCenteredTextFieldCell(textCell: text)
            cell.isEditable = true
            cell.isSelectable = true
            cell.isBordered = false
            cell.isBezeled = false
            cell.drawsBackground = false
            cell.backgroundColor = .clear
            cell.isScrollable = true
            cell.wraps = false
            cell.usesSingleLineMode = true
            cell.lineBreakMode = .byClipping
            cell.font = nsView.font
            cell.placeholderString = placeholder
            nsView.cell = cell
            if editorIsEditing {
                nsView.window?.makeFirstResponder(nsView)
            }
        }
        if isFocused, nsView.window?.firstResponder !== nsView.currentEditor() {
            nsView.window?.makeFirstResponder(nsView)
        }
    }

    func makeCoordinator() -> Coordinator {
        Coordinator(text: $text, isFocused: $isFocused)
    }

    final class Coordinator: NSObject, NSTextFieldDelegate {
        @Binding private var text: String
        @Binding private var isFocused: Bool

        init(text: Binding<String>, isFocused: Binding<Bool>) {
            _text = text
            _isFocused = isFocused
        }

        func controlTextDidChange(_ notification: Notification) {
            guard let textField = notification.object as? NSTextField else { return }
            text = textField.stringValue
        }

        func controlTextDidBeginEditing(_ notification: Notification) {
            isFocused = true
        }

        func controlTextDidEndEditing(_ notification: Notification) {
            isFocused = false
        }
    }
}

private final class FocusableTextField: NSTextField {
    override var acceptsFirstResponder: Bool { true }

    override func mouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        super.mouseDown(with: event)
    }
}

private final class VerticallyCenteredTextFieldCell: NSTextFieldCell {
    override func titleRect(forBounds rect: NSRect) -> NSRect {
        centeredRect(super.titleRect(forBounds: rect))
    }

    override func drawingRect(forBounds rect: NSRect) -> NSRect {
        centeredRect(super.drawingRect(forBounds: rect))
    }

    private func centeredRect(_ rect: NSRect) -> NSRect {
        guard let font else { return rect }
        let textHeight = ceil(font.ascender - font.descender)
        let y = rect.origin.y + max(0, (rect.height - textHeight) / 2) - 1
        return NSRect(x: rect.origin.x, y: y, width: rect.width, height: textHeight)
    }
}

/// 隐藏密钥时用的同款垂直居中 cell
private final class VerticallyCenteredSecureTextFieldCell: NSSecureTextFieldCell {
    override func titleRect(forBounds rect: NSRect) -> NSRect {
        centeredRect(super.titleRect(forBounds: rect))
    }

    override func drawingRect(forBounds rect: NSRect) -> NSRect {
        centeredRect(super.drawingRect(forBounds: rect))
    }

    private func centeredRect(_ rect: NSRect) -> NSRect {
        guard let font else { return rect }
        let textHeight = ceil(font.ascender - font.descender)
        let y = rect.origin.y + max(0, (rect.height - textHeight) / 2) - 1
        return NSRect(x: rect.origin.x, y: y, width: rect.width, height: textHeight)
    }
}

enum MacAppUpdateChecker {
    private static let autoCheckKey = "autoCheckUpdatesOnStartup"
    private static let lastCheckKey = "lastAutomaticUpdateCheckAt"
    private static let detectedVersionKey = "lastDetectedUpdateVersion"
    private static let detectedReleaseURLKey = "lastDetectedUpdateReleaseURL"
    private static let automaticCheckInterval: TimeInterval = 24 * 60 * 60

    static func checkAtStartupIfNeeded() {
        let defaults = UserDefaults.standard
        let isEnabled = defaults.object(forKey: autoCheckKey) as? Bool ?? true
        guard isEnabled else { return }
        guard ProcessInfo.processInfo.environment["UITEST_MODE"] != "1" else { return }

        let now = Date().timeIntervalSince1970
        let lastCheck = defaults.double(forKey: lastCheckKey)
        guard lastCheck <= 0 || now - lastCheck >= automaticCheckInterval else { return }

        defaults.set(now, forKey: lastCheckKey)
        Task.detached(priority: .background) {
            do {
                let release = try await fetchLatestRelease()
                let currentVersion = currentAppVersion()
                await MainActor.run {
                    let defaults = UserDefaults.standard
                    if isVersion(release.tagName, newerThan: currentVersion) {
                        defaults.set(release.tagName, forKey: detectedVersionKey)
                        defaults.set(release.htmlURL, forKey: detectedReleaseURLKey)
                    } else {
                        defaults.removeObject(forKey: detectedVersionKey)
                        defaults.removeObject(forKey: detectedReleaseURLKey)
                    }
                }
            } catch {
                // 启动时检查只做静默提示，网络失败不打扰用户。
            }
        }
    }

    static func cachedAutomaticUpdateMessage(currentVersion: String) -> String? {
        let defaults = UserDefaults.standard
        guard let version = defaults.string(forKey: detectedVersionKey),
              isVersion(version, newerThan: currentVersion) else {
            return nil
        }
        return "后台自动检查发现新版本 \(version)，可点击“直接更新”。"
    }

    nonisolated private static func fetchLatestRelease() async throws -> (tagName: String, htmlURL: String) {
        guard let url = URL(string: "https://api.github.com/repos/nandieling/OmniPlay/releases/latest") else { return ("", "") }
        var request = URLRequest(url: url)
        request.setValue("OmniPlay", forHTTPHeaderField: "User-Agent")
        let (data, response) = try await URLSession.shared.data(for: request)
        if let http = response as? HTTPURLResponse, http.statusCode == 404 {
            return ("", "https://github.com/nandieling/OmniPlay")
        }
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return ("", "")
        }
        return (
            (object["tag_name"] as? String) ?? "",
            (object["html_url"] as? String) ?? "https://github.com/nandieling/OmniPlay/releases/latest"
        )
    }

    nonisolated private static func currentAppVersion() -> String {
        let info = Bundle.main.infoDictionary
        return (info?["CFBundleShortVersionString"] as? String)
            ?? (info?["CFBundleVersion"] as? String)
            ?? "dev"
    }

    nonisolated private static func isVersion(_ candidate: String, newerThan current: String) -> Bool {
        let lhs = versionParts(candidate)
        let rhs = versionParts(current)
        guard !lhs.isEmpty else { return false }
        let count = max(lhs.count, rhs.count)
        for index in 0..<count {
            let left = index < lhs.count ? lhs[index] : 0
            let right = index < rhs.count ? rhs[index] : 0
            if left != right { return left > right }
        }
        return false
    }

    nonisolated private static func versionParts(_ value: String) -> [Int] {
        value.lowercased()
            .trimmingCharacters(in: CharacterSet(charactersIn: "v "))
            .split { !$0.isNumber }
            .compactMap { Int($0) }
    }
}
