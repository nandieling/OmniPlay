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

private struct WebDAVBrowserStarredFolder: Identifiable, Equatable {
    let key: String
    let protocolKind: MediaSourceProtocol
    let url: String
    let name: String
    let authConfig: String?

    var id: String { key }
}

private struct MediaServerSourceDraft {
    let protocolKind: MediaSourceProtocol
    let normalizedURL: String
    let token: String
    let userId: String
    let finalName: String
    let authConfig: String?
    let localLabel: String
    let externalAddresses: [MediaSourceExternalAddress]
}

private struct SourceConnectivityResult {
    let isReachable: Bool
    let address: String
    let label: String
    let message: String
}

private struct SourceAddressCandidate {
    let address: String
    let label: String
}

private struct PlexPINAuthSession {
    let id: String
    let code: String
    let authorizationURL: URL
}

private final class PlexPINAuthClient {
    static let shared = PlexPINAuthClient()
    private let clientIdentifierKey = "OmniPlayPlexClientIdentifier"
    private let session: URLSession

    private init() {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 12
        configuration.timeoutIntervalForResource = 12
        configuration.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        self.session = URLSession(configuration: configuration)
    }

    func begin() async throws -> PlexPINAuthSession {
        let pin = try await createPIN()
        return PlexPINAuthSession(
            id: pin.id,
            code: pin.code,
            authorizationURL: authorizationURL(for: pin.code)
        )
    }

    func waitForToken(pinID: String, timeoutSeconds: Int = 120) async throws -> String {
        for _ in 0..<timeoutSeconds {
            try Task.checkCancellation()
            try await Task.sleep(nanoseconds: 1_000_000_000)
            if let token = try await checkPIN(pinID: pinID) {
                return token
            }
        }
        throw PlexPINAuthError.timeout
    }

    private func createPIN() async throws -> (id: String, code: String) {
        guard let url = URL(string: "https://plex.tv/api/v2/pins") else {
            throw PlexPINAuthError.invalidResponse
        }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        applyHeaders(to: &request)

        let (data, response) = try await session.data(for: request)
        try validate(response: response)
        guard let id = value(named: "id", in: data),
              let code = value(named: "code", in: data),
              !id.isEmpty,
              !code.isEmpty else {
            throw PlexPINAuthError.invalidResponse
        }
        return (id, code)
    }

    private func checkPIN(pinID: String) async throws -> String? {
        guard let url = URL(string: "https://plex.tv/api/v2/pins/\(pinID)") else {
            throw PlexPINAuthError.invalidResponse
        }
        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        applyHeaders(to: &request)

        let (data, response) = try await session.data(for: request)
        try validate(response: response)
        return value(named: "authToken", in: data)
    }

    private func authorizationURL(for code: String) -> URL {
        var components = URLComponents(string: "https://plex.tv/link/")!
        components.queryItems = [URLQueryItem(name: "pin", value: code)]
        return components.url!
    }

    private var clientIdentifier: String {
        if let existing = UserDefaults.standard.string(forKey: clientIdentifierKey), !existing.isEmpty {
            return existing
        }
        let identifier = "omniplay-mac-\(UUID().uuidString)"
        UserDefaults.standard.set(identifier, forKey: clientIdentifierKey)
        return identifier
    }

    private func applyHeaders(to request: inout URLRequest) {
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("OmniPlay", forHTTPHeaderField: "X-Plex-Product")
        request.setValue("1.0", forHTTPHeaderField: "X-Plex-Version")
        request.setValue(clientIdentifier, forHTTPHeaderField: "X-Plex-Client-Identifier")
        request.setValue("OmniPlay", forHTTPHeaderField: "X-Plex-Device-Name")
        request.setValue("macOS", forHTTPHeaderField: "X-Plex-Platform")
    }

    private func validate(response: URLResponse) throws {
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw PlexPINAuthError.requestFailed
        }
    }

    private func value(named name: String, in data: Data) -> String? {
        if let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
            return object[name] as? String ?? (object[name] as? NSNumber)?.stringValue
        }
        if let document = try? XMLDocument(data: data, options: [.nodeLoadExternalEntitiesNever]) {
            return document.rootElement()?.attribute(forName: name)?.stringValue
        }
        return nil
    }
}

private enum PlexPINAuthError: LocalizedError {
    case invalidResponse
    case requestFailed
    case timeout

