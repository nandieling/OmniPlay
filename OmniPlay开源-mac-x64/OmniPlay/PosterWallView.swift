import SwiftUI
import GRDB
import AppKit
import Network

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

private enum WebDAVCredentialFlow: Sendable {
    case manual
    case discovered
}

extension MediaSource: Hashable {
    public static func == (lhs: MediaSource, rhs: MediaSource) -> Bool { return lhs.id == rhs.id }
    public func hash(into hasher: inout Hasher) { hasher.combine(id) }
}

extension View { @ViewBuilder func conditionalHelp(_ text: String, show: Bool) -> some View { if show { self.help(text) } else { self } } }

enum MovieSortOption: String, CaseIterable { case name = "名称"; case rating = "评分"; case year = "上映年份" }

private actor LocalNetworkPermissionRequester {
    static let shared = LocalNetworkPermissionRequester()
    private var hasRequested = false

    func requestIfNeeded() async {
        guard !hasRequested else { return }
        hasRequested = true

        let params = NWParameters.tcp
        params.includePeerToPeer = true
        let browser = NWBrowser(for: .bonjour(type: "_http._tcp", domain: nil), using: params)
        let queue = DispatchQueue(label: "nan.omniplay.localnetwork.permission")
        browser.start(queue: queue)
        try? await Task.sleep(nanoseconds: 1_200_000_000)
        browser.cancel()
    }
}

struct PosterWallView: View {
    private static let recentWebDAVHistoryKey = "PosterWallRecentWebDAVHistory"
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
    
    @AppStorage("keepLocalPosters") var keepLocalPosters = true
    @AppStorage("autoScanOnStartup") var autoScanOnStartup = true
    @AppStorage("enableFastTooltip") var enableFastTooltip = true
    @AppStorage("showMediaSourceRealPath") var showMediaSourceRealPath = true
    @AppStorage("removeWebDAVCredentialWhenRemovingSource") var removeWebDAVCredentialWhenRemovingSource = false
    
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
    @State private var lastScanResults: [MediaSourceScanResult] = []
    @State private var lastScanDiagnosticsText: String = ""
    @State private var lastPreflightDiagnosticsText: String = ""
    @State private var scanSummaryMessage: String = ""
    @State private var isShowingScanSummaryAlert: Bool = false
    @State private var activeScanningSourceID: Int64? = nil
    @State private var removedSourceIDsDuringRun: Set<Int64> = []
    @State private var recentWebDAVHistory: [RecentWebDAVHistoryItem] = PosterWallView.loadRecentWebDAVHistory()
    @State private var isShowingRemoveSourceSheet = false
    @State private var sourcePendingRemoval: MediaSource? = nil
    @State private var isShowingDiscoveredWebDAVLoginSheet = false
    @State private var isShowingWebDAVFolderPickerSheet = false
    @State private var webDAVBrowserBaseURL: String = ""
    @State private var webDAVBrowserDisplayName: String = ""
    @State private var webDAVBrowserUsername: String = ""
    @State private var webDAVBrowserPassword: String = ""
    @State private var webDAVBrowserCredentialID: String? = nil
    @State private var webDAVSharedFolders: [WebDAVDirectoryItem] = []
    @State private var webDAVFolderListMessage: String? = nil
    @State private var webDAVFolderListIsError: Bool = false
    @State private var webDAVIsLoadingFolders: Bool = false
    
    // 🌟 彻底删除了所有与 network 相关的 State
    
    let columns = [GridItem(.adaptive(minimum: 160), spacing: 20)]
    init() {
        let isFast = UserDefaults.standard.bool(forKey: "enableFastTooltip")
        UserDefaults.standard.set(isFast ? 50 : 1000, forKey: "NSInitialToolTipDelay")
    }
    
    var displayedMovies: [Movie] { var result = movies; if !searchText.isEmpty { result = result.filter { $0.title.localizedCaseInsensitiveContains(searchText) } }; result.sort { m1, m2 in let isLess: Bool; switch selectedSortOption { case .name: isLess = m1.title.localizedStandardCompare(m2.title) == .orderedAscending; case .year: isLess = (m1.releaseDate ?? "") < (m2.releaseDate ?? ""); case .rating: isLess = (m1.voteAverage ?? 0.0) < (m2.voteAverage ?? 0.0) }; return isAscending ? isLess : !isLess }; return result }
    private var scannableProtocolValues: [String] { [MediaSourceProtocol.local.rawValue, MediaSourceProtocol.webdav.rawValue] }
    private var discoveredWebDAVDevices: [DiscoveredDevice] {
        lanScanner.discoveredDevices.filter { $0.type == .webdavHTTP || $0.type == .webdavHTTPS }
    }
    private var unifiedDiagnosticsText: String {
        [lastPreflightDiagnosticsText, lastScanDiagnosticsText]
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
            .joined(separator: "\n\n----------------\n\n")
    }

