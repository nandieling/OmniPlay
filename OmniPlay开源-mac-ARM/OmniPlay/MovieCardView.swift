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
    @State private var refreshTask: Task<Void, Never>? = nil
    
    private let availabilityTimer = Timer.publish(every: 15.0, on: .main, in: .common).autoconnect()
    
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            ZStack(alignment: .topTrailing) {
                ZStack(alignment: .bottomLeading) {
                    CachedPosterView(posterPath: movie.posterPath)
                        .frame(width: 160, height: 240)
                        .cornerRadius(12)
                        .shadow(color: .black.opacity(0.1), radius: 8, y: 4)
                    
                    if let vote = movie.voteAverage, vote > 0 {
                        Text(String(format: "%.1f", vote))
                            .font(.caption.bold())
                            .padding(.horizontal, 6)
                            .padding(.vertical, 4)
                            .background(Color.black.opacity(0.7))
                            .foregroundColor(Color(hex: "FFD700"))
                            .cornerRadius(6)
                            .padding(8)
                    }
                }
                
                if isHovering {
                    HStack(spacing: 8) {
                        Button(action: { showSearchModal = true }) {
                            Image(systemName: "magnifyingglass.circle.fill")
                                .font(.title2)
                                .foregroundColor(.white)
                                .shadow(radius: 3)
                        }
                        .buttonStyle(.plain)
                        
                        Button(action: { showEditModal = true }) {
                            Image(systemName: "pencil.circle.fill")
                                .font(.title2)
                                .foregroundColor(.white)
                                .shadow(radius: 3)
                        }
                        .buttonStyle(.plain)
                    }
                    .padding(8)
                    .transition(.opacity)
                }
            }
            .onHover { isHovering = $0 }
            
            Text(movie.title)
                .font(.headline)
                .foregroundColor(theme.textPrimary)
                .lineLimit(nil)
                .fixedSize(horizontal: false, vertical: true)
                .multilineTextAlignment(.leading)
            
            VStack(alignment: .leading, spacing: 6) {
                HStack(spacing: 8) {
                    if let date = movie.releaseDate, date.count >= 4 {
                        Text(String(date.prefix(4)))
                            .font(.caption)
                            .foregroundColor(theme.textSecondary)
                    }
                    
                    Spacer(minLength: 0)
                    
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
                    
                    Button(action: toggleWatched) {
                        Image(systemName: isFullyWatched ? "checkmark.circle.fill" : "circle")
                            .font(.system(size: 13))
                            .foregroundColor(isFullyWatched ? theme.accent : theme.textSecondary.opacity(0.5))
                    }
                    .buttonStyle(.plain)
                }
                
                if let progress = continueWatchingProgressSnapshot {
                    VStack(alignment: .leading, spacing: 4) {
                        GeometryReader { geo in
                            ZStack(alignment: .leading) {
                                Capsule()
                                    .fill(theme.surface.opacity(0.75))
                                    .frame(height: 4)
                                Capsule()
                                    .fill(theme.accent)
                                    .frame(width: max(4, geo.size.width * progress.ratio), height: 4)
                            }
                        }
                        .frame(height: 4)
                        
                        Text("\(progress.current) / \(progress.total)")
                            .font(.caption2.monospacedDigit())
                            .foregroundColor(theme.textSecondary)
                    }
                }
            }
        }
        .frame(width: 160)
        .onAppear { refreshCardState() }
        .onDisappear {
            refreshTask?.cancel()
            refreshTask = nil
        }
        .onReceive(NotificationCenter.default.publisher(for: .libraryUpdated)) { _ in refreshCardState() }
        .onReceive(cacheManager.$cachedFileKeys) { _ in refreshAvailabilityWithoutDB() }
        .onReceive(cacheManager.$cachedFileNames) { _ in refreshAvailabilityWithoutDB() }
        .onReceive(availabilityTimer) { _ in refreshAvailabilityWithoutDB() }
        .alert("离线缓存确认", isPresented: $showCacheAlert) {
            Button("取消", role: .cancel) { }
            Button("确定缓存") { cacheAllFiles() }
        } message: {
            Text("确定要将《\(movie.title)》包含的 \(filesToCache.count) 个视频文件加入后台缓存队列吗？")
        }
        .sheet(isPresented: $showSearchModal) { MovieSearchModalView(movie: movie) }
        .sheet(isPresented: $showEditModal) { MovieEditModalView(movie: movie) }
    }
    
    private var isDownloadingAnyFile: Bool {
        movieFiles.contains { cacheManager.downloadProgress[$0.id] != nil }
    }

    private var isCacheSupportedForAnyFile: Bool {
        sourcePairs.contains { _, source in cacheManager.supportsCaching(mediaSource: source) }
    }

    private var continueWatchingProgressSnapshot: (ratio: Double, current: String, total: String)? {
        guard isContinueWatchingContext else { return nil }
        let sortedFiles = movieFiles.enumerated().sorted {
            MediaNameParser.episodeSortKey(for: $0.element.fileName, fallbackIndex: $0.offset) <
            MediaNameParser.episodeSortKey(for: $1.element.fileName, fallbackIndex: $1.offset)
        }.map(\.element)
        guard let targetFile = sortedFiles.first(where: { file in
            guard file.duration > 0 else { return false }
            let ratio = file.playProgress / file.duration
            return file.playProgress > 5.0 && ratio < 0.95
        }) else {
            return nil
        }
        let duration = targetFile.duration
        let progress = min(max(targetFile.playProgress, 0), duration)
        let ratio = min(max(progress / duration, 0), 1)
        return (ratio, formatTime(progress), formatTime(duration))
    }
    
    private func refreshCardState() {
        refreshTask?.cancel()
        refreshTask = Task {
            do {
                let pairs = try await AppDatabase.shared.dbQueue.read { db in
                    try VideoFile.fetchVisibleSourcePairs(movieId: movie.id, in: db)
                }
                if Task.isCancelled { return }
                let files = pairs.map(\.0)
                let allFilesWatched = !files.isEmpty && files.allSatisfy { file in
                    file.duration > 0 && (file.playProgress / file.duration) >= 0.95
                }
                let missingFiles = evaluateMissingState(with: pairs)
                let remoteUncacheable = evaluateRemoteUncacheableState(with: pairs)
                await MainActor.run {
                    self.movieFiles = files
                    self.sourcePairs = pairs
                    self.isFullyWatched = allFilesWatched
                    self.hasMissingFiles = missingFiles
                    self.hasRemoteUncacheableFiles = remoteUncacheable
                }
            } catch {}
        }
    }
    
    private func toggleWatched() {
        Task {
            do {
                try await AppDatabase.shared.dbQueue.write { db in
                    let files = try VideoFile.fetchVisibleFiles(movieId: movie.id, in: db)
                    let shouldMarkWatched = files.contains { file in
                        !(file.duration > 0 && (file.playProgress / file.duration) >= 0.95)
                    }
                    for var file in files {
                        if shouldMarkWatched {
                            let duration = file.duration > 0 ? file.duration : 100
                            file.duration = duration
                            file.playProgress = duration
                        } else {
                            file.playProgress = 0
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
                    try VideoFile.fetchVisibleSourcePairs(movieId: movie.id, in: db)
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
        for file in filesToCache {
            if !cacheManager.isCached(file) {
                cacheManager.startDownload(file: file)
            }
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
    
    private func formatTime(_ time: Double) -> String {
        guard time.isFinite, time >= 0 else { return "00:00" }
        let t = Int(time.rounded(.down))
        if t / 3600 > 0 {
            return String(format: "%02d:%02d:%02d", t / 3600, (t % 3600) / 60, t % 60)
        }
        return String(format: "%02d:%02d", (t % 3600) / 60, t % 60)
    }
}
