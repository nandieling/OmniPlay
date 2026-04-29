import SwiftUI
import GRDB
import AppKit

private struct RecentWebDAVHistoryItem: Identifiable, Codable, Equatable {
    var id: UUID
    var name: String
    var baseURL: String
    var username: String
    var credentialID: String?
    var lastUsed: Date

    init(id: UUID = UUID(), name: String, baseURL: String, username: String, credentialID: String?, lastUsed: Date = Date()) {
        self.id = id
        self.name = name
        self.baseURL = baseURL
        self.username = username
        self.credentialID = credentialID
        self.lastUsed = lastUsed
    }
}

extension MediaSource: Hashable {
    public static func == (lhs: MediaSource, rhs: MediaSource) -> Bool { return lhs.id == rhs.id }
    public func hash(into hasher: inout Hasher) { hasher.combine(id) }
}

extension View { @ViewBuilder func conditionalHelp(_ text: String, show: Bool) -> some View { if show { self.help(text) } else { self } } }

enum MovieSortOption: String, CaseIterable { case name = "名称"; case rating = "评分"; case year = "上映年份" }

struct PosterWallView: View {
    private static let recentWebDAVHistoryKey = "PosterWallRecentWebDAVHistory"
    private let topBarHeight: CGFloat = 52
    @State private var movies: [Movie] = []
    @State private var continueWatchingMovies: [Movie] = []
    let libraryManager = MediaLibraryManager()
    
    @State private var currentScanTask: Task<Void, Never>? = nil
    @State private var isProcessing = false
    @State private var processingMessage = ""
    @State private var showSettings = false
    @State private var mediaSources: [MediaSource] = []
    
    // 删除了关于网络别名的复杂状态变量
    @State private var isShowingManageSources = false
    @State private var needsRescanAfterCurrentRun = false
    @State private var isShowingRenameSourceSheet = false
    @State private var sourceToRename: MediaSource? = nil
    @State private var renamingSourceName: String = ""
    @State private var isShowingAddWebDAVSheet = false
    @State private var webDAVName: String = ""
    @State private var webDAVBaseURL: String = ""
    @State private var webDAVUsername: String = ""
    @State private var webDAVPassword: String = ""
    @State private var webDAVValidationMessage: String? = nil
    @State private var webDAVValidationIsError: Bool = false
    @State private var webDAVIsTestingConnection: Bool = false
    @State private var webDAVLastPreflight: WebDAVPreflightResult? = nil
    @State private var isShowingWebDAVPreScanLoginSheet = false
    @State private var isShowingWebDAVFolderBrowserSheet = false
    @State private var webDAVBrowserName: String = ""
    @State private var webDAVBrowserBaseURL: String = ""
    @State private var webDAVBrowserUsername: String = ""
    @State private var webDAVBrowserPassword: String = ""
    @State private var webDAVBrowserCredentialID: String? = nil
    @State private var webDAVBrowserCurrentURL: String = ""
    @State private var webDAVBrowserPathStack: [String] = []
    @State private var webDAVBrowserItems: [WebDAVDirectoryItem] = []
    @State private var webDAVBrowserMountedURLs: Set<String> = []
    @State private var webDAVBrowserMessage: String? = nil
    @State private var webDAVBrowserMessageIsError = false
    @State private var webDAVBrowserIsLoading = false
    @State private var webDAVBrowserMountingURL: String? = nil
    
    @AppStorage("keepLocalPosters") var keepLocalPosters = true
    @AppStorage("autoScanOnStartup") var autoScanOnStartup = true
    @AppStorage("enableFastTooltip") var enableFastTooltip = true
    @AppStorage("showMediaSourceRealPath") var showMediaSourceRealPath = true
    @AppStorage("removeWebDAVCredentialsOnDelete") var removeWebDAVCredentialsOnDelete = false
    
    @AppStorage("appTheme") var appTheme = ThemeType.crystal.rawValue
    var theme: AppTheme { ThemeType(rawValue: appTheme)?.colors ?? ThemeType.crystal.colors }
    
    @ObservedObject var thumbManager = ThumbnailManager.shared
    @ObservedObject var cacheManager = OfflineCacheManager.shared
    @StateObject private var lanScanner = LANScanner()
    
    @State private var searchText: String = ""
    @State private var isSearchActive: Bool = false
    @State private var selectedSortOption: MovieSortOption = .year
    @State private var isAscending: Bool = false
    @State private var pendingLibraryReloadTask: Task<Void, Never>? = nil
    @State private var activeScanningSourceID: Int64? = nil
    @State private var removedSourceIDsDuringRun: Set<Int64> = []
    @State private var recentWebDAVHistory: [RecentWebDAVHistoryItem] = PosterWallView.loadRecentWebDAVHistory()
    @State private var isShowingRemoveSourceSheet = false
    @State private var sourcePendingRemoval: MediaSource? = nil
    @State private var isLoadingLibraryData = false
    @State private var needsLibraryReloadAfterCurrentLoad = false
    
    // 🌟 彻底删除了所有与 network 相关的 State
    
    let columns = [GridItem(.adaptive(minimum: 160), spacing: 20)]
    init() {
        let isFast = UserDefaults.standard.bool(forKey: "enableFastTooltip")
        UserDefaults.standard.set(isFast ? 50 : 1000, forKey: "NSInitialToolTipDelay")
    }
    
    var displayedMovies: [Movie] { var result = movies; if !searchText.isEmpty { result = result.filter { $0.title.localizedCaseInsensitiveContains(searchText) } }; result.sort { m1, m2 in let isLess: Bool; switch selectedSortOption { case .name: isLess = m1.title.localizedStandardCompare(m2.title) == .orderedAscending; case .year: isLess = (m1.releaseDate ?? "") < (m2.releaseDate ?? ""); case .rating: isLess = (m1.voteAverage ?? 0.0) < (m2.voteAverage ?? 0.0) }; return isAscending ? isLess : !isLess }; return result }
    private var discoveredWebDAVDevices: [DiscoveredDevice] {
        lanScanner.discoveredDevices.filter { $0.type == .webdavHTTP || $0.type == .webdavHTTPS }
    }

