import SwiftUI
import GRDB
import AppKit
import UniformTypeIdentifiers

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

/// 局域网媒体源弹窗里的类型选择：SMB 只在 UI 层出现，入库仍是本地目录 + SMB 挂载信息
private enum RemoteSourceKind: String, CaseIterable, Identifiable {
    case webdav, smb, plex, emby, jellyfin

    var id: String { rawValue }

    var label: String {
        switch self {
        case .webdav: return "WebDAV"
        case .smb: return "SMB"
        case .plex: return "Plex"
        case .emby: return "Emby"
        case .jellyfin: return "Jellyfin"
        }
    }

    var protocolKind: MediaSourceProtocol? {
        MediaSourceProtocol(rawValue: rawValue)
    }

    init(_ protocolKind: MediaSourceProtocol) {
        self = RemoteSourceKind(rawValue: protocolKind.rawValue) ?? .webdav
    }
}

private struct SMBBrowserEntry: Identifiable, Equatable {
    let share: String
    let relativePath: String
    let name: String

    var id: String { "\(share)|\(relativePath)" }
    var key: String { id }
}

private struct SMBStarredTarget: Identifiable, Equatable {
    let share: String
    let relativePath: String
    let name: String

    var id: String { "\(share)|\(relativePath)" }
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

private func posterWallDropURLs(from providers: [NSItemProvider]) async -> [URL] {
    var urls: [URL] = []
    for provider in providers {
        guard provider.hasItemConformingToTypeIdentifier(UTType.fileURL.identifier) else { continue }
        let url: URL? = await withCheckedContinuation { continuation in
            provider.loadObject(ofClass: NSURL.self) { object, _ in
                continuation.resume(returning: (object as? NSURL) as URL?)
            }
        }
        if let url { urls.append(url) }
    }
    return urls
}

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
    @State private var isShowingSourceInfoSheet = false
    @State private var sourceInfoTarget: MediaSource?
    @State private var sourceToRename: MediaSource? = nil
    @State private var renamingSourceName: String = ""
    @State private var isShowingManualRemoteSourceSheet = false
    @State private var remoteSourceKind: RemoteSourceKind = .plex
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
    @State private var mediaServerProtocol: MediaSourceProtocol = .plex
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
    @State private var isShowingLuckyStunSourceSheet = false
    @State private var luckySourceName = ""
    @State private var luckySourceProtocol: MediaSourceProtocol = .plex
    @State private var luckyManagementURL = ""
    @State private var luckyUsername = ""
    @State private var luckyPassword = ""
    @State private var luckyRuleID = ""
    @State private var luckyStunAddress = ""
    @State private var luckyAutoUpdate = true
    @State private var luckyUpdateIntervalMinutes = 30
    @State private var luckyDockerUsername = ""
    @State private var luckyDockerPassword = ""
    @State private var luckyStatusMessage: String? = nil
    @State private var isTestingLuckySource = false
    @State private var pendingLuckyConfiguration: LuckyStunSourceConfiguration? = nil
    @State private var isShowingWebDAVPreScanLoginSheet = false
    @State private var isShowingWebDAVFolderBrowserSheet = false
    @State private var isShowingSMBBrowserSheet = false
    @State private var shouldShowSMBBrowserAfterLoginDismiss = false
    @State private var smbHost = ""
    @State private var smbUser = ""
    @State private var smbPassword = ""
    @State private var smbSourceName = ""
    @State private var smbMessage = ""
    @State private var smbMessageIsError = false
    @State private var smbIsWorking = false
    @State private var smbIsLoadingBrowser = false
    @State private var smbIsBatchMounting = false
    @State private var smbConnection: SMBShareInfo? = nil
    @State private var smbShareNames: [String] = []
    @State private var smbBrowserShare = ""
    @State private var smbBrowserMountPath = ""
    @State private var smbBrowserRelative = ""
    @State private var smbBrowserStack: [String] = []
    @State private var smbBrowserEntries: [SMBBrowserEntry] = []
    @State private var smbStarredTargets: [SMBStarredTarget] = []
    @State private var smbMountedKeys: Set<String> = []
    @State private var smbTempMountedShares: Set<String> = []
    @State private var smbTempMountPaths: [String: String] = [:]
    @State private var shouldStartLuckyWebDAVAfterDismiss = false
    @State private var shouldShowWebDAVBrowserAfterLoginDismiss = false
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
    
    @AppStorage(AppThemeResolver.colorKey) var appTheme = ThemeType.crystal.rawValue
    @AppStorage(AppThemeResolver.styleKey) var appThemeStyle = ThemeStyle.classic.rawValue
    var theme: AppTheme { AppThemeResolver.theme(colorRaw: appTheme, styleRaw: appThemeStyle) }
    @Environment(\.colorScheme) private var colorScheme
    
    @ObservedObject var thumbManager = ThumbnailManager.shared
    @ObservedObject var cacheManager = OfflineCacheManager.shared
    @ObservedObject private var directPlayback = DirectFilePlaybackManager.shared
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
    @State private var sourceConnectivityStatus: [Int64: String] = [:]
    @State private var checkingSourceIDs: Set<Int64> = []
    @State private var isLoadingLibraryData = false
    @State private var needsLibraryReloadAfterCurrentLoad = false
    @State private var isShowingTMDBConnectionAlert = false
    @State private var tmdbConnectionMessage = ""
    @State private var isShowingScanErrorAlert = false
    @State private var scanErrorMessage = ""
    @State private var scanErrorAlertTitle = "扫描刮削失败"
    @State private var isHomeCacheModeActive = false

    // 🌟 首页分类（导演/主演文件夹）
    @AppStorage("homeCategoryMode") var homeCategoryMode = HomeCategoryMode.none.rawValue
    @AppStorage("categoryFolderSortAsc") var categoryFolderSortAsc = true
    @State private var categoryFolderRecords: [String: CategoryFolder] = [:]
    @State private var categoryFolderRandomPosters: [String: String?] = [:]
    @State private var categoryPosterPickerGroup: CategoryFolderGroup? = nil
    // 分类文件夹编辑弹窗里的人物名输入框草稿
    @State private var categoryFolderNameDraft = ""
    @State private var personProfiles: [String: PersonProfile] = [:]
    @State private var folderFilmography: [String: [TMDBFilmographyItem]] = [:]
    @State private var folderFilmographyLoading: Set<String> = []
    /// 用户正在滚动海报墙。滚动期间卡片少干活：不接 hover 抖动，玻璃垫降成纯染色底。
    @State private var isWallScrolling = false
    
    // 🌟 彻底删除了所有与 network 相关的 State
    
    let columns = [GridItem(.adaptive(minimum: 160), spacing: 20)]
    init() {
        let isFast = UserDefaults.standard.bool(forKey: "enableFastTooltip")
        UserDefaults.standard.set(isFast ? 50 : 1000, forKey: "NSInitialToolTipDelay")
    }
    
    var displayedMovies: [Movie] { var result = movies; if !searchText.isEmpty { result = result.filter { $0.title.localizedCaseInsensitiveContains(searchText) } }; result.sort { m1, m2 in let isLess: Bool; switch selectedSortOption { case .name: isLess = m1.title.localizedStandardCompare(m2.title) == .orderedAscending; case .year: isLess = (m1.releaseDate ?? "") < (m2.releaseDate ?? ""); case .rating: isLess = (m1.voteAverage ?? 0.0) < (m2.voteAverage ?? 0.0) }; return isAscending ? isLess : !isLess }; return result }
    private var discoveredNetworkDevices: [DiscoveredDevice] {
        lanScanner.discoveredDevices.filter { $0.type.isWebDAV || $0.type.isMediaServer || $0.type == .smb }
    }

    // MARK: - 首页分类文件夹（导演/主演）

    private var activeCategoryMode: HomeCategoryMode {
        HomeCategoryMode(rawValue: homeCategoryMode) ?? .none
    }

    @ViewBuilder
    private func homeMovieCard(movie: Movie) -> some View {
        if isHomeCacheModeActive {
            MovieCardView(movie: movie, isHomeCacheModeActive: isHomeCacheModeActive)
        } else {
            NavigationLink(destination: MovieDetailView(movie: movie)) {
                MovieCardView(movie: movie)
            }.buttonStyle(.plain)
        }
    }

    private var categoryFolderGroups: [CategoryFolderGroup] {
        let mode = activeCategoryMode
        guard mode != .none, let kind = mode.personKind else { return [] }
        var buckets: [String: [Movie]] = [:]
        var order: [String] = []
        var uncategorized: [Movie] = []
        for movie in displayedMovies {
            let names = (mode == .director ? movie.directorList : movie.castList)
            if names.isEmpty {
                uncategorized.append(movie)
                continue
            }
            for name in names {
                let id = CategoryFolder.folderID(kind: kind, name: name)
                if buckets[id] == nil {
                    buckets[id] = []
                    order.append(id)
                }
                buckets[id]?.append(movie)
            }
        }
        let groups = order.compactMap { id -> CategoryFolderGroup? in
            guard let movies = buckets[id], !movies.isEmpty else { return nil }
            let name = String(id.dropFirst(kind.count + 1))
            guard !name.isEmpty else { return nil }
            return CategoryFolderGroup(
                folder: CategoryFolder(id: id, kind: kind, name: name, posterPath: nil, updatedAt: nil),
                movies: movies
            )
        }
        .sorted { lhs, rhs in
            // 没有中文译名的外文分类永远排在中文名后面，组内升降序由首页箭头按钮切换
            let lhsForeign = !TMDBService.containsChinese(lhs.folder.name)
            let rhsForeign = !TMDBService.containsChinese(rhs.folder.name)
            if lhsForeign != rhsForeign { return !lhsForeign }
            let cmp = lhs.folder.name.localizedStandardCompare(rhs.folder.name)
            if cmp != .orderedSame {
                return categoryFolderSortAsc ? cmp == .orderedAscending : cmp == .orderedDescending
            }
            return lhs.movies.count > rhs.movies.count
        }
        var result = groups
        if !uncategorized.isEmpty {
            result.append(CategoryFolderGroup(
                folder: CategoryFolder(id: CategoryFolder.folderID(kind: kind, name: "未标注"), kind: kind, name: "未标注", posterPath: nil, updatedAt: nil),
                movies: uncategorized
            ))
        }
        return result
    }

    // 封面优先级：手动指定 > 随机影视海报（人物照片只用于详情页）
    private func groupPosterPath(for group: CategoryFolderGroup) -> String? {
        if let custom = categoryFolderRecords[group.id]?.posterPath, !custom.isEmpty {
            return custom
        }
        if let random = categoryFolderRandomPosters[group.id], let random, !random.isEmpty {
            return random
        }
        return group.movies.compactMap { $0.posterPath }.first { !$0.isEmpty }
    }

    private func personPhotoPath(for group: CategoryFolderGroup) -> String? {
        guard let photo = personProfiles[group.id]?.photoPath, !photo.isEmpty else { return nil }
        return photo
    }

    private func hasCustomFolderPoster(for group: CategoryFolderGroup) -> Bool {
        (categoryFolderRecords[group.id]?.posterPath ?? "").isEmpty == false
    }

    private func refreshCategoryFolderState() {
        refreshCategoryRandomPosters()
        PersonProfileStore.backfillMissingFilmographies()
        ChineseNameStore.backfillExistingChineseNames()
        Task {
            let folders = try? await AppDatabase.shared.dbQueue.read { db in
                try CategoryFolder.fetchAll(db)
            }
            let persons = try? await AppDatabase.shared.dbQueue.read { db in
                try PersonProfile.fetchAll(db)
            }
            await MainActor.run {
                self.categoryFolderRecords = Dictionary(uniqueKeysWithValues: (folders ?? []).map { ($0.id, $0) })
                self.personProfiles = Dictionary(uniqueKeysWithValues: (persons ?? []).map { ($0.id, $0) })
                // 作品名可能被后台补齐成了中文，用库内数据刷新内存快照，避免详情页继续显示旧片名
                for profile in self.personProfiles.values where !profile.filmography.isEmpty {
                    self.folderFilmography[profile.id] = profile.filmography
                }
            }
        }
    }

    private func refreshCategoryRandomPosters() {
        var result: [String: String?] = [:]
        for group in categoryFolderGroups {
            let candidates = group.movies.compactMap { $0.posterPath }.filter { !$0.isEmpty }
            result[group.id] = candidates.randomElement()
        }
        categoryFolderRandomPosters = result
    }