    var errorDescription: String? {
        switch self {
        case .invalidResponse:
            return "Plex 授权响应无效。"
        case .requestFailed:
            return "无法连接 Plex 授权服务。"
        case .timeout:
            return "Plex 授权超时，请重新点击登录。"
        }
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
    @State private var currentScanRunID = UUID()
    @State private var isProcessing = false
    @State private var processingMessage = ""
    @State private var showSettings = false
    @State private var settingsFocusTMDBApi = false
    @State private var mediaSources: [MediaSource] = []

    // 删除了关于网络别名的复杂状态变量
    @State private var isShowingManageSources = false
    @State private var needsRescanAfterCurrentRun = false
    @State private var isShowingRenameSourceSheet = false
    @State private var sourceToRename: MediaSource? = nil
    @State private var renamingSourceName: String = ""
    @State private var isShowingManualRemoteSourceSheet = false
    @State private var isShowingAddWebDAVSheet = false
    @State private var webDAVName: String = ""
    @State private var webDAVBaseURL: String = ""
    @State private var webDAVUsername: String = ""
    @State private var webDAVPassword: String = ""
    @State private var webDAVValidationMessage: String? = nil
    @State private var webDAVValidationIsError: Bool = false
    @State private var webDAVIsTestingConnection: Bool = false
    @State private var webDAVLastPreflight: WebDAVPreflightResult? = nil
    @State private var isShowingMediaServerSheet = false
    @State private var mediaServerProtocol: MediaSourceProtocol = .omniplayDocker
    @State private var mediaServerName = ""
    @State private var mediaServerBaseURL = ""
    @State private var mediaServerToken = ""
    @State private var mediaServerUserId = ""
    @State private var mediaServerPassword = ""
    @State private var mediaServerMessage: String? = nil
    @State private var mediaServerIsPreScanning = false
    @State private var mediaServerLocalLabel = "局域网"
    @State private var pendingExternalAddresses: [MediaSourceExternalAddress] = []
    @State private var webDAVBrowserExternalAddresses: [MediaSourceExternalAddress] = []
    @State private var webDAVLocalLabel = "局域网"
    @State private var webDAVBrowserLocalLabel = "局域网"
    @State private var luckyStunRules: [LuckyStunRule] = []
    @State private var isShowingWebDAVPreScanLoginSheet = false
    @State private var isShowingWebDAVFolderBrowserSheet = false
    @State private var webDAVBrowserName: String = ""
    @State private var webDAVBrowserBaseURL: String = ""
    @State private var webDAVBrowserUsername: String = ""
    @State private var webDAVBrowserPassword: String = ""
    @State private var webDAVBrowserCredentialID: String? = nil
    @State private var webDAVBrowserProtocol: MediaSourceProtocol = .webdav
    @State private var webDAVBrowserCurrentURL: String = ""
    @State private var webDAVBrowserPathStack: [String] = []
    @State private var webDAVBrowserItems: [WebDAVDirectoryItem] = []
    @State private var webDAVBrowserMountedURLs: Set<String> = []
    @State private var webDAVBrowserMessage: String? = nil
    @State private var webDAVBrowserMessageIsError = false
    @State private var webDAVBrowserIsLoading = false
    @State private var webDAVBrowserIsBatchMounting = false
    @State private var webDAVBrowserStarredFolders: [WebDAVBrowserStarredFolder] = []

    @AppStorage("keepLocalPosters") var keepLocalPosters = true
    @AppStorage("autoScanOnStartup") var autoScanOnStartup = true
    @AppStorage("enableFastTooltip") var enableFastTooltip = true
    @AppStorage("showMediaSourceRealPath") var showMediaSourceRealPath = true
    @AppStorage("removeWebDAVCredentialsOnDelete") var removeWebDAVCredentialsOnDelete = false

    @AppStorage("appTheme") var appTheme = ThemeType.crystal.rawValue
    var theme: AppTheme { ThemeType(rawValue: appTheme)?.colors ?? ThemeType.crystal.colors }
    @Environment(\.colorScheme) private var colorScheme

    @ObservedObject var thumbManager = ThumbnailManager.shared
    @ObservedObject var cacheManager = OfflineCacheManager.shared
    @StateObject private var lanScanner = LANScanner()

    @State private var searchText: String = ""
    @State private var isSearchActive: Bool = false
    @State private var selectedSortOption: MovieSortOption = .year
    @State private var isAscending: Bool = false
    @State private var pendingLibraryReloadTask: Task<Void, Never>? = nil
    @State private var dockerAutoSyncTask: Task<Void, Never>? = nil
    @State private var activeScanningSourceID: Int64? = nil
    @State private var removedSourceIDsDuringRun: Set<Int64> = []
    @State private var recentWebDAVHistory: [RecentWebDAVHistoryItem] = PosterWallView.loadRecentWebDAVHistory()
    @State private var isShowingRemoveSourceSheet = false
    @State private var sourcePendingRemoval: MediaSource? = nil
    @State private var sourceConnectivityStatus: [Int64: String] = [:]
    @State private var checkingSourceIDs: Set<Int64> = []
    @State private var isLoadingLibraryData = false
    @State private var needsLibraryReloadAfterCurrentLoad = false
    @State private var isShowingTMDBConnectionAlert = false
    @State private var tmdbConnectionMessage = ""
    @State private var isHomeCacheModeActive = false

    // 🌟 彻底删除了所有与 network 相关的 State

    let columns = [GridItem(.adaptive(minimum: 160), spacing: 20)]
    init() {
        let isFast = UserDefaults.standard.bool(forKey: "enableFastTooltip")
        UserDefaults.standard.set(isFast ? 50 : 1000, forKey: "NSInitialToolTipDelay")
    }

    var displayedMovies: [Movie] { var result = movies; if !searchText.isEmpty { result = result.filter { $0.title.localizedCaseInsensitiveContains(searchText) } }; result.sort { m1, m2 in let isLess: Bool; switch selectedSortOption { case .name: isLess = m1.title.localizedStandardCompare(m2.title) == .orderedAscending; case .year: isLess = (m1.releaseDate ?? "") < (m2.releaseDate ?? ""); case .rating: isLess = (m1.voteAverage ?? 0.0) < (m2.voteAverage ?? 0.0) }; return isAscending ? isLess : !isLess }; return result }
    private var discoveredNetworkDevices: [DiscoveredDevice] {
        lanScanner.discoveredDevices.filter { $0.type.isWebDAV || $0.type.isMediaServer }
    }
    private var topToolbarInactiveIconColor: Color {
        colorScheme == .dark ? theme.textPrimary.opacity(0.78) : .secondary
    }
    private var topToolbarDisabledIconColor: Color {
        colorScheme == .dark ? theme.textPrimary.opacity(0.32) : theme.textSecondary.opacity(0.5)
    }
    private var topToolbarStatusTextColor: Color {
        colorScheme == .dark ? theme.textPrimary.opacity(0.86) : theme.textSecondary
    }
    private var webDAVBrowserStarredKeys: Set<String> {
        Set(webDAVBrowserStarredFolders.map(\.key))
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
                        VStack(alignment: .leading, spacing: 24) {
                            if !continueWatchingMovies.isEmpty {
                                Text("继续播放")
                                    .font(.title2).fontWeight(.bold)
                                    .foregroundColor(theme.textPrimary)
                                    .padding(.horizontal, 25).padding(.top, 8)
                                ScrollView(.horizontal, showsIndicators: false) {
                                    HStack(spacing: 20) {
                                        ForEach(continueWatchingMovies, id: \.id) { movie in
                                            if isHomeCacheModeActive {
                                                MovieCardView(
                                                    movie: movie,
                                                    isContinueWatchingContext: true,
                                                    isHomeCacheModeActive: isHomeCacheModeActive
                                                )
                                                .frame(width: 160)
                                            } else {
                                                NavigationLink(destination: MovieDetailView(movie: movie)) {
                                                    MovieCardView(movie: movie, isContinueWatchingContext: true).frame(width: 160)
                                                }.buttonStyle(.plain)
                                            }
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
                            }.padding(.horizontal, 25).padding(.top, continueWatchingMovies.isEmpty ? 6 : 4)

                            LazyVGrid(columns: columns, spacing: 30) {
                                ForEach(displayedMovies, id: \.id) { movie in
                                    if isHomeCacheModeActive {
                                        MovieCardView(movie: movie, isHomeCacheModeActive: isHomeCacheModeActive)
                                    } else {
                                        NavigationLink(destination: MovieDetailView(movie: movie)) {
                                            MovieCardView(movie: movie)
                                        }.buttonStyle(.plain)
                                    }
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
                    if isProcessing || !thumbManager.progressMessage.isEmpty {
                        let statusMessage = isProcessing
                            ? (processingMessage.isEmpty ? "正在扫描媒体源..." : processingMessage)
                            : thumbManager.progressMessage
                        HStack(spacing: 8) {
                            ProgressView().controlSize(.small)
                                .tint(topToolbarStatusTextColor)
                            Text(statusMessage)
                                .font(.caption)
                                .foregroundColor(topToolbarStatusTextColor)
                                .lineLimit(1)
                                .frame(maxWidth: 220, alignment: .leading)
                        }
                    }

                    Spacer(minLength: 20)

                    Button(action: { isShowingManageSources.toggle() }) {
                        Image(systemName: "folder.badge.plus")
                            .font(.system(size: 21, weight: .medium))
                            .foregroundColor(topToolbarInactiveIconColor)
                            .frame(width: 28, height: 28)
                    }
                    .buttonStyle(.plain)
                    .accessibilityIdentifier("toolbar.addSource")
                    .conditionalHelp("查看已挂载媒体源，并添加本地文件夹或 WebDAV 媒体源", show: enableFastTooltip)
                    .popover(isPresented: $isShowingManageSources, arrowEdge: .top) { folderMenuPanel }

                    Button(action: { triggerScanAndScrape() }) {
                        Image(systemName: "arrow.triangle.2.circlepath")
                            .font(.system(size: 21, weight: .medium))
                            .foregroundColor(isProcessing ? topToolbarDisabledIconColor : topToolbarInactiveIconColor)
                            .frame(width: 28, height: 28)
                    }
                    .buttonStyle(.plain)
                    .accessibilityIdentifier("toolbar.sync")
                    .disabled(isProcessing)
                    .conditionalHelp("重新扫描目录并刷新刮削结果", show: enableFastTooltip)

                    Button(action: { withAnimation { isHomeCacheModeActive.toggle() } }) {
                        Image(systemName: isHomeCacheModeActive ? "icloud.fill" : "icloud")
                            .font(.system(size: 21, weight: .medium))
                            .foregroundColor(isHomeCacheModeActive ? theme.accent : topToolbarInactiveIconColor)
                            .frame(width: 28, height: 28)
                    }
                    .buttonStyle(.plain)
                    .conditionalHelp("切换离线缓存编辑模式", show: enableFastTooltip)

                    Button(action: {
                        settingsFocusTMDBApi = false
                        showSettings = true
                    }) {
                        Image(systemName: "gearshape")
                            .font(.system(size: 21, weight: .medium))
                            .foregroundColor(topToolbarInactiveIconColor)
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
            .sheet(isPresented: $showSettings) {
                SettingsView(focusTMDBApi: settingsFocusTMDBApi)
                    .onDisappear { settingsFocusTMDBApi = false }
            }
            .sheet(isPresented: $isShowingRenameSourceSheet) {
                renameSourceSheet()
            }
            .sheet(isPresented: $isShowingManualRemoteSourceSheet) {
                manualRemoteSourceSheet()
            }
            .sheet(isPresented: $isShowingWebDAVPreScanLoginSheet) {
                webDAVPreScanLoginSheet()
            }
            .sheet(isPresented: $isShowingWebDAVFolderBrowserSheet) {
                webDAVFolderBrowserSheet()
            }
            .sheet(isPresented: $isShowingMediaServerSheet) {
                mediaServerSourceSheet()
            }
            .sheet(isPresented: $isShowingRemoveSourceSheet) {
                removeSourceSheet()
            }
            .alert("TMDB API 无法连接", isPresented: $isShowingTMDBConnectionAlert) {
                Button("本次不刮削", role: .cancel) { }
                Button("添加 API / 设置代理", role: .cancel) {
                    settingsFocusTMDBApi = true
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
                        showSettings = true
                    }
                }
            } message: {
                Text("检测到有待刮削的影视，但 TMDB 暂时无法连接。请添加自定义 TMDB API，或开启代理后在设置中重新检测。\n\(tmdbConnectionMessage)")
            }
            // 🌟 彻底删除了那个恶心的 SMB/WebDAV Sheet 弹窗！
        }
        .onAppear {
            loadData()
            startDockerAutoSyncIfNeeded()
            luckyStunRules = LuckyStunCoordinator.shared.cachedRules
            LuckyStunCoordinator.shared.startAutomaticUpdates()
            MacAppUpdateChecker.checkAtStartupIfNeeded()
            if ProcessInfo.processInfo.environment["UITEST_OPEN_WEBDAV_SHEET"] == "1" {
                prepareManualWebDAVBrowserLogin()
                isShowingWebDAVPreScanLoginSheet = true
            }
            if autoScanOnStartup && ProcessInfo.processInfo.environment["UITEST_MODE"] != "1" {
                triggerScanAndScrape()
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: .libraryUpdated)) { _ in
            scheduleDebouncedLibraryReload()
        }
        .onReceive(NotificationCenter.default.publisher(for: LuckyStunCoordinator.sourceUpdatedNotification)) { _ in
            scheduleDebouncedLibraryReload()
        }
        .onReceive(NotificationCenter.default.publisher(for: LuckyStunCoordinator.rulesUpdatedNotification)) { notification in
            if let rules = notification.object as? [LuckyStunRule] {
                luckyStunRules = rules
            }
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
            dockerAutoSyncTask?.cancel()
            dockerAutoSyncTask = nil
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
            let absolutePath = MediaSourceProtocol.local.normalizedBaseURL(selectedURL.path)
            let sourceName = selectedURL.lastPathComponent
            let addressConfig = addressConfigurationString(
                protocolKind: .local,
                localAddress: absolutePath,
                localLabel: "本地",
                externalAddresses: []
            )
            Task {
                do {
                    // 直接存入数据库，丢弃了所有的 authConfig 和协议判断
                    try await AppDatabase.shared.dbQueue.write { db in
                        let count = try Int.fetchOne(db, sql: "SELECT COUNT(*) FROM mediaSource WHERE baseUrl = ?", arguments: [absolutePath]) ?? 0
                        if count == 0 {
                            try db.execute(sql: "INSERT INTO mediaSource (name, baseUrl, protocolType, addressConfig) VALUES (?, ?, ?, ?)",
                                           arguments: [sourceName, absolutePath, MediaSourceProtocol.local.rawValue, addressConfig])
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
                    Image(systemName: sourceIconName(source)).foregroundColor(.blue)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(source.displayNameWithActiveLabel)
                            .font(.body)
                            .fontWeight(.semibold)
                            .foregroundColor(.primary)
                            .lineLimit(1)
                        Text("\(sourceProtocolLabel(source)) · \(source.isEnabled ? "已开启" : "已关闭")")
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
                        if let sourceID = source.id, let status = sourceConnectivityStatus[sourceID] {
                            Text(status)
                                .font(.caption2)
                                .foregroundColor(status.hasPrefix("连接成功") ? .green : (status == "检测中..." ? .secondary : .red))
                                .lineLimit(2)
                        }
                    }
                    .frame(maxWidth: 300, alignment: .leading)
                    Spacer()
                    Button(checkingSourceIDs.contains(source.id ?? -1) ? "检测中..." : "检测连通性") {
                        checkMediaSourceConnectivity(source)
                    }
                    .font(.caption.weight(.semibold))
                    .disabled(source.id == nil || checkingSourceIDs.contains(source.id ?? -1))
                    .help("优先检测局域网地址，失败后依次检测外网地址")
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
                    openManualRemoteSourceChooser()
                }) {
                    Label("局域网媒体源", systemImage: "network.badge.shield.half.filled")
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .buttonStyle(.plain)
                .accessibilityIdentifier("menu.addRemoteSource")
            }

            Divider()
                .padding(.top, 2)

            VStack(alignment: .leading, spacing: 10) {
                HStack {
                    Text("预扫描局域网媒体源")
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

                if discoveredNetworkDevices.isEmpty {
                    Text("扫描到 OmniPlay Docker、WebDAV、Plex、Emby 或 Jellyfin 后，点击设备进入登录。")
                        .font(.caption)
                        .foregroundColor(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                } else {
                    VStack(alignment: .leading, spacing: 8) {
                        ForEach(discoveredNetworkDevices) { device in
                            Button(action: {
                                openDiscoveredDeviceFromManageSheet(device)
                            }) {
                                HStack(alignment: .top, spacing: 10) {
                                    Image(systemName: device.type.isMediaServer ? "server.rack" : "globe.asia.australia.fill")
                                        .foregroundColor(.blue)
                                        .frame(width: 22)
                                        .padding(.top, 1)
                                    VStack(alignment: .leading, spacing: 3) {
                                        Text(discoveredDeviceTitle(device))
                                            .foregroundColor(.primary)
                                            .lineLimit(1)
                                        Text(verbatim: discoveredDeviceURLString(for: device))
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

    private func sourceProtocolLabel(_ source: MediaSource) -> String {
        switch source.protocolKind {
        case .webdav: return "WebDAV"
        case .plex: return "Plex"
        case .emby: return "Emby"
        case .jellyfin: return "Jellyfin"
        case .omniplayDocker: return "OmniPlay Docker"
        case .direct: return "直连"
        case .local, .none: return "本地目录"
        }
    }

    private func sourceIconName(_ source: MediaSource) -> String {
        switch source.protocolKind {
        case .webdav: return "network"
        case .plex, .emby, .jellyfin, .omniplayDocker: return "server.rack"
        default: return "folder.fill"
        }
    }

    private func externalAddressBinding(
        for id: UUID,
        in addresses: Binding<[MediaSourceExternalAddress]>
    ) -> Binding<MediaSourceExternalAddress> {
        Binding(
            get: {
                addresses.wrappedValue.first(where: { $0.id == id }) ?? MediaSourceExternalAddress()
            },
            set: { value in
                guard let index = addresses.wrappedValue.firstIndex(where: { $0.id == id }) else { return }
                addresses.wrappedValue[index] = value
            }
        )
    }

    private func externalAddressEditor(
        addresses: Binding<[MediaSourceExternalAddress]>,
        title: String = "外网地址"
    ) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(title).font(.subheadline.weight(.semibold))
                Spacer()
                Button {
                    addresses.wrappedValue.append(MediaSourceExternalAddress())
                } label: {
                    Label("添加外网地址", systemImage: "plus.circle")
                }
                .buttonStyle(.plain)
            }

            if addresses.wrappedValue.isEmpty {
                Text("可添加多个外网入口；检测连通性时会在局域网失败后按顺序尝试。")
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            } else {
                ForEach(addresses.wrappedValue) { item in
                    let itemBinding = externalAddressBinding(for: item.id, in: addresses)
                    VStack(alignment: .leading, spacing: 6) {
                        HStack(spacing: 8) {
                            TextField("外网地址（请输入与局域网媒体源对应的完整地址）", text: itemBinding.address)
                                .textFieldStyle(.roundedBorder)
                            Button {
                                addresses.wrappedValue.removeAll { $0.id == item.id }
                            } label: {
                                Image(systemName: "minus.circle.fill").foregroundColor(.red.opacity(0.8))
                            }
                            .buttonStyle(.plain)
                        }
                        TextField("自定义标签（例如：家、公司、远程）", text: itemBinding.label)
                            .textFieldStyle(.roundedBorder)
                        Toggle("使用 Lucky STUN 自动更新此地址", isOn: itemBinding.useLuckyStun)
                            .toggleStyle(.checkbox)
                        if itemBinding.wrappedValue.useLuckyStun {
                            let ruleSelection = Binding<String>(
                                get: { itemBinding.wrappedValue.luckyRuleId },
                                set: { value in
                                    var updated = itemBinding.wrappedValue
                                    updated.luckyRuleId = value
                                    updated.luckyRuleName = luckyStunRules.first(where: { $0.id == value })?.name ?? ""
                                    itemBinding.wrappedValue = updated
                                }
                            )
                            Picker("指定 Lucky STUN 规则", selection: ruleSelection) {
                                Text(luckyStunRules.isEmpty ? "请先在设置中登录 Lucky STUN" : "选择规则").tag("")
                                ForEach(luckyStunRules) { rule in
                                    Text("\(rule.name) · \(rule.address)").tag(rule.id)
                                }
                            }
                            if luckyStunRules.isEmpty {
                                Text("请先在设置中点击“登录检测并更新”，登录成功后这里会显示规则列表。")
                                    .font(.caption2)
                                    .foregroundColor(.orange)
                                    .fixedSize(horizontal: false, vertical: true)
                            }
                        }
                    }
                    .padding(9)
                    .background(.ultraThinMaterial)
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                }
            }
        }
    }

    private nonisolated func normalizedExternalAddresses(
        _ addresses: [MediaSourceExternalAddress],
        protocolKind: MediaSourceProtocol
    ) -> [MediaSourceExternalAddress] {
        addresses.compactMap { item in
            var normalized = item
            let raw = item.address.trimmingCharacters(in: .whitespacesAndNewlines)
            if raw.isEmpty {
                return item.useLuckyStun ? normalized : nil
            }
            let value = protocolKind.normalizedBaseURL(raw)
            guard protocolKind.isValidBaseURL(value) else { return nil }
            normalized.address = value
            return normalized
        }
    }

    private nonisolated func addressConfigurationString(
        protocolKind: MediaSourceProtocol,
        localAddress: String,
        localLabel: String,
        externalAddresses: [MediaSourceExternalAddress]
    ) -> String? {
        let normalizedLocalAddress = protocolKind.normalizedBaseURL(localAddress)
        let normalizedLabel = localLabel.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "局域网" : localLabel.trimmingCharacters(in: .whitespacesAndNewlines)
        let configuration = MediaSourceAddressConfiguration(
            localAddress: normalizedLocalAddress,
            localLabel: normalizedLabel,
            externalAddresses: normalizedExternalAddresses(externalAddresses, protocolKind: protocolKind),
            activeAddress: normalizedLocalAddress,
            activeLabel: normalizedLabel
        )
        guard let data = try? JSONEncoder().encode(configuration) else { return nil }
        return String(data: data, encoding: .utf8)
    }

    private func mediaServerProtocolLabel(_ value: MediaSourceProtocol) -> String {
        switch value {
        case .omniplayDocker: return "OmniPlay Docker"
        case .webdav: return "WebDAV"
        case .plex: return "Plex"
        case .emby: return "Emby"
        case .jellyfin: return "Jellyfin"
        default: return "媒体服务器"
        }
    }

    private func defaultMediaServerName(for protocolKind: MediaSourceProtocol) -> String {
        switch protocolKind {
        case .omniplayDocker:
            return "OmniPlay Docker"
        case .webdav:
            return "WebDAV"
        case .plex:
            return "Plex"
        case .emby:
            return "Emby"
        case .jellyfin:
            return "Jellyfin"
        default:
            return ""
        }
    }

    private var mediaServerAddressPlaceholder: String {
        switch mediaServerProtocol {
        case .omniplayDocker:
            return "Docker 地址，例如 http://192.168.0.100:45722"
        case .webdav:
            return "WebDAV 地址，例如 https://nas:5006/dav"
        case .plex:
            return "Plex 地址，例如 http://127.0.0.1:32400"
        case .emby:
            return "Emby 地址，例如 http://127.0.0.1:8096"
        case .jellyfin:
            return "Jellyfin 地址，例如 http://127.0.0.1:8096"
        default:
            return "服务器地址，例如 http://127.0.0.1:8096"
        }
    }

    private var mediaServerTokenPlaceholder: String {
        mediaServerProtocol == .plex ? "Plex 访问令牌（X-Plex-Token）" : "API Key / 访问令牌"
    }

    private func defaultMediaServerBaseURL(for protocolKind: MediaSourceProtocol) -> String {
        switch protocolKind {
        case .omniplayDocker:
            return "http://192.168.0.100:45722"
        case .webdav:
            return ""
        case .plex:
            return "http://127.0.0.1:32400"
        case .emby, .jellyfin:
            return "http://127.0.0.1:8096"
        default:
            return ""
        }
    }

    private func isDefaultMediaServerBaseURL(_ value: String) -> Bool {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed == defaultMediaServerBaseURL(for: .plex)
            || trimmed == defaultMediaServerBaseURL(for: .emby)
            || trimmed == defaultMediaServerBaseURL(for: .jellyfin)
            || trimmed == defaultMediaServerBaseURL(for: .omniplayDocker)
    }

    private func mediaServerConnectionHint(for protocolKind: MediaSourceProtocol) -> String {
        switch protocolKind {
        case .omniplayDocker:
            return "Docker 服务默认地址为 http://192.168.0.100:45722。登录后会同步服务端媒体库，播放仍由 mac 本地硬解。"
        case .webdav:
            return "WebDAV 会先进入共享文件夹列表；可给多个目标文件夹点星标，关闭列表时统一挂载并扫描刮削。"
        case .plex:
            return "Plex 默认地址为 http://127.0.0.1:32400，点击“登录 Plex”会自动获取访问令牌。"
        case .emby, .jellyfin:
            return "\(mediaServerProtocolLabel(protocolKind)) 默认地址为 http://127.0.0.1:8096，请填写 API Key 或访问令牌。"
        default:
            return "保存后会先预扫描媒体库列表；可给多个库点星标，关闭列表时统一挂载并扫描刮削。"
        }
    }

    private func mediaServerMissingTokenMessage(for protocolKind: MediaSourceProtocol) -> String {
        if protocolKind == .webdav {
            return "WebDAV 可直接保存进入文件夹列表。"
        }
        if protocolKind == .plex {
            return "Plex 需要访问令牌（X-Plex-Token）才能读取媒体库。"
        }
        if protocolKind == .omniplayDocker {
            return "OmniPlay Docker 需要用户名和密码才能同步媒体库。"
        }
        return "\(mediaServerProtocolLabel(protocolKind)) 需要 API Key 或访问令牌才能读取媒体列表和生成播放地址。"
    }

    private func mediaServerProtocolDidChange(_ newValue: MediaSourceProtocol, resetIdentity: Bool = false) {
        if resetIdentity {
            mediaServerName = defaultMediaServerName(for: newValue)
            mediaServerBaseURL = defaultMediaServerBaseURL(for: newValue)
        }
        let currentBaseURL = mediaServerBaseURL.trimmingCharacters(in: .whitespacesAndNewlines)
        if currentBaseURL.isEmpty || isDefaultMediaServerBaseURL(currentBaseURL) {
            mediaServerBaseURL = defaultMediaServerBaseURL(for: newValue)
        }
        if newValue == .plex || newValue == .omniplayDocker || newValue == .webdav {
            mediaServerUserId = ""
        }
        if newValue != .omniplayDocker && newValue != .webdav {
            mediaServerPassword = ""
        }
        if newValue == .webdav {
            mediaServerToken = ""
        }
        mediaServerMessage = mediaServerConnectionHint(for: newValue)
    }

    private var remoteSourceSaveButtonTitle: String {
        if mediaServerIsPreScanning {
            return mediaServerProtocol == .omniplayDocker ? "同步中..." : "读取中..."
        }
        switch mediaServerProtocol {
        case .omniplayDocker:
            return "保存并同步"
        case .webdav:
            return "继续"
        default:
            return "保存"
        }
    }

    private func dismissRemoteSourceForm() {
        isShowingManualRemoteSourceSheet = false
        isShowingMediaServerSheet = false
    }

    private func saveRemoteSourceForm() {
        if mediaServerProtocol == .webdav {
            openWebDAVLoginFromRemoteSourceForm()
        } else {
            saveMediaServerSource()
        }
    }

    private func openWebDAVLoginFromRemoteSourceForm() {
        let draftName = mediaServerName.trimmingCharacters(in: .whitespacesAndNewlines)
        let draftBaseURL = mediaServerBaseURL.trimmingCharacters(in: .whitespacesAndNewlines)
        let draftUsername = mediaServerUserId.trimmingCharacters(in: .whitespacesAndNewlines)
        let draftPassword = mediaServerPassword
        let draftLocalLabel = mediaServerLocalLabel
        let draftExternalAddresses = pendingExternalAddresses

        dismissRemoteSourceForm()
        prepareManualWebDAVBrowserLogin()
        if !draftName.isEmpty {
            webDAVBrowserName = draftName
        }
        if !draftBaseURL.isEmpty {
            webDAVBrowserBaseURL = draftBaseURL
        }
        if !draftUsername.isEmpty {
            webDAVBrowserUsername = draftUsername
        }
        if !draftPassword.isEmpty {
            webDAVBrowserPassword = draftPassword
        }
        webDAVBrowserLocalLabel = draftLocalLabel
        webDAVBrowserExternalAddresses = draftExternalAddresses
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) {
            isShowingWebDAVPreScanLoginSheet = true
        }
    }

    private func closeRemoteSourceSheets() {
        isShowingManualRemoteSourceSheet = false
        isShowingMediaServerSheet = false
        isShowingWebDAVPreScanLoginSheet = false
        isShowingWebDAVFolderBrowserSheet = false
    }

    private func openManualRemoteSourceChooser() {
        isShowingManageSources = false
        closeRemoteSourceSheets()
        resetOmniPlayDockerForm()
        DispatchQueue.main.async {
            isShowingManualRemoteSourceSheet = true
        }
    }

    private func openOmniPlayDockerSourceSheet() {
        closeRemoteSourceSheets()
        resetOmniPlayDockerForm()
        DispatchQueue.main.async {
            isShowingMediaServerSheet = true
        }
    }

    private func openMediaServerProviderSourceSheet() {
        closeRemoteSourceSheets()
        resetMediaServerForm()
        DispatchQueue.main.async {
            isShowingMediaServerSheet = true
        }
    }

    private func mediaServerSourceSheet() -> some View {
        manualRemoteSourceSheet()
    }

    private func mediaServerProviderSourceSheet() -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("添加局域网媒体源")
                .font(.title3.bold())
            Picker("类型", selection: $mediaServerProtocol) {
                Text("OmniPlay Docker").tag(MediaSourceProtocol.omniplayDocker)
                Text("WebDAV").tag(MediaSourceProtocol.webdav)
                Text("Plex").tag(MediaSourceProtocol.plex)
                Text("Emby").tag(MediaSourceProtocol.emby)
                Text("Jellyfin").tag(MediaSourceProtocol.jellyfin)
            }
            .pickerStyle(.segmented)
            TextField("显示名称（可选）", text: $mediaServerName)
                .textFieldStyle(.roundedBorder)
            TextField(mediaServerAddressPlaceholder, text: $mediaServerBaseURL)
                .textFieldStyle(.roundedBorder)
            TextField("局域网自定义标签（默认：局域网）", text: $mediaServerLocalLabel)
                .textFieldStyle(.roundedBorder)
            externalAddressEditor(addresses: $pendingExternalAddresses)
            if mediaServerProtocol == .webdav {
                TextField("用户名（可选）", text: $mediaServerUserId)
                    .textFieldStyle(.roundedBorder)
                SecureField("密码（可选）", text: $mediaServerPassword)
                    .textFieldStyle(.roundedBorder)
            } else if mediaServerProtocol != .plex && mediaServerProtocol != .omniplayDocker {
                TextField("用户名 / 用户 ID（可选）", text: $mediaServerUserId)
                    .textFieldStyle(.roundedBorder)
            }
            if mediaServerProtocol == .plex {
                Button(mediaServerIsPreScanning ? "等待 Plex 授权..." : "登录 Plex") {
                    authorizePlex()
                }
                .disabled(mediaServerIsPreScanning)
            }
            if mediaServerProtocol == .omniplayDocker {
                TextField("用户名", text: $mediaServerUserId)
                    .textFieldStyle(.roundedBorder)
                SecureField("密码", text: $mediaServerPassword)
                    .textFieldStyle(.roundedBorder)
            } else if mediaServerProtocol != .webdav {
                SecureField(mediaServerTokenPlaceholder, text: $mediaServerToken)
                    .textFieldStyle(.roundedBorder)
            }
            if let mediaServerMessage {
                Text(mediaServerMessage)
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            HStack {
                Spacer()
                Button("取消") { dismissRemoteSourceForm() }
                Button(remoteSourceSaveButtonTitle) { saveRemoteSourceForm() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(mediaServerIsPreScanning)
            }
        }
        .padding(20)
        .frame(width: 520)
        .onChange(of: mediaServerProtocol) { oldValue, newValue in
            mediaServerProtocolDidChange(newValue, resetIdentity: oldValue != newValue)
        }
    }

    private func omniPlayDockerSourceSheet() -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("添加 OmniPlay Docker")
                .font(.title3.bold())
            Text("填写 Docker 版服务地址和登录账号，保存后会立即同步 Docker 端电影、剧集和已扫描元数据。")
                .font(.caption)
                .foregroundColor(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            TextField("显示名称（可选）", text: $mediaServerName)
                .textFieldStyle(.roundedBorder)
            TextField(mediaServerAddressPlaceholder, text: $mediaServerBaseURL)
                .textFieldStyle(.roundedBorder)
            TextField("局域网自定义标签（默认：局域网）", text: $mediaServerLocalLabel)
                .textFieldStyle(.roundedBorder)
            externalAddressEditor(addresses: $pendingExternalAddresses)
            TextField("用户名", text: $mediaServerUserId)
                .textFieldStyle(.roundedBorder)
            SecureField("密码", text: $mediaServerPassword)
                .textFieldStyle(.roundedBorder)

            if let mediaServerMessage {
                Text(mediaServerMessage)
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack {
                Spacer()
                Button("取消") { isShowingMediaServerSheet = false }
                Button(mediaServerIsPreScanning ? "同步中..." : "保存并同步") { saveMediaServerSource() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(mediaServerIsPreScanning)
            }
        }
        .padding(20)
        .frame(width: 500)
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

    private func manualRemoteSourceSheet() -> some View {
        mediaServerProviderSourceSheet()
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
        webDAVLocalLabel = "局域网"
        pendingExternalAddresses = []
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

    private func applyRecentWebDAVHistoryToBrowser(_ item: RecentWebDAVHistoryItem, shouldBumpUsage: Bool, replaceBaseURL: Bool) {
        if item.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
           let host = URL(string: item.baseURL)?.host,
           webDAVBrowserName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            webDAVBrowserName = host
        } else if !item.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                  webDAVBrowserName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || replaceBaseURL {
            webDAVBrowserName = item.name
        }
        if replaceBaseURL {
            webDAVBrowserBaseURL = readableWebDAVURL(from: item.baseURL)
        }
        webDAVBrowserUsername = item.username
        webDAVBrowserCredentialID = item.credentialID
        if let credentialID = item.credentialID,
           let credential = WebDAVCredentialStore.shared.loadCredential(id: credentialID) {
            webDAVBrowserUsername = credential.username
            webDAVBrowserPassword = credential.password
        } else {
            webDAVBrowserPassword = ""
        }
        if shouldBumpUsage { bumpHistoryUsage(for: item.id) }
    }

    private func matchingRecentWebDAVHistory(for baseURL: String) -> RecentWebDAVHistoryItem? {
        let normalizedURL = normalizedWebDAVBrowserURL(baseURL)
        let sorted = recentWebDAVHistory.sorted(by: { $0.lastUsed > $1.lastUsed })
        if let exact = sorted.first(where: { normalizedWebDAVBrowserURL($0.baseURL) == normalizedURL }) {
            return exact
        }
        return sorted.first { isSameWebDAVEndpoint($0.baseURL, normalizedURL) }
    }

    nonisolated private func isSameWebDAVEndpoint(_ lhs: String, _ rhs: String) -> Bool {
        guard let left = URL(string: MediaSourceProtocol.webdav.normalizedBaseURL(lhs)),
              let right = URL(string: MediaSourceProtocol.webdav.normalizedBaseURL(rhs)),
              let leftHost = left.host?.lowercased(),
              let rightHost = right.host?.lowercased(),
              leftHost == rightHost else {
            return false
        }
        return left.scheme?.lowercased() == right.scheme?.lowercased()
            && left.port == right.port
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

    private func readableRemoteBrowserURL(_ raw: String) -> String {
        readableWebDAVURL(from: raw)
    }

    nonisolated private func remoteBrowserProtocolLabel(_ value: MediaSourceProtocol) -> String {
        switch value {
        case .webdav: return "WebDAV"
        case .plex: return "Plex"
        case .emby: return "Emby"
        case .jellyfin: return "Jellyfin"
        case .omniplayDocker: return "OmniPlay Docker"
        default: return "媒体源"
        }
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

                if discoveredNetworkDevices.isEmpty {
                    Text("点击“扫描设备”自动发现局域网中的 WebDAV、Plex、Emby 和 Jellyfin 服务。")
                        .font(.caption)
                        .foregroundColor(.secondary)
                } else {
                    ScrollView {
                        VStack(alignment: .leading, spacing: 6) {
                            ForEach(discoveredNetworkDevices) { device in
                                Button(action: {
                                    applyDiscoveredDevice(device)
                                }) {
                                    HStack(spacing: 8) {
                                        Image(systemName: device.type.isMediaServer ? "server.rack" : "network")
                                            .foregroundColor(.secondary)
                                        Text(discoveredDeviceTitle(device))
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
            TextField("局域网自定义标签（默认：局域网）", text: $webDAVLocalLabel)
                .textFieldStyle(.roundedBorder)
            externalAddressEditor(addresses: $pendingExternalAddresses)
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
        recentWebDAVHistory = PosterWallView.loadRecentWebDAVHistory()
        webDAVBrowserName = ""
        webDAVBrowserBaseURL = ""
        webDAVBrowserUsername = ""
        webDAVBrowserPassword = ""
        webDAVBrowserCredentialID = nil
        webDAVBrowserProtocol = .webdav
        webDAVBrowserLocalLabel = "局域网"
        webDAVBrowserExternalAddresses = []
        webDAVBrowserCurrentURL = ""
        webDAVBrowserPathStack = []
        webDAVBrowserItems = []
        webDAVBrowserMountedURLs = mountedRemoteBrowserKeys(from: mediaSources)
        webDAVBrowserMessage = nil
        webDAVBrowserMessageIsError = false
        webDAVBrowserIsLoading = false
        webDAVBrowserIsBatchMounting = false
        webDAVBrowserStarredFolders = []
        if let latest = recentWebDAVHistory.sorted(by: { $0.lastUsed > $1.lastUsed }).first {
            applyRecentWebDAVHistoryToBrowser(latest, shouldBumpUsage: false, replaceBaseURL: true)
        } else if let snapshot = fetchLatestWebDAVSourceSnapshot() {
            applyRecentWebDAVHistoryToBrowser(snapshot, shouldBumpUsage: false, replaceBaseURL: true)
        }
    }

    private func prepareWebDAVBrowserLogin(for device: DiscoveredDevice) {
        let deviceURL = discoveredDeviceURLString(for: device)
        recentWebDAVHistory = PosterWallView.loadRecentWebDAVHistory()
        webDAVBrowserName = device.name.isEmpty ? "WebDAV \(device.ipAddress)" : device.name
        webDAVBrowserBaseURL = deviceURL
        webDAVBrowserUsername = ""
        webDAVBrowserPassword = ""
        webDAVBrowserCredentialID = nil
        webDAVBrowserProtocol = .webdav
        webDAVBrowserLocalLabel = "局域网"
        webDAVBrowserExternalAddresses = []
        webDAVBrowserCurrentURL = ""
        webDAVBrowserPathStack = []
        webDAVBrowserItems = []
        webDAVBrowserMountedURLs = mountedRemoteBrowserKeys(from: mediaSources)
        webDAVBrowserMessage = nil
        webDAVBrowserMessageIsError = false
        webDAVBrowserIsLoading = false
        webDAVBrowserIsBatchMounting = false
        webDAVBrowserStarredFolders = []
        if let history = matchingRecentWebDAVHistory(for: deviceURL) {
            applyRecentWebDAVHistoryToBrowser(history, shouldBumpUsage: false, replaceBaseURL: false)
        }
    }

    private func webDAVPreScanLoginSheet() -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("登录 WebDAV")
                .font(.title3.bold())
                .accessibilityIdentifier("webdav.sheet.title")
            Text("保存登录信息后会先进入共享文件夹列表；可给多个目标文件夹点星标，关闭列表时统一挂载并扫描刮削。")
                .font(.caption)
                .foregroundColor(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            TextField("显示名称（可选）", text: $webDAVBrowserName)
                .textFieldStyle(.roundedBorder)
                .accessibilityIdentifier("webdav.name")
            TextField("WebDAV 地址，例如 https://nas:5006/dav", text: $webDAVBrowserBaseURL)
                .textFieldStyle(.roundedBorder)
                .accessibilityIdentifier("webdav.baseURL")
            TextField("局域网自定义标签（默认：局域网）", text: $webDAVBrowserLocalLabel)
                .textFieldStyle(.roundedBorder)
            externalAddressEditor(addresses: $webDAVBrowserExternalAddresses)
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
                Text("选择 \(remoteBrowserProtocolLabel(webDAVBrowserProtocol)) 共享文件夹")
                    .font(.title3.bold())
                Spacer()
                if webDAVBrowserIsLoading || webDAVBrowserIsBatchMounting {
                    ProgressView().controlSize(.small)
                }
                Button {
                    closeWebDAVFolderBrowser()
                } label: {
                    Image(systemName: "xmark.circle.fill")
                        .font(.title3)
                        .foregroundColor(.secondary)
                }
                .buttonStyle(.plain)
                .disabled(webDAVBrowserIsLoading || webDAVBrowserIsBatchMounting)
                .help(webDAVBrowserStarredFolders.isEmpty ? "关闭" : "关闭并挂载标星文件夹")
            }

            VStack(alignment: .leading, spacing: 4) {
                Text(webDAVBrowserName.isEmpty ? remoteBrowserProtocolLabel(webDAVBrowserProtocol) : webDAVBrowserName)
                    .font(.subheadline.weight(.semibold))
                Text(readableRemoteBrowserURL(webDAVBrowserCurrentURL.isEmpty ? webDAVBrowserBaseURL : webDAVBrowserCurrentURL))
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }

            if webDAVBrowserProtocol == .webdav {
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
            }

            if !webDAVBrowserStarredFolders.isEmpty {
                Text("已标星 \(webDAVBrowserStarredFolders.count) 个文件夹，关闭列表后会加入挂载媒体源。")
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
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
                    Text(webDAVBrowserProtocol == .webdav ? "当前目录没有可浏览的子文件夹。" : "当前服务器没有可挂载的媒体库。")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }

            HStack {
                Spacer()
                Button(webDAVBrowserStarredFolders.isEmpty ? "关闭" : "关闭并挂载 \(webDAVBrowserStarredFolders.count) 个文件夹") {
                    closeWebDAVFolderBrowser()
                }
                .disabled(webDAVBrowserIsLoading || webDAVBrowserIsBatchMounting)
            }
        }
        .padding(20)
        .frame(width: 560)
    }

    private func webDAVFolderBrowserRow(_ item: WebDAVDirectoryItem) -> some View {
        let key = remoteBrowserKey(for: item)
        let isMounted = webDAVBrowserMountedURLs.contains(key)
        let isStarred = webDAVBrowserStarredKeys.contains(key)
        let isBusy = webDAVBrowserIsLoading || webDAVBrowserIsBatchMounting

        return HStack(spacing: 10) {
            if item.protocolKind == .webdav {
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
                .disabled(isBusy)
            } else {
                HStack(spacing: 8) {
                    Image(systemName: "rectangle.stack.fill")
                        .foregroundColor(.blue)
                    Text(item.displayName)
                        .foregroundColor(.primary)
                        .lineLimit(1)
                    Spacer()
                }
            }

            Button {
                toggleWebDAVBrowserStar(item)
            } label: {
                Image(systemName: (isMounted || isStarred) ? "star.fill" : "star")
                    .foregroundColor((isMounted || isStarred) ? .yellow : .secondary)
            }
            .buttonStyle(.plain)
            .help(isMounted ? "已挂载" : (isStarred ? "取消标星" : "标星此文件夹"))
            .disabled(isMounted || isBusy)
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
                    webDAVBrowserProtocol = .webdav
                    webDAVBrowserBaseURL = sanitizedURL
                    webDAVBrowserCurrentURL = sanitizedURL
                    webDAVBrowserPathStack = []
                    webDAVBrowserItems = items
                    webDAVBrowserMountedURLs = mountedRemoteBrowserKeys(from: mediaSources)
                    webDAVBrowserStarredFolders = []
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
                    webDAVBrowserMountedURLs = mountedRemoteBrowserKeys(from: mediaSources)
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

    private func toggleWebDAVBrowserStar(_ item: WebDAVDirectoryItem) {
        let key = remoteBrowserKey(for: item)
        let normalizedURL = normalizedRemoteBrowserURL(protocolKind: item.protocolKind, raw: item.url.absoluteString)
        guard !webDAVBrowserMountedURLs.contains(key) else { return }
        if let index = webDAVBrowserStarredFolders.firstIndex(where: { $0.key == key }) {
            webDAVBrowserStarredFolders.remove(at: index)
        } else {
            let rawName = item.displayName.trimmingCharacters(in: .whitespacesAndNewlines)
            let name = item.protocolKind == .webdav || rawName.isEmpty
                ? rawName
                : "\(remoteBrowserProtocolLabel(item.protocolKind)) · \(rawName)"
            webDAVBrowserStarredFolders.append(
                WebDAVBrowserStarredFolder(
                    key: key,
                    protocolKind: item.protocolKind,
                    url: normalizedURL,
                    name: name.isEmpty ? "\(remoteBrowserProtocolLabel(item.protocolKind)) 媒体源" : name,
                    authConfig: item.authConfig
                )
            )
        }
    }

    private func closeWebDAVFolderBrowser() {
        guard !webDAVBrowserIsLoading, !webDAVBrowserIsBatchMounting else { return }
        guard !webDAVBrowserStarredFolders.isEmpty else {
            isShowingWebDAVFolderBrowserSheet = false
            resetWebDAVBrowserState(clearLogin: true)
            return
        }
        mountStarredWebDAVBrowserFoldersAndClose()
    }

    private func mountStarredWebDAVBrowserFoldersAndClose() {
        let folders = webDAVBrowserStarredFolders
        guard !folders.isEmpty else { return }
        let authConfig = webDAVBrowserCredentialID.map { WebDAVCredentialStore.shared.authReference(for: $0) }
        let username = webDAVBrowserUsername
        let credentialID = webDAVBrowserCredentialID
        let configuredExternalAddresses = webDAVBrowserExternalAddresses
        let browserLocalLabel = webDAVBrowserLocalLabel

        webDAVBrowserIsBatchMounting = true
        webDAVBrowserMessage = nil
        webDAVBrowserMessageIsError = false

        Task {
            do {
                let sourceIDs = try await AppDatabase.shared.dbQueue.write { db -> [Int64] in
                    var mountedIDs: [Int64] = []
                    for folder in folders {
                        let baseURL = folder.url
                        let protocolValue = folder.protocolKind.rawValue
                        let folderAuthConfig = folder.protocolKind == .webdav ? authConfig : folder.authConfig
                        let sourceName = folder.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "\(remoteBrowserProtocolLabel(folder.protocolKind)) 媒体源" : folder.name
                        let existing = try MediaSource.fetchAll(
                            db,
                            sql: "SELECT * FROM mediaSource WHERE protocolType = ?",
                            arguments: [protocolValue]
                        ).first { source in
                            let configuration = source.addressConfiguration()
                            return folder.protocolKind.normalizedBaseURL(source.baseUrl).caseInsensitiveCompare(baseURL) == .orderedSame
                                || folder.protocolKind.normalizedBaseURL(configuration.localAddress).caseInsensitiveCompare(baseURL) == .orderedSame
                                || remoteBrowserKey(protocolKind: folder.protocolKind, baseURL: source.baseUrl, authConfig: source.authConfig) == folder.key
                        }
                        let existingID = existing?.id
                        var source = existing ?? MediaSource(
                            id: nil,
                            name: sourceName,
                            protocolType: protocolValue,
                            baseUrl: baseURL,
                            authConfig: folderAuthConfig
                        )
                        source.name = sourceName
                        source.baseUrl = source.baseUrl.isEmpty ? baseURL : source.baseUrl
                        if existingID == nil { source.baseUrl = baseURL }
                        if let folderAuthConfig { source.authConfig = folderAuthConfig }
                        source.isEnabled = true
                        source.disabledAt = nil
                        var configuration = existing?.addressConfiguration() ?? MediaSourceAddressConfiguration()
                        configuration.localAddress = baseURL
                        configuration.localLabel = browserLocalLabel.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "局域网" : browserLocalLabel.trimmingCharacters(in: .whitespacesAndNewlines)
                        let normalizedExternal = normalizedExternalAddresses(configuredExternalAddresses, protocolKind: folder.protocolKind)
                        configuration.externalAddresses = normalizedExternal
                        if existingID == nil || configuration.activeAddress.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                            configuration.activeAddress = baseURL
                            configuration.activeLabel = configuration.localLabel
                        }
                        source.setAddressConfiguration(configuration)
                        if existingID != nil {
                            try source.update(db)
                            mountedIDs.append(source.id ?? 0)
                        } else {
                            try source.insert(db)
                            mountedIDs.append(db.lastInsertedRowID)
                        }
                    }
                    return mountedIDs
                }

                await MainActor.run {
                    for folder in folders {
                        webDAVBrowserMountedURLs.insert(folder.key)
                    }
                    webDAVBrowserIsBatchMounting = false
                    if let credentialID {
                        for folder in folders {
                            recordRecentWebDAVHistory(name: folder.name, baseURL: folder.url, username: username, credentialID: credentialID)
                        }
                    }
                    isShowingWebDAVFolderBrowserSheet = false
                    loadData()
                    resetWebDAVBrowserState(clearLogin: true)
                    if isProcessing {
                        needsRescanAfterCurrentRun = true
                        processingMessage = "扫描中，标星的媒体源已加入下一轮队列..."
                    } else {
                        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) {
                            triggerScanAndScrape(sourceIDs: sourceIDs)
                        }
                    }
                }
            } catch {
                await MainActor.run {
                    webDAVBrowserIsBatchMounting = false
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
            webDAVBrowserProtocol = .webdav
            webDAVBrowserLocalLabel = "局域网"
            webDAVBrowserExternalAddresses = []
        }
        webDAVBrowserCurrentURL = ""
        webDAVBrowserPathStack = []
        webDAVBrowserItems = []
        webDAVBrowserMessage = nil
        webDAVBrowserMessageIsError = false
        webDAVBrowserIsLoading = false
        webDAVBrowserIsBatchMounting = false
        webDAVBrowserStarredFolders = []
    }

    nonisolated private func sanitizedWebDAVBrowserURL(_ raw: String) -> String {
        let normalized = MediaSourceProtocol.webdav.normalizedBaseURL(raw)
        guard var components = URLComponents(string: normalized) else { return normalized }
        components.user = nil
        components.password = nil
        return MediaSourceProtocol.webdav.normalizedBaseURL(components.string ?? normalized)
    }

    nonisolated private func normalizedWebDAVBrowserURL(_ raw: String) -> String {
        sanitizedWebDAVBrowserURL(raw)
    }

    nonisolated private func normalizedRemoteBrowserURL(protocolKind: MediaSourceProtocol, raw: String) -> String {
        if protocolKind == .webdav {
            return normalizedWebDAVBrowserURL(raw)
        }
        return protocolKind.normalizedBaseURL(raw)
    }

    nonisolated private func remoteBrowserKey(protocolKind: MediaSourceProtocol, baseURL: String, authConfig: String?) -> String {
        let normalizedURL = normalizedRemoteBrowserURL(protocolKind: protocolKind, raw: baseURL)
        if protocolKind == .plex || protocolKind == .emby || protocolKind == .jellyfin {
            let libraryId = MediaServerAuthConfig.decode(authConfig)?.libraryId?.trimmingCharacters(in: .whitespacesAndNewlines)
            return "\(protocolKind.rawValue):\(normalizedURL):\(libraryId?.isEmpty == false ? libraryId! : "all")"
        }
        if protocolKind == .omniplayDocker {
            return "\(protocolKind.rawValue):\(normalizedURL)"
        }
        return "\(protocolKind.rawValue):\(normalizedURL)"
    }

    nonisolated private func remoteBrowserKey(for item: WebDAVDirectoryItem) -> String {
        remoteBrowserKey(protocolKind: item.protocolKind, baseURL: item.url.absoluteString, authConfig: item.authConfig)
    }

    private func mountedRemoteBrowserKeys(from sources: [MediaSource]) -> Set<String> {
        Set(sources.compactMap { source in
            guard let protocolKind = source.protocolKind else { return nil }
            guard protocolKind == .webdav || protocolKind == .plex || protocolKind == .emby || protocolKind == .jellyfin || protocolKind == .omniplayDocker else { return nil }
            return remoteBrowserKey(protocolKind: protocolKind, baseURL: source.baseUrl, authConfig: source.authConfig)
        })
    }

    private func applyDiscoveredDevice(_ device: DiscoveredDevice) {
        switch device.type {
        case .webdavHTTP:
            webDAVBaseURL = discoveredDeviceURLString(for: device)
            if webDAVName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                webDAVName = device.name.isEmpty ? "WebDAV \(device.ipAddress)" : device.name
            }
            webDAVValidationMessage = nil
        case .webdavHTTPS:
            webDAVBaseURL = discoveredDeviceURLString(for: device)
            if webDAVName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                webDAVName = device.name.isEmpty ? "WebDAV \(device.ipAddress)" : device.name
            }
            webDAVValidationMessage = nil
        case .plex, .emby, .jellyfin, .omniplayDocker:
            prepareMediaServerLogin(for: device)
            isShowingAddWebDAVSheet = false
            isShowingManualRemoteSourceSheet = true
        case .smb:
            break
        }
    }

    private func openDiscoveredDeviceFromManageSheet(_ device: DiscoveredDevice) {
        lanScanner.stopScanning()
        isShowingManageSources = false
        closeRemoteSourceSheets()
        if device.type.isMediaServer {
            prepareMediaServerLogin(for: device)
            DispatchQueue.main.async {
                isShowingManualRemoteSourceSheet = true
            }
        } else if device.type.isWebDAV {
            prepareWebDAVBrowserLogin(for: device)
            DispatchQueue.main.async {
                isShowingWebDAVPreScanLoginSheet = true
            }
        }
    }

    private func prepareMediaServerLogin(for device: DiscoveredDevice) {
        switch device.type {
        case .plex:
            mediaServerProtocol = .plex
        case .emby:
            mediaServerProtocol = .emby
        case .jellyfin:
            mediaServerProtocol = .jellyfin
        case .omniplayDocker:
            mediaServerProtocol = .omniplayDocker
        default:
            mediaServerProtocol = .plex
        }
        if mediaServerProtocol == .omniplayDocker {
            mediaServerName = "OmniPlay Docker"
        } else {
            mediaServerName = device.name.isEmpty ? "\(mediaServerProtocolLabel(mediaServerProtocol)) \(device.ipAddress)" : device.name
        }
        mediaServerBaseURL = discoveredDeviceURLString(for: device)
        mediaServerToken = ""
        mediaServerUserId = ""
        mediaServerPassword = ""
        mediaServerIsPreScanning = false
        mediaServerLocalLabel = "局域网"
        pendingExternalAddresses = []
        mediaServerMessage = mediaServerConnectionHint(for: mediaServerProtocol)
    }

    private func discoveredDeviceURLString(for device: DiscoveredDevice) -> String {
        let scheme = (device.type == .webdavHTTPS || device.port == 443 || device.port == 5006 || device.port == 8920) ? "https" : "http"
        return "\(scheme)://\(device.ipAddress):\(device.port)"
    }

    private func discoveredDeviceTitle(_ device: DiscoveredDevice) -> String {
        if !device.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return device.name
        }
        return "\(device.type.rawValue) \(device.ipAddress)"
    }

    private func resetMediaServerForm() {
        mediaServerProtocol = .plex
        mediaServerName = ""
        mediaServerBaseURL = defaultMediaServerBaseURL(for: .plex)
        mediaServerToken = ""
        mediaServerUserId = ""
        mediaServerPassword = ""
        mediaServerIsPreScanning = false
        mediaServerLocalLabel = "局域网"
        pendingExternalAddresses = []
        mediaServerMessage = mediaServerConnectionHint(for: .plex)
    }

    private func resetOmniPlayDockerForm() {
        mediaServerProtocol = .omniplayDocker
        mediaServerName = ""
        mediaServerBaseURL = defaultMediaServerBaseURL(for: .omniplayDocker)
        mediaServerToken = ""
        mediaServerUserId = ""
        mediaServerPassword = ""
        mediaServerIsPreScanning = false
        mediaServerLocalLabel = "局域网"
        pendingExternalAddresses = []
        mediaServerMessage = mediaServerConnectionHint(for: .omniplayDocker)
    }

    private func authorizePlex() {
        guard mediaServerProtocol == .plex else { return }
        if mediaServerBaseURL.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            mediaServerBaseURL = defaultMediaServerBaseURL(for: .plex)
        }
        mediaServerIsPreScanning = true
        mediaServerMessage = "正在创建 Plex 授权请求..."

        Task {
            do {
                let authSession = try await PlexPINAuthClient.shared.begin()
                await MainActor.run {
                    NSWorkspace.shared.open(authSession.authorizationURL)
                    mediaServerMessage = "已打开 Plex Link 页面，请确认 PIN：\(authSession.code)"
                }
                let token = try await PlexPINAuthClient.shared.waitForToken(pinID: authSession.id)
                await MainActor.run {
                    mediaServerToken = token
                    mediaServerIsPreScanning = false
                    mediaServerMessage = "Plex 授权成功，已自动填入访问令牌。"
                }
            } catch {
                await MainActor.run {
                    mediaServerIsPreScanning = false
                    mediaServerMessage = "Plex 授权失败：\(error.localizedDescription)"
                }
            }
        }
    }

    private func currentMediaServerDraft() -> MediaServerSourceDraft? {
        guard mediaServerProtocol != .webdav else {
            openWebDAVLoginFromRemoteSourceForm()
            return nil
        }

        let normalizedURL = mediaServerProtocol.normalizedBaseURL(mediaServerBaseURL)
        guard mediaServerProtocol.isValidBaseURL(normalizedURL) else {
            mediaServerMessage = "服务器地址无效，请输入 http(s):// 开头且包含主机名的地址。"
            return nil
        }

        let finalName: String = {
            let trimmed = mediaServerName.trimmingCharacters(in: .whitespacesAndNewlines)
            if !trimmed.isEmpty { return trimmed }
            let host = URL(string: normalizedURL)?.host ?? "媒体服务器"
            return "\(mediaServerProtocolLabel(mediaServerProtocol)) · \(host)"
        }()
        if mediaServerProtocol == .omniplayDocker {
            let username = mediaServerUserId.trimmingCharacters(in: .whitespacesAndNewlines)
            let password = mediaServerPassword.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !username.isEmpty, !password.isEmpty else {
                mediaServerMessage = mediaServerMissingTokenMessage(for: mediaServerProtocol)
                return nil
            }
            return MediaServerSourceDraft(
                protocolKind: mediaServerProtocol,
                normalizedURL: normalizedURL,
                token: password,
                userId: username,
                finalName: finalName,
                authConfig: nil,
                localLabel: mediaServerLocalLabel,
                externalAddresses: normalizedExternalAddresses(pendingExternalAddresses, protocolKind: mediaServerProtocol)
            )
        }

        let token = mediaServerToken.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !token.isEmpty else {
            mediaServerMessage = mediaServerMissingTokenMessage(for: mediaServerProtocol)
            return nil
        }
        let userId = mediaServerProtocol == .plex ? "" : mediaServerUserId.trimmingCharacters(in: .whitespacesAndNewlines)
        let authConfig = MediaServerAuthConfig.encode(token: token, userId: userId)
        return MediaServerSourceDraft(
            protocolKind: mediaServerProtocol,
            normalizedURL: normalizedURL,
            token: token,
            userId: userId,
            finalName: finalName,
            authConfig: authConfig,
            localLabel: mediaServerLocalLabel,
            externalAddresses: normalizedExternalAddresses(pendingExternalAddresses, protocolKind: mediaServerProtocol)
        )
    }

    private func preScanMediaServerSource() {
        guard let draft = currentMediaServerDraft() else { return }
        Task {
            _ = await performMediaServerPreScan(draft: draft)
        }
    }

    private func performMediaServerPreScan(draft: MediaServerSourceDraft) async -> Bool {
        await MainActor.run {
            mediaServerIsPreScanning = true
            mediaServerMessage = "正在预扫描 \(mediaServerProtocolLabel(draft.protocolKind)) 媒体列表..."
        }
        let result = await MediaServerPreflightChecker().check(
            protocolKind: draft.protocolKind,
            baseURL: draft.normalizedURL,
            token: draft.token,
            userId: draft.userId
        )
        await MainActor.run {
            mediaServerIsPreScanning = false
            mediaServerMessage = result.message
        }
        return result.isReachable
    }

    private func saveMediaServerSource() {
        guard let draft = currentMediaServerDraft() else { return }

        if draft.protocolKind == .omniplayDocker {
            saveOmniPlayDockerSource(draft: draft)
            return
        }

        Task {
            do {
                await MainActor.run {
                    mediaServerIsPreScanning = true
                    mediaServerMessage = "正在预扫描 \(mediaServerProtocolLabel(draft.protocolKind)) 共享文件夹..."
                }
                let items = try await MediaServerLibraryBrowser().listLibraries(
                    protocolKind: draft.protocolKind,
                    baseURL: draft.normalizedURL,
                    token: draft.token,
                    userId: draft.userId
                )

                await MainActor.run {
                    mediaServerIsPreScanning = false
                    isShowingMediaServerSheet = false
                    webDAVBrowserProtocol = draft.protocolKind
                    webDAVBrowserName = draft.finalName
                    webDAVBrowserBaseURL = draft.normalizedURL
                    webDAVBrowserLocalLabel = draft.localLabel
                    webDAVBrowserExternalAddresses = draft.externalAddresses
                    webDAVBrowserUsername = ""
                    webDAVBrowserPassword = ""
                    webDAVBrowserCredentialID = nil
                    webDAVBrowserCurrentURL = draft.normalizedURL
                    webDAVBrowserPathStack = []
                    webDAVBrowserItems = items
                    webDAVBrowserMountedURLs = mountedRemoteBrowserKeys(from: mediaSources)
                    webDAVBrowserStarredFolders = []
                    webDAVBrowserMessage = items.isEmpty ? "连接成功，但当前服务器没有可挂载的媒体库。" : "预扫描成功：选择要挂载的媒体库并点星标。"
                    webDAVBrowserMessageIsError = false
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) {
                        isShowingWebDAVFolderBrowserSheet = true
                    }
                }
            } catch {
                await MainActor.run {
                    mediaServerIsPreScanning = false
                    mediaServerMessage = "媒体服务器预扫描失败：\(error.localizedDescription)"
                }
            }
        }
    }

    private func saveOmniPlayDockerSource(draft: MediaServerSourceDraft) {
        Task {
            do {
                await MainActor.run {
                    mediaServerIsPreScanning = true
                    mediaServerMessage = "正在登录 OmniPlay Docker..."
                }
                let client = try OmniPlayDockerClient(baseURLString: draft.normalizedURL)
                try await client.login(username: draft.userId, password: draft.token)
                let authConfig = OmniPlayDockerAuthConfig.encode(username: draft.userId, sessionCookie: client.sessionCookie)
                let sourceId = try await AppDatabase.shared.dbQueue.write { db -> Int64 in
                    let existing = try MediaSource.fetchAll(
                        db,
                        sql: "SELECT * FROM mediaSource WHERE protocolType = ?",
                        arguments: [MediaSourceProtocol.omniplayDocker.rawValue]
                    ).first { source in
                        let configuration = source.addressConfiguration()
                        return MediaSourceProtocol.omniplayDocker.normalizedBaseURL(source.baseUrl).caseInsensitiveCompare(draft.normalizedURL) == .orderedSame
                            || MediaSourceProtocol.omniplayDocker.normalizedBaseURL(configuration.localAddress).caseInsensitiveCompare(draft.normalizedURL) == .orderedSame
                    }
                    if var existing {
                        existing.name = draft.finalName
                        existing.authConfig = authConfig
                        existing.isEnabled = true
                        existing.disabledAt = nil
                        var configuration = existing.addressConfiguration()
                        configuration.localAddress = draft.normalizedURL
                        configuration.localLabel = draft.localLabel.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "局域网" : draft.localLabel.trimmingCharacters(in: .whitespacesAndNewlines)
                        configuration.externalAddresses = draft.externalAddresses
                        if configuration.activeAddress.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                            configuration.activeAddress = draft.normalizedURL
                            configuration.activeLabel = configuration.localLabel
                        }
                        existing.setAddressConfiguration(configuration)
                        try existing.update(db)
                        return existing.id ?? 0
                    }

                    var source = MediaSource(
                        id: nil,
                        name: draft.finalName,
                        protocolType: MediaSourceProtocol.omniplayDocker.rawValue,
                        baseUrl: draft.normalizedURL,
                        authConfig: authConfig,
                        isEnabled: true,
                        disabledAt: nil
                    )
                    let localLabel = draft.localLabel.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "局域网" : draft.localLabel.trimmingCharacters(in: .whitespacesAndNewlines)
                    source.setAddressConfiguration(MediaSourceAddressConfiguration(
                        localAddress: draft.normalizedURL,
                        localLabel: localLabel,
                        externalAddresses: draft.externalAddresses,
                        activeAddress: draft.normalizedURL,
                        activeLabel: localLabel
                    ))
                    try source.insert(db)
                    return db.lastInsertedRowID
                }

                let source = try await AppDatabase.shared.dbQueue.read { db in
                    try MediaSource.fetchOne(db, key: sourceId)
                }
                if let source {
                    _ = await libraryManager.syncOmniPlayDockerSourceWithResult(source)
                }

                await MainActor.run {
                    mediaServerIsPreScanning = false
                    mediaServerMessage = "Docker 同步完成"
                    dismissRemoteSourceForm()
                    loadData()
                }
            } catch {
                await MainActor.run {
                    mediaServerIsPreScanning = false
                    mediaServerMessage = "Docker 连接失败：\(error.localizedDescription)"
                }
            }
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
        let configuredExternalAddresses = normalizedExternalAddresses(pendingExternalAddresses, protocolKind: .webdav)
        let configuredLocalLabel = webDAVLocalLabel

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
                    let existing = try MediaSource.fetchAll(
                        db,
                        sql: "SELECT * FROM mediaSource WHERE protocolType = ?",
                        arguments: [MediaSourceProtocol.webdav.rawValue]
                    ).first { source in
                        let configuration = source.addressConfiguration()
                        return MediaSourceProtocol.webdav.normalizedBaseURL(source.baseUrl).caseInsensitiveCompare(normalizedURL) == .orderedSame
                            || MediaSourceProtocol.webdav.normalizedBaseURL(configuration.localAddress).caseInsensitiveCompare(normalizedURL) == .orderedSame
                    }
                    let existingID = existing?.id
                    var source = existing ?? MediaSource(
                        id: nil,
                        name: finalName,
                        protocolType: MediaSourceProtocol.webdav.rawValue,
                        baseUrl: normalizedURL,
                        authConfig: authConfig
                    )
                    source.name = finalName
                    if existingID == nil { source.baseUrl = normalizedURL }
                    if let authConfig { source.authConfig = authConfig }
                    var configuration = existing?.addressConfiguration() ?? MediaSourceAddressConfiguration()
                    configuration.localAddress = normalizedURL
                    configuration.localLabel = configuredLocalLabel.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "局域网" : configuredLocalLabel.trimmingCharacters(in: .whitespacesAndNewlines)
                    configuration.externalAddresses = configuredExternalAddresses
                    if existingID == nil || configuration.activeAddress.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                        configuration.activeAddress = normalizedURL
                        configuration.activeLabel = configuration.localLabel
                    }
                    source.setAddressConfiguration(configuration)
                    if existingID == nil { try source.insert(db) } else { try source.update(db) }
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

    private func checkMediaSourceConnectivity(_ source: MediaSource) {
        guard let sourceID = source.id else { return }
        checkingSourceIDs.insert(sourceID)
        sourceConnectivityStatus[sourceID] = "检测中..."
        let candidates = sourceAddressCandidates(for: source)
        Task {
            let result = await checkSourceCandidates(source: source, candidates: candidates)
            var persisted = false
            if result.isReachable {
                persisted = await MainActor.run {
                    (try? AppDatabase.shared.dbQueue.write { db -> Bool in
                        guard var latest = try MediaSource.fetchOne(db, key: sourceID) else { return false }
                        var configuration = latest.addressConfiguration()
                        latest.baseUrl = result.address
                        configuration.activeAddress = result.address
                        configuration.activeLabel = result.label
                        latest.setAddressConfiguration(configuration)
                        try latest.update(db)
                        return true
                    }) ?? false
                }
            }
            await MainActor.run {
                checkingSourceIDs.remove(sourceID)
                if result.isReachable && persisted {
                    sourceConnectivityStatus[sourceID] = "连接成功：\(result.label) · \(result.address)"
                    loadData()
                } else if result.isReachable {
                    sourceConnectivityStatus[sourceID] = "连接成功：\(result.label) · \(result.address)"
                } else {
                    sourceConnectivityStatus[sourceID] = result.message
                }
            }
        }
    }

    private func sourceAddressCandidates(for source: MediaSource) -> [SourceAddressCandidate] {
        let configuration = source.addressConfiguration()
        let localAddress = configuration.localAddress.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? source.baseUrl : configuration.localAddress
        var result = [SourceAddressCandidate(address: localAddress, label: configuration.localLabel.isEmpty ? "局域网" : configuration.localLabel)]
        for external in configuration.externalAddresses {
            let address = external.address.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !address.isEmpty else { continue }
            if !result.contains(where: { $0.address.caseInsensitiveCompare(address) == .orderedSame }) {
                result.append(SourceAddressCandidate(address: address, label: external.displayLabel))
            }
        }
        return result
    }

    private func checkSourceCandidates(source: MediaSource, candidates: [SourceAddressCandidate]) async -> SourceConnectivityResult {
        guard let protocolKind = source.protocolKind else { return SourceConnectivityResult(isReachable: false, address: source.baseUrl, label: "", message: "媒体源协议无效。") }
        var failures: [String] = []
        for candidate in candidates {
            do {
                switch protocolKind {
                case .local:
                    var isDirectory: ObjCBool = false
                    guard FileManager.default.fileExists(atPath: candidate.address, isDirectory: &isDirectory), isDirectory.boolValue else { throw NSError(domain: "OmniPlayConnectivity", code: 1, userInfo: [NSLocalizedDescriptionKey: "目录不存在或不可访问"]) }
                case .webdav:
                    let credential = WebDAVCredentialStore.shared.credentialID(from: source.authConfig).flatMap { WebDAVCredentialStore.shared.loadCredential(id: $0) } ?? WebDAVCredentialStore.shared.decodeLegacyCredential(from: source.authConfig)
                    let result = await WebDAVPreflightChecker().check(baseURL: candidate.address, username: credential?.username ?? "", password: credential?.password ?? "")
                    guard result.isReachable else { throw NSError(domain: "OmniPlayConnectivity", code: 2, userInfo: [NSLocalizedDescriptionKey: result.message]) }
                case .plex, .emby, .jellyfin:
                    let auth = MediaServerAuthConfig.decode(source.authConfig)
                    let result = await MediaServerPreflightChecker().check(protocolKind: protocolKind, baseURL: candidate.address, token: auth?.token ?? "", userId: auth?.userId ?? "")
                    guard result.isReachable else { throw NSError(domain: "OmniPlayConnectivity", code: 3, userInfo: [NSLocalizedDescriptionKey: result.message]) }
                case .omniplayDocker:
                    let auth = OmniPlayDockerAuthConfig.decode(source.authConfig)
                    guard let cookie = auth?.sessionCookie, !cookie.isEmpty else { throw OmniPlayDockerClientError.authenticationRequired }
                    let client = try OmniPlayDockerClient(baseURLString: candidate.address, sessionCookie: cookie)
                    _ = try await client.libraryItems()
                case .direct:
                    break
                }
                return SourceConnectivityResult(isReachable: true, address: protocolKind.normalizedBaseURL(candidate.address), label: candidate.label, message: "连接成功")
            } catch { failures.append("\(candidate.label)：\(error.localizedDescription)") }
        }
        return SourceConnectivityResult(isReachable: false, address: source.baseUrl, label: "", message: failures.isEmpty ? "没有可检测的媒体源地址。" : failures.joined(separator: "；"))
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
                    self.webDAVBrowserMountedURLs = self.mountedRemoteBrowserKeys(from: fetchedSources)
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

    private func startDockerAutoSyncIfNeeded() {
        guard dockerAutoSyncTask == nil else { return }
        dockerAutoSyncTask = Task {
            try? await Task.sleep(nanoseconds: 5_000_000_000)
            while !Task.isCancelled {
                await syncEnabledOmniPlayDockerSourcesInBackground()
                try? await Task.sleep(nanoseconds: 10 * 60 * 1_000_000_000)
            }
        }
    }

    private func syncEnabledOmniPlayDockerSourcesInBackground() async {
        guard !isProcessing else { return }
        let sources = (try? await AppDatabase.shared.dbQueue.read { db in
            try MediaSource.fetchEnabledScannableSources(in: db).filter { $0.protocolKind == .omniplayDocker }
        }) ?? []
        guard !sources.isEmpty else { return }
        for source in sources {
            if Task.isCancelled { return }
            _ = await libraryManager.syncOmniPlayDockerSourceWithResult(source)
        }
    }

    private func cancelCurrentScanRunForSource(_ sourceID: Int64) {
        removedSourceIDsDuringRun.insert(sourceID)
        ThumbnailManager.shared.cancelTasks(forSourceID: sourceID)
        currentScanTask?.cancel()
        currentScanTask = nil
        currentScanRunID = UUID()
        activeScanningSourceID = nil
        needsRescanAfterCurrentRun = false
        withAnimation {
            isProcessing = false
            processingMessage = ""
        }
    }

    private func removeMediaSource(_ source: MediaSource, removeCredential: Bool) {
        let removingSourceID = source.id
        if let sid = removingSourceID {
            if isProcessing {
                cancelCurrentScanRunForSource(sid)
            } else {
                removedSourceIDsDuringRun.insert(sid)
                ThumbnailManager.shared.cancelTasks(forSourceID: sid)
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
            cancelCurrentScanRunForSource(sid)
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
                    }
                }
            } catch {
                print("❌ 切换媒体源状态失败: \(error)")
            }
        }
    }

    private func finishScanRun(runID: UUID, reloadData: Bool) {
        guard currentScanRunID == runID else { return }
        activeScanningSourceID = nil
        removedSourceIDsDuringRun = []
        currentScanTask = nil
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

    private func triggerScanAndScrape(sourceID: Int64? = nil, sourceIDs: [Int64]? = nil, skipTMDBScrape: Bool = false) {
        guard !isProcessing else { return }
        currentScanTask?.cancel()
        let runID = UUID()
        currentScanRunID = runID
        withAnimation {
            isProcessing = true
            processingMessage = "准备扫描..."
        }
        removedSourceIDsDuringRun = []
        activeScanningSourceID = nil

        currentScanTask = Task(priority: .utility) {
            do {
                if Task.isCancelled {
                    await MainActor.run { finishScanRun(runID: runID, reloadData: true) }
                    return
                }
                try? libraryManager.cleanupExpiredDisabledSources()
                let validSources = try await AppDatabase.shared.dbQueue.read { db in
                    if let sourceIDs, !sourceIDs.isEmpty {
                        let targetIDs = Set(sourceIDs)
                        return try MediaSource.fetchEnabledScannableSources(in: db).filter { source in
                            guard let id = source.id else { return false }
                            return targetIDs.contains(id)
                        }
                    } else if let sourceID {
                        if let source = try MediaSource.fetchEnabledScannableSource(id: sourceID, in: db) {
                            return [source]
                        }
                        return []
                    }
                    return try MediaSource.fetchEnabledScannableSources(in: db)
                }

                if validSources.isEmpty {
                    await MainActor.run { finishScanRun(runID: runID, reloadData: true) }
                    return
                }

                await MainActor.run {
                    guard self.currentScanRunID == runID else { return }
                    withAnimation { self.processingMessage = "扫描目录中..." }
                }
                var sourceResults: [MediaSourceScanResult] = []
                for (index, source) in validSources.enumerated() {
                    if Task.isCancelled {
                        await MainActor.run { finishScanRun(runID: runID, reloadData: true) }
                        return
                    }
                    if let sid = source.id, removedSourceIDsDuringRun.contains(sid) { continue }
                    await MainActor.run {
                        guard self.currentScanRunID == runID else { return }
                        activeScanningSourceID = source.id
                    }
                    await MainActor.run {
                        guard self.currentScanRunID == runID else { return }
                        withAnimation {
                            self.processingMessage = "扫描中 (\(index + 1)/\(validSources.count))：\(source.name)"
                        }
                    }
                    let result: MediaSourceScanResult
                    if source.protocolKind == .omniplayDocker {
                        result = await libraryManager.syncOmniPlayDockerSourceWithResult(source)
                    } else if skipTMDBScrape {
                        result = await libraryManager.scanLocalSourceWithResult(
                            source,
                            deferUnidentifiedGroups: false
                        )
                    } else {
                        result = await libraryManager.scanLocalSourceWithResult(
                            source,
                            deferUnidentifiedGroups: false
                        )
                    }
                    let isCurrentRun = await MainActor.run { self.currentScanRunID == runID }
                    guard isCurrentRun else { return }
                    await MainActor.run { activeScanningSourceID = nil }
                    sourceResults.append(result)
                    await MainActor.run {
                        guard self.currentScanRunID == runID else { return }
                        withAnimation {
                            self.processingMessage = result.isSuccess
                                ? "完成：\(source.name)（新增\(result.insertedCount) 移除\(result.removedCount)）"
                                : "失败：\(source.name)（\(result.errorCategory?.displayName ?? "未知错误")）"
                        }
                    }
                    if result.isSuccess, let sid = source.id, !removedSourceIDsDuringRun.contains(sid) {
                        if Task.isCancelled {
                            await MainActor.run { finishScanRun(runID: runID, reloadData: true) }
                            return
                        }
                        if skipTMDBScrape && source.protocolKind != .omniplayDocker {
                            await MainActor.run {
                                guard self.currentScanRunID == runID else { return }
                                withAnimation {
                                    self.processingMessage = "显示未刮削海报：\(source.name)"
                                }
                            }
                            try? await libraryManager.exposeQueuedUnmatchedPlaceholders(sourceID: sid)
                            await MainActor.run {
                                guard self.currentScanRunID == runID else { return }
                                loadData()
                            }
                            continue
                        }
                    }
                }

                if Task.isCancelled {
                    await MainActor.run { finishScanRun(runID: runID, reloadData: true) }
                    return
                }
                await MainActor.run {
                    guard self.currentScanRunID == runID else { return }
                    withAnimation { self.processingMessage = "更新首页..." }
                }
                let unidentifiedInsertedCount = await libraryManager.insertDeferredUnidentifiedMedia(from: sourceResults)
                if unidentifiedInsertedCount > 0 {
                    await MainActor.run {
                        guard self.currentScanRunID == runID else { return }
                        loadData()
                    }
                }
                await MainActor.run {
                    guard self.currentScanRunID == runID else { return }
                    loadData()
                }
                if skipTMDBScrape {
                    await MainActor.run {
                        guard self.currentScanRunID == runID else { return }
                        withAnimation { self.processingMessage = "等待 TMDB API 连通后刮削..." }
                    }
                    try? await libraryManager.exposeQueuedUnmatchedPlaceholders()
                    await MainActor.run {
                        guard self.currentScanRunID == runID else { return }
                        loadData()
                        finishScanRun(runID: runID, reloadData: true)
                    }
                    return
                }
                let successfulSources = sourceResults.compactMap { result -> (id: Int64, name: String)? in
                    guard result.isSuccess,
                          let sid = result.sourceId,
                          !removedSourceIDsDuringRun.contains(sid) else {
                        return nil
                    }
                    return (sid, result.sourceName)
                }
                let successfulSourceIDs = successfulSources.map(\.id)
                let hasPendingScrape = (try? await libraryManager.hasPendingMetadataScrape(sourceIDs: successfulSourceIDs)) ?? false
                if !hasPendingScrape {
                    await MainActor.run {
                        guard self.currentScanRunID == runID else { return }
                        withAnimation { self.processingMessage = "没有需要 TMDB 刮削的内容" }
                    }
                    await MainActor.run {
                        finishScanRun(runID: runID, reloadData: true)
                    }
                    return
                }
                await MainActor.run {
                    guard self.currentScanRunID == runID else { return }
                    withAnimation { self.processingMessage = "检测 TMDB 连通性..." }
                }
                let tmdbConnection = await TMDBService.shared.checkConnection()
                if !tmdbConnection.isConnected {
                    await MainActor.run {
                        guard self.currentScanRunID == runID else { return }
                        tmdbConnectionMessage = tmdbConnection.message
                        finishScanRun(runID: runID, reloadData: true)
                        isShowingTMDBConnectionAlert = true
                    }
                    return
                }
                for (index, source) in successfulSources.enumerated() {
                    if Task.isCancelled {
                        await MainActor.run { finishScanRun(runID: runID, reloadData: true) }
                        return
                    }
                    await MainActor.run {
                        guard self.currentScanRunID == runID else { return }
                        withAnimation {
                            self.processingMessage = "刮削元数据和海报 (\(index + 1)/\(successfulSources.count))：\(source.name)"
                        }
                    }
                    try? await libraryManager.processUnmatchedFiles(sourceID: source.id)
                    await MainActor.run {
                        guard self.currentScanRunID == runID else { return }
                        loadData()
                    }
                }
                if Task.isCancelled {
                    await MainActor.run { finishScanRun(runID: runID, reloadData: true) }
                    return
                }
                await MainActor.run {
                    guard self.currentScanRunID == runID else { return }
                    withAnimation { self.processingMessage = "补齐分集剧照..." }
                }
                let orderedMovieIDs = await MainActor.run {
                    self.displayedMovies.compactMap(\.id)
                }
                ThumbnailManager.shared.enqueueMissingEpisodeThumbnailsAcrossLibrary(orderedMovieIDs: orderedMovieIDs)

                await MainActor.run {
                    finishScanRun(runID: runID, reloadData: true)
                }
            } catch {
                await MainActor.run { finishScanRun(runID: runID, reloadData: true) }
            }
        }
    }

}

// 🌟 彻底删除了原先几百行的内网目录浏览器 (SourceFileBrowserView)
// 因为我们现在全部交给原生的 Finder 来选了！
