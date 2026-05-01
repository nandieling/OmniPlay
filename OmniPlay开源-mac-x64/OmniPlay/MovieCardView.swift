import SwiftUI
import GRDB
import Combine

struct MovieCardView: View {
    let movie: Movie
    var isContinueWatchingContext: Bool = false
    
    @AppStorage("appTheme") var appTheme = ThemeType.appleLight.rawValue
    var theme: AppTheme { ThemeType(rawValue: appTheme)?.colors ?? ThemeType.appleLight.colors }
    @AppStorage("enableFastTooltip") var enableFastTooltip = true
    
    @ObservedObject var cacheManager = OfflineCacheManager.shared
    
    @State private var showCacheAlert = false
    @State private var filesToCache: [VideoFile] = []
    @State private var showSearchModal = false
    @State private var showEditModal = false
    @State private var hasMissingFiles = false
    @State private var hasRemoteUncacheableFiles = false
    
    @State private var isHovering = false
    @State private var isFullyWatched = false
    @State private var movieFiles: [VideoFile] = []
    @State private var sourcePairs: [(VideoFile, MediaSource?)] = []
    
    private let availabilityTimer = Timer.publish(every: 5.0, on: .main, in: .common).autoconnect()
    
    // 🌟 删除了原有的 posterURLString，交由底层的 CachedPosterView 智能处理
    
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            ZStack(alignment: .topTrailing) {
                ZStack(alignment: .bottomLeading) {
                    
                    // 🌟 核心升级：替换为智能本地缓存海报组件
                    CachedPosterView(posterPath: movie.posterPath)
                        .frame(width: 160, height: 240)
                        .cornerRadius(12)
                        .shadow(color: .black.opacity(0.1), radius: 8, y: 4)
                    
                    if let vote = movie.voteAverage, vote > 0 {
                        Text(String(format: "%.1f", vote))
                            .font(.caption.bold())
                            .padding(.horizontal, 6).padding(.vertical, 4)
                            .background(Color.black.opacity(0.7))
                            .foregroundColor(Color(hex: "FFD700"))
                            .cornerRadius(6)
                            .padding(8)
                    }
                }
                
                // 搜索与编辑按钮 (悬停显示)
                if isHovering {
                    HStack(spacing: 8) {
                        Button(action: { showSearchModal = true }) {
                            Image(systemName: "magnifyingglass.circle.fill").font(.title2).foregroundColor(.white).shadow(radius: 3)
                        }.buttonStyle(.plain)
                        
                        Button(action: { showEditModal = true }) {
                            Image(systemName: "pencil.circle.fill").font(.title2).foregroundColor(.white).shadow(radius: 3)
                        }.buttonStyle(.plain)
                    }
                    .padding(8)
                    .transition(.opacity)
                }
            }
            .onHover { isHovering = $0 }
            
            Text(movie.title)
                .font(.headline)
                .foregroundColor(theme.textPrimary)
                .lineLimit(1)
            