    var body: some View {
        NavigationStack {
            ZStack(alignment: .top) {
                theme.background.ignoresSafeArea()
                
                ScrollView {
                    if movies.isEmpty && continueWatchingMovies.isEmpty {
                        VStack(spacing: 20) {
                            Image(systemName: "film").font(.system(size: 60)).foregroundColor(.secondary)
                            Text("媒体库空空如也，快去添加文件夹吧！").font(.title2).foregroundColor(.secondary)
                        }.padding(.top, 150).frame(maxWidth: .infinity)
                    } else {
                        VStack(alignment: .leading, spacing: 30) {
                            if !continueWatchingMovies.isEmpty {
                                Text("继续播放")
                                    .font(.title2).fontWeight(.bold)
                                    .foregroundColor(theme.textPrimary)
                                    .padding(.horizontal, 25).padding(.top, 20)
                                ScrollView(.horizontal, showsIndicators: false) {
                                    HStack(spacing: 20) {
                                        ForEach(continueWatchingMovies, id: \.id) { movie in
                                            NavigationLink(destination: MovieDetailView(movie: movie)) {
                                                MovieCardView(movie: movie, isContinueWatchingContext: true).frame(width: 160)
                                            }.buttonStyle(.plain)
                                        }
                                    }.padding(.horizontal, 25)
                                }
                                Divider().background(theme.textSecondary.opacity(0.3)).padding(.vertical, 10)
                            }
                            HStack(spacing: 12) {
                                Text("所有影视")
                                    .font(.title2).fontWeight(.bold)
                                    .foregroundColor(theme.textPrimary)
                                Spacer().frame(width: 15)
                                if isSearchActive {
                                    TextField("搜索...", text: $searchText)
                                        .textFieldStyle(.roundedBorder).frame(width: 160).transition(.opacity.combined(with: .move(edge: .trailing)))
                                }
                                Button(action: { withAnimation(.easeInOut(duration: 0.2)) { isSearchActive.toggle(); if !isSearchActive { searchText = "" } } }) {
                                    Image(systemName: "magnifyingglass").font(.system(size: 15, weight: .semibold)).foregroundColor(isSearchActive ? theme.accent : .secondary)
                                }.buttonStyle(.plain)
                                Menu {
                                    Picker("", selection: $selectedSortOption) {
                                        ForEach(MovieSortOption.allCases, id: \.self) { option in Text(option.rawValue).tag(option) }
                                    }.labelsHidden().pickerStyle(.inline)
                                } label: {
                                    Image(systemName: "line.3.horizontal.decrease.circle").font(.system(size: 16, weight: .semibold)).foregroundColor(.secondary)
                                }.menuStyle(.borderlessButton).fixedSize()
                                Button(action: { withAnimation { isAscending.toggle() } }) {
                                    Image(systemName: isAscending ? "arrow.up" : "arrow.down").font(.system(size: 14, weight: .bold)).foregroundColor(.secondary)
                                }.buttonStyle(.plain)
                                Spacer()
                            }.padding(.horizontal, 25).padding(.top, 15)
                            
                            LazyVGrid(columns: columns, spacing: 30) {
                                ForEach(displayedMovies, id: \.id) { movie in
                                    NavigationLink(destination: MovieDetailView(movie: movie)) { MovieCardView(movie: movie) }.buttonStyle(.plain)
                                }
                            }.padding(.horizontal, 25).padding(.bottom, 25).animation(.easeInOut, value: displayedMovies.count)
                        }
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .padding(.top, topBarHeight)
                
                if let cacheMessage = cacheManager.cacheStatusMessage {
                    Text(cacheMessage)
                        .font(.subheadline.weight(.semibold))
                        .foregroundColor(theme.textPrimary)
                        .padding(.horizontal, 16)
                        .padding(.vertical, 10)
                        .background(.ultraThinMaterial)
                        .clipShape(Capsule())
                        .shadow(radius: 8)
                        .padding(.top, 18)
                        .padding(.trailing, 18)
                        .transition(.move(edge: .top).combined(with: .opacity))
                }

                HStack(alignment: .center, spacing: 16) {
                    if !thumbManager.progressMessage.isEmpty {
                        HStack(spacing: 8) {
                            ProgressView().controlSize(.small)
                            Text(thumbManager.progressMessage)
                                .font(.caption)
                                .foregroundColor(.secondary)
                                .lineLimit(1)
                                .frame(maxWidth: 220, alignment: .leading)
                        }
                    }
                    
                    Spacer(minLength: 20)
                    
                    Button(action: { withAnimation { OfflineCacheManager.shared.isCacheModeActive.toggle() } }) {
                        Image(systemName: OfflineCacheManager.shared.isCacheModeActive ? "icloud.fill" : "icloud")
                            .font(.system(size: 21, weight: .medium))
                            .foregroundColor(OfflineCacheManager.shared.isCacheModeActive ? theme.accent : .secondary)
                            .frame(width: 28, height: 28)
                    }
                    .buttonStyle(.plain)
                    .conditionalHelp("切换离线缓存编辑模式", show: enableFastTooltip)
                    
                    Button(action: { isShowingManageSources.toggle() }) {
                        Image(systemName: "folder.badge.plus")
                            .font(.system(size: 21, weight: .medium))
                            .foregroundColor(.secondary)
                            .frame(width: 28, height: 28)
                    }
                    .buttonStyle(.plain)
                    .accessibilityIdentifier("toolbar.addSource")
                    .conditionalHelp("查看已挂载媒体源，并添加本地文件夹或 WebDAV 媒体源", show: enableFastTooltip)
                    .popover(isPresented: $isShowingManageSources, arrowEdge: .top) { folderMenuPanel }
                    
                    Button(action: { triggerScanAndScrape() }) {
                        Image(systemName: "arrow.triangle.2.circlepath")
                            .font(.system(size: 21, weight: .medium))
                            .foregroundColor(isProcessing ? theme.textSecondary.opacity(0.5) : .secondary)
                            .frame(width: 28, height: 28)
                    }
                    .buttonStyle(.plain)
                    .accessibilityIdentifier("toolbar.sync")
                    .disabled(isProcessing)
                    .conditionalHelp("重新扫描目录并刷新刮削结果", show: enableFastTooltip)
                    
                    Button(action: { showSettings = true }) {
                        Image(systemName: "gearshape")
                            .font(.system(size: 21, weight: .medium))
                            .foregroundColor(.secondary)
                            .frame(width: 28, height: 28)
                    }
                    .buttonStyle(.plain)
                    .accessibilityIdentifier("toolbar.settings")
                    .conditionalHelp("打开偏好设置", show: enableFastTooltip)
                }
                .padding(.horizontal, 24)
                .padding(.top, 10)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity).navigationTitle("我的觅影库")
            .sheet(isPresented: $showSettings) { SettingsView() }
            .sheet(isPresented: $isShowingRenameSourceSheet) {
                renameSourceSheet()
            }
            .sheet(isPresented: $isShowingWebDAVPreScanLoginSheet) {
                webDAVPreScanLoginSheet()
            }
            .sheet(isPresented: $isShowingWebDAVFolderBrowserSheet) {
                webDAVFolderBrowserSheet()
            }
            .sheet(isPresented: $isShowingRemoveSourceSheet) {
                removeSourceSheet()
            }
            // 🌟 彻底删除了那个恶心的 SMB/WebDAV Sheet 弹窗！
        }
        .onAppear {
            loadData()
            if ProcessInfo.processInfo.environment["UITEST_OPEN_WEBDAV_SHEET"] == "1" {
                prepareManualWebDAVBrowserLogin()
                isShowingWebDAVPreScanLoginSheet = true
            }
            if autoScanOnStartup {
                DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) {
                    if !isProcessing { triggerScanAndScrape() }
                }
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: .libraryUpdated)) { _ in
            scheduleDebouncedLibraryReload()
        }
        .onReceive(NotificationCenter.default.publisher(for: NSNotification.Name("TriggerScanAndScrape"))) { _ in triggerScanAndScrape() }
        .onChange(of: enableFastTooltip) { _, isFast in
            UserDefaults.standard.set(isFast ? 50 : 1000, forKey: "NSInitialToolTipDelay")
        }
        .onChange(of: isShowingManageSources) { _, isShown in
            if isShown {
                lanScanner.startScanning()
            } else {
                lanScanner.stopScanning()
            }
        }
        .onReceive(cacheManager.$cacheStatusMessage) { message in
            guard message != nil else { return }
            DispatchQueue.main.asyncAfter(deadline: .now() + 2.2) {
                if self.cacheManager.cacheStatusMessage == message {
                    withAnimation { self.cacheManager.cacheStatusMessage = nil }
                }
            }
        }
        .onDisappear {
            pendingLibraryReloadTask?.cancel()
        }
    }
    
    // ==========================================
    // 🌟 原生 Finder 添加目录逻辑 (无沙盒精简版)
    // ==========================================
    private func promptAndSaveLocalFolder() {
        let panel = NSOpenPanel()
        panel.message = "选择包含视频的本地或 NAS 文件夹"
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = false
        panel.allowsMultipleSelection = false
        
        if panel.runModal() == .OK, let selectedURL = panel.url {
            Task {
                do {
                    let absolutePath = MediaSourceProtocol.local.normalizedBaseURL(selectedURL.path)
                    // 直接存入数据库，丢弃了所有的 authConfig 和协议判断
                    try await AppDatabase.shared.dbQueue.write { db in
                        let count = try Int.fetchOne(db, sql: "SELECT COUNT(*) FROM mediaSource WHERE baseUrl = ?", arguments: [absolutePath]) ?? 0
                        if count == 0 {
                            try db.execute(sql: "INSERT INTO mediaSource (name, baseUrl, protocolType) VALUES (?, ?, ?)",
                                           arguments: [selectedURL.lastPathComponent, MediaSourceProtocol.local.normalizedBaseURL(absolutePath), MediaSourceProtocol.local.rawValue])
                        }
                    }
                    await MainActor.run {
                        loadData()
                        if isProcessing {
                            // 当前扫描中不打断任务，标记下一轮自动增量扫描。
                            needsRescanAfterCurrentRun = true
                            processingMessage = "扫描中，新增文件夹已加入下一轮队列..."
                        } else {
                            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { triggerScanAndScrape() }
                        }
                    }
                } catch { print("❌ 添加媒体源失败: \(error)") }
            }
        }
    }
    
    private var folderMenuPanel: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(mediaSources.isEmpty ? "暂无源" : "已挂载媒体源：").font(.headline).foregroundColor(.secondary)
            ForEach(mediaSources, id: \.id) { source in
                HStack(spacing: 10) {
                    Image(systemName: source.protocolKind == .webdav ? "network" : "folder.fill").foregroundColor(.blue)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(source.name)
                            .font(.body)
                            .fontWeight(.semibold)
                            .foregroundColor(.primary)
                            .lineLimit(1)
                        Text(source.isEnabled
                             ? (source.protocolKind == .webdav ? "WebDAV · 已开启" : "本地目录 · 已开启")
                             : (source.protocolKind == .webdav ? "WebDAV · 已关闭" : "本地目录 · 已关闭"))
                            .font(.caption2)
                            .foregroundColor(source.isEnabled ? .secondary : .orange)
                        if !source.isEnabled {
                            Text("索引与剧照默认保留 30 天，重新开启后会立即重新扫描。")
                                .font(.caption2)
                                .foregroundColor(.secondary)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                        if showMediaSourceRealPath {
                            Text(source.displayBaseURL())
                                .font(.caption)
                                .foregroundColor(.secondary)
                                .lineLimit(1)
                                .truncationMode(.middle)
                        }
                    }
                    .frame(maxWidth: 300, alignment: .leading)
                    Spacer()
                    Button(action: { toggleMediaSourceEnabled(source) }) {
                        Text(source.isEnabled ? "关闭" : "开启")
                            .font(.caption.weight(.semibold))
                            .padding(.horizontal, 10)
                            .padding(.vertical, 5)
                            .foregroundColor(source.isEnabled ? .orange : .green)
                            .background((source.isEnabled ? Color.orange : Color.green).opacity(0.12))
                            .clipShape(Capsule())
                    }
                    .buttonStyle(.plain)
                    Button(action: {
                        renamingSourceName = source.name
                        sourceToRename = source
                        isShowingRenameSourceSheet = true
                    }) {
                        Image(systemName: "pencil.circle.fill")
                            .foregroundColor(.secondary)
                    }.buttonStyle(.plain)
                    Button(action: {
                        sourcePendingRemoval = source
                        isShowingRemoveSourceSheet = true
                    }) {
                        Image(systemName: "minus.circle.fill").foregroundColor(.red.opacity(0.8))
                    }.buttonStyle(.plain)
                }
            }
            
            Divider()
                .padding(.top, 4)
            
            VStack(alignment: .leading, spacing: 10) {
                Text("新增媒体源").font(.subheadline.weight(.semibold)).foregroundColor(.secondary)
                Button(action: {
                    isShowingManageSources = false
                    promptAndSaveLocalFolder()
                }) {
                    Label("添加本地文件夹", systemImage: "folder.badge.plus")
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .buttonStyle(.plain)
                .accessibilityIdentifier("menu.addLocalFolder")
                
                Button(action: {
                    isShowingManageSources = false
                    prepareManualWebDAVBrowserLogin()
                    isShowingWebDAVPreScanLoginSheet = true
                }) {
                    Label("添加 WebDAV 媒体源", systemImage: "network")
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .buttonStyle(.plain)
                .accessibilityIdentifier("menu.addWebDAV")
            }

            Divider()
                .padding(.top, 2)

            VStack(alignment: .leading, spacing: 10) {
                HStack {
                    Text("预扫描 WebDAV")
                        .font(.subheadline.weight(.semibold))
                        .foregroundColor(.secondary)
                    Spacer()
                    Button(lanScanner.isScanning ? "预扫描中..." : "重新扫描") {
                        lanScanner.startScanning()
                    }
                    .buttonStyle(.plain)
                    .disabled(lanScanner.isScanning)
                    .accessibilityIdentifier("menu.prescanWebDAV")
                }

                if lanScanner.isScanning {
                    HStack(spacing: 8) {
                        ProgressView().controlSize(.small)
                        Text("正在扫描网络环境...")
                            .font(.caption)
                            .foregroundColor(.secondary)
                    }
                }

                if discoveredWebDAVDevices.isEmpty {
                    Text("扫描到 WebDAV 后，点击设备进入登录，再从共享文件夹右侧点星标挂载。")
                        .font(.caption)
                        .foregroundColor(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                } else {
                    VStack(alignment: .leading, spacing: 8) {
                        ForEach(discoveredWebDAVDevices) { device in
                            Button(action: {
                                prepareWebDAVBrowserLogin(for: device)
                                lanScanner.stopScanning()
                                isShowingManageSources = false
                                isShowingWebDAVPreScanLoginSheet = true
                            }) {
                                HStack(alignment: .top, spacing: 10) {
                                    Image(systemName: "globe.asia.australia.fill")
                                        .foregroundColor(.blue)
                                        .frame(width: 22)
                                        .padding(.top, 1)
                                    VStack(alignment: .leading, spacing: 3) {
                                        Text(device.name.isEmpty ? "WebDAV \(device.ipAddress)" : device.name)
                                            .foregroundColor(.primary)
                                            .lineLimit(1)
                                        Text(verbatim: "\(device.ipAddress):\(device.port)")
                                            .font(.caption.monospacedDigit())
                                            .foregroundColor(.secondary)
                                            .lineLimit(1)
                                    }
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                }
                                .padding(.horizontal, 10)
                                .padding(.vertical, 8)
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .background(.ultraThinMaterial)
                                .clipShape(RoundedRectangle(cornerRadius: 10))
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                        }
                    }
                }
            }
        }.padding(18).frame(minWidth: 280, maxWidth: 420)
    }

    private func renameSourceSheet() -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("重命名媒体源")
                .font(.title3.bold())
            Text("仅修改应用内显示名称，不会改动真实文件夹。")
                .font(.caption)
                .foregroundColor(.secondary)
            TextField("输入新的显示名称", text: $renamingSourceName)
                .textFieldStyle(.roundedBorder)
            HStack {
                Spacer()
                Button("取消") {
                    isShowingRenameSourceSheet = false
                    sourceToRename = nil
                    renamingSourceName = ""
                }
                Button("保存") {
                    guard let source = sourceToRename else { return }
                    renameMediaSource(source, to: renamingSourceName)
                }
                .keyboardShortcut(.defaultAction)
                .disabled(renamingSourceName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        .padding(20)
        .frame(width: 420)
    }

    private func removeSourceSheet() -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("移除媒体源")
                .font(.title3.bold())
                if let source = sourcePendingRemoval {
                    Text("将移除“\(source.name)”及其影片索引。")
                        .font(.subheadline)
                        .foregroundColor(.secondary)
                    if source.protocolKind == .webdav {
                        if WebDAVCredentialStore.shared.credentialID(from: source.authConfig) != nil {
                            let message = removeWebDAVCredentialsOnDelete
                            ? "设置中已开启“移除 WebDAV 源时同时删除凭据”，本次会一并清除保存的登录信息。"
                            : "设置中未开启“移除 WebDAV 源时同时删除凭据”，将保留保存的登录信息。可在设置里调整此行为。"
                            Text(message)
                                .font(.caption)
                                .foregroundColor(.secondary)
                                .fixedSize(horizontal: false, vertical: true)
                        } else {
                            Text("此 WebDAV 源未保存凭据。")
                                .font(.caption)
                                .foregroundColor(.secondary)
                        }
                    }
                } else {
                    Text("未找到待移除的媒体源。")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
                HStack {
                Spacer()
                Button("取消") {
                    isShowingRemoveSourceSheet = false
                    sourcePendingRemoval = nil
                }
                Button("确认移除", role: .destructive) {
                    if let source = sourcePendingRemoval {
                        let hasCredential = source.protocolKind == .webdav && WebDAVCredentialStore.shared.credentialID(from: source.authConfig) != nil
                        let shouldRemove = hasCredential ? removeWebDAVCredentialsOnDelete : false
                        removeMediaSource(source, removeCredential: shouldRemove)
                    }
                    isShowingRemoveSourceSheet = false
                    sourcePendingRemoval = nil
                }
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding(22)
        .frame(width: 420)
    }

    private func resetWebDAVDraft(useRecentHistory: Bool = true) {
        recentWebDAVHistory = PosterWallView.loadRecentWebDAVHistory()
        if useRecentHistory, let latest = recentWebDAVHistory.sorted(by: { $0.lastUsed > $1.lastUsed }).first {
            applyRecentWebDAVHistory(latest, shouldBumpUsage: false)
        } else if useRecentHistory, let snapshot = fetchLatestWebDAVSourceSnapshot() {
            applyRecentWebDAVHistory(snapshot, shouldBumpUsage: false)
        } else {
            webDAVName = ""
            webDAVBaseURL = ""
            webDAVUsername = ""
            webDAVPassword = ""
        }
        webDAVValidationMessage = nil
        webDAVValidationIsError = false
        webDAVIsTestingConnection = false
        webDAVLastPreflight = nil
    }

    private func applyRecentWebDAVHistory(_ item: RecentWebDAVHistoryItem, shouldBumpUsage: Bool) {
        if item.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
           let host = URL(string: item.baseURL)?.host {
            webDAVName = host
        } else {
            webDAVName = item.name
        }
        webDAVBaseURL = readableWebDAVURL(from: item.baseURL)
        webDAVUsername = item.username
        if let credentialID = item.credentialID,
           let credential = WebDAVCredentialStore.shared.loadCredential(id: credentialID) {
            webDAVUsername = credential.username
            webDAVPassword = credential.password
        } else {
            webDAVPassword = ""
        }
        if shouldBumpUsage { bumpHistoryUsage(for: item.id) }
    }

    private func bumpHistoryUsage(for id: UUID) {
        guard let index = recentWebDAVHistory.firstIndex(where: { $0.id == id }) else { return }
        recentWebDAVHistory[index].lastUsed = Date()
        recentWebDAVHistory.sort(by: { $0.lastUsed > $1.lastUsed })
        PosterWallView.saveRecentWebDAVHistory(recentWebDAVHistory)
    }

    private func recordRecentWebDAVHistory(name: String, baseURL: String, username: String, credentialID: String?) {
        let trimmedURL = baseURL.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedURL.isEmpty else { return }
        let trimmedName = name.trimmingCharacters(in: .whitespacesAndNewlines)
        let trimmedUser = username.trimmingCharacters(in: .whitespacesAndNewlines)
        var updated = recentWebDAVHistory.filter { $0.baseURL != trimmedURL }
        let newItem = RecentWebDAVHistoryItem(name: trimmedName, baseURL: trimmedURL, username: trimmedUser, credentialID: credentialID, lastUsed: Date())
        updated.insert(newItem, at: 0)
        if updated.count > 5 { updated = Array(updated.prefix(5)) }
        recentWebDAVHistory = updated
        PosterWallView.saveRecentWebDAVHistory(updated)
    }

    private func clearRecentWebDAVHistory() {
        recentWebDAVHistory = []
        PosterWallView.saveRecentWebDAVHistory([])
    }

    private func stripCredentialFromHistory(withID credentialID: String) {
        let filtered = recentWebDAVHistory.filter { $0.credentialID != credentialID }
        if filtered.count != recentWebDAVHistory.count {
            recentWebDAVHistory = filtered
            PosterWallView.saveRecentWebDAVHistory(filtered)
        }
    }

    private func ensureHistoryHasSource(source: MediaSource, credentialID: String) {
        let storedCredential = WebDAVCredentialStore.shared.loadCredential(id: credentialID)
        recordRecentWebDAVHistory(
            name: source.name,
            baseURL: source.baseUrl,
            username: storedCredential?.username ?? "",
            credentialID: credentialID
        )
    }
    
    private func fetchLatestWebDAVSourceSnapshot() -> RecentWebDAVHistoryItem? {
        guard let queue = AppDatabase.shared.dbQueue else { return nil }
        return try? queue.read { db -> RecentWebDAVHistoryItem? in
            guard let source = try MediaSource
                .filter(Column("protocolType") == MediaSourceProtocol.webdav.rawValue)
                .order(Column("id").desc)
                .fetchOne(db) else { return nil }
            let credentialID = WebDAVCredentialStore.shared.credentialID(from: source.authConfig)
            let username: String = {
                if let credentialID,
                   let stored = WebDAVCredentialStore.shared.loadCredential(id: credentialID) {
                    return stored.username
                }
                return ""
            }()
            return RecentWebDAVHistoryItem(
                name: source.name,
                baseURL: source.baseUrl,
                username: username,
                credentialID: credentialID,
                lastUsed: Date()
            )
        }
    }
    
    private func readableWebDAVURL(from raw: String) -> String {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return "" }
        return trimmed.removingPercentEncoding ?? trimmed
    }

    private static func loadRecentWebDAVHistory() -> [RecentWebDAVHistoryItem] {
        guard let data = UserDefaults.standard.data(forKey: recentWebDAVHistoryKey) else { return [] }
        return (try? JSONDecoder().decode([RecentWebDAVHistoryItem].self, from: data)) ?? []
    }

    private static func saveRecentWebDAVHistory(_ items: [RecentWebDAVHistoryItem]) {
        guard let data = try? JSONEncoder().encode(items) else {
            UserDefaults.standard.removeObject(forKey: recentWebDAVHistoryKey)
            return
        }
        UserDefaults.standard.set(data, forKey: recentWebDAVHistoryKey)
    }

    private func webDAVSourceSheet() -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("添加 WebDAV 媒体源")
                .font(.title3.bold())
                .accessibilityIdentifier("webdav.sheet.title")
            Text("支持 http/https。请直接填写 NAS 里的媒体文件夹地址（不要只填服务根）。例如：https://nas:5006/dav/Movies")
                .font(.caption)
                .foregroundColor(.secondary)

            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text("局域网发现")
                        .font(.subheadline.bold())
                    Spacer()
                    Button(lanScanner.isScanning ? "扫描中..." : "扫描设备") {
                        if lanScanner.isScanning {
                            lanScanner.stopScanning()
                        } else {
                            lanScanner.startScanning()
                        }
                    }
                    .disabled(lanScanner.isScanning)
                }

                if discoveredWebDAVDevices.isEmpty {
                    Text("点击“扫描设备”自动发现局域网中的 WebDAV 服务。")
                        .font(.caption)
                        .foregroundColor(.secondary)
                } else {
                    ScrollView {
                        VStack(alignment: .leading, spacing: 6) {
                            ForEach(discoveredWebDAVDevices) { device in
                                Button(action: {
                                    applyDiscoveredDevice(device)
                                }) {
                                    HStack(spacing: 8) {
                                        Image(systemName: "network")
                                            .foregroundColor(.secondary)
                                        Text(device.name.isEmpty ? device.ipAddress : device.name)
                                            .foregroundColor(.primary)
                                        Text(device.type.rawValue)
                                            .font(.caption2)
                                            .foregroundColor(.secondary)
                                        Spacer()
                                        Text(verbatim: "\(device.ipAddress):\(device.port)")
                                            .font(.caption)
                                            .foregroundColor(.secondary)
                                    }
                                }
                                .buttonStyle(.plain)
                            }
                        }
                    }
                    .frame(maxHeight: 140)
                }
            }
            .padding(10)
            .background(.ultraThinMaterial)
            .clipShape(RoundedRectangle(cornerRadius: 10))

            TextField("显示名称（可选）", text: $webDAVName)
                .textFieldStyle(.roundedBorder)
                .accessibilityIdentifier("webdav.name")
            HStack(spacing: 8) {
                TextField("WebDAV 地址，例如 https://nas:5006/dav/Media", text: $webDAVBaseURL)
                    .textFieldStyle(.roundedBorder)
                    .accessibilityIdentifier("webdav.baseURL")
                if webDAVIsTestingConnection {
                    ProgressView().controlSize(.small)
                }
                if !recentWebDAVHistory.isEmpty {
                    Menu("最近使用") {
                        ForEach(recentWebDAVHistory.sorted(by: { $0.lastUsed > $1.lastUsed })) { item in
                            Button(action: { applyRecentWebDAVHistory(item, shouldBumpUsage: true) }) {
                                VStack(alignment: .leading, spacing: 2) {
                                    Text((item.name.isEmpty ? (URL(string: item.baseURL)?.host ?? "未命名") : item.name))
                                        .font(.body)
                                    Text(readableWebDAVURL(from: item.baseURL))
                                        .font(.caption)
                                        .foregroundColor(.secondary)
                                }
                            }
                        }
                        Button("清除历史", role: .destructive) { clearRecentWebDAVHistory() }
                    }
                    .menuStyle(.borderlessButton)
                }
            }
            TextField("用户名（可选）", text: $webDAVUsername)
                .textFieldStyle(.roundedBorder)
                .accessibilityIdentifier("webdav.username")
            SecureField("密码（可选）", text: $webDAVPassword)
                .textFieldStyle(.roundedBorder)
                .accessibilityIdentifier("webdav.password")

            if let webDAVValidationMessage {
                Text(webDAVValidationMessage)
                    .font(.caption)
                    .foregroundColor(webDAVValidationIsError ? .red : .green)
            }

            HStack {
                Spacer()
                Button(webDAVIsTestingConnection ? "测试中..." : "测试连接") {
                    Task {
                        _ = await performWebDAVPreflight(showSuccessMessage: true)
                    }
                }
                .accessibilityIdentifier("webdav.testConnection")
                .disabled(webDAVIsTestingConnection)
                Button("取消") {
                    isShowingAddWebDAVSheet = false
                }
                .accessibilityIdentifier("webdav.cancel")
                Button("保存") {
                    saveWebDAVSource()
                }
                .accessibilityIdentifier("webdav.save")
                .keyboardShortcut(.defaultAction)
                .disabled(webDAVIsTestingConnection)
            }
        }
        .padding(20)
        .frame(width: 500)
        .onDisappear {
            lanScanner.stopScanning()
        }
    }