    private func saveFolderPoster(group: CategoryFolderGroup, posterPath: String?) {
        let current = categoryFolderRecords[group.id]?.posterPath
        if current == posterPath { return }
        if let posterPath {
            PosterManager.shared.downloadPoster(posterPath: posterPath)
        }
        let kind = group.folder.kind
        let name = group.folder.name
        let groupID = group.id
        Task {
            try? await AppDatabase.shared.dbQueue.write { db in
                if let posterPath {
                    if var existing = try CategoryFolder.fetchOne(db, key: groupID) {
                        existing.posterPath = posterPath
                        existing.updatedAt = Date().timeIntervalSince1970
                        try existing.update(db)
                    } else {
                        let record = CategoryFolder(id: groupID, kind: kind, name: name, posterPath: posterPath, updatedAt: Date().timeIntervalSince1970)
                        try record.insert(db)
                    }
                } else {
                    _ = try CategoryFolder.deleteOne(db, key: groupID)
                }
            }
            await MainActor.run {
                if let posterPath {
                    self.categoryFolderRecords[groupID] = CategoryFolder(id: groupID, kind: kind, name: name, posterPath: posterPath, updatedAt: Date().timeIntervalSince1970)
                } else {
                    self.categoryFolderRecords[groupID] = nil
                }
            }
        }
    }

    private func selectCustomFolderPoster(for group: CategoryFolderGroup) {
        let panel = NSOpenPanel()
        panel.message = "为「\(group.folder.name)」文件夹选择自定义海报"
        panel.allowedContentTypes = [.image]
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let newName = "folder_poster_\(UUID().uuidString.prefix(8)).jpg"
        do {
            let destURL = OmniPlayICloudSync.posterDirectory().appendingPathComponent(newName)
            try FileManager.default.copyItem(at: url, to: destURL)
            saveFolderPoster(group: group, posterPath: "/" + newName)
        } catch {
            print("文件夹海报拷贝失败: \(error)")
        }
    }

    private var categoryMenuButton: some View {
        Menu {
            Picker("首页分类", selection: Binding(
                get: { activeCategoryMode },
                set: { newMode in
                    withAnimation(.easeInOut(duration: 0.2)) { homeCategoryMode = newMode.rawValue }
                    // 切进导演/主演分类时催一轮外文人名中文译名补齐，
                    // 补齐完成后通过 libraryUpdated 刷新分类标题
                    if newMode.personKind != nil {
                        refreshCategoryFolderState()
                    }
                }
            )) {
                ForEach(HomeCategoryMode.allCases) { mode in
                    Text(mode.displayName).tag(mode)
                }
            }
            .pickerStyle(.inline)
        } label: {
            HStack(spacing: 5) {
                Image(systemName: activeCategoryMode == .none ? "square.grid.2x2" : "person.2.fill")
                    .font(.system(size: 13, weight: .semibold))
                // 当前分类只在弹窗里显示，按钮上只留图标与上下箭头
                Image(systemName: "chevron.up.chevron.down")
                    .font(.system(size: 10, weight: .bold))
            }
            .foregroundColor(activeCategoryMode == .none ? Color.secondary : theme.accent)
            .padding(.horizontal, 10)
            .padding(.vertical, 5)
            .themeVibrant(theme, capsule: true, tint: activeCategoryMode == .none ? theme.chipFill : theme.accent.opacity(0.12))
            .overlay(Capsule().stroke(activeCategoryMode == .none ? Color.secondary.opacity(0.25) : theme.accent.opacity(0.5), lineWidth: 1))
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .conditionalHelp("切换首页分类方式（导演/主演）", show: enableFastTooltip)
    }

    @ViewBuilder
    private var categoryFolderList: some View {
        if categoryFolderGroups.isEmpty {
            VStack(spacing: 16) {
                Image(systemName: "folder")
                    .font(.system(size: 50))
                    .foregroundColor(.secondary)
                Text("该分类下暂无影视，可先为影视补充导演/主演信息")
                    .font(.subheadline)
                    .foregroundColor(.secondary)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 80)
        } else {
            LazyVGrid(columns: columns, spacing: 30) {
                ForEach(categoryFolderGroups) { group in
                    categoryFolderCard(for: group)
                }
            }
        }
    }

    @ViewBuilder
    private func categoryFolderCard(for group: CategoryFolderGroup) -> some View {
        CategoryFolderCard(
            group: group,
            posterPath: groupPosterPath(for: group),
            theme: theme,
            enableFastTooltip: enableFastTooltip,
            onEdit: { categoryPosterPickerGroup = group },
            destination: { categoryFolderDetailView(for: group) }
        )
    }

    // 简介单行：年份标签（同年仅第一部显示）+ 作品名与评分
    private struct FolderBioRow {
        let yearLabel: String
        let text: String
        let isLocal: Bool
    }

    // 简介数据：本地影视 + TMDB 该人物执导/参演的全部作品（含评分），按上映时间正序
    private func folderBio(for group: CategoryFolderGroup) -> (header: String, rows: [FolderBioRow]) {
        if group.folder.name == "未标注" {
            return ("这些 \(group.movies.count) 部影视尚未补充导演/主演信息，补充后将自动归入对应文件夹。", [])
        }
        let verb = group.folder.kind == "director" ? "执导" : "参演"
        struct Entry { let title: String; let rating: Double?; let isLocal: Bool; let year: Int?; let date: String? }
        var entries: [Entry] = []
        let remote = folderFilmography[group.id] ?? []
        var remoteByID: [Int: TMDBFilmographyItem] = [:]
        for item in remote {
            if let rid = item.tmdbId { remoteByID[rid] = item }
        }
        var representedRemoteIds = Set<Int>()
        for movie in group.movies {
            let localYear = Int((movie.releaseDate ?? "").prefix(4))
            if let rid = movie.id, let r = remoteByID[Int(rid)] {
                // 同一作品以 TMDB 中文标题展示
                representedRemoteIds.insert(Int(rid))
                let zh = TMDBService.shared.simplifyChineseIfNeeded(r.title) ?? r.title
                let title = zh.isEmpty ? movie.title : zh
                entries.append(Entry(title: title, rating: movie.voteAverage ?? r.rating, isLocal: true, year: r.year ?? localYear, date: r.date ?? movie.releaseDate))
            } else {
                let title = TMDBService.shared.simplifyChineseIfNeeded(movie.title) ?? movie.title
                entries.append(Entry(title: title, rating: movie.voteAverage, isLocal: true, year: localYear, date: movie.releaseDate))
            }
        }
        for item in remote {
            if let rid = item.tmdbId, representedRemoteIds.contains(rid) { continue }
            let title = TMDBService.shared.simplifyChineseIfNeeded(item.title) ?? item.title
            entries.append(Entry(title: title, rating: item.rating, isLocal: false, year: item.year, date: item.date))
        }
        let header = entries.isEmpty
            ? "\(group.folder.name)暂无作品记录。"
            : "\(group.folder.name)共\(verb)过 \(entries.count) 部影视"
        guard !entries.isEmpty else { return (header, []) }
        // 按上映时间正序（最早的在前），缺日期的排最后
        let sorted = entries.sorted { lhs, rhs in
            let ld = lhs.date ?? "9999-12-31"
            let rd = rhs.date ?? "9999-12-31"
            if ld != rd { return ld < rd }
            return (lhs.year ?? 9999) < (rhs.year ?? 9999)
        }
        var rows: [FolderBioRow] = []
        var lastLabelledYear: Int?
        var labelledNoYear = false
        for entry in sorted {
            let rating = entry.rating.map { String(format: "%.1f", $0) } ?? "暂无评分"
            let yearLabel: String
            if let year = entry.year {
                if lastLabelledYear != year {
                    yearLabel = "\(year)："
                    lastLabelledYear = year
                    labelledNoYear = false
                } else {
                    yearLabel = ""
                }
            } else if !labelledNoYear {
                yearLabel = "其他："
                labelledNoYear = true
            } else {
                yearLabel = ""
            }
            rows.append(FolderBioRow(
                yearLabel: yearLabel,
                text: "《\(entry.title)》\(rating)",
                isLocal: entry.isLocal
            ))
        }
        return (header, rows)
    }

    // 简介视图：三列展示，每行一部作品与评分
    @ViewBuilder
    private func folderBioView(for group: CategoryFolderGroup) -> some View {
        let bio = folderBio(for: group)
        VStack(alignment: .leading, spacing: 6) {
            Text(bio.header)
                .foregroundColor(theme.textSecondary)
            if !bio.rows.isEmpty {
                let chunk = max(1, (bio.rows.count + 2) / 3)
                HStack(alignment: .top, spacing: 20) {
                    ForEach(0..<3, id: \.self) { column in
                        VStack(alignment: .leading, spacing: 4) {
                            let start = column * chunk
                            let end = min(start + chunk, bio.rows.count)
                            if start < end {
                                ForEach(0..<(end - start), id: \.self) { rowIndex in
                                    let row = bio.rows[start + rowIndex]
                                    HStack(alignment: .top, spacing: 2) {
                                        Text(row.yearLabel)
                                            .foregroundColor(theme.textSecondary)
                                            .frame(width: 42, alignment: .leading)
                                        Text(row.text)
                                            .fontWeight(row.isLocal ? .bold : .regular)
                                            .foregroundColor(row.isLocal ? theme.accent : theme.textSecondary)
                                            .fixedSize(horizontal: false, vertical: true)
                                    }
                                }
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                }
            }
            if folderFilmographyLoading.contains(group.id) {
                Text("（正在获取完整作品列表…）")
                    .foregroundColor(theme.textSecondary)
            }
        }
        .font(.subheadline)
        .fixedSize(horizontal: false, vertical: true)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    // 打开文件夹详情时按需拉取人物完整作品列表并持久化缓存
    private func loadFilmographyIfNeeded(for group: CategoryFolderGroup) async {
        guard let person = personProfiles[group.id] else { return }
        let cached = folderFilmography[group.id] ?? person.filmography
        if !cached.isEmpty {
            if folderFilmography[group.id] == nil { folderFilmography[group.id] = cached }
            return
        }
        guard let personId = person.tmdbPersonId else { return }
        guard folderFilmographyLoading.contains(group.id) == false else { return }
        folderFilmographyLoading.insert(group.id)
        defer { folderFilmographyLoading.remove(group.id) }
        guard TMDBService.shared.currentCredentialSource().isAvailable,
              let items = await ChineseNameStore.filmography(personId: Int(personId), kind: group.folder.kind) else {
            return
        }
        let profileID = group.id
        if let data = try? JSONEncoder().encode(items), let json = String(data: data, encoding: .utf8) {
            try? await AppDatabase.shared.dbQueue.write { db in
                if var p = try PersonProfile.fetchOne(db, key: profileID) {
                    p.filmographyJson = json
                    p.updatedAt = Date().timeIntervalSince1970
                    try p.update(db)
                }
            }
        }
        folderFilmography[group.id] = items
    }

    @ViewBuilder
    private func categoryFolderDetailView(for group: CategoryFolderGroup) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                HStack(alignment: .top, spacing: 20) {
                    CachedPosterView(posterPath: personPhotoPath(for: group) ?? groupPosterPath(for: group))
                        .frame(width: 150, height: 225)
                        .cornerRadius(12)
                        .shadow(color: .black.opacity(0.1), radius: 8, y: 4)
                        .conditionalHelp(personPhotoPath(for: group) != nil ? "人物照片" : "文件夹封面", show: enableFastTooltip)
                    VStack(alignment: .leading, spacing: 10) {
                        Text(group.folder.name)
                            .font(.title2)
                            .fontWeight(.bold)
                            .foregroundColor(theme.textPrimary)
                            .lineLimit(1)
                        // 简介（含全部作品及评分）放在名称下方，三列、按上映时间正序
                        folderBioView(for: group)
                    }
                    Spacer(minLength: 0)
                }
                Divider().background(theme.textSecondary.opacity(0.3))
                LazyVGrid(columns: columns, spacing: 30) {
                    ForEach(group.movies, id: \.id) { movie in
                        homeMovieCard(movie: movie)
                    }
                }
            }
            .padding(25)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(theme.backdropLayer.ignoresSafeArea())
        .modifier(RemoveNavTitleModifier())
        .task(id: group.id) {
            await loadFilmographyIfNeeded(for: group)
        }
    }

    private func categoryPosterPickerSheet(for group: CategoryFolderGroup) -> some View {
        VStack(spacing: 0) {
            HStack {
                Text("编辑「\(group.folder.name)」")
                    .font(.title3.bold())
                Spacer()
                Button {
                    categoryPosterPickerGroup = nil
                } label: {
                    Image(systemName: "xmark.circle.fill").font(.title2).foregroundColor(.secondary)
                }
                .buttonStyle(.plain)
            }
            .padding()
            Divider()
            if group.folder.name != "未标注" {
                HStack(spacing: 10) {
                    Text("\(group.folder.kindLabel)名称")
                        .font(.subheadline)
                        .foregroundColor(theme.textSecondary)
                    TextField("输入\(group.folder.kindLabel)名称", text: $categoryFolderNameDraft)
                        .textFieldStyle(.roundedBorder)
                        .frame(maxWidth: 300)
                        .onSubmit { commitCategoryFolderRename(for: group) }
                    Button("保存名称") { commitCategoryFolderRename(for: group) }
                        .disabled(categoryFolderNameDraft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                                  || categoryFolderNameDraft == group.folder.name)
                        .conditionalHelp("手动改成中文译名，会同步更新影片名单、封面与人物资料", show: enableFastTooltip)
                    Spacer()
                }
                .padding(.horizontal, 20)
                .padding(.vertical, 12)
                Divider()
            }
            if group.movies.allSatisfy({ ($0.posterPath ?? "").isEmpty }) {
                VStack(spacing: 10) {
                    Image(systemName: "photo.badge.exclamationmark")
                        .font(.system(size: 34))
                        .foregroundColor(.secondary)
                    Text("该文件夹下的影视暂无海报，可先使用下方「选择本地图片...」设置自定义海报。")
                        .font(.subheadline)
                        .foregroundColor(theme.textSecondary)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView {
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: 120), spacing: 16)], spacing: 20) {
                        ForEach(group.movies.filter { ($0.posterPath ?? "").isEmpty == false }, id: \.id) { movie in
                            Button(action: {
                                saveFolderPoster(group: group, posterPath: movie.posterPath)
                                categoryPosterPickerGroup = nil
                            }) {
                                VStack(alignment: .leading, spacing: 6) {
                                    CachedPosterView(posterPath: movie.posterPath)
                                        .frame(width: 120, height: 180)
                                        .cornerRadius(8)
                                        .overlay(
                                            RoundedRectangle(cornerRadius: 8)
                                                .stroke(groupPosterPath(for: group) == movie.posterPath ? theme.accent : .clear, lineWidth: 2)
                                        )
                                    Text(movie.title)
                                        .font(.caption)
                                        .lineLimit(1)
                                        .foregroundColor(theme.textPrimary)
                                }
                            }
                            .buttonStyle(.plain)
                        }
                    }
                    .padding(20)
                }
            }
            Divider()
            HStack {
                Button(action: {
                    categoryPosterPickerGroup = nil
                    selectCustomFolderPoster(for: group)
                }) {
                    Label("选择本地图片...", systemImage: "photo.badge.plus")
                        .font(.subheadline)
                }
                .buttonStyle(.plain)
                .foregroundColor(theme.textSecondary)
                .conditionalHelp("使用本地图片作为该文件夹封面", show: enableFastTooltip)
                Spacer()
                Button(action: {
                    saveFolderPoster(group: group, posterPath: nil)
                    refreshCategoryRandomPosters()
                    categoryPosterPickerGroup = nil
                }) {
                    Label("恢复默认封面", systemImage: "arrow.counterclockwise")
                        .font(.subheadline)
                }
                .buttonStyle(.plain)
                .foregroundColor(theme.textSecondary)
                .conditionalHelp("封面改回默认（人物照片，无照片时为随机影视海报）", show: enableFastTooltip)
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 14)
        }
        .frame(width: 640, height: 560)
        .task(id: group.id) {
            categoryFolderNameDraft = group.folder.name
        }
    }