            // 底部状态栏
            HStack(spacing: 8) {
                if let date = movie.releaseDate, date.count >= 4 {
                    Text(String(date.prefix(4))).font(.caption).foregroundColor(theme.textSecondary)
                }
                
                Spacer(minLength: 0)
                
                // 缓存按钮 (受全局开关控制)
                if cacheManager.isCacheModeActive {
                    Button(action: { fetchFilesAndShowAlert() }) {
                        Group {
                            if isDownloadingAnyFile {
                                ProgressView().controlSize(.small)
                            } else if !isCacheSupportedForAnyFile {
                                Image(systemName: "icloud.slash")
                                    .font(.system(size: 13, weight: .bold))
                            } else {
                                Image(systemName: "icloud.and.arrow.down")
                                    .font(.system(size: 13, weight: .bold))
                            }
                        }
                        .foregroundColor(isCacheSupportedForAnyFile ? theme.accent : theme.textSecondary)
                    }
                    .buttonStyle(.plain)
                    .disabled(!isCacheSupportedForAnyFile)
                    .conditionalHelp("远程源暂不支持离线缓存", show: enableFastTooltip && !isCacheSupportedForAnyFile)
                }
                
                if hasMissingFiles {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .font(.system(size: 12))
                        .foregroundColor(.orange)
                        .conditionalHelp("部分源文件不存在，需重新连接外置硬盘/NAS 或使用本地缓存播放", show: enableFastTooltip)
                }
                if hasRemoteUncacheableFiles && cacheManager.isCacheModeActive {
                    Image(systemName: "icloud.slash")
                        .font(.system(size: 12))
                        .foregroundColor(.secondary)
                        .conditionalHelp("包含远程源文件：当前版本不支持离线缓存下载", show: enableFastTooltip)
                }
                
                // 标记已播/未播按钮
                Button(action: toggleWatched) {
                    Image(systemName: isFullyWatched ? "checkmark.circle.fill" : "circle")
                        .font(.system(size: 13))
                        .foregroundColor(isFullyWatched ? theme.accent : theme.textSecondary.opacity(0.5))
                }.buttonStyle(.plain)
            }
        }
        .frame(width: 160)
        .onAppear { checkWatchedStatus(); checkFileAvailability() }
        .onReceive(NotificationCenter.default.publisher(for: .libraryUpdated)) { _ in checkWatchedStatus(); checkFileAvailability() }
        .onReceive(cacheManager.$cachedFileKeys) { _ in checkFileAvailability() }
        .onReceive(cacheManager.$cachedFileNames) { _ in checkFileAvailability() }
        .onReceive(availabilityTimer) { _ in refreshAvailabilityWithoutDB() }
        .alert("离线缓存确认", isPresented: $showCacheAlert) {
            Button("取消", role: .cancel) { }
            Button("确定缓存") { cacheAllFiles() }
        } message: { Text("确定要将《\(movie.title)》包含的 \(filesToCache.count) 个视频文件加入后台缓存队列吗？") }
        .sheet(isPresented: $showSearchModal) { MovieSearchModalView(movie: movie) }
        .sheet(isPresented: $showEditModal) { MovieEditModalView(movie: movie) }
    }
    
    private var isDownloadingAnyFile: Bool {
        movieFiles.contains { cacheManager.downloadProgress[$0.id] != nil }
    }

    private var isCacheSupportedForAnyFile: Bool {
        sourcePairs.contains { _, source in cacheManager.supportsCaching(mediaSource: source) }
    }
    
    // ======== 私有数据库操作方法 ========
    private func checkWatchedStatus() {
        Task {
            do {
                let files = try await AppDatabase.shared.dbQueue.read {
                    try VideoFile.filter(Column("movieId") == movie.id).fetchAll($0)
                }
                let hasWatchedFile = files.contains { file in
                    file.duration > 0 && (file.playProgress / file.duration) >= 0.95
                }
                await MainActor.run { self.isFullyWatched = hasWatchedFile }
            } catch {}
        }
    }
    
    private func toggleWatched() {
        Task {
            do {
                try await AppDatabase.shared.dbQueue.write { db in
                    let files = try VideoFile.filter(Column("movieId") == movie.id).fetchAll(db)
                    for var file in files {
                        let isWatched = file.duration > 0 && (file.playProgress / file.duration) >= 0.95
                        if isWatched {
                            file.playProgress = 0
                        } else {
                            let duration = file.duration > 0 ? file.duration : 100
                            file.duration = duration
                            file.playProgress = duration
                        }
                        try file.update(db)
                    }
                }
                await MainActor.run { NotificationCenter.default.post(name: .libraryUpdated, object: nil) }
            } catch {}
        }
    }
    
    private func fetchFilesAndShowAlert() {
        Task {
            do {
                let pairs = try await AppDatabase.shared.dbQueue.read { db in
                    let files = try movie.request(for: Movie.videoFiles).fetchAll(db)
                    return try files.map { file in
                        (file, try file.request(for: VideoFile.mediaSource).fetchOne(db))
                    }
                }
                let cacheableFiles = pairs.filter { file, source in
                    cacheManager.supportsCaching(mediaSource: source) && !cacheManager.isCached(file)
                }.map(\.0)
                await MainActor.run {
                    if cacheableFiles.isEmpty {
                        cacheManager.cacheStatusMessage = "远程源暂不支持离线缓存"
                    } else {
                        self.filesToCache = cacheableFiles
                        self.showCacheAlert = true
                    }
                }
            } catch {}
        }
    }
    
    private func cacheAllFiles() {
        for file in filesToCache { if !cacheManager.isCached(file) { cacheManager.startDownload(file: file) } }
    }
    
    private func checkFileAvailability() {
        Task {
            do {
                let pairs = try await AppDatabase.shared.dbQueue.read { db in
                    let files = try movie.request(for: Movie.videoFiles).fetchAll(db)
                    return try files.map { file in
                        (file, try file.request(for: VideoFile.mediaSource).fetchOne(db))
                    }
                }
                await MainActor.run {
                    self.movieFiles = pairs.map(\.0)
                    self.sourcePairs = pairs
                    self.hasMissingFiles = evaluateMissingState(with: pairs)
                    self.hasRemoteUncacheableFiles = evaluateRemoteUncacheableState(with: pairs)
                }
            } catch {}
        }
    }
    
    private func refreshAvailabilityWithoutDB() {
        guard !sourcePairs.isEmpty else { return }
        let latestMissing = evaluateMissingState(with: sourcePairs)
        if latestMissing != hasMissingFiles {
            hasMissingFiles = latestMissing
        }
    }
    
    private func evaluateMissingState(with pairs: [(VideoFile, MediaSource?)]) -> Bool {
        pairs.contains { file, source in
            cacheManager.hasMissingSource(for: file, mediaSource: source)
        }
    }

    private func evaluateRemoteUncacheableState(with pairs: [(VideoFile, MediaSource?)]) -> Bool {
        pairs.contains { _, source in
            guard let source else { return false }
            return !cacheManager.supportsCaching(mediaSource: source)
        }
    }
}