    private func prepareManualWebDAVBrowserLogin() {
        webDAVBrowserName = ""
        webDAVBrowserBaseURL = ""
        webDAVBrowserUsername = ""
        webDAVBrowserPassword = ""
        webDAVBrowserCredentialID = nil
        webDAVBrowserCurrentURL = ""
        webDAVBrowserPathStack = []
        webDAVBrowserItems = []
        webDAVBrowserMountedURLs = mountedWebDAVURLs(from: mediaSources)
        webDAVBrowserMessage = nil
        webDAVBrowserMessageIsError = false
        webDAVBrowserIsLoading = false
        webDAVBrowserMountingURL = nil
    }

    private func prepareWebDAVBrowserLogin(for device: DiscoveredDevice) {
        let scheme = device.type == .webdavHTTPS ? "https" : "http"
        webDAVBrowserName = device.name.isEmpty ? "WebDAV \(device.ipAddress)" : device.name
        webDAVBrowserBaseURL = "\(scheme)://\(device.ipAddress):\(device.port)"
        webDAVBrowserUsername = ""
        webDAVBrowserPassword = ""
        webDAVBrowserCredentialID = nil
        webDAVBrowserCurrentURL = ""
        webDAVBrowserPathStack = []
        webDAVBrowserItems = []
        webDAVBrowserMountedURLs = mountedWebDAVURLs(from: mediaSources)
        webDAVBrowserMessage = nil
        webDAVBrowserMessageIsError = false
        webDAVBrowserIsLoading = false
        webDAVBrowserMountingURL = nil
    }