    var body: some View {
        NavigationStack {
            ZStack(alignment: .topTrailing) {
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
                                            }
                                            .buttonStyle(.plain)
                                            .focusable(false)
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
                                    NavigationLink(destination: MovieDetailView(movie: movie)) {
                                        MovieCardView(movie: movie)
                                    }
                                    .buttonStyle(.plain)
                                    .focusable(false)
                                }
                            }.padding(.horizontal, 25).padding(.bottom, 25).animation(.easeInOut, value: displayedMovies.count)
                        }
                    }
                }.frame(maxWidth: .infinity, maxHeight: .infinity)
                
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
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity).navigationTitle("我的觅影库")
            .toolbar {
                ToolbarItemGroup(placement: .navigation) { if !thumbManager.progressMessage.isEmpty { HStack { ProgressView().controlSize(.small); Text(thumbManager.progressMessage).font(.caption).foregroundColor(.secondary).lineLimit(1).frame(maxWidth: 200, alignment: .leading) } } }
                ToolbarItemGroup(placement: .primaryAction) {
                    Button(action: { withAnimation { OfflineCacheManager.shared.isCacheModeActive.toggle() } }) {
                        Label("缓存模式", systemImage: OfflineCacheManager.shared.isCacheModeActive ? "icloud.fill" : "icloud")
                            .foregroundColor(OfflineCacheManager.shared.isCacheModeActive ? theme.accent : .secondary)
                    }
                    .conditionalHelp("切换离线缓存编辑模式", show: enableFastTooltip)
                    
                    if isProcessing { HStack(spacing: 8) { ProgressView().controlSize(.small); Text(processingMessage).font(.subheadline).foregroundColor(.secondary) }.padding(.trailing, 10) }
                    if !unifiedDiagnosticsText.isEmpty {
                        Button(action: { copyLastDiagnosticsToPasteboard() }) {
                            Label("复制诊断", systemImage: "doc.on.doc")
                        }
                        .accessibilityIdentifier("toolbar.copyDiagnostics")
                        .conditionalHelp("复制最近一次扫描失败或 WebDAV 预检失败的诊断信息", show: enableFastTooltip)
                    }
                    
                    Button(action: { isShowingManageSources.toggle() }) {
                        Label("媒体源管理", systemImage: "externaldrive.badge.plus")
                    }
                        .accessibilityIdentifier("toolbar.addSource")
                        .conditionalHelp("管理已挂载目录、添加本地文件夹或 WebDAV 媒体源", show: enableFastTooltip)
                        .popover(isPresented: $isShowingManageSources, arrowEdge: .top) { folderMenuPanel }
                    Button(action: { triggerScanAndScrape() }) { Label("同步", systemImage: "arrow.triangle.2.circlepath") }
                        .accessibilityIdentifier("toolbar.sync")
                        .disabled(isProcessing)
                        .conditionalHelp("重新扫描目录并刷新刮削结果", show: enableFastTooltip)
                    Button(action: { showSettings = true }) { Label("设置", systemImage: "gearshape") }
                        .accessibilityIdentifier("toolbar.settings")
                        .conditionalHelp("打开偏好设置", show: enableFastTooltip)
                }
            }
            .alert("同步提示", isPresented: $isShowingScanSummaryAlert) {
                Button("复制诊断信息") { copyLastDiagnosticsToPasteboard() }
                Button("我知道了", role: .cancel) {}
            } message: {
                Text(scanSummaryMessage)
            }
            .sheet(isPresented: $showSettings) { SettingsView() }
            .sheet(isPresented: $isShowingRenameSourceSheet) {
                renameSourceSheet()
            }
            .sheet(isPresented: $isShowingAddWebDAVSheet) {
                webDAVSourceSheet()
            }
            .sheet(isPresented: $isShowingDiscoveredWebDAVLoginSheet) {
                discoveredWebDAVLoginSheet()
            }
            .sheet(isPresented: $isShowingWebDAVFolderPickerSheet) {
                webDAVFolderPickerSheet()
            }
            .sheet(isPresented: $isShowingRemoveSourceSheet) {
                removeSourceSheet()
            }
            // 🌟 彻底删除了那个恶心的 SMB/WebDAV Sheet 弹窗！
        }
        .onAppear {
            loadData()
            if ProcessInfo.processInfo.environment["UITEST_OPEN_WEBDAV_SHEET"] == "1" {
                resetWebDAVDraft(useRecentHistory: false)
                isShowingAddWebDAVSheet = true
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
                    let mountedSourceID = try await AppDatabase.shared.dbQueue.write { db -> Int64? in
                        if let existingID = try Int64.fetchOne(
                            db,
                            sql: "SELECT id FROM mediaSource WHERE baseUrl = ? AND protocolType = ? LIMIT 1",
                            arguments: [absolutePath, MediaSourceProtocol.local.rawValue]
                        ) {
                            try db.execute(
                                sql: "UPDATE mediaSource SET name = ?, isEnabled = 1, disabledAt = NULL WHERE id = ?",
                                arguments: [selectedURL.lastPathComponent, existingID]
                            )
                            return existingID
                        } else {
                            try db.execute(sql: "INSERT INTO mediaSource (name, baseUrl, protocolType) VALUES (?, ?, ?)",
                                           arguments: [selectedURL.lastPathComponent, MediaSourceProtocol.local.normalizedBaseURL(absolutePath), MediaSourceProtocol.local.rawValue])
                            return db.lastInsertedRowID
                        }
                    }
                    await MainActor.run {
                        loadData()
                        if isProcessing {
                            // 当前扫描中不打断任务，标记下一轮自动增量扫描。
                            needsRescanAfterCurrentRun = true
                            processingMessage = "扫描中，新增文件夹已加入下一轮队列..."
                        } else {
                            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { triggerScanAndScrape(onlySourceID: mountedSourceID) }
                        }
                    }
                } catch { print("❌ 添加媒体源失败: \(error)") }
            }
        }
    }
    
    private var folderMenuPanel: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                Text("媒体源管理")
                    .font(.headline)
                
                VStack(alignment: .leading, spacing: 10) {
                    Text(mediaSources.isEmpty ? "暂无已挂载媒体源" : "已挂载媒体源")
                        .font(.subheadline.bold())
                        .foregroundColor(.secondary)
                    if mediaSources.isEmpty {
                        Text("添加本地文件夹或 WebDAV 文件夹后会显示在这里。")
                            .font(.caption)
                            .foregroundColor(.secondary)
                    } else {
                        ScrollView {
                            VStack(alignment: .leading, spacing: 10) {
                                ForEach(mediaSources, id: \.id) { source in
                                    mountedSourceRow(source)
                                }
                            }
                        }
                        .frame(maxHeight: 260)
                    }
                }
                
                Divider()
                
                VStack(alignment: .leading, spacing: 8) {
                    Text("添加媒体源")
                        .font(.subheadline.bold())
                        .foregroundColor(.secondary)
                    VStack(alignment: .leading, spacing: 10) {
                        Button {
                            isShowingManageSources = false
                            promptAndSaveLocalFolder()
                        } label: {
                            Label("添加本地文件夹", systemImage: "folder.badge.plus")
                        }
                        .accessibilityIdentifier("menu.addLocalFolder")
                        .frame(maxWidth: .infinity, alignment: .leading)
                        
                        Button {
                            isShowingManageSources = false
                            resetWebDAVDraft(useRecentHistory: true)
                            isShowingAddWebDAVSheet = true
                        } label: {
                            Label("添加 WebDAV 媒体源", systemImage: "network.badge.shield.half.filled")
                        }
                        .accessibilityIdentifier("menu.addWebDAV")
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                }
                
                Divider()
                
                VStack(alignment: .leading, spacing: 8) {
                    HStack {
                        Text("预扫描出的 WebDAV 链接")
                            .font(.subheadline.bold())
                            .foregroundColor(.secondary)
                        Spacer()
                        Button(lanScanner.isScanning ? "扫描中..." : "重新扫描") {
                            startManagedWebDAVScan(force: true)
                        }
                        .disabled(lanScanner.isScanning)
                    }
                    
                    if discoveredWebDAVDevices.isEmpty {
                        Text(lanScanner.isScanning ? "正在扫描局域网 WebDAV 服务..." : "暂未发现 WebDAV 链接。")
                            .font(.caption)
                            .foregroundColor(.secondary)
                    } else {
                        ScrollView {
                            VStack(alignment: .leading, spacing: 8) {
                                ForEach(discoveredWebDAVDevices) { device in
                                    Button {
                                        openDiscoveredWebDAVLogin(device)
                                    } label: {
                                        HStack(spacing: 8) {
                                            Image(systemName: "network")
                                                .foregroundColor(.blue)
                                            VStack(alignment: .leading, spacing: 2) {
                                                Text(device.name.isEmpty ? "WebDAV \(device.ipAddress)" : device.name)
                                                    .foregroundColor(.primary)
                                                    .lineLimit(1)
                                                Text(discoveredWebDAVURLString(for: device))
                                                    .font(.caption)
                                                    .foregroundColor(.secondary)
                                                    .lineLimit(nil)
                                                    .fixedSize(horizontal: false, vertical: true)
                                            }
                                            .frame(maxWidth: .infinity, alignment: .leading)
                                            Spacer()
                                        }
                                        .frame(maxWidth: .infinity, alignment: .leading)
                                    }
                                    .buttonStyle(.plain)
                                }
                            }
                        }
                        .frame(maxHeight: 320)
                    }
                }
            }
            .padding(16)
        }
        .frame(width: 540, alignment: .topLeading)
        .frame(minHeight: 560, maxHeight: 720, alignment: .topLeading)
        .onAppear {
            startManagedWebDAVScan(force: false)
        }
    }

    private func mountedSourceRow(_ source: MediaSource) -> some View {
        HStack(spacing: 10) {
            Image(systemName: source.protocolKind == .webdav ? "network" : "folder.fill").foregroundColor(.blue)
            VStack(alignment: .leading, spacing: 2) {
                Text(source.name)
                    .font(.body)
                    .fontWeight(.semibold)
                    .foregroundColor(.primary)
                    .lineLimit(1)
                Text(source.protocolKind == .webdav ? "WebDAV" : "本地目录")
                    .font(.caption2)
                    .foregroundColor(.secondary)
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
                let hasCredential = source.protocolKind == .webdav && WebDAVCredentialStore.shared.credentialID(from: source.authConfig) != nil
                if hasCredential {
                    Text(removeWebDAVCredentialWhenRemovingSource
                         ? "将按设置同时删除保存的 WebDAV 登录凭据。"
                         : "将按设置保留已保存的 WebDAV 登录凭据。")
                        .font(.caption)
                        .foregroundColor(.secondary)
                } else if source.protocolKind == .webdav {
                    Text("此 WebDAV 源未保存凭据。")
                        .font(.caption)
                        .foregroundColor(.secondary)
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
                        removeMediaSource(
                            source,
                            removeCredential: hasCredential ? removeWebDAVCredentialWhenRemovingSource : false
                        )
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
        webDAVFolderListMessage = nil
        webDAVFolderListIsError = false
        webDAVIsLoadingFolders = false
        webDAVSharedFolders = []
        webDAVBrowserCredentialID = nil
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
            Text("连接 WebDAV")
                .font(.title3.bold())
                .accessibilityIdentifier("webdav.sheet.title")

            VStack(alignment: .leading, spacing: 8) {
                TextField("WebDAV 地址，例如 https://nas:5006 或 https://nas:5006/dav", text: $webDAVBaseURL)
                    .textFieldStyle(.roundedBorder)
                    .accessibilityIdentifier("webdav.baseURL")
                Text("手动输入 WebDAV 服务地址。保存后会读取该服务暴露的共享文件夹列表。")
                    .font(.caption)
                    .foregroundColor(.secondary)
            }
            TextField("用户名（可选）", text: $webDAVUsername)
                .textFieldStyle(.roundedBorder)
                .accessibilityIdentifier("webdav.username")
            SecureField("密码（可选）", text: $webDAVPassword)
                .textFieldStyle(.roundedBorder)
                .accessibilityIdentifier("webdav.password")
            Text("保存后会读取该 WebDAV 服务暴露的共享文件夹列表。")
                .font(.caption)
                .foregroundColor(.secondary)

            if webDAVIsLoadingFolders {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text("正在读取共享文件夹...")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }

            if let message = webDAVFolderListMessage {
                Text(message)
                    .font(.caption)
                    .foregroundColor(webDAVFolderListIsError ? .red : .green)
            }

            HStack {
                Spacer()
                Button("取消") {
                    isShowingAddWebDAVSheet = false
                }
                .accessibilityIdentifier("webdav.cancel")
                Button(webDAVIsLoadingFolders ? "读取中..." : "保存") {
                    saveManualWebDAVCredentialAndLoadFolders()
                }
                .accessibilityIdentifier("webdav.save")
                .keyboardShortcut(.defaultAction)
                .disabled(webDAVIsLoadingFolders)
            }
        }
        .padding(22)
        .frame(width: 440)
        .onDisappear {
            webDAVIsLoadingFolders = false
        }
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

    private func discoveredWebDAVURLString(for device: DiscoveredDevice) -> String {
        let scheme = device.type == .webdavHTTPS ? "https" : "http"
        return "\(scheme)://\(device.ipAddress):\(device.port)"
    }

    private func startManagedWebDAVScan(force: Bool) {
        guard force || (!lanScanner.isScanning && discoveredWebDAVDevices.isEmpty) else { return }
        Task {
            await LocalNetworkPermissionRequester.shared.requestIfNeeded()
            await MainActor.run {
                if force || !lanScanner.isScanning {
                    lanScanner.startScanning()
                }
            }
        }
    }

    private func openDiscoveredWebDAVLogin(_ device: DiscoveredDevice) {
        let baseURL = discoveredWebDAVURLString(for: device)
        let matchedHistory = recentWebDAVHistory
            .sorted(by: { $0.lastUsed > $1.lastUsed })
            .first { item in
                let normalizedHistory = MediaSourceProtocol.webdav.normalizedBaseURL(item.baseURL)
                return normalizedHistory.hasPrefix(MediaSourceProtocol.webdav.normalizedBaseURL(baseURL))
            }

        webDAVBrowserBaseURL = baseURL
        webDAVBrowserDisplayName = device.name.isEmpty ? "WebDAV \(device.ipAddress)" : device.name
        webDAVBrowserUsername = matchedHistory?.username ?? ""
        webDAVBrowserPassword = ""
        if let credentialID = matchedHistory?.credentialID,
           let credential = WebDAVCredentialStore.shared.loadCredential(id: credentialID) {
            webDAVBrowserUsername = credential.username
            webDAVBrowserPassword = credential.password
        }
        webDAVBrowserCredentialID = nil
        webDAVSharedFolders = []
        webDAVFolderListMessage = nil
        webDAVFolderListIsError = false
        webDAVIsLoadingFolders = false
        isShowingManageSources = false
        isShowingDiscoveredWebDAVLoginSheet = true
    }

    private func discoveredWebDAVLoginSheet() -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("连接 WebDAV")
                .font(.title3.bold())
            VStack(alignment: .leading, spacing: 4) {
                Text(webDAVBrowserDisplayName)
                    .font(.subheadline.bold())
                Text(webDAVBrowserBaseURL)
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .textSelection(.enabled)
            }
            TextField("用户名（可选）", text: $webDAVBrowserUsername)
                .textFieldStyle(.roundedBorder)
                .accessibilityIdentifier("webdav.discovery.username")
            SecureField("密码（可选）", text: $webDAVBrowserPassword)
                .textFieldStyle(.roundedBorder)
                .accessibilityIdentifier("webdav.discovery.password")
            Text("保存后会读取该 WebDAV 服务暴露的共享文件夹列表。")
                .font(.caption)
                .foregroundColor(.secondary)

            if webDAVIsLoadingFolders {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text("正在读取共享文件夹...")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }

            if let webDAVFolderListMessage {
                Text(webDAVFolderListMessage)
                    .font(.caption)
                    .foregroundColor(webDAVFolderListIsError ? .red : .green)
            }

            HStack {
                Spacer()
                Button("取消") {
                    isShowingDiscoveredWebDAVLoginSheet = false
                }
                Button(webDAVIsLoadingFolders ? "读取中..." : "保存") {
                    saveDiscoveredWebDAVCredentialAndLoadFolders()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(webDAVIsLoadingFolders)
            }
        }
        .padding(22)
        .frame(width: 440)
    }

    private func webDAVFolderPickerSheet() -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("选择 WebDAV 共享文件夹")
                .font(.title3.bold())
            Text("点击文件夹右侧星标后，该文件夹会作为媒体源挂载，并立即开始扫描和刮削。")
                .font(.caption)
                .foregroundColor(.secondary)

            if let webDAVFolderListMessage {
                Text(webDAVFolderListMessage)
                    .font(.caption)
                    .foregroundColor(webDAVFolderListIsError ? .red : .green)
            }

            if webDAVSharedFolders.isEmpty {
                Text("未发现可挂载的共享文件夹。")
                    .foregroundColor(.secondary)
                    .frame(maxWidth: .infinity, minHeight: 120)
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: 10) {
                        ForEach(webDAVSharedFolders) { folder in
                            HStack(spacing: 10) {
                                Image(systemName: "folder.fill")
                                    .foregroundColor(.blue)
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(folder.name)
                                        .font(.body.weight(.semibold))
                                        .lineLimit(1)
                                    Text(folder.displayURL)
                                        .font(.caption)
                                        .foregroundColor(.secondary)
                                        .lineLimit(1)
                                        .truncationMode(.middle)
                                }
                                Spacer()
                                let mounted = isWebDAVFolderMounted(folder)
                                Button {
                                    mountWebDAVFolder(folder)
                                } label: {
                                    Image(systemName: mounted ? "star.fill" : "star")
                                        .foregroundColor(mounted ? .yellow : .secondary)
                                }
                                .buttonStyle(.plain)
                                .disabled(mounted || webDAVIsLoadingFolders)
                                .help(mounted ? "已挂载" : "挂载并扫描")
                            }
                            .padding(10)
                            .background(.ultraThinMaterial)
                            .clipShape(RoundedRectangle(cornerRadius: 10))
                        }
                    }
                }
                .frame(minHeight: 180, maxHeight: 320)
            }

            HStack {
                Spacer()
                Button("关闭") {
                    isShowingWebDAVFolderPickerSheet = false
                }
            }
        }
        .padding(22)
        .frame(width: 560)
    }

    private func saveManualWebDAVCredentialAndLoadFolders() {
        let normalizedBaseURL = MediaSourceProtocol.webdav.normalizedBaseURL(webDAVBaseURL)
        guard MediaSourceProtocol.webdav.isValidBaseURL(normalizedBaseURL) else {
            webDAVFolderListMessage = "WebDAV 地址无效，请输入 http(s):// 开头且包含主机名的地址。"
            webDAVFolderListIsError = true
            return
        }

        webDAVBrowserBaseURL = normalizedBaseURL
        webDAVBrowserDisplayName = URL(string: normalizedBaseURL)?.host ?? "WebDAV 媒体源"
        webDAVBrowserUsername = webDAVUsername.trimmingCharacters(in: .whitespacesAndNewlines)
        webDAVBrowserPassword = webDAVPassword
        webDAVBrowserCredentialID = nil
        webDAVSharedFolders = []
        webDAVValidationMessage = nil
        webDAVValidationIsError = false
        loadWebDAVFoldersFromBrowserDraft(flow: .manual)
    }

    private func saveDiscoveredWebDAVCredentialAndLoadFolders() {
        loadWebDAVFoldersFromBrowserDraft(flow: .discovered)
    }

    private func loadWebDAVFoldersFromBrowserDraft(flow: WebDAVCredentialFlow) {
        let normalizedBaseURL = MediaSourceProtocol.webdav.normalizedBaseURL(webDAVBrowserBaseURL)
        guard MediaSourceProtocol.webdav.isValidBaseURL(normalizedBaseURL) else {
            webDAVFolderListMessage = "WebDAV 地址无效。"
            webDAVFolderListIsError = true
            return
        }

        let username = webDAVBrowserUsername.trimmingCharacters(in: .whitespacesAndNewlines)
        let password = webDAVBrowserPassword
        webDAVIsLoadingFolders = true
        webDAVFolderListMessage = nil
        webDAVFolderListIsError = false

        Task {
            do {
                await LocalNetworkPermissionRequester.shared.requestIfNeeded()
                let credentialID: String?
                if username.isEmpty {
                    credentialID = nil
                } else {
                    credentialID = try WebDAVCredentialStore.shared.saveCredential(username: username, password: password)
                }

                let folders = try await WebDAVDirectoryBrowser().listSharedFolders(
                    baseURL: normalizedBaseURL,
                    username: username,
                    password: password
                )

                await MainActor.run {
                    webDAVBrowserCredentialID = credentialID
                    webDAVSharedFolders = folders
                    webDAVFolderListMessage = folders.isEmpty ? "连接成功，但服务端没有返回共享文件夹。" : "连接成功，请选择要挂载的文件夹。"
                    webDAVFolderListIsError = false
                    webDAVIsLoadingFolders = false
                    switch flow {
                    case .manual:
                        isShowingAddWebDAVSheet = false
                    case .discovered:
                        isShowingDiscoveredWebDAVLoginSheet = false
                    }
                    isShowingWebDAVFolderPickerSheet = true
                }
            } catch {
                await MainActor.run {
                    webDAVFolderListMessage = error.localizedDescription
                    webDAVFolderListIsError = true
                    webDAVIsLoadingFolders = false
                }
            }
        }
    }

    private func isWebDAVFolderMounted(_ folder: WebDAVDirectoryItem) -> Bool {
        let normalizedURL = MediaSourceProtocol.webdav.normalizedBaseURL(folder.url.absoluteString)
        return mediaSources.contains { source in
            source.protocolKind == .webdav && source.normalizedBaseURL() == normalizedURL
        }
    }

    private func mountWebDAVFolder(_ folder: WebDAVDirectoryItem) {
        let normalizedURL = MediaSourceProtocol.webdav.normalizedBaseURL(folder.url.absoluteString)
        guard MediaSourceProtocol.webdav.isValidBaseURL(normalizedURL) else {
            webDAVFolderListMessage = "该 WebDAV 文件夹地址无效，无法挂载。"
            webDAVFolderListIsError = true
            return
        }

        let authConfig = webDAVBrowserCredentialID.map { WebDAVCredentialStore.shared.authReference(for: $0) }
        let username = webDAVBrowserUsername.trimmingCharacters(in: .whitespacesAndNewlines)
        webDAVIsLoadingFolders = true
        webDAVFolderListMessage = nil
        webDAVFolderListIsError = false

        Task {
            do {
                let mountedSourceID = try await AppDatabase.shared.dbQueue.write { db -> Int64? in
                    if let existing = try MediaSource.fetchOne(
                        db,
                        sql: "SELECT * FROM mediaSource WHERE protocolType = ? AND baseUrl = ? LIMIT 1",
                        arguments: [MediaSourceProtocol.webdav.rawValue, normalizedURL]
                    ) {
                        try db.execute(
                            sql: "UPDATE mediaSource SET name = ?, authConfig = ?, isEnabled = 1, disabledAt = NULL WHERE id = ?",
                            arguments: [folder.name, authConfig, existing.id]
                        )
                        return existing.id
                    } else {
                        let source = MediaSource(
                            id: nil,
                            name: folder.name,
                            protocolType: MediaSourceProtocol.webdav.rawValue,
                            baseUrl: normalizedURL,
                            authConfig: authConfig
                        )
                        try source.insert(db)
                        return db.lastInsertedRowID
                    }
                }

                await MainActor.run {
                    recordRecentWebDAVHistory(
                        name: folder.name,
                        baseURL: normalizedURL,
                        username: username,
                        credentialID: webDAVBrowserCredentialID
                    )
                    webDAVIsLoadingFolders = false
                    webDAVFolderListMessage = "已挂载“\(folder.name)”。"
                    webDAVFolderListIsError = false
                    loadData()
                    isShowingWebDAVFolderPickerSheet = false
                    if isProcessing {
                        needsRescanAfterCurrentRun = true
                        processingMessage = "扫描中，新增 WebDAV 文件夹已加入下一轮队列..."
                    } else {
                        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { triggerScanAndScrape(onlySourceID: mountedSourceID) }
                    }
                }
            } catch {
                await MainActor.run {
                    webDAVIsLoadingFolders = false
                    webDAVFolderListMessage = error.localizedDescription
                    webDAVFolderListIsError = true
                }
            }
        }
    }

    private func saveWebDAVSource() {
        saveManualWebDAVCredentialAndLoadFolders()
    }

    private func performWebDAVPreflight(showSuccessMessage: Bool) async -> Bool {
        let normalizedURL = MediaSourceProtocol.webdav.normalizedBaseURL(webDAVBaseURL)
        guard MediaSourceProtocol.webdav.isValidBaseURL(normalizedURL) else {
            let message = "WebDAV 地址无效，请输入 http(s):// 开头且包含主机名的地址。"
            let result = WebDAVPreflightResult(
                isReachable: false,
                category: .config,
                message: message,
                httpStatusCode: nil,
                urlErrorCode: nil,
                sanitizedEndpoint: MediaSourceScanDiagnosticsFormatter.sanitizedEndpoint(from: normalizedURL)
            )
            await MainActor.run {
                webDAVValidationMessage = message
                webDAVValidationIsError = true
                webDAVIsTestingConnection = false
                webDAVLastPreflight = result
                lastPreflightDiagnosticsText = WebDAVPreflightDiagnosticsFormatter.diagnosticsReport(
                    result: result,
                    sourceName: currentWebDAVDraftSourceName()
                )
            }
            return false
        }

        await MainActor.run {
            webDAVIsTestingConnection = true
            webDAVValidationMessage = nil
            webDAVValidationIsError = false
        }

        await LocalNetworkPermissionRequester.shared.requestIfNeeded()

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
                lastPreflightDiagnosticsText = ""
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
                lastPreflightDiagnosticsText = WebDAVPreflightDiagnosticsFormatter.diagnosticsReport(
                    result: result,
                    sourceName: currentWebDAVDraftSourceName()
                )
            }
        }

        return result.isReachable
    }

    private func currentWebDAVDraftSourceName() -> String {
        let trimmed = webDAVName.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmed.isEmpty { return trimmed }
        if let host = URL(string: webDAVBaseURL)?.host, !host.isEmpty { return host }
        return "WebDAV 媒体源"
    }

    private func loadData() {
        DispatchQueue.global(qos: .utility).async {
            do {
                let fetchedMovies = try libraryManager.fetchAllMovies()
                let fetchedSources = try AppDatabase.shared.dbQueue.read { db in
                    try MediaSource.fetchAll(
                        db,
                        sql: "SELECT * FROM mediaSource WHERE protocolType IN (?, ?) ORDER BY id DESC",
                        arguments: [MediaSourceProtocol.local.rawValue, MediaSourceProtocol.webdav.rawValue]
                    )
                }
                let fetchedContinueWatching = try AppDatabase.shared.dbQueue.read { db in
                    let sql = """
                    SELECT DISTINCT movie.*
                    FROM movie
                    JOIN videoFile ON videoFile.movieId = movie.id
                    JOIN mediaSource ON mediaSource.id = videoFile.sourceId
                    WHERE videoFile.playProgress > 5
                      AND (videoFile.duration = 0 OR (videoFile.playProgress / videoFile.duration) < 0.95)
                    """
                    return try Movie.fetchAll(db, sql: sql)
                }
                DispatchQueue.main.async {
                    self.movies = fetchedMovies
                    self.mediaSources = fetchedSources
                    self.continueWatchingMovies = fetchedContinueWatching
                }
            } catch {}
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
            ThumbnailManager.shared.cancelTasks(forSourceID: sid)
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
                try await AppDatabase.shared.dbQueue.write { db in
                    if let sid = source.id {
                        try db.execute(sql: "DELETE FROM videoFile WHERE sourceId = ?", arguments: [sid])
                        try db.execute(sql: "DELETE FROM mediaSource WHERE id = ?", arguments: [sid])
                    }
                    try db.execute(sql: "DELETE FROM movie WHERE id NOT IN (SELECT DISTINCT movieId FROM videoFile WHERE movieId IS NOT NULL)")
                }
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
    
    private func triggerScanAndScrape(onlySourceID: Int64? = nil) {
        guard !isProcessing else { return }
        withAnimation {
            isProcessing = true
            processingMessage = "准备扫描..."
            scanSummaryMessage = ""
            isShowingScanSummaryAlert = false
        }
        lastScanResults = []
        lastScanDiagnosticsText = ""
        removedSourceIDsDuringRun = []
        activeScanningSourceID = nil
        
        currentScanTask?.cancel()
        currentScanTask = Task(priority: .utility) {
            do {
                if Task.isCancelled {
                    await MainActor.run {
                        activeScanningSourceID = nil
                        removedSourceIDsDuringRun = []
                        isProcessing = false
                    }
                    return
                }
                let scanProtocols = scannableProtocolValues
                let validSources = try await AppDatabase.shared.dbQueue.read { db in
                    let p1 = scanProtocols[0]
                    let p2 = scanProtocols[1]
                    if let onlySourceID {
                        return try MediaSource.fetchAll(
                            db,
                            sql: """
                            SELECT * FROM mediaSource
                            WHERE id = ?
                              AND protocolType IN (?, ?)
                            ORDER BY id ASC
                            """,
                            arguments: [onlySourceID, p1, p2]
                        )
                    }
                    return try MediaSource.fetchAll(
                        db,
                        sql: "SELECT * FROM mediaSource WHERE protocolType IN (?, ?) ORDER BY id ASC",
                        arguments: [p1, p2]
                    )
                }
                
                if validSources.isEmpty {
                    await MainActor.run {
                        activeScanningSourceID = nil
                        removedSourceIDsDuringRun = []
                        isProcessing = false
                        loadData()
                    }
                    return
                }
                
                await MainActor.run { withAnimation { self.processingMessage = "扫描目录中..." } }
                var sourceResults: [MediaSourceScanResult] = []
                for (index, source) in validSources.enumerated() {
                    if Task.isCancelled {
                        await MainActor.run {
                            activeScanningSourceID = nil
                            removedSourceIDsDuringRun = []
                            isProcessing = false
                        }
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
                    sourceResults.append(result)
                    await MainActor.run {
                        withAnimation {
                            self.processingMessage = result.isSuccess
                                ? "完成：\(source.name)（新增\(result.insertedCount) 移除\(result.removedCount)）"
                                : "失败：\(source.name)（\(result.errorCategory?.displayName ?? "未知错误")）"
                        }
                    }
                }
                await MainActor.run {
                    self.lastScanResults = sourceResults
                    self.lastScanDiagnosticsText = MediaSourceScanDiagnosticsFormatter.diagnosticsReport(results: sourceResults)
                }
                
                if Task.isCancelled {
                    await MainActor.run {
                        activeScanningSourceID = nil
                        removedSourceIDsDuringRun = []
                        isProcessing = false
                    }
                    return
                }
                await MainActor.run { withAnimation { self.processingMessage = "全网刮削中..." } }
                try Task.checkCancellation()
                try await libraryManager.processUnmatchedFiles(sourceID: onlySourceID)
                try Task.checkCancellation()
                await MainActor.run { withAnimation { self.processingMessage = "批量补抓分集剧照中..." } }
                thumbManager.enqueueMissingEpisodeThumbnailsForLibrary(retryFailed: true)
                
                if Task.isCancelled {
                    await MainActor.run {
                        activeScanningSourceID = nil
                        removedSourceIDsDuringRun = []
                        isProcessing = false
                    }
                    return
                }
                await MainActor.run {
                    activeScanningSourceID = nil
                    removedSourceIDsDuringRun = []
                    loadData()
                    let failedResults = self.lastScanResults.filter { !$0.isSuccess }
                    if !failedResults.isEmpty {
                        let names = failedResults.map(\.sourceName).joined(separator: "、")
                        let firstMessage = failedResults.first?.userMessage ?? "部分源扫描失败。"
                        self.scanSummaryMessage = "本次同步有 \(failedResults.count) 个源失败：\(names)\n\(firstMessage)"
                        self.isShowingScanSummaryAlert = true
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
            } catch {
                await MainActor.run {
                    activeScanningSourceID = nil
                    removedSourceIDsDuringRun = []
                    isProcessing = false
                }
            }
        }
    }

    private func copyLastDiagnosticsToPasteboard() {
        guard !unifiedDiagnosticsText.isEmpty else { return }
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(unifiedDiagnosticsText, forType: .string)
        scanSummaryMessage = "诊断信息已复制到剪贴板。"
        isShowingScanSummaryAlert = true
    }
    
}

// 🌟 彻底删除了原先几百行的内网目录浏览器 (SourceFileBrowserView)
// 因为我们现在全部交给原生的 Finder 来选了！