    // 弹窗里手动改导演/主演名：迁移库内数据并记住该名字，避免下次刮削又退回外文名
    private func commitCategoryFolderRename(for group: CategoryFolderGroup) {
        let newName = categoryFolderNameDraft.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !newName.isEmpty, newName != group.folder.name else { return }
        let folder = group.folder
        Task {
            await ChineseNameStore.setUserPersonName(kind: folder.kind, currentName: folder.name, newName: newName)
            await MainActor.run {
                categoryPosterPickerGroup = nil
                NotificationCenter.default.post(name: .libraryUpdated, object: nil)
            }
        }
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
                theme.backdropLayer.ignoresSafeArea()
                
                ScrollView {
                    if movies.isEmpty && continueWatchingMovies.isEmpty {
                        VStack(spacing: 20) {
                            Image(systemName: "film").font(.system(size: 60)).foregroundColor(.secondary)
                            Text("媒体库空空如也，快去添加文件夹吧！").font(.title2).foregroundColor(.secondary)
                        }.padding(.top, 150).frame(maxWidth: .infinity)
                    } else {
                        VStack(alignment: .leading, spacing: 24) {
                            if !continueWatchingMovies.isEmpty {
                                // 玻璃主题下给标题垫一条玻璃胶囊，文字左缘仍对齐海报
                                Text("继续播放")
                                    .font(.title2).fontWeight(.bold)
                                    .foregroundColor(theme.textPrimary)
                                    .themeGlassPill(theme)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                    .padding(.leading, 25)
                                    .padding(.top, 8)
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
                                // 标题与筛选控件整体做成一颗玻璃胶囊；非玻璃主题不加任何内边距，观感原样
                                HStack(spacing: 12) {
                                Text("所有影视")
                                    .font(.title2).fontWeight(.bold)
                                    .foregroundColor(theme.textPrimary)
                                categoryMenuButton
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
                                Button(action: {
                                    withAnimation {
                                        if activeCategoryMode == .none { isAscending.toggle() } else { categoryFolderSortAsc.toggle() }
                                    }
                                }) {
                                    Image(systemName: (activeCategoryMode == .none ? isAscending : categoryFolderSortAsc) ? "arrow.up" : "arrow.down")
                                        .font(.system(size: 14, weight: .bold)).foregroundColor(.secondary)
                                }.buttonStyle(.plain)
                                .conditionalHelp(activeCategoryMode == .none ? "切换排序方向" : "分类文件夹按名称排序，点击切换升/降序", show: enableFastTooltip)
                                }
                                .themeGlassPill(theme)
                                Spacer()
                            }.padding(.horizontal, 25).padding(.top, continueWatchingMovies.isEmpty ? 6 : 4)
                            
                            Group {
                                if activeCategoryMode == .none {
                                    LazyVGrid(columns: columns, spacing: 30) {
                                        ForEach(displayedMovies, id: \.id) { movie in
                                            homeMovieCard(movie: movie)
                                        }
                                    }
                                } else {
                                    categoryFolderList
                                }
                            }
                            .padding(.horizontal, 25)
                            .padding(.bottom, 25)
                            .animation(.easeInOut, value: displayedMovies.count)
                        }
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .environment(\.isPosterWallScrolling, isWallScrolling)
                .wallScrollPhaseGating(isScrolling: $isWallScrolling)
                .padding(.top, topBarHeight)
                
                if let cacheMessage = cacheManager.cacheStatusMessage {
                    Text(cacheMessage)
                        .font(.subheadline.weight(.semibold))
                        .foregroundColor(theme.textPrimary)
                        .padding(.horizontal, 16)
                        .padding(.vertical, 10)
                        .themeVibrant(theme, capsule: true)
                        .shadow(radius: 8)
                        .padding(.top, 18)
                        .padding(.trailing, 18)
                        .transition(.move(edge: .top).combined(with: .opacity))
                }

                HStack(alignment: .center, spacing: 16) {
                    if cacheManager.isCaching {
                        HStack(spacing: 8) {
                            ProgressView(value: cacheManager.overallDownloadProgress)
                                .progressViewStyle(.circular)
                                .controlSize(.small)
                                .tint(theme.accent)
                            VStack(alignment: .leading, spacing: 1) {
                                Text(cacheManager.activeCacheTitle ?? "离线缓存")
                                    .font(.caption.weight(.semibold))
                                    .foregroundColor(topToolbarStatusTextColor)
                                    .lineLimit(1)
                                Text("\(cacheManager.currentCacheFileName ?? "等待缓存") · 第 \(min(cacheManager.completedDownloadCount + 1, cacheManager.totalDownloadCount))/\(cacheManager.totalDownloadCount) 集 · \(Int(cacheManager.overallDownloadProgress * 100))%")
                                    .font(.caption2)
                                    .foregroundColor(topToolbarStatusTextColor.opacity(0.8))
                                    .lineLimit(1)
                            }
                            .frame(maxWidth: 260, alignment: .leading)
                        }
                    }
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

                    // 右上角这一簇按钮整体垫一块圆角玻璃，玻璃只包住按钮本身；非玻璃主题原样
                    HStack(spacing: 16) {
                        Button(action: { isShowingManageSources.toggle() }) {
                            Image(systemName: "folder.badge.plus")
                                .font(.system(size: 21, weight: .medium))
                                .foregroundColor(topToolbarInactiveIconColor)
                                .frame(width: 28, height: 28)
                        }
                        .buttonStyle(.plain)
                        .accessibilityIdentifier("toolbar.addSource")
                        .conditionalHelp("查看已挂载媒体源，并添加本地文件夹、SMB 共享或 WebDAV 媒体源", show: enableFastTooltip)
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
                    .themeTopBarCluster(theme)
                }
                .padding(.horizontal, 24)
                .padding(.top, 10)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity).modifier(RemoveNavTitleModifier())
            .preferredColorScheme(theme.preferredScheme)
            .sheet(isPresented: $showSettings) {
                SettingsView(focusTMDBApi: settingsFocusTMDBApi)
                    .onDisappear { settingsFocusTMDBApi = false }
            }
            .sheet(isPresented: $isShowingRenameSourceSheet) {
                renameSourceSheet()
            }
            .sheet(isPresented: $isShowingSourceInfoSheet) {
                if let source = sourceInfoTarget {
                    sourceInfoSheet(source)
                }
            }
            .sheet(isPresented: $isShowingManualRemoteSourceSheet, onDismiss: showPendingSMBBrowser) {
                manualRemoteSourceSheet()
            }
            .sheet(isPresented: $isShowingSMBBrowserSheet, onDismiss: finalizeSMBBrowserIfNeeded) {
                smbBrowserSheet()
            }
            .sheet(isPresented: $isShowingWebDAVPreScanLoginSheet, onDismiss: showPendingWebDAVBrowser) {
                webDAVPreScanLoginSheet()
            }
            .sheet(isPresented: $isShowingWebDAVFolderBrowserSheet) {
                webDAVFolderBrowserSheet()
            }
            .sheet(item: $categoryPosterPickerGroup) { group in
                categoryPosterPickerSheet(for: group)
            }
            .sheet(isPresented: $isShowingMediaServerSheet, onDismiss: showPendingSMBBrowser) {
                mediaServerSourceSheet()
            }
            .sheet(isPresented: $isShowingLuckyStunSourceSheet, onDismiss: startPendingLuckyWebDAV) {
                luckyStunSourceSheet()
            }
            .sheet(isPresented: $isShowingRemoveSourceSheet) {
                removeSourceSheet()
            }
            .onDrop(of: [UTType.fileURL], isTargeted: nil, perform: handleFileDrop)
            .alert("无法打开视频文件", isPresented: Binding(
                get: { directPlayback.lastOpenError != nil },
                set: { if !$0 { directPlayback.clearOpenError() } })) {
                Button("好", role: .cancel) { }
            } message: {
                Text(directPlayback.lastOpenError?.message ?? "")
            }
            .alert(Text(scanErrorAlertTitle), isPresented: $isShowingScanErrorAlert) {
                Button("好", role: .cancel) { }
            } message: {
                Text(scanErrorMessage)
            }
            .alert("TMDB API 无法连接", isPresented: $isShowingTMDBConnectionAlert) {
                Button("本次不刮削", role: .cancel) {
                    // 不刮元数据，但缺失的分集剧照仍然要补抓。
                    Task { await enqueueEpisodeStillBackfill(runID: UUID(), movieIDs: []) }
                }
                Button("重试扫描") {
                    isShowingTMDBConnectionAlert = false
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) { triggerScanAndScrape() }
                }
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
            // 媒体库结构没升级完整时得当场说清楚，否则用户只会看到「扫完了，但什么都没有」。
            if let databaseError = AppDatabase.shared.setupError {
                scanErrorAlertTitle = "媒体库没能升级完成"
                scanErrorMessage = databaseError
                isShowingScanErrorAlert = true
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: .libraryUpdated)) { notification in
            if notification.userInfo?[LibraryUpdateUserInfoKey.metadataScrapeCompleted] as? Bool == true {
                pendingLibraryReloadTask?.cancel()
                loadData()
            } else {
                scheduleDebouncedLibraryReload()
            }
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
        .onChange(of: homeCategoryMode) { _, _ in
            refreshCategoryRandomPosters()
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
    
    // MARK: - SMB 网络共享源

    /// SMB 表单就长在「局域网媒体源」弹窗里，这里只负责预选类型与地址
    private func openSMBSourceSheet(host: String = "") {
        isShowingManageSources = false
        closeRemoteSourceSheets()
        resetSMBBrowserState()
        smbHost = host
        remoteSourceKind = .smb
        // 刚关掉的浮层要等一轮 runloop 才能让位给新弹窗，和 WebDAV 那条路一致
        DispatchQueue.main.async {
            isShowingManualRemoteSourceSheet = true
        }
    }

    private func resetSMBBrowserState() {
        smbHost = ""
        smbUser = ""
        smbPassword = ""
        smbSourceName = ""
        smbMessage = ""
        smbMessageIsError = false
        smbIsWorking = false
        smbIsLoadingBrowser = false
        smbIsBatchMounting = false
        smbConnection = nil
        smbShareNames = []
        smbBrowserShare = ""
        smbBrowserMountPath = ""
        smbBrowserRelative = ""
        smbBrowserStack = []
        smbBrowserEntries = []
        smbStarredTargets = []
        smbMountedKeys = []
        smbTempMountedShares = []
        smbTempMountPaths = [:]
    }

    private var smbStarredKeys: Set<String> { Set(smbStarredTargets.map(\.id)) }

    private var smbCanGoBack: Bool { !smbBrowserShare.isEmpty }

    private var smbBrowserLocationLabel: String {
        guard let connection = smbConnection else { return "smb://\(smbHost)" }
        guard !smbBrowserShare.isEmpty else { return "smb://\(connection.host)" }
        let suffix = smbBrowserRelative.isEmpty ? "" : "/" + smbBrowserRelative
        return "smb://\(connection.host)/\(smbBrowserShare)\(suffix)"
    }

    /// 当前浏览的这一层能不能整体标星（共享根或共享里的某个文件夹）
    private var smbCurrentFolderTarget: SMBStarredTarget? {
        guard !smbBrowserShare.isEmpty else { return nil }
        let name = smbBrowserRelative.isEmpty
            ? smbBrowserShare
            : String(smbBrowserRelative.split(separator: "/").last ?? "")
        return SMBStarredTarget(share: smbBrowserShare, relativePath: smbBrowserRelative, name: name)
    }

    private var smbLoginFormFields: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("填好地址后会列出服务器上的共享；可给多个共享或共享里的文件夹点星标，关闭列表时统一挂载并扫描刮削。")
                .font(.caption)
                .foregroundColor(theme.textSecondary)
                .fixedSize(horizontal: false, vertical: true)
            TextField("显示名称（可选，留空用共享名）", text: $smbSourceName)
                .textFieldStyle(.roundedBorder)
            TextField("服务器地址，例如 smb://192.168.1.20 或 192.168.1.20/影视", text: $smbHost)
                .textFieldStyle(.roundedBorder)
            HStack(spacing: 8) {
                TextField("用户名（可选）", text: $smbUser)
                    .textFieldStyle(.roundedBorder)
                SecureField("密码（可选）", text: $smbPassword)
                    .textFieldStyle(.roundedBorder)
            }
            if !smbMessage.isEmpty {
                Text(smbMessage)
                    .font(.caption)
                    .foregroundColor(smbMessageIsError ? theme.dangerColor : theme.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private func smbBrowserSheet() -> some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 10) {
                Text(smbBrowserShare.isEmpty ? "选择 SMB 共享" : "选择 SMB 共享文件夹")
                    .font(.title3.bold())
                    .foregroundColor(theme.textPrimary)
                Spacer()
                if smbIsLoadingBrowser || smbIsBatchMounting {
                    ProgressView().controlSize(.small)
                }
                Button {
                    closeSMBBrowser()
                } label: {
                    Image(systemName: "xmark.circle.fill")
                        .font(.title3)
                        .foregroundColor(theme.textSecondary)
                }
                .buttonStyle(.plain)
                .disabled(smbIsLoadingBrowser || smbIsBatchMounting)
                .help(smbStarredTargets.isEmpty ? "关闭" : "关闭并挂载标星的共享")
            }

            HStack(spacing: 8) {
                VStack(alignment: .leading, spacing: 4) {
                    Text(smbSourceName.isEmpty ? (smbConnection?.host ?? smbHost) : smbSourceName)
                        .font(.subheadline.weight(.semibold))
                        .foregroundColor(theme.textPrimary)
                    Text(verbatim: smbBrowserLocationLabel)
                        .font(.caption)
                        .foregroundColor(theme.textSecondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
                if let target = smbCurrentFolderTarget {
                    Button {
                        toggleSMBStar(target)
                    } label: {
                        Image(systemName: smbStarredKeys.contains(target.id) ? "star.fill" : "star")
                    }
                    .buttonStyle(.bordered)
                    .help(smbStarredKeys.contains(target.id) ? "取消标星当前文件夹" : "标星当前文件夹")
                }
                Spacer()
            }

            HStack {
                Button {
                    navigateSMBBrowserBack()
                } label: {
                    Label("返回上级", systemImage: "chevron.left")
                }
                .disabled(!smbCanGoBack || smbIsLoadingBrowser)
                Spacer()
                Button {
                    reloadSMBBrowserLevel()
                } label: {
                    Label("刷新", systemImage: "arrow.clockwise")
                }
                .disabled(smbIsLoadingBrowser)
            }

            if !smbStarredTargets.isEmpty {
                Text("已标星 \(smbStarredTargets.count) 个共享/文件夹，关闭列表后会加入挂载媒体源。")
                    .font(.caption)
                    .foregroundColor(theme.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if !smbMessage.isEmpty {
                Text(smbMessage)
                    .font(.caption)
                    .foregroundColor(smbMessageIsError ? theme.dangerColor : theme.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            ScrollView {
                LazyVStack(alignment: .leading, spacing: 8) {
                    ForEach(smbBrowserEntries) { entry in
                        smbBrowserRow(entry)
                    }
                }
            }
            .frame(minHeight: 220, maxHeight: 360)
            .overlay {
                if smbBrowserEntries.isEmpty && !smbIsLoadingBrowser {
                    Text(smbBrowserShare.isEmpty ? "这台服务器没有可挂载的共享。" : "这个文件夹里没有子文件夹，点右上角星标收藏当前文件夹。")
                        .font(.caption)
                        .foregroundColor(theme.textSecondary)
                }
            }

            HStack {
                Spacer()
                Button(smbStarredTargets.isEmpty ? "关闭" : "关闭并挂载 \(smbStarredTargets.count) 个共享") {
                    closeSMBBrowser()
                }
                .disabled(smbIsLoadingBrowser || smbIsBatchMounting)
            }
        }
        .padding(20)
        .frame(width: 560)
        .themeSheetChrome(theme)
    }

    private func smbBrowserRow(_ entry: SMBBrowserEntry) -> some View {
        let isMounted = smbMountedKeys.contains(entry.key)
        let isStarred = smbStarredKeys.contains(entry.key)
        let isBusy = smbIsLoadingBrowser || smbIsBatchMounting

        return HStack(spacing: 10) {
            Button {
                enterSMBEntry(entry)
            } label: {
                HStack(spacing: 8) {
                    Image(systemName: smbBrowserShare.isEmpty
                        ? "externaldrive.connected.to.line.below"
                        : "folder")
                        .foregroundColor(theme.accent)
                    Text(entry.name)
                        .foregroundColor(theme.textPrimary)
                        .lineLimit(1)
                    if isMounted {
                        Text("已挂载")
                            .font(.caption2)
                            .foregroundColor(theme.successColor)
                    }
                    Spacer()
                    Image(systemName: "chevron.right")
                        .font(.caption)
                        .foregroundColor(theme.textSecondary)
                }
            }
            .buttonStyle(.plain)
            .disabled(isBusy)

            Button {
                toggleSMBStar(entry)
            } label: {
                Image(systemName: (isMounted || isStarred) ? "star.fill" : "star")
                    .foregroundColor((isMounted || isStarred) ? theme.accent : theme.textSecondary)
            }
            .buttonStyle(.plain)
            .help(isMounted ? "已挂载" : (isStarred ? "取消标星" : "标星此项"))
            .disabled(isMounted || isBusy)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
        .themeVibrant(theme, cornerRadius: 10)
    }

    private func connectSMBAndBrowse() {
        guard var location = SMBSourceSupport.parseLocation(smbHost) else {
            smbMessage = "服务器地址无效，请输入 smb://主机 或 主机/共享 这样的地址。"
            smbMessageIsError = true
            return
        }
        // 表单里填的账号优先于地址里带的
        if let user = nilIfBlank(smbUser) { location.username = user }
        if !smbPassword.isEmpty { location.password = smbPassword }
        let targetShare = location.share
        let probe = SMBShareInfo(host: location.host, share: "", username: location.username, password: location.password)

        smbIsWorking = true
        smbMessage = "正在读取共享列表..."
        smbMessageIsError = false

        Task {
            let result = await SMBSourceManager.listShares(probe)
            await MainActor.run {
                smbIsWorking = false
                if case .failure(let error) = result {
                    smbMessage = """
                    连接失败：\(error.localizedDescription)
                    也可以在访达里用「连接服务器」连到 smb://\(probe.host)，觅影会直接复用已经挂载好的目录。
                    """
                    smbMessageIsError = true
                    return
                }
                let shares = (try? result.get()) ?? []
                smbConnection = probe
                smbUser = location.username ?? ""
                smbPassword = location.password ?? ""
                smbShareNames = shares
                smbMountedKeys = smbMountedSourceKeys(from: mediaSources)
                smbBrowserStack = []
                showSMSShareList()
                smbMessage = shares.isEmpty ? "连接成功，但这台服务器没有列出磁盘共享。" : ""
                smbMessageIsError = false
                presentSMBBrowser()
                // 地址里写明了共享：直接进去看它的文件夹
                if !targetShare.isEmpty, shares.contains(targetShare) {
                    loadSMSShareLevel(share: targetShare, relative: "")
                }
            }
        }
    }

    private func presentSMBBrowser() {
        if isShowingManualRemoteSourceSheet || isShowingMediaServerSheet {
            shouldShowSMBBrowserAfterLoginDismiss = true
            dismissRemoteSourceForm()
        } else {
            isShowingSMBBrowserSheet = true
        }
    }

    private func showPendingSMBBrowser() {
        guard shouldShowSMBBrowserAfterLoginDismiss else { return }
        shouldShowSMBBrowserAfterLoginDismiss = false
        isShowingSMBBrowserSheet = true
    }

    private func showSMSShareList() {
        smbBrowserShare = ""
        smbBrowserMountPath = ""
        smbBrowserRelative = ""
        smbBrowserEntries = smbShareNames.map { SMBBrowserEntry(share: $0, relativePath: "", name: $0) }
    }

    private func reloadSMBBrowserLevel() {
        if smbBrowserShare.isEmpty {
            refreshSMSShareList()
        } else {
            loadSMSShareLevel(share: smbBrowserShare, relative: smbBrowserRelative)
        }
    }

    private func refreshSMSShareList() {
        guard let connection = smbConnection else { return }
        let probe = SMBShareInfo(host: connection.host, share: "", username: connection.username, password: connection.password)
        smbIsLoadingBrowser = true
        smbMessage = ""
        smbMessageIsError = false
        Task {
            let result = await SMBSourceManager.listShares(probe)
            await MainActor.run {
                smbIsLoadingBrowser = false
                switch result {
                case .success(let shares):
                    smbShareNames = shares
                    showSMSShareList()
                    smbMessage = shares.isEmpty ? "这台服务器没有列出磁盘共享。" : ""
                    smbMessageIsError = false
                case .failure(let error):
                    smbMessage = "读取共享失败：\(error.localizedDescription)"
                    smbMessageIsError = true
                }
            }
        }
    }

    private func enterSMBEntry(_ entry: SMBBrowserEntry) {
        if smbBrowserShare.isEmpty {
            smbBrowserStack = []
            loadSMSShareLevel(share: entry.share, relative: "")
        } else {
            smbBrowserStack.append(smbBrowserRelative)
            let nextRelative = smbBrowserRelative.isEmpty ? entry.name : "\(smbBrowserRelative)/\(entry.name)"
            loadSMSShareLevel(share: smbBrowserShare, relative: nextRelative)
        }
    }

    private func navigateSMBBrowserBack() {
        guard !smbBrowserShare.isEmpty else { return }
        if let previous = smbBrowserStack.last {
            smbBrowserStack.removeLast()
            loadSMSShareLevel(share: smbBrowserShare, relative: previous)
        } else {
            showSMSShareList()
        }
    }

    /// 挂载共享并列出它下面某一层的子目录；没挂过的共享算「只为浏览而挂」，取消时会顺手卸载
    private func loadSMSShareLevel(share: String, relative: String) {
        guard let connection = smbConnection else { return }
        let info = SMBShareInfo(host: connection.host, share: share, username: connection.username, password: connection.password)
        if !SMBSourceManager.isMounted("/Volumes/" + share) {
            smbTempMountedShares.insert(share)
        }
        smbIsLoadingBrowser = true
        smbMessage = ""
        smbMessageIsError = false

        Task {
            let mountResult = await SMBSourceManager.mount(info)
            guard case .success(let mountPath) = mountResult else {
                let reason = smbFailureReason(mountResult)
                await MainActor.run {
                    smbIsLoadingBrowser = false
                    smbMessage = "挂载共享失败：\(reason)"
                    smbMessageIsError = true
                }
                return
            }
            let directory = smbLocalPath(mountPath: mountPath, relative: relative)
            let names = await Task.detached(priority: .userInitiated) {
                SMBSourceManager.listSubdirectories(at: directory)
            }.value
            await MainActor.run {
                smbBrowserShare = share
                smbBrowserMountPath = mountPath
                smbBrowserRelative = relative
                if smbTempMountedShares.contains(share) {
                    // 记下真实挂载点，卷名撞车时也能卸干净
                    smbTempMountPaths[share] = mountPath
                }
                smbBrowserEntries = names.map {
                    SMBBrowserEntry(share: share, relativePath: relative.isEmpty ? $0 : "\(relative)/\($0)", name: $0)
                }
                smbMountedKeys = smbMountedSourceKeys(from: mediaSources)
                smbIsLoadingBrowser = false
                smbMessage = names.isEmpty ? "这个文件夹里没有子文件夹，可以标星当前文件夹。" : ""
                smbMessageIsError = false
            }
        }
    }

    private nonisolated func smbFailureReason(_ result: Result<String, SMBMountError>) -> String {
        if case .failure(let error) = result { return error.localizedDescription }
        return "未知错误"
    }

    private func toggleSMBStar(_ entry: SMBBrowserEntry) {
        toggleSMBStar(SMBStarredTarget(share: entry.share, relativePath: entry.relativePath, name: entry.name))
    }

    private func toggleSMBStar(_ target: SMBStarredTarget) {
        guard !smbMountedKeys.contains(target.id) else { return }
        if let index = smbStarredTargets.firstIndex(where: { $0.id == target.id }) {
            smbStarredTargets.remove(at: index)
        } else {
            smbStarredTargets.append(target)
        }
    }

    /// 按 Esc 直接关掉共享列表时也别丢掉标星：照样挂载入库
    private func finalizeSMBBrowserIfNeeded() {
        guard !smbStarredTargets.isEmpty, !smbIsBatchMounting else { return }
        mountStarredSMBTargetsAndClose()
    }

    private func closeSMBBrowser() {
        guard !smbIsLoadingBrowser, !smbIsBatchMounting else { return }
        guard !smbStarredTargets.isEmpty else {
            isShowingSMBBrowserSheet = false
            unmountBrowseOnlyShares(smbTempMountPaths, keeping: smbSourcesShares())
            resetSMBBrowserState()
            return
        }
        mountStarredSMBTargetsAndClose()
    }

    private func mountStarredSMBTargetsAndClose() {
        let targets = smbStarredTargets
        guard let connection = smbConnection, !targets.isEmpty else { return }
        // 先清空标星，Esc 关闭触发的 onDismiss 才不会重复挂载
        smbStarredTargets = []
        let customName = nilIfBlank(smbSourceName)
        let tempMounts = smbTempMountPaths
        let keptShares = Set(targets.map(\.share)).union(smbSourcesShares())

        smbIsBatchMounting = true
        smbMessage = ""
        smbMessageIsError = false

        Task {
            var saved: [(target: SMBStarredTarget, mountPath: String, path: String)] = []
            var failures: [String] = []
            for target in targets {
                let info = SMBShareInfo(host: connection.host, share: target.share, username: connection.username, password: connection.password)
                let mountResult = await SMBSourceManager.mount(info)
                switch mountResult {
                case .success(let mountPath):
                    saved.append((target, mountPath, smbLocalPath(mountPath: mountPath, relative: target.relativePath)))
                case .failure(let error):
                    failures.append("\(target.share)：\(error.localizedDescription)")
                }
            }

            let sourceIDs: [Int64]
            do {
                sourceIDs = try await saveSMBSources(saved, connection: connection, customName: customName)
            } catch {
                await MainActor.run {
                    smbIsBatchMounting = false
                    smbMessage = "保存媒体源失败：\(error.localizedDescription)"
                    smbMessageIsError = true
                }
                return
            }

            await MainActor.run {
                smbIsBatchMounting = false
                guard !saved.isEmpty else {
                    smbMessage = "共享挂载失败：" + (failures.isEmpty ? "没有选中可挂载的共享。" : failures.joined(separator: "；"))
                    smbMessageIsError = true
                    return
                }
                for item in saved {
                    smbMountedKeys.insert(item.target.id)
                }
                isShowingSMBBrowserSheet = false
                loadData()
                resetSMBBrowserState()
                unmountBrowseOnlyShares(tempMounts, keeping: keptShares)
                if isProcessing {
                    needsRescanAfterCurrentRun = true
                    processingMessage = "扫描中，标星的 SMB 共享已加入下一轮队列..."
                } else if !sourceIDs.isEmpty {
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) {
                        triggerScanAndScrape(sourceIDs: sourceIDs)
                    }
                }
            }
        }
    }

    /// 标星目标入库：统一按本地目录入库，authConfig 带 SMB 信息供重挂与协议标签使用
    private func saveSMBSources(
        _ saved: [(target: SMBStarredTarget, mountPath: String, path: String)],
        connection: SMBShareInfo,
        customName: String?
    ) async throws -> [Int64] {
        let payloads: [(name: String, path: String, authConfig: String?)] = saved.map { item in
            let info = SMBShareInfo(
                host: connection.host,
                share: item.target.share,
                username: connection.username,
                password: connection.password,
                mountPath: item.mountPath
            )
            let generated = item.target.relativePath.isEmpty
                ? item.target.share
                : "\(item.target.share) · \(item.target.name)"
            let name = (saved.count == 1 ? customName : nil) ?? generated
            return (name.isEmpty ? "SMB 媒体源" : name, item.path, SMBSourceSupport.authConfigJSON(for: info))
        }

        return try await AppDatabase.shared.dbQueue.write { db -> [Int64] in
            var ids: [Int64] = []
            for payload in payloads {
                let addressConfig = addressConfigurationString(
                    protocolKind: .local,
                    localAddress: payload.path,
                    localLabel: "SMB",
                    externalAddresses: []
                )
                let existing = try MediaSource.fetchAll(
                    db,
                    sql: "SELECT * FROM mediaSource WHERE baseUrl = ?",
                    arguments: [payload.path]
                ).first
                if var source = existing {
                    source.name = payload.name
                    source.baseUrl = payload.path
                    source.authConfig = payload.authConfig
                    source.addressConfig = addressConfig
                    source.isEnabled = true
                    source.disabledAt = nil
                    try source.update(db)
                    ids.append(source.id ?? 0)
                } else {
                    var source = MediaSource(
                        id: nil,
                        name: payload.name,
                        protocolType: MediaSourceProtocol.local.rawValue,
                        baseUrl: payload.path,
                        authConfig: payload.authConfig
                    )
                    source.addressConfig = addressConfig
                    try source.insert(db)
                    ids.append(source.id ?? db.lastInsertedRowID)
                }
            }
            return ids
        }
    }

    /// 现有 SMB 源已经占用的 key，用来把「已挂载」标出来
    private nonisolated func smbMountedSourceKeys(from sources: [MediaSource]) -> Set<String> {
        var keys: Set<String> = []
        for source in sources {
            guard let info = SMBSourceSupport.shareInfo(from: source) else { continue }
            let roots = [info.mountPath, "/Volumes/" + info.share].compactMap { $0 }.filter { !$0.isEmpty }
            for shareRoot in roots {
                if source.baseUrl == shareRoot {
                    keys.insert("\(info.share)|")
                } else if source.baseUrl.hasPrefix(shareRoot + "/") {
                    keys.insert("\(info.share)|" + String(source.baseUrl.dropFirst(shareRoot.count + 1)))
                }
            }
        }
        return keys
    }

    /// 现有 SMB 源用到的共享名：这些共享的挂载点不能被浏览收尾时卸掉
    private func smbSourcesShares() -> Set<String> {
        Set(mediaSources.compactMap { SMBSourceSupport.shareInfo(from: $0)?.share })
    }

    private nonisolated func smbLocalPath(mountPath: String, relative: String) -> String {
        relative.isEmpty ? mountPath : mountPath + "/" + relative
    }

    /// 只为浏览而挂载、又没被标星或已有媒体源占用的共享，收尾时卸掉
    private func unmountBrowseOnlyShares(_ mounts: [String: String], keeping keptShares: Set<String>) {
        let doomed = mounts.filter { !keptShares.contains($0.key) }
        guard !doomed.isEmpty else { return }
        Task {
            for (share, mountPath) in doomed {
                _ = await SMBSourceManager.unmount(mountPath.isEmpty ? "/Volumes/" + share : mountPath)
            }
        }
    }

    private func nilIfBlank(_ value: String) -> String? {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }

    private var folderMenuPanel: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(mediaSources.isEmpty ? "暂无源" : "已挂载媒体源：").font(.headline).foregroundColor(theme.textSecondary)
            ForEach(mediaSources, id: \.id) { source in
                HStack(spacing: 10) {
                    Image(systemName: sourceIconName(source)).foregroundColor(theme.accent)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(source.displayNameWithActiveLabel)
                            .font(.body)
                            .fontWeight(.semibold)
                            .foregroundColor(theme.textPrimary)
                            .lineLimit(1)
                        Text("\(sourceProtocolLabel(source)) · \(source.isEnabled ? "已开启" : "已关闭")")
                            .font(.caption2)
                            .foregroundColor(source.isEnabled ? theme.textSecondary : theme.warningColor)
                        if showMediaSourceRealPath {
                            Text(source.displayBaseURL())
                                .font(.caption)
                                .foregroundColor(theme.textSecondary)
                                .lineLimit(1)
                                .truncationMode(.middle)
                        }
                        if let sourceID = source.id, let status = sourceConnectivityStatus[sourceID] {
                            Text(status)
                                .font(.caption2)
                                .foregroundColor(status.hasPrefix("连接成功")
                                    ? theme.successColor
                                    : (status == "检测中..." ? theme.textSecondary : theme.dangerColor))
                                .lineLimit(2)
                        }
                    }
                    .frame(maxWidth: 300, alignment: .leading)
                    Spacer()
                    Button(action: { toggleMediaSourceEnabled(source) }) {
                        Image(systemName: source.isEnabled ? "power" : "power.circle")
                            .foregroundColor(source.isEnabled ? theme.warningColor : theme.successColor)
                    }
                    .buttonStyle(.bordered)
                    .help(source.isEnabled ? "停用这个媒体源" : "启用这个媒体源")
                    Button(action: { checkMediaSourceConnectivity(source) }) {
                        Image(systemName: checkingSourceIDs.contains(source.id ?? -1) ? "hourglass" : "bolt.fill")
                    }
                    .buttonStyle(.bordered)
                    .disabled(source.id == nil || checkingSourceIDs.contains(source.id ?? -1))
                    .help("优先检测局域网地址，失败后依次检测外网地址")
                    Button(action: {
                        sourceInfoTarget = source
                        isShowingSourceInfoSheet = true
                    }) {
                        Image(systemName: "pencil")
                            .foregroundColor(theme.textSecondary)
                    }.buttonStyle(.bordered)
                    Button(action: {
                        sourcePendingRemoval = source
                        isShowingRemoveSourceSheet = true
                    }) {
                        Image(systemName: "minus.circle.fill").foregroundColor(.red.opacity(0.8))
                    }.buttonStyle(.bordered)
                }
            }
            
            Divider()
                .padding(.top, 4)
            
            VStack(alignment: .leading, spacing: 10) {
                Text("新增媒体源").font(.subheadline.weight(.semibold)).foregroundColor(theme.textSecondary)
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

                Button(action: openLuckyStunSourceSheet) {
                    Label("Lucky STUN 穿透媒体源", systemImage: "point.3.connected.trianglepath.dotted")
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .buttonStyle(.plain)
            }

            Divider()
                .padding(.top, 2)

            VStack(alignment: .leading, spacing: 10) {
                HStack {
                    Text("预扫描局域网媒体源")
                        .font(.subheadline.weight(.semibold))
                        .foregroundColor(theme.textSecondary)
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
                            .foregroundColor(theme.textSecondary)
                    }
                }

                if discoveredNetworkDevices.isEmpty {
                    Text("扫描到 SMB 共享、WebDAV、Plex、Emby 或 Jellyfin 后，点击设备继续设置。")
                        .font(.caption)
                        .foregroundColor(theme.textSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                } else {
                    VStack(alignment: .leading, spacing: 8) {
                        ForEach(discoveredNetworkDevices) { device in
                            Button(action: {
                                openDiscoveredDeviceFromManageSheet(device)
                            }) {
                                HStack(alignment: .top, spacing: 10) {
                                    Image(systemName: device.type == .smb
                                        ? "externaldrive.connected.to.line.below"
                                        : (device.type.isMediaServer ? "server.rack" : "globe.asia.australia.fill"))
                                        .foregroundColor(theme.accent)
                                        .frame(width: 22)
                                        .padding(.top, 1)
                                    VStack(alignment: .leading, spacing: 3) {
                                        Text(discoveredDeviceTitle(device))
                                            .foregroundColor(theme.textPrimary)
                                            .lineLimit(1)
                                        Text(verbatim: discoveredDeviceURLString(for: device))
                                            .font(.caption.monospacedDigit())
                                            .foregroundColor(theme.textSecondary)
                                            .lineLimit(1)
                                    }
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                }
                                .padding(.horizontal, 10)
                                .padding(.vertical, 8)
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .themeVibrant(theme, cornerRadius: 10)
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                        }
                    }
                }
            }
        }.padding(18).frame(minWidth: 280, maxWidth: 420)
        .themeSheetChrome(theme)
    }

    private func sourceProtocolLabel(_ source: MediaSource) -> String {
        if let luckyStunDisplayName = source.luckyStunDisplayName {
            return luckyStunDisplayName
        }
        if SMBSourceSupport.shareInfo(from: source) != nil {
            return "SMB"
        }
        switch source.protocolKind {
        case .webdav: return "WebDAV"
        case .plex: return "Plex"
        case .emby: return "Emby"
        case .jellyfin: return "Jellyfin"
        case .direct: return "直连"
        case .local, .none: return "本地目录"
        }
    }

    private func sourceIconName(_ source: MediaSource) -> String {
        if SMBSourceSupport.shareInfo(from: source) != nil {
            return "externaldrive.connected.to.line.below"
        }
        switch source.protocolKind {
        case .webdav: return "network"
        case .plex, .emby, .jellyfin: return "server.rack"
        default: return "folder.fill"
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
        case .webdav: return "WebDAV"
        case .plex: return "Plex"
        case .emby: return "Emby"
        case .jellyfin: return "Jellyfin"
        default: return "媒体服务器"
        }
    }

    private func defaultMediaServerName(for protocolKind: MediaSourceProtocol) -> String {
        switch protocolKind {
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
    }

    private func mediaServerConnectionHint(for protocolKind: MediaSourceProtocol) -> String {
        switch protocolKind {
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
        if newValue == .plex || newValue == .webdav {
            mediaServerUserId = ""
        }
        if newValue != .webdav {
            mediaServerPassword = ""
        }
        if newValue == .webdav {
            mediaServerToken = ""
        }
        mediaServerMessage = mediaServerConnectionHint(for: newValue)
    }

    private var remoteSourceSaveButtonTitle: String {
        if mediaServerIsPreScanning {
            return "读取中..."
        }
        switch mediaServerProtocol {
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
        shouldStartLuckyWebDAVAfterDismiss = false
        shouldShowWebDAVBrowserAfterLoginDismiss = false
        shouldShowSMBBrowserAfterLoginDismiss = false
        isShowingManualRemoteSourceSheet = false
        isShowingMediaServerSheet = false
        isShowingLuckyStunSourceSheet = false
        isShowingWebDAVPreScanLoginSheet = false
        isShowingWebDAVFolderBrowserSheet = false
        isShowingSMBBrowserSheet = false
    }

    private func openManualRemoteSourceChooser() {
        isShowingManageSources = false
        closeRemoteSourceSheets()
        resetMediaServerForm()
        remoteSourceKind = RemoteSourceKind(mediaServerProtocol)
        DispatchQueue.main.async {
            isShowingManualRemoteSourceSheet = true
        }
    }

    private func openLuckyStunSourceSheet() {
        isShowingManageSources = false
        closeRemoteSourceSheets()
        luckySourceName = ""
        luckySourceProtocol = .plex
        luckyManagementURL = ""
        luckyUsername = ""
        luckyPassword = ""
        luckyRuleID = ""
        luckyStunAddress = ""
        luckyStunRules = []
        luckyAutoUpdate = true
        luckyUpdateIntervalMinutes = 30
        luckyDockerUsername = ""
        luckyDockerPassword = ""
        luckyStatusMessage = nil
        isTestingLuckySource = false
        pendingLuckyConfiguration = nil
        isShowingLuckyStunSourceSheet = true
    }

    private func startPendingLuckyWebDAV() {
        guard shouldStartLuckyWebDAVAfterDismiss else { return }
        shouldStartLuckyWebDAVAfterDismiss = false
        // Present the browser immediately so a slow first tunnel request still
        // exposes the current URL and an actionable refresh button.
        webDAVBrowserIsLoading = true
        isShowingWebDAVFolderBrowserSheet = true
        saveWebDAVBrowserLoginAndBrowse(presentBrowserWhenLoaded: false)
    }

    private func showPendingWebDAVBrowser() {
        guard shouldShowWebDAVBrowserAfterLoginDismiss else { return }
        shouldShowWebDAVBrowserAfterLoginDismiss = false
        isShowingWebDAVFolderBrowserSheet = true
    }

    private func testLuckyStunLogin() {
        let managementURL = luckyManagementURL
        let username = luckyUsername
        let password = luckyPassword
        isTestingLuckySource = true
        luckyStatusMessage = "正在登录并读取穿透规则..."
        Task { @MainActor in
            do {
                let rules = try await LuckyStunClient.shared.fetch(
                    managementURL: managementURL,
                    username: username,
                    password: password,
                    selectedRuleID: luckyRuleID,
                    selectedRuleName: ""
                )
                luckyStunRules = rules
                if !rules.contains(where: { $0.id == luckyRuleID }) {
                    luckyRuleID = rules.first?.id ?? ""
                }
                luckyStunAddress = rules.first(where: { $0.id == luckyRuleID })?.address ?? ""
                luckyStatusMessage = rules.isEmpty ? "登录成功，但没有读取到穿透规则。" : "登录成功，已读取 \(rules.count) 条穿透规则。"
            } catch {
                luckyStunRules = []
                luckyStatusMessage = error.localizedDescription
            }
            isTestingLuckySource = false
        }
    }

    private func luckyStunSourceSheet() -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("添加 Lucky STUN 穿透媒体源")
                .font(.title3.bold())
            Picker("类型", selection: $luckySourceProtocol) {
                Text("WebDAV").tag(MediaSourceProtocol.webdav)
                Text("Plex").tag(MediaSourceProtocol.plex)
                Text("Emby").tag(MediaSourceProtocol.emby)
                Text("Jellyfin").tag(MediaSourceProtocol.jellyfin)
            }
            .pickerStyle(.segmented)
            TextField("显示名称（可选）", text: $luckySourceName)
                .textFieldStyle(.roundedBorder)
            TextField("Lucky 管理地址", text: $luckyManagementURL)
                .textFieldStyle(.roundedBorder)
            TextField("Lucky 管理账号", text: $luckyUsername)
                .textFieldStyle(.roundedBorder)
            SecureField("Lucky 管理密码", text: $luckyPassword)
                .textFieldStyle(.roundedBorder)

            HStack {
                Button(isTestingLuckySource ? "检测中..." : "登录检测") { testLuckyStunLogin() }
                    .disabled(
                        isTestingLuckySource ||
                        luckyManagementURL.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
                        luckyUsername.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
                        luckyPassword.isEmpty
                    )
                if isTestingLuckySource { ProgressView().controlSize(.small) }
            }

            Picker("穿透规则", selection: Binding(get: { luckyRuleID }, set: { value in
                luckyRuleID = value
                luckyStunAddress = luckyStunRules.first(where: { $0.id == value })?.address ?? ""
            })) {
                Text("请选择穿透规则").tag("")
                ForEach(luckyStunRules) { rule in
                    Text("\(rule.name) · \(rule.address)").tag(rule.id)
                }
            }
            .disabled(luckyStunRules.isEmpty)

            Toggle("自动更新穿透地址", isOn: $luckyAutoUpdate)
                .toggleStyle(.checkbox)
            if luckyAutoUpdate {
                Stepper("更新周期：\(luckyUpdateIntervalMinutes) 分钟", value: $luckyUpdateIntervalMinutes, in: 5...1440, step: 5)
            }

            Divider()
            Text("\(mediaServerProtocolLabel(luckySourceProtocol)) 登录")
                .font(.subheadline.weight(.semibold))
            TextField("穿透地址", text: $luckyStunAddress)
                .textFieldStyle(.roundedBorder)
                .disabled(true)
            if luckySourceProtocol == .plex {
                SecureField("Plex 访问令牌", text: $luckyDockerPassword)
                    .textFieldStyle(.roundedBorder)
            } else {
                TextField(luckySourceProtocol == .webdav ? "用户名（可选）" : "用户名 / 用户 ID", text: $luckyDockerUsername)
                    .textFieldStyle(.roundedBorder)
                SecureField(
                    luckySourceProtocol == .webdav ? "密码（可选）" : "密码 / API Key / 访问令牌",
                    text: $luckyDockerPassword
                )
                .textFieldStyle(.roundedBorder)
            }

            if let luckyStatusMessage {
                Text(luckyStatusMessage)
                    .font(.caption)
                    .foregroundColor(theme.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack {
                Spacer()
                Button("取消") { isShowingLuckyStunSourceSheet = false }
                Button("保存并同步") { saveLuckyStunSource() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(isTestingLuckySource || luckyRuleID.isEmpty || !isLuckySourceCredentialComplete)
            }
        }
        .padding(20)
        .frame(width: 520)
        .themeSheetChrome(theme)
    }

    private var isLuckySourceCredentialComplete: Bool {
        switch luckySourceProtocol {
        case .plex, .emby, .jellyfin:
            return !luckyDockerPassword.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        case .webdav:
            return true
        default:
            return false
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
            Picker("类型", selection: $remoteSourceKind) {
                ForEach(RemoteSourceKind.allCases) { kind in
                    Text(kind.label).tag(kind)
                }
            }
            .pickerStyle(.segmented)

            if remoteSourceKind == .smb {
                smbLoginFormFields
            } else {
                mediaServerFormFields
            }

            HStack {
                Spacer()
                Button("取消") { dismissRemoteSourceForm() }
                if remoteSourceKind == .smb {
                    Button(smbIsWorking ? "连接中..." : "保存并列出共享") { connectSMBAndBrowse() }
                        .keyboardShortcut(.defaultAction)
                        .disabled(smbIsWorking)
                } else {
                    Button(remoteSourceSaveButtonTitle) { saveRemoteSourceForm() }
                        .keyboardShortcut(.defaultAction)
                        .disabled(mediaServerIsPreScanning)
                }
            }
        }
        .padding(20)
        .frame(width: 520)
        .onChange(of: remoteSourceKind) { _, newValue in
            remoteSourceKindDidChange(newValue)
        }
        .onChange(of: mediaServerProtocol) { oldValue, newValue in
            remoteSourceKind = RemoteSourceKind(newValue)
            mediaServerProtocolDidChange(newValue, resetIdentity: oldValue != newValue)
        }
        .themeSheetChrome(theme)
    }

    /// 分段里选了普通媒体服务器：把选择同步回 mediaServerProtocol，
    /// 让原来的 onChange 继续负责重置表单；选 SMB 时不动媒体服务器的状态
    private func remoteSourceKindDidChange(_ kind: RemoteSourceKind) {
        guard let protocolKind = kind.protocolKind else { return }
        if protocolKind == mediaServerProtocol {
            mediaServerProtocolDidChange(protocolKind, resetIdentity: false)
        } else {
            mediaServerProtocol = protocolKind
        }
    }

    private var mediaServerFormFields: some View {
        VStack(alignment: .leading, spacing: 14) {
            TextField("显示名称（可选）", text: $mediaServerName)
                .textFieldStyle(.roundedBorder)
            TextField(mediaServerAddressPlaceholder, text: $mediaServerBaseURL)
                .textFieldStyle(.roundedBorder)
            if mediaServerProtocol == .webdav {
                TextField("用户名（可选）", text: $mediaServerUserId)
                    .textFieldStyle(.roundedBorder)
                SecureField("密码（可选）", text: $mediaServerPassword)
                    .textFieldStyle(.roundedBorder)
            } else if mediaServerProtocol != .plex {
                TextField("用户名 / 用户 ID（可选）", text: $mediaServerUserId)
                    .textFieldStyle(.roundedBorder)
            }
            if mediaServerProtocol == .plex {
                Button(mediaServerIsPreScanning ? "等待 Plex 授权..." : "登录 Plex") {
                    authorizePlex()
                }
                .disabled(mediaServerIsPreScanning)
            }
            if mediaServerProtocol != .webdav {
                SecureField(mediaServerTokenPlaceholder, text: $mediaServerToken)
                    .textFieldStyle(.roundedBorder)
            }
            if let mediaServerMessage {
                Text(mediaServerMessage)
                    .font(.caption)
                    .foregroundColor(theme.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private func sourceInfoSheet(_ source: MediaSource) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("媒体源登录信息").font(.title3.bold())
            Text(source.displayNameWithActiveLabel).font(.headline)
            Text("地址：\(source.displayBaseURL())").font(.caption).foregroundColor(theme.textSecondary)
            Divider()
            Text("协议：\(sourceProtocolLabel(source))")
            Text("登录信息")
                .font(.subheadline.weight(.semibold))
            Text(source.authConfig?.isEmpty == false ? source.authConfig! : "未保存登录信息")
                .font(.system(.body, design: .monospaced))
                .textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                Spacer()
                Button("关闭") {
                    isShowingSourceInfoSheet = false
                    sourceInfoTarget = nil
                }
            }
        }
        .padding(20)
        .frame(width: 480)
        .themeSheetChrome(theme)
    }

    private func renameSourceSheet() -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("重命名媒体源")
                .font(.title3.bold())
            Text("仅修改应用内显示名称，不会改动真实文件夹。")
                .font(.caption)
                .foregroundColor(theme.textSecondary)
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
        .themeSheetChrome(theme)
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
                        .foregroundColor(theme.textSecondary)
                    if source.protocolKind == .webdav {
                        if WebDAVCredentialStore.shared.credentialID(from: source.authConfig) != nil {
                            let message = removeWebDAVCredentialsOnDelete
                            ? "设置中已开启“移除 WebDAV 源时同时删除凭据”，本次会一并清除保存的登录信息。"
                            : "设置中未开启“移除 WebDAV 源时同时删除凭据”，将保留保存的登录信息。可在设置里调整此行为。"
                            Text(message)
                                .font(.caption)
                                .foregroundColor(theme.textSecondary)
                                .fixedSize(horizontal: false, vertical: true)
                        } else {
                            Text("此 WebDAV 源未保存凭据。")
                                .font(.caption)
                                .foregroundColor(theme.textSecondary)
                        }
                    }
                } else {
                    Text("未找到待移除的媒体源。")
                        .font(.caption)
                        .foregroundColor(theme.textSecondary)
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
        .themeSheetChrome(theme)
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
            .themeVibrant(theme, cornerRadius: 10)

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
                .foregroundColor(theme.textSecondary)
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
                    .foregroundColor(webDAVBrowserMessageIsError ? theme.dangerColor : theme.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack {
                Spacer()
                Button("取消") {
                    shouldShowWebDAVBrowserAfterLoginDismiss = false
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
        .themeSheetChrome(theme)
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
                        .foregroundColor(theme.textSecondary)
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
                    .foregroundColor(theme.textSecondary)
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
                        loadWebDAVBrowserDirectory(
                            at: webDAVBrowserCurrentURL.isEmpty ? webDAVBrowserBaseURL : webDAVBrowserCurrentURL,
                            replaceStackWith: webDAVBrowserPathStack
                        )
                    } label: {
                        Label("刷新", systemImage: "arrow.clockwise")
                    }
                    // Keep refresh available during the first tunnel request so a
                    // stalled/empty response can be retried without leaving the sheet.
                    .disabled(webDAVBrowserCurrentURL.isEmpty && webDAVBrowserBaseURL.isEmpty)
                }
            }

            if !webDAVBrowserStarredFolders.isEmpty {
                Text("已标星 \(webDAVBrowserStarredFolders.count) 个文件夹，关闭列表后会加入挂载媒体源。")
                    .font(.caption)
                    .foregroundColor(theme.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if let webDAVBrowserMessage {
                Text(webDAVBrowserMessage)
                    .font(.caption)
                    .foregroundColor(webDAVBrowserMessageIsError ? theme.dangerColor : theme.textSecondary)
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
                        .foregroundColor(theme.textSecondary)
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
        .themeSheetChrome(theme)
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
                            .foregroundColor(theme.accent)
                        Text(item.displayName)
                            .foregroundColor(theme.textPrimary)
                            .lineLimit(1)
                        Spacer()
                        Image(systemName: "chevron.right")
                            .font(.caption)
                            .foregroundColor(theme.textSecondary)
                    }
                }
                .buttonStyle(.plain)
                .disabled(isBusy)
            } else {
                HStack(spacing: 8) {
                    Image(systemName: "rectangle.stack.fill")
                        .foregroundColor(theme.accent)
                    Text(item.displayName)
                        .foregroundColor(theme.textPrimary)
                        .lineLimit(1)
                    Spacer()
                }
            }

            Button {
                toggleWebDAVBrowserStar(item)
            } label: {
                Image(systemName: (isMounted || isStarred) ? "star.fill" : "star")
                    .foregroundColor((isMounted || isStarred) ? theme.accent : theme.textSecondary)
            }
            .buttonStyle(.plain)
            .help(isMounted ? "已挂载" : (isStarred ? "取消标星" : "标星此文件夹"))
            .disabled(isMounted || isBusy)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
        .themeVibrant(theme, cornerRadius: 10)
    }

    private func saveWebDAVBrowserLoginAndBrowse(presentBrowserWhenLoaded: Bool = true) {
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
                    if presentBrowserWhenLoaded {
                        if isShowingWebDAVPreScanLoginSheet {
                            shouldShowWebDAVBrowserAfterLoginDismiss = true
                            isShowingWebDAVPreScanLoginSheet = false
                        } else {
                            isShowingWebDAVFolderBrowserSheet = true
                        }
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
        let luckyConfiguration = pendingLuckyConfiguration
        let luckyRootAddress = webDAVBrowserBaseURL

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
                        if var mountedLuckyConfiguration = luckyConfiguration {
                            mountedLuckyConfiguration.pathSuffix = relativePathSuffix(
                                from: luckyRootAddress,
                                to: baseURL
                            )
                            configuration.luckyStun = mountedLuckyConfiguration
                        }
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
            pendingLuckyConfiguration = nil
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

    nonisolated private func relativePathSuffix(from rootAddress: String, to mountedAddress: String) -> String {
        guard let root = URLComponents(string: rootAddress),
              let mounted = URLComponents(string: mountedAddress) else { return "" }
        let rootPath = root.path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        let mountedPath = mounted.path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        guard !mountedPath.isEmpty else { return "" }
        if rootPath.isEmpty { return mountedPath }
        if mountedPath == rootPath { return "" }
        let prefix = rootPath + "/"
        return mountedPath.hasPrefix(prefix) ? String(mountedPath.dropFirst(prefix.count)) : mountedPath
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
        return "\(protocolKind.rawValue):\(normalizedURL)"
    }

    nonisolated private func remoteBrowserKey(for item: WebDAVDirectoryItem) -> String {
        remoteBrowserKey(protocolKind: item.protocolKind, baseURL: item.url.absoluteString, authConfig: item.authConfig)
    }

    private func mountedRemoteBrowserKeys(from sources: [MediaSource]) -> Set<String> {
        Set(sources.compactMap { source in
            guard let protocolKind = source.protocolKind else { return nil }
            guard protocolKind == .webdav || protocolKind == .plex || protocolKind == .emby || protocolKind == .jellyfin else { return nil }
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
        case .plex, .emby, .jellyfin:
            prepareMediaServerLogin(for: device)
            isShowingAddWebDAVSheet = false
            isShowingManualRemoteSourceSheet = true
        case .smb:
            isShowingAddWebDAVSheet = false
            openSMBSourceSheet(host: device.ipAddress)
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
        } else if device.type == .smb {
            openSMBSourceSheet(host: device.ipAddress)
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
        default:
            mediaServerProtocol = .plex
        }
        mediaServerName = device.name.isEmpty ? "\(mediaServerProtocolLabel(mediaServerProtocol)) \(device.ipAddress)" : device.name
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
        if device.type == .smb {
            return "smb://\(device.ipAddress):\(device.port)"
        }
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

    private func saveLuckyStunSource() {
        guard let rule = luckyStunRules.first(where: { $0.id == luckyRuleID }),
              let address = LuckyStunClient.normalizeAddress(rule.address) else {
            luckyStatusMessage = "请先登录检测并选择有效的穿透规则。"
            return
        }
        let managementURL = luckyManagementURL.trimmingCharacters(in: .whitespacesAndNewlines)
        let luckyAccount = luckyUsername.trimmingCharacters(in: .whitespacesAndNewlines)
        let mediaAccount = luckyDockerUsername.trimmingCharacters(in: .whitespacesAndNewlines)
        let customDisplayName = luckySourceName.trimmingCharacters(in: .whitespacesAndNewlines)
        let finalName = customDisplayName.isEmpty
            ? "Lucky STUN · \(rule.name)"
            : customDisplayName
        guard !managementURL.isEmpty, isLuckySourceCredentialComplete else {
            luckyStatusMessage = "请填写 Lucky 管理地址和媒体源登录信息。"
            return
        }

        let luckyConfiguration = LuckyStunSourceConfiguration(
            managementURL: managementURL,
            username: luckyAccount,
            password: luckyPassword,
            ruleID: rule.id,
            ruleName: rule.name,
            displayName: customDisplayName,
            autoUpdate: luckyAutoUpdate,
            updateIntervalMinutes: luckyUpdateIntervalMinutes,
            lastUpdatedAt: Date().timeIntervalSince1970
        )

        if luckySourceProtocol == .webdav {
            pendingLuckyConfiguration = luckyConfiguration
            webDAVBrowserName = finalName
            let browserAddress = sanitizedWebDAVBrowserURL(address)
            webDAVBrowserBaseURL = browserAddress
            webDAVBrowserCurrentURL = browserAddress
            webDAVBrowserUsername = mediaAccount
            webDAVBrowserPassword = luckyDockerPassword
            webDAVBrowserProtocol = .webdav
            webDAVBrowserLocalLabel = ""
            webDAVBrowserExternalAddresses = []
            webDAVBrowserPathStack = []
            webDAVBrowserItems = []
            webDAVBrowserStarredFolders = []
            webDAVBrowserMessage = nil
            webDAVBrowserMessageIsError = false
            webDAVBrowserIsLoading = false
            shouldStartLuckyWebDAVAfterDismiss = true
            isShowingLuckyStunSourceSheet = false
            return
        }

        if luckySourceProtocol == .plex || luckySourceProtocol == .emby || luckySourceProtocol == .jellyfin {
            isTestingLuckySource = true
            luckyStatusMessage = "正在读取 \(mediaServerProtocolLabel(luckySourceProtocol)) 媒体库..."
            Task {
                do {
                    let items = try await MediaServerLibraryBrowser().listLibraries(
                        protocolKind: luckySourceProtocol,
                        baseURL: address,
                        token: luckyDockerPassword,
                        userId: mediaAccount
                    )
                    await MainActor.run {
                        pendingLuckyConfiguration = luckyConfiguration
                        webDAVBrowserProtocol = luckySourceProtocol
                        webDAVBrowserName = finalName
                        webDAVBrowserBaseURL = address
                        webDAVBrowserLocalLabel = ""
                        webDAVBrowserExternalAddresses = []
                        webDAVBrowserUsername = ""
                        webDAVBrowserPassword = ""
                        webDAVBrowserCredentialID = nil
                        webDAVBrowserCurrentURL = address
                        webDAVBrowserPathStack = []
                        webDAVBrowserItems = items
                        webDAVBrowserMountedURLs = mountedRemoteBrowserKeys(from: mediaSources)
                        webDAVBrowserStarredFolders = []
                        webDAVBrowserMessage = items.isEmpty ? "连接成功，但当前服务器没有可挂载的媒体库。" : "请选择要挂载的媒体库。"
                        webDAVBrowserMessageIsError = false
                        isTestingLuckySource = false
                        isShowingLuckyStunSourceSheet = false
                        isShowingWebDAVFolderBrowserSheet = true
                    }
                } catch {
                    await MainActor.run {
                        isTestingLuckySource = false
                        luckyStatusMessage = "媒体库读取失败：\(error.localizedDescription)"
                    }
                }
            }
            return
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
                    if existingID == nil {
                        try source.insert(db)
                    } else {
                        try source.update(db)
                    }
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
                    sourceConnectivityStatus[sourceID] = "连接成功：\(result.address)"
                    loadData()
                } else if result.isReachable {
                    sourceConnectivityStatus[sourceID] = "连接成功：\(result.address)"
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

    private func checkSourceCandidates(
        source: MediaSource,
        candidates: [SourceAddressCandidate]
    ) async -> SourceConnectivityResult {
        guard let protocolKind = source.protocolKind else {
            return SourceConnectivityResult(isReachable: false, address: source.baseUrl, label: "", message: "媒体源协议无效。")
        }
        var failures: [String] = []
        for candidate in candidates {
            do {
                switch protocolKind {
                case .local:
                    // /Volumes 下只剩挂载失败留下的空壳时不能算连通
                    if SMBSourceManager.isUsableSourceDirectory(candidate.address) {
                        break
                    }
                    // SMB 源掉挂载：先按保存的凭据静默重挂一次，并把新位置写回库里再判定
                    if let smbInfo = SMBSourceSupport.shareInfo(from: source),
                       let dbQueue = AppDatabase.shared.dbQueue,
                       await SMBSourceManager.remount(smbInfo, for: source, dbQueue: dbQueue) != nil {
                        break
                    }
                    throw NSError(domain: "OmniPlayConnectivity", code: 1, userInfo: [NSLocalizedDescriptionKey: "目录不存在或不可访问"])
                case .webdav:
                    // 钥匙串读取放后台：这一步跑在连通性预检里，同步读会把指针钉在转圈上
                    var storedCredential: WebDAVCredentialStore.Credential?
                    if let credentialID = WebDAVCredentialStore.shared.credentialID(from: source.authConfig) {
                        storedCredential = await WebDAVCredentialStore.shared.loadCredentialAsync(id: credentialID)
                    }
                    let credential = storedCredential
                        ?? WebDAVCredentialStore.shared.decodeLegacyCredential(from: source.authConfig)
                    let result = await WebDAVPreflightChecker().check(
                        baseURL: candidate.address,
                        username: credential?.username ?? "",
                        password: credential?.password ?? ""
                    )
                    guard result.isReachable else { throw NSError(domain: "OmniPlayConnectivity", code: 2, userInfo: [NSLocalizedDescriptionKey: result.message]) }
                case .plex, .emby, .jellyfin:
                    let auth = MediaServerAuthConfig.decode(source.authConfig)
                    let result = await MediaServerPreflightChecker().check(
                        protocolKind: protocolKind,
                        baseURL: candidate.address,
                        token: auth?.token ?? "",
                        userId: auth?.userId ?? ""
                    )
                    guard result.isReachable else { throw NSError(domain: "OmniPlayConnectivity", code: 3, userInfo: [NSLocalizedDescriptionKey: result.message]) }
                case .direct:
                    break
                }
                return SourceConnectivityResult(
                    isReachable: true,
                    address: protocolKind.normalizedBaseURL(candidate.address),
                    label: candidate.label,
                    message: "连接成功"
                )
            } catch {
                failures.append("\(candidate.label)：\(error.localizedDescription)")
            }
        }
        return SourceConnectivityResult(
            isReachable: false,
            address: source.baseUrl,
            label: "",
            message: failures.isEmpty ? "没有可检测的媒体源地址。" : failures.joined(separator: "；")
        )
    }

    private func loadData() {
        if isLoadingLibraryData {
            needsLibraryReloadAfterCurrentLoad = true
            return
        }
        isLoadingLibraryData = true
        needsLibraryReloadAfterCurrentLoad = false

        // 这里的读写是同步访问，占着 GRDB 的读连接。跑在 utility 上时主线程一旦等连接，
        // 调度器就报优先级反转（user-interactive 等 utility），所以媒体库访问统一用 user-initiated。
        DispatchQueue.global(qos: .userInitiated).async {
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
                    self.refreshCategoryFolderState()
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

    // 扫描必须有回音：单个媒体源失败、或扫描成功但一个文件都没找到时，原先只在左上角
    // 闪过一行状态文字就收尾，用户看到的效果是「点了按钮什么都没发生」。
    private func reportScanOutcomes(sourceResults: [MediaSourceScanResult], scannedSources: [MediaSource]) {
        let failures = sourceResults.filter { !$0.isSuccess }
        if !failures.isEmpty {
            scanErrorAlertTitle = failures.count == sourceResults.count ? "扫描刮削失败" : "部分媒体源扫描失败"
            scanErrorMessage = MediaSourceScanDiagnosticsFormatter.diagnosticsReport(results: sourceResults)
            isShowingScanErrorAlert = true
            return
        }
        // 这一轮真的找到并入库过文件，就别再报「没有扫描到视频文件」
        let foundCount = sourceResults.reduce(0) { $0 + $1.scannedCount + $1.insertedCount }
        if foundCount > 0 { return }
        // 库里已经有内容时不再打扰：那种情况下 0 命中只是「没有新增」。
        guard movies.isEmpty else { return }
        scanErrorAlertTitle = "没有扫描到视频文件"
        scanErrorMessage = MediaSourceScanDiagnosticsFormatter.emptyScanReport(sources: scannedSources)
        isShowingScanErrorAlert = true
    }

    // 统一入口：按首页显示顺序补抓缺失的分集剧照。
    // 剧照下载是后台长任务，这里不阻塞扫描流程；进度通过 ThumbnailManager.progressMessage 展示。
    private func enqueueEpisodeStillBackfill(runID: UUID, movieIDs: [Int64]) async {
        if !movieIDs.isEmpty, await MainActor.run(body: { self.currentScanRunID == runID }) {
            // 显式传入的剧集优先，其余影片按首页顺序补在后面。
            ThumbnailManager.shared.enqueueMissingEpisodeThumbnailsAcrossLibrary(orderedMovieIDs: movieIDs)
        } else {
            await MainActor.run {
                guard self.currentScanRunID == runID else { return }
                withAnimation { self.processingMessage = "补齐分集剧照..." }
            }
            let orderedMovieIDs = await MainActor.run {
                self.displayedMovies.compactMap(\.id)
            }
            ThumbnailManager.shared.enqueueMissingEpisodeThumbnailsAcrossLibrary(orderedMovieIDs: orderedMovieIDs)
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
        
        currentScanTask = Task(priority: .userInitiated) {
            do {
                if Task.isCancelled {
                    await MainActor.run { finishScanRun(runID: runID, reloadData: true) }
                    return
                }
                // SMB 共享在重启/休眠后可能掉挂载，扫描开始前先静默重挂
                await SMBSourceManager.remountPersistedSources()
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
                    // 一个可扫描的源都没有（比如刚删完源）不值得弹窗拦人，安静收尾就行
                    await MainActor.run {
                        finishScanRun(runID: runID, reloadData: true)
                    }
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
                    if skipTMDBScrape {
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
                        if skipTMDBScrape {
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
                    reportScanOutcomes(sourceResults: sourceResults, scannedSources: validSources)
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
                        withAnimation { self.processingMessage = "没有需要 TMDB 刮削的内容，检查缺失的分集剧照..." }
                    }
                    // 库已经全部刮削过时不能直接 return，否则缺失的分集剧照永远不会补抓。
                    await self.enqueueEpisodeStillBackfill(runID: runID, movieIDs: [])
                    await MainActor.run {
                        guard self.currentScanRunID == runID else { return }
                        finishScanRun(runID: runID, reloadData: true)
                    }
                    return
                }
                await MainActor.run {
                    guard self.currentScanRunID == runID else { return }
                    withAnimation { self.processingMessage = "检测 TMDB 连通性..." }
                }
                // 连通性探测在全局执行器上完成，检测期间主线程仍可响应界面与指针
                let tmdbConnection = await Task.detached(priority: .utility) {
                    await TMDBService.probeConnection(credential: TMDBService.credentialSource())
                }.value
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
                await self.enqueueEpisodeStillBackfill(runID: runID, movieIDs: [])

                await MainActor.run {
                    guard self.currentScanRunID == runID else { return }
                    finishScanRun(runID: runID, reloadData: true)
                }
            } catch {
                await MainActor.run {
                    finishScanRun(runID: runID, reloadData: true)
                    // 失败时弹窗显示错误，避免“点了没反应”却看不到原因
                    if currentScanRunID == runID {
                        scanErrorAlertTitle = "扫描刮削失败"
                        scanErrorMessage = "扫描刮削失败：\(error.localizedDescription)"
                        isShowingScanErrorAlert = true
                    }
                }
            }
        }
    }
    
}

// 🌟 彻底删除了原先几百行的内网目录浏览器 (SourceFileBrowserView)
// 因为我们现在全部交给原生的 Finder 来选了！


// 首页分类文件夹卡片：封面 + 名称 + 数量；鼠标悬停到右上角时出现编辑按钮
// 隐藏导航栏标题：macOS 15+ 用 toolbar(removing:)，macOS 14 用空标题兜底
struct RemoveNavTitleModifier: ViewModifier {
    func body(content: Content) -> some View {
        if #available(macOS 15.0, *) {
            content.toolbar(removing: .title)
        } else {
            content.navigationTitle("")
        }
    }
}

private struct CategoryFolderCard<D: View>: View {
    let group: CategoryFolderGroup
    let posterPath: String?
    let theme: AppTheme
    let enableFastTooltip: Bool
    let onEdit: () -> Void
    @ViewBuilder let destination: () -> D
    @State private var isHovering = false

    var body: some View {
        NavigationLink(destination: destination()) {
            VStack(alignment: .leading, spacing: 8) {
                ZStack(alignment: .topTrailing) {
                    CachedPosterView(posterPath: posterPath)
                        .frame(width: 160, height: 240)
                        .cornerRadius(12)
                        .shadow(color: .black.opacity(0.1), radius: 8, y: 4)
                    if isHovering {
                        Button(action: onEdit) {
                            Image(systemName: "pencil")
                                .font(.system(size: 11, weight: .semibold))
                                .foregroundColor(.white)
                                .frame(width: 26, height: 26)
                                .background(Circle().fill(.black.opacity(0.55)))
                                .overlay(Circle().stroke(.white.opacity(0.3), lineWidth: 0.5))
                        }
                        .buttonStyle(.plain)
                        .padding(8)
                        .transition(.opacity)
                        .conditionalHelp("编辑该文件夹封面与名称", show: enableFastTooltip)
                    }
                }
                .onHover { hovering in
                    withAnimation(.easeInOut(duration: 0.15)) { isHovering = hovering }
                }
                // 名称 + 数量数字 同行显示
                HStack(spacing: 6) {
                    Text(group.folder.name)
                        .font(.subheadline.weight(.semibold))
                        .foregroundColor(theme.textPrimary)
                        .lineLimit(1)
                    Text("\(group.movies.count)")
                        .font(.caption.weight(.medium))
                        .foregroundColor(theme.textSecondary)
                        .lineLimit(1)
                }
                .frame(width: 160, height: 20, alignment: .center)
            }
        }
        .buttonStyle(.plain)
        .conditionalHelp("打开「\(group.folder.name)」分类下的全部影视", show: enableFastTooltip)
    }
}

// 首页分类文件夹的视图分组：一名导演/主演 + 其名下影视
private struct CategoryFolderGroup: Identifiable {
    let folder: CategoryFolder
    let movies: [Movie]

    var id: String { folder.id }
}

extension PosterWallView {
    /// 把访达里拖进来的文件交给直开链路，视频之外的文件由它回报原因。
    fileprivate func handleFileDrop(_ providers: [NSItemProvider]) -> Bool {
        guard !providers.isEmpty else { return false }
        Task { @MainActor in
            let urls = await posterWallDropURLs(from: providers)
            guard !urls.isEmpty else {
                DirectFilePlaybackManager.shared.reportDroppedNothing()
                return
            }
            DirectFilePlaybackManager.shared.handleOpen(urls: urls)
        }
        return true
    }
}

// MARK: - 滚动相位

private struct WallScrollPhaseModifier: ViewModifier {
    @Binding var isScrolling: Bool

    func body(content: Content) -> some View {
        if #available(macOS 15.0, *) {
            content.onScrollPhaseChange { _, phase in
                let active = phase != .idle
                if active != isScrolling { isScrolling = active }
            }
        } else {
            content
        }
    }
}

extension View {
    /// 把「用户正在滚动」传给海报卡：macOS 15 起用系统的滚动相位回调，更早系统维持原行为。
    func wallScrollPhaseGating(isScrolling: Binding<Bool>) -> some View {
        modifier(WallScrollPhaseModifier(isScrolling: isScrolling))
    }
}