    private func webDAVPreScanLoginSheet() -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("登录 WebDAV")
                .font(.title3.bold())
                .accessibilityIdentifier("webdav.sheet.title")
            Text("保存登录信息后会先进入共享文件夹列表；在目标文件夹右侧点星标即可挂载并立即扫描刮削。")
                .font(.caption)
                .foregroundColor(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            TextField("显示名称（可选）", text: $webDAVBrowserName)
                .textFieldStyle(.roundedBorder)
                .accessibilityIdentifier("webdav.name")
            TextField("WebDAV 地址，例如 https://nas:5006/dav", text: $webDAVBrowserBaseURL)
                .textFieldStyle(.roundedBorder)
                .accessibilityIdentifier("webdav.baseURL")
            TextField("用户名（可选）", text: $webDAVBrowserUsername)
                .textFieldStyle(.roundedBorder)
                .accessibilityIdentifier("webdav.username")
            SecureField("密码（可选）", text: $webDAVBrowserPassword)
                .textFieldStyle(.roundedBorder)
                .accessibilityIdentifier("webdav.password")

            if let webDAVBrowserMessage {
                Text(webDAVBrowserMessage)
                    .font(.caption)
                    .foregroundColor(webDAVBrowserMessageIsError ? .red : .secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack {
                Spacer()
                Button("取消") {
                    isShowingWebDAVPreScanLoginSheet = false
                    resetWebDAVBrowserState(clearLogin: true)
                }
                .accessibilityIdentifier("webdav.cancel")
                Button(webDAVBrowserIsLoading ? "读取中..." : "保存") {
                    saveWebDAVBrowserLoginAndBrowse()
                }
                .accessibilityIdentifier("webdav.save")
                .keyboardShortcut(.defaultAction)
                .disabled(webDAVBrowserIsLoading)
            }
        }
        .padding(20)
        .frame(width: 500)
    }

    private func webDAVFolderBrowserSheet() -> some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 10) {
                Text("选择 WebDAV 共享文件夹")
                    .font(.title3.bold())
                Spacer()
                if webDAVBrowserIsLoading {
                    ProgressView().controlSize(.small)
                }
            }

            VStack(alignment: .leading, spacing: 4) {
                Text(webDAVBrowserName.isEmpty ? "WebDAV" : webDAVBrowserName)
                    .font(.subheadline.weight(.semibold))
                Text(readableWebDAVURL(from: webDAVBrowserCurrentURL.isEmpty ? webDAVBrowserBaseURL : webDAVBrowserCurrentURL))
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }

            HStack {
                Button {
                    navigateWebDAVBrowserBack()
                } label: {
                    Label("返回上级", systemImage: "chevron.left")
                }
                .disabled(webDAVBrowserPathStack.isEmpty || webDAVBrowserIsLoading)
                Spacer()
                Button {
                    loadWebDAVBrowserDirectory(at: webDAVBrowserCurrentURL, replaceStackWith: webDAVBrowserPathStack)
                } label: {
                    Label("刷新", systemImage: "arrow.clockwise")
                }
                .disabled(webDAVBrowserCurrentURL.isEmpty || webDAVBrowserIsLoading)
            }

            if let webDAVBrowserMessage {
                Text(webDAVBrowserMessage)
                    .font(.caption)
                    .foregroundColor(webDAVBrowserMessageIsError ? .red : .secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            ScrollView {
                LazyVStack(alignment: .leading, spacing: 8) {
                    ForEach(webDAVBrowserItems) { item in
                        webDAVFolderBrowserRow(item)
                    }
                }
            }
            .frame(minHeight: 220, maxHeight: 360)
            .overlay {
                if webDAVBrowserItems.isEmpty && !webDAVBrowserIsLoading {
                    Text("当前目录没有可浏览的子文件夹。")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }

            HStack {
                Spacer()
                Button("关闭") {
                    isShowingWebDAVFolderBrowserSheet = false
                    resetWebDAVBrowserState(clearLogin: true)
                }
            }
        }
        .padding(20)
        .frame(width: 560)
    }

    private func webDAVFolderBrowserRow(_ item: WebDAVDirectoryItem) -> some View {
        let normalizedURL = normalizedWebDAVBrowserURL(item.url.absoluteString)
        let isMounted = webDAVBrowserMountedURLs.contains(normalizedURL)
        let isMounting = webDAVBrowserMountingURL == normalizedURL

        return HStack(spacing: 10) {
            Button {
                navigateWebDAVBrowser(to: item)
            } label: {
                HStack(spacing: 8) {
                    Image(systemName: "folder")
                        .foregroundColor(.blue)
                    Text(item.displayName)
                        .foregroundColor(.primary)
                        .lineLimit(1)
                    Spacer()
                    Image(systemName: "chevron.right")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }
            .buttonStyle(.plain)
            .disabled(webDAVBrowserIsLoading || isMounting)

            Button {
                mountWebDAVBrowserFolder(item)
            } label: {
                if isMounting {
                    ProgressView().controlSize(.small)
                } else {
                    Image(systemName: isMounted ? "star.fill" : "star")
                        .foregroundColor(isMounted ? .yellow : .secondary)
                }
            }
            .buttonStyle(.plain)
            .help(isMounted ? "已挂载" : "标星并挂载此文件夹")
            .disabled(isMounted || webDAVBrowserIsLoading || isMounting)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
        .background(.ultraThinMaterial)
        .clipShape(RoundedRectangle(cornerRadius: 10))
    }

    private func saveWebDAVBrowserLoginAndBrowse() {
        let normalizedInput = MediaSourceProtocol.webdav.normalizedBaseURL(webDAVBrowserBaseURL)
        guard MediaSourceProtocol.webdav.isValidBaseURL(normalizedInput) else {
            webDAVBrowserMessage = "WebDAV 地址无效，请输入 http(s):// 开头且包含主机名的地址。"
            webDAVBrowserMessageIsError = true
            return
        }

        let parsedURL = URL(string: normalizedInput)
        let username = webDAVBrowserUsername.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            ? (parsedURL?.user ?? "")
            : webDAVBrowserUsername.trimmingCharacters(in: .whitespacesAndNewlines)
        let password = webDAVBrowserPassword.isEmpty ? (parsedURL?.password ?? "") : webDAVBrowserPassword
        let sanitizedURL = sanitizedWebDAVBrowserURL(normalizedInput)

        webDAVBrowserIsLoading = true
        webDAVBrowserMessage = nil
        webDAVBrowserMessageIsError = false

        Task {
            do {
                let items = try await WebDAVDirectoryBrowser().listDirectories(
                    at: normalizedInput,
                    username: username,
                    password: password
                )
                let credentialID: String?
                if !username.isEmpty {
                    credentialID = try WebDAVCredentialStore.shared.saveCredential(username: username, password: password)
                } else {
                    credentialID = nil
                }

                await MainActor.run {
                    webDAVBrowserUsername = username
                    webDAVBrowserPassword = password
                    webDAVBrowserCredentialID = credentialID
                    webDAVBrowserBaseURL = sanitizedURL
                    webDAVBrowserCurrentURL = sanitizedURL
                    webDAVBrowserPathStack = []
                    webDAVBrowserItems = items
                    webDAVBrowserMountedURLs = mountedWebDAVURLs(from: mediaSources)
                    webDAVBrowserIsLoading = false
                    webDAVBrowserMessage = items.isEmpty ? "连接成功，但当前目录没有可浏览的子文件夹。" : nil
                    webDAVBrowserMessageIsError = false
                    isShowingWebDAVPreScanLoginSheet = false
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) {
                        isShowingWebDAVFolderBrowserSheet = true
                    }
                }
            } catch {
                await MainActor.run {
                    webDAVBrowserIsLoading = false
                    webDAVBrowserMessage = error.localizedDescription
                    webDAVBrowserMessageIsError = true
                }
            }
        }
    }

    private func navigateWebDAVBrowser(to item: WebDAVDirectoryItem) {
        let nextStack = webDAVBrowserCurrentURL.isEmpty ? [] : webDAVBrowserPathStack + [webDAVBrowserCurrentURL]
        loadWebDAVBrowserDirectory(at: item.url.absoluteString, replaceStackWith: nextStack)
    }

    private func navigateWebDAVBrowserBack() {
        guard let previous = webDAVBrowserPathStack.last else { return }
        var nextStack = webDAVBrowserPathStack
        nextStack.removeLast()
        loadWebDAVBrowserDirectory(at: previous, replaceStackWith: nextStack)
    }

    private func loadWebDAVBrowserDirectory(at rawURL: String, replaceStackWith stack: [String]) {
        let targetURL = sanitizedWebDAVBrowserURL(rawURL)
        let username = webDAVBrowserUsername
        let password = webDAVBrowserPassword

        webDAVBrowserIsLoading = true
        webDAVBrowserMessage = nil
        webDAVBrowserMessageIsError = false

        Task {
            do {
                let items = try await WebDAVDirectoryBrowser().listDirectories(
                    at: rawURL,
                    username: username,
                    password: password
                )
                await MainActor.run {
                    webDAVBrowserCurrentURL = targetURL
                    webDAVBrowserPathStack = stack
                    webDAVBrowserItems = items
                    webDAVBrowserMountedURLs = mountedWebDAVURLs(from: mediaSources)
                    webDAVBrowserIsLoading = false
                    webDAVBrowserMessage = items.isEmpty ? "当前目录没有可浏览的子文件夹。" : nil
                    webDAVBrowserMessageIsError = false
                }
            } catch {
                await MainActor.run {
                    webDAVBrowserIsLoading = false
                    webDAVBrowserMessage = error.localizedDescription
                    webDAVBrowserMessageIsError = true
                }
            }
        }
    }

    private func mountWebDAVBrowserFolder(_ item: WebDAVDirectoryItem) {
        let baseURL = normalizedWebDAVBrowserURL(item.url.absoluteString)
        let authConfig = webDAVBrowserCredentialID.map { WebDAVCredentialStore.shared.authReference(for: $0) }
        let sourceName = item.displayName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "WebDAV 媒体源" : item.displayName
        let username = webDAVBrowserUsername
        let credentialID = webDAVBrowserCredentialID

        webDAVBrowserMountingURL = baseURL
        webDAVBrowserMessage = nil
        webDAVBrowserMessageIsError = false

        Task {
            do {
                let sourceID = try await AppDatabase.shared.dbQueue.write { db -> Int64 in
                    if let existing = try MediaSource.fetchOne(
                        db,
                        sql: "SELECT * FROM mediaSource WHERE protocolType = ? AND baseUrl = ? LIMIT 1",
                        arguments: [MediaSourceProtocol.webdav.rawValue, baseURL]
                    ), let existingID = existing.id {
                        try db.execute(
                            sql: """
                            UPDATE mediaSource
                            SET authConfig = COALESCE(?, authConfig),
                                isEnabled = 1,
                                disabledAt = NULL
                            WHERE id = ?
                            """,
                            arguments: [authConfig, existingID]
                        )
                        return existingID
                    }

                    let source = MediaSource(
                        id: nil,
                        name: sourceName,
                        protocolType: MediaSourceProtocol.webdav.rawValue,
                        baseUrl: baseURL,
                        authConfig: authConfig
                    )
                    try source.insert(db)
                    return db.lastInsertedRowID
                }

                await MainActor.run {
                    webDAVBrowserMountedURLs.insert(baseURL)
                    webDAVBrowserMountingURL = nil
                    if let credentialID {
                        recordRecentWebDAVHistory(name: sourceName, baseURL: baseURL, username: username, credentialID: credentialID)
                    }
                    isShowingWebDAVFolderBrowserSheet = false
                    loadData()
                    resetWebDAVBrowserState(clearLogin: true)
                    if isProcessing {
                        needsRescanAfterCurrentRun = true
                        processingMessage = "扫描中，标星的 WebDAV 文件夹已加入下一轮队列..."
                    } else {
                        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) {
                            triggerScanAndScrape(sourceID: sourceID)
                        }
                    }
                }
            } catch {
                await MainActor.run {
                    webDAVBrowserMountingURL = nil
                    webDAVBrowserMessage = error.localizedDescription
                    webDAVBrowserMessageIsError = true
                }
            }
        }
    }

    private func resetWebDAVBrowserState(clearLogin: Bool) {
        if clearLogin {
            webDAVBrowserName = ""
            webDAVBrowserBaseURL = ""
            webDAVBrowserUsername = ""
            webDAVBrowserPassword = ""
            webDAVBrowserCredentialID = nil
        }
        webDAVBrowserCurrentURL = ""
        webDAVBrowserPathStack = []
        webDAVBrowserItems = []
        webDAVBrowserMessage = nil
        webDAVBrowserMessageIsError = false
        webDAVBrowserIsLoading = false
        webDAVBrowserMountingURL = nil
    }

    private func sanitizedWebDAVBrowserURL(_ raw: String) -> String {
        let normalized = MediaSourceProtocol.webdav.normalizedBaseURL(raw)
        guard var components = URLComponents(string: normalized) else { return normalized }
        components.user = nil
        components.password = nil
        return MediaSourceProtocol.webdav.normalizedBaseURL(components.string ?? normalized)
    }

    private func normalizedWebDAVBrowserURL(_ raw: String) -> String {
        sanitizedWebDAVBrowserURL(raw)
    }

    private func mountedWebDAVURLs(from sources: [MediaSource]) -> Set<String> {
        Set(sources.compactMap { source in
            guard source.protocolKind == .webdav else { return nil }
            return normalizedWebDAVBrowserURL(source.baseUrl)
        })
    }

    private func applyDiscoveredDevice(_ device: DiscoveredDevice) {
        switch device.type {
        case .webdavHTTP:
            webDAVBaseURL = "http://\(device.ipAddress):\(device.port)"
            if webDAVName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                webDAVName = device.name.isEmpty ? "WebDAV \(device.ipAddress)" : device.name
            }
            webDAVValidationMessage = nil
        case .webdavHTTPS:
            webDAVBaseURL = "https://\(device.ipAddress):\(device.port)"
            if webDAVName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                webDAVName = device.name.isEmpty ? "WebDAV \(device.ipAddress)" : device.name
            }
            webDAVValidationMessage = nil
        case .smb:
            break
        }
    }

    private func saveWebDAVSource() {
        let normalizedURL = MediaSourceProtocol.webdav.normalizedBaseURL(webDAVBaseURL)
        guard MediaSourceProtocol.webdav.isValidBaseURL(normalizedURL) else {
            webDAVValidationMessage = "WebDAV 地址无效，请输入 http(s):// 开头且包含主机名的地址。"
            webDAVValidationIsError = true
            return
        }
        if let pathError = MediaSourceProtocol.webdav.webDAVPathValidationError(normalizedURL) {
            webDAVValidationMessage = pathError
            webDAVValidationIsError = true
            return
        }

        let finalName: String = {
            let trimmed = webDAVName.trimmingCharacters(in: .whitespacesAndNewlines)
            if !trimmed.isEmpty { return trimmed }
            return URL(string: normalizedURL)?.host ?? "WebDAV 媒体源"
        }()

        let username = webDAVUsername.trimmingCharacters(in: .whitespacesAndNewlines)
        let password = webDAVPassword
        var credentialIDForHistory: String? = nil

        Task {
            let preflightPassed = await performWebDAVPreflight(showSuccessMessage: false)
            guard preflightPassed else { return }

            do {
                let authConfig: String?
                if !username.isEmpty {
                    let credentialID = try WebDAVCredentialStore.shared.saveCredential(username: username, password: password)
                    credentialIDForHistory = credentialID
                    authConfig = WebDAVCredentialStore.shared.authReference(for: credentialID)
                } else {
                    authConfig = nil
                }

                try await AppDatabase.shared.dbQueue.write { db in
                    let count = try Int.fetchOne(
                        db,
                        sql: "SELECT COUNT(*) FROM mediaSource WHERE protocolType = ? AND baseUrl = ?",
                        arguments: [MediaSourceProtocol.webdav.rawValue, normalizedURL]
                    ) ?? 0
                    guard count == 0 else {
                        throw NSError(
                            domain: "PosterWallView",
                            code: 1001,
                            userInfo: [NSLocalizedDescriptionKey: "该 WebDAV 地址已存在。"]
                        )
                    }

                    let source = MediaSource(
                        id: nil,
                        name: finalName,
                        protocolType: MediaSourceProtocol.webdav.rawValue,
                        baseUrl: normalizedURL,
                        authConfig: authConfig
                    )
                    try source.insert(db)
                }

                await MainActor.run {
                    recordRecentWebDAVHistory(name: finalName, baseURL: normalizedURL, username: username, credentialID: credentialIDForHistory)
                    isShowingAddWebDAVSheet = false
                    webDAVValidationMessage = nil
                    webDAVValidationIsError = false
                    loadData()
                    if isProcessing {
                        needsRescanAfterCurrentRun = true
                        processingMessage = "扫描中，新增 WebDAV 源已加入下一轮队列..."
                    } else {
                        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { triggerScanAndScrape() }
                    }
                }
            } catch {
                await MainActor.run {
                    webDAVValidationMessage = error.localizedDescription
                    webDAVValidationIsError = true
                }
            }
        }
    }

    private func performWebDAVPreflight(showSuccessMessage: Bool) async -> Bool {
        let normalizedURL = MediaSourceProtocol.webdav.normalizedBaseURL(webDAVBaseURL)
        if let pathError = MediaSourceProtocol.webdav.webDAVPathValidationError(normalizedURL) {
            let result = WebDAVPreflightResult(
                isReachable: false,
                category: .config,
                message: pathError,
                httpStatusCode: nil,
                urlErrorCode: nil,
                sanitizedEndpoint: MediaSourceScanDiagnosticsFormatter.sanitizedEndpoint(from: normalizedURL)
            )
            await MainActor.run {
                webDAVValidationMessage = pathError
                webDAVValidationIsError = true
                webDAVIsTestingConnection = false
                webDAVLastPreflight = result
            }
            return false
        }

        await MainActor.run {
            webDAVIsTestingConnection = true
            webDAVValidationMessage = nil
            webDAVValidationIsError = false
        }

        let checker = WebDAVPreflightChecker()
        let result = await checker.check(
            baseURL: webDAVBaseURL,
            username: webDAVUsername,
            password: webDAVPassword
        )

        await MainActor.run {
            webDAVIsTestingConnection = false
            webDAVLastPreflight = result
            if result.isReachable {
                if showSuccessMessage {
                    if let code = result.httpStatusCode {
                        webDAVValidationMessage = "连接测试成功（HTTP \(code)）。"
                    } else {
                        webDAVValidationMessage = "连接测试成功。"
                    }
                    webDAVValidationIsError = false
                }
            } else {
                webDAVValidationMessage = result.message
                webDAVValidationIsError = true
            }
        }

        return result.isReachable
    }

    private func loadData() {
        if isLoadingLibraryData {
            needsLibraryReloadAfterCurrentLoad = true
            return
        }
        isLoadingLibraryData = true
        needsLibraryReloadAfterCurrentLoad = false

        DispatchQueue.global(qos: .utility).async {
            do {
                try libraryManager.cleanupExpiredDisabledSources()
                let fetchedMovies = try libraryManager.fetchAllMovies()
                let fetchedSources = try AppDatabase.shared.dbQueue.read { db in
                    try MediaSource.fetchManageableSources(in: db)
                }
                let fetchedContinueWatching = try AppDatabase.shared.dbQueue.read { db in
                    try Movie.fetchVisibleContinueWatching(in: db)
                }
                DispatchQueue.main.async {
                    self.movies = fetchedMovies
                    self.mediaSources = fetchedSources
                    self.webDAVBrowserMountedURLs = self.mountedWebDAVURLs(from: fetchedSources)
                    self.continueWatchingMovies = fetchedContinueWatching
                    self.completeLoadDataCycle()
                }
            } catch {
                DispatchQueue.main.async {
                    self.completeLoadDataCycle()
                }
            }
        }
    }

    private func completeLoadDataCycle() {
        isLoadingLibraryData = false
        let shouldReload = needsLibraryReloadAfterCurrentLoad
        needsLibraryReloadAfterCurrentLoad = false
        if shouldReload {
            loadData()
        }
    }

    private func scheduleDebouncedLibraryReload() {
        pendingLibraryReloadTask?.cancel()
        pendingLibraryReloadTask = Task {
            try? await Task.sleep(nanoseconds: 300_000_000)
            if Task.isCancelled { return }
            await MainActor.run { loadData() }
        }
    }
    
    private func removeMediaSource(_ source: MediaSource, removeCredential: Bool) {
        let removingSourceID = source.id
        if let sid = removingSourceID {
            removedSourceIDsDuringRun.insert(sid)
            if activeScanningSourceID == sid {
                currentScanTask?.cancel()
                needsRescanAfterCurrentRun = true
            }
        }
        if source.protocolKind == .webdav,
           let credentialID = WebDAVCredentialStore.shared.credentialID(from: source.authConfig) {
            if removeCredential {
                WebDAVCredentialStore.shared.removeCredential(id: credentialID)
                stripCredentialFromHistory(withID: credentialID)
            } else {
                ensureHistoryHasSource(source: source, credentialID: credentialID)
            }
        }
        Task {
            do {
                let removedFileIDs = try await AppDatabase.shared.dbQueue.write { db -> [String] in
                    guard let sid = source.id else {
                        try db.execute(sql: "DELETE FROM movie WHERE id NOT IN (SELECT DISTINCT movieId FROM videoFile WHERE movieId IS NOT NULL)")
                        return []
                    }
                    let removedFileIDs = try String.fetchAll(
                        db,
                        sql: "SELECT id FROM videoFile WHERE sourceId = ?",
                        arguments: [sid]
                    )
                    try db.execute(sql: "DELETE FROM videoFile WHERE sourceId = ?", arguments: [sid])
                    try db.execute(sql: "DELETE FROM mediaSource WHERE id = ?", arguments: [sid])
                    try db.execute(sql: "DELETE FROM movie WHERE id NOT IN (SELECT DISTINCT movieId FROM videoFile WHERE movieId IS NOT NULL)")
                    return removedFileIDs
                }
                ThumbnailManager.shared.removeAssets(for: removedFileIDs)
                if !keepLocalPosters { PosterManager.shared.clearCache() }
                await MainActor.run { loadData() }
            } catch { print("大清洗失败: \(error)") }
        }
    }

    private func renameMediaSource(_ source: MediaSource, to newName: String) {
        let trimmed = newName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, let sid = source.id else { return }
        Task {
            do {
                try await AppDatabase.shared.dbQueue.write { db in
                    try db.execute(
                        sql: "UPDATE mediaSource SET name = ? WHERE id = ?",
                        arguments: [trimmed, sid]
                    )
                }
                await MainActor.run {
                    isShowingRenameSourceSheet = false
                    sourceToRename = nil
                    renamingSourceName = ""
                    loadData()
                }
            } catch {
                print("❌ 重命名媒体源失败: \(error)")
            }
        }
    }
    
    private func toggleMediaSourceEnabled(_ source: MediaSource) {
        guard let sid = source.id else { return }
        let enabling = !source.isEnabled
        if !enabling {
            removedSourceIDsDuringRun.insert(sid)
        }

        Task {
            do {
                let disabledAt = enabling ? nil : Date().timeIntervalSince1970
                try await AppDatabase.shared.dbQueue.write { db in
                    try db.execute(
                        sql: "UPDATE mediaSource SET isEnabled = ?, disabledAt = ? WHERE id = ?",
                        arguments: [enabling, disabledAt, sid]
                    )
                }

                await MainActor.run {
                    NotificationCenter.default.post(name: .libraryUpdated, object: nil)
                    loadData()
                    if enabling {
                        removedSourceIDsDuringRun.remove(sid)
                        if isProcessing {
                            needsRescanAfterCurrentRun = true
                            processingMessage = "扫描中，重新开启的媒体源已加入下一轮同步..."
                        } else {
                            DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
                                triggerScanAndScrape(sourceID: sid)
                            }
                        }
                    } else if activeScanningSourceID == sid {
                        processingMessage = "当前媒体源已关闭，本轮结束后将自动刷新首页。"
                    }
                }
            } catch {
                print("❌ 切换媒体源状态失败: \(error)")
            }
        }
    }

    private func finishScanRun(reloadData: Bool) {
        activeScanningSourceID = nil
        removedSourceIDsDuringRun = []
        if reloadData {
            loadData()
        }
        let shouldChainRescan = needsRescanAfterCurrentRun
        needsRescanAfterCurrentRun = false
        withAnimation { isProcessing = false }
        if shouldChainRescan {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) {
                triggerScanAndScrape()
            }
        }
    }

    private func triggerScanAndScrape(sourceID: Int64? = nil) {
        guard !isProcessing else { return }
        withAnimation {
            isProcessing = true
            processingMessage = "准备扫描..."
        }
        removedSourceIDsDuringRun = []
        activeScanningSourceID = nil
        
        currentScanTask?.cancel()
        currentScanTask = Task(priority: .utility) {
            do {
                if Task.isCancelled {
                    await MainActor.run { finishScanRun(reloadData: true) }
                    return
                }
                try? libraryManager.cleanupExpiredDisabledSources()
                let validSources = try await AppDatabase.shared.dbQueue.read { db in
                    if let sourceID {
                        if let source = try MediaSource.fetchEnabledScannableSource(id: sourceID, in: db) {
                            return [source]
                        }
                        return []
                    }
                    return try MediaSource.fetchEnabledScannableSources(in: db)
                }
                
                if validSources.isEmpty {
                    await MainActor.run { finishScanRun(reloadData: true) }
                    return
                }
                
                await MainActor.run { withAnimation { self.processingMessage = "扫描目录中..." } }
                for (index, source) in validSources.enumerated() {
                    if Task.isCancelled {
                        await MainActor.run { finishScanRun(reloadData: true) }
                        return
                    }
                    if let sid = source.id, removedSourceIDsDuringRun.contains(sid) { continue }
                    await MainActor.run { activeScanningSourceID = source.id }
                    await MainActor.run {
                        withAnimation {
                            self.processingMessage = "扫描中 (\(index + 1)/\(validSources.count))：\(source.name)"
                        }
                    }
                    let result = await libraryManager.scanLocalSourceWithResult(source)
                    await MainActor.run { activeScanningSourceID = nil }
                    await MainActor.run {
                        withAnimation {
                            self.processingMessage = result.isSuccess
                                ? "完成：\(source.name)（新增\(result.insertedCount) 移除\(result.removedCount)）"
                                : "失败：\(source.name)（\(result.errorCategory?.displayName ?? "未知错误")）"
                        }
                    }
                }
                
                if Task.isCancelled {
                    await MainActor.run { finishScanRun(reloadData: true) }
                    return
                }
                await MainActor.run { withAnimation { self.processingMessage = "全网刮削中..." } }
                try? await libraryManager.processUnmatchedFiles()
                
                if Task.isCancelled {
                    await MainActor.run { finishScanRun(reloadData: true) }
                    return
                }
                
                await MainActor.run { withAnimation { self.processingMessage = "补齐分集剧照..." } }
                ThumbnailManager.shared.enqueueMissingEpisodeThumbnailsAcrossLibrary()
                
                await MainActor.run {
                    finishScanRun(reloadData: true)
                }
            } catch {
                await MainActor.run { finishScanRun(reloadData: true) }
            }
        }
    }
    
}

// 🌟 彻底删除了原先几百行的内网目录浏览器 (SourceFileBrowserView)
// 因为我们现在全部交给原生的 Finder 来选了！
