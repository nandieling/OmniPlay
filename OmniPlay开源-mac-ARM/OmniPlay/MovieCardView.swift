import SwiftUI
import GRDB
import Combine

struct MovieCardView: View {
    let movie: Movie
    var isContinueWatchingContext: Bool = false
    var isHomeCacheModeActive: Bool = false
    
    @AppStorage("appTheme") var appTheme = ThemeType.appleLight.rawValue
    var theme: AppTheme { ThemeType(rawValue: appTheme)?.colors ?? ThemeType.appleLight.colors }
    @AppStorage("enableFastTooltip") var enableFastTooltip = true
    
    @ObservedObject var cacheManager = OfflineCacheManager.shared
    
    @State private var showSearchModal = false
    @State private var showEditModal = false
    @State private var hasMissingFiles = false
    @State private var showCancelCacheConfirmation = false
    
    @State private var isHovering = false
    @State private var isFullyWatched = false
    @State private var movieFiles: [VideoFile] = []
    @State private var sourcePairs: [(VideoFile, MediaSource?)] = []
    @State private var doubanMetadata: DoubanMetadata? = nil
    @State private var refreshTask: Task<Void, Never>? = nil
    
    private let availabilityTimer = Timer.publish(every: 15.0, on: .main, in: .common).autoconnect()
    
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            ZStack(alignment: .topTrailing) {
                ZStack {
                    CachedPosterView(posterPath: movie.posterPath)
                        .frame(width: 160, height: 240)
                        .cornerRadius(12)
                        .shadow(color: .black.opacity(0.1), radius: 8, y: 4)
                    
                    VStack {
                        Spacer()
                        HStack(alignment: .bottom, spacing: 5) {
                            ratingBadge(value: movie.voteAverage, tint: Color(hex: "FFD700"))
                            ratingBadge(value: doubanMetadata?.rating, tint: Color(hex: "00B51D"))
                            Spacer(minLength: 0)
                        }
                        .padding(8)
                    }

                    if isHomeCacheModeActive {
                        Button(action: {
                            if isDownloadingAnyFile {
                                showCancelCacheConfirmation = true
                            } else {
                                fetchFilesAndStartCache()
                            }
                        }) {
                            posterCacheOverlayContent
                        }
                        .buttonStyle(.plain)
                        .disabled(!isCacheSupportedForAnyFile || allCacheableFilesCached)
                        .conditionalHelp(cacheOverlayHelpText, show: enableFastTooltip)
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
                .lineLimit(2)
                .frame(height: 42, alignment: .topLeading)
                .multilineTextAlignment(.leading)
            
            VStack(alignment: .leading, spacing: 6) {
                HStack(spacing: 8) {
                    if let date = movie.releaseDate, date.count >= 4 {
                        Text(String(date.prefix(4)))
                            .font(.caption)
                            .foregroundColor(theme.textSecondary)
                    }
                    
                    Spacer(minLength: 0)
                    
                    if hasMissingFiles {
                        Image(systemName: "exclamationmark.triangle.fill")
                            .font(.system(size: 12))
                            .foregroundColor(.orange)
                            .conditionalHelp("部分源文件不存在，需重新连接外置硬盘/NAS 或使用本地缓存播放", show: enableFastTooltip)
                    }
                    Button(action: toggleWatched) {
                        Image(systemName: isFullyWatched ? "checkmark.circle.fill" : "circle")
                            .font(.system(size: 13))
                            .foregroundColor(isFullyWatched ? theme.accent : theme.textSecondary.opacity(0.5))
                    }
                    .buttonStyle(.plain)
                }
                
                if let progressRatio = continueWatchingProgressRatio {
                    GeometryReader { geo in
                        ZStack(alignment: .leading) {
                            Capsule()
                                .fill(theme.surface.opacity(0.75))
                                .frame(height: 4)
                            Capsule()
                                .fill(theme.accent)
                                .frame(width: max(4, geo.size.width * progressRatio), height: 4)
                        }
                    }
                    .frame(height: 4)
                }
            }
            .frame(height: isContinueWatchingContext ? 20 : 18, alignment: .topLeading)
        }
        .frame(width: 160, height: isContinueWatchingContext ? 318 : 310, alignment: .topLeading)
        .onAppear { refreshCardState() }
        .onDisappear {
            refreshTask?.cancel()
            refreshTask = nil
        }
        .onReceive(NotificationCenter.default.publisher(for: .libraryUpdated)) { _ in refreshCardState() }
        .onReceive(cacheManager.$cachedFileKeys) { _ in refreshAvailabilityWithoutDB() }
        .onReceive(cacheManager.$cachedFileNames) { _ in refreshAvailabilityWithoutDB() }
        .onReceive(availabilityTimer) { _ in refreshAvailabilityWithoutDB() }
        .sheet(isPresented: $showSearchModal) { MovieSearchModalView(movie: movie) }
        .sheet(isPresented: $showEditModal) { MovieEditModalView(movie: movie) }
        .alert("取消离线缓存？", isPresented: $showCancelCacheConfirmation) {
            Button("取消缓存", role: .destructive) {
                cacheManager.cancelDownloads(files: movieFiles)
            }
            Button("继续缓存", role: .cancel) { }
        } message: {
            Text("已缓存的内容会保留，未完成的文件将被删除。")
        }
    }
    
    private var isDownloadingAnyFile: Bool {
        movieFiles.contains { cacheManager.progress(for: $0) != nil }
    }

    private var isCacheSupportedForAnyFile: Bool {
        sourcePairs.contains { _, source in cacheManager.supportsCaching(mediaSource: source) }
    }

    private var allCacheableFilesCached: Bool {
        let cacheableFiles = sourcePairs.filter { _, source in cacheManager.supportsCaching(mediaSource: source) }.map(\.0)
        return !cacheableFiles.isEmpty && cacheableFiles.allSatisfy { cacheManager.isCached($0) }
    }

    private var aggregateCacheProgress: Double? {
        guard isDownloadingAnyFile else { return nil }
        let cacheableFiles = sourcePairs.filter { _, source in cacheManager.supportsCaching(mediaSource: source) }.map(\.0)
        guard !cacheableFiles.isEmpty else { return nil }
        let total = cacheableFiles.reduce(0.0) { partial, file in
            if cacheManager.isCached(file) { return partial + 1.0 }
            return partial + (cacheManager.progress(for: file) ?? 0.0)
        }
        return total / Double(cacheableFiles.count)
    }

    private var cacheOverlayHelpText: String {
        if allCacheableFilesCached { return "已缓存到本地" }
        if !isCacheSupportedForAnyFile { return "该媒体源暂不支持离线缓存" }
        if isDownloadingAnyFile { return "正在离线缓存" }
        return "离线缓存整部影片或整部剧集"
    }

    @ViewBuilder
    private func ratingBadge(value: Double?, tint: Color) -> some View {
        if let value, value > 0 {
            Text(String(format: "%.1f", value))
                .font(.caption.bold())
            .padding(.horizontal, 5)
            .padding(.vertical, 4)
            .background(Color.black.opacity(0.72))
            .foregroundColor(tint)
            .cornerRadius(6)
            .lineLimit(1)
        }
    }

    @ViewBuilder
    private var posterCacheOverlayContent: some View {
        if let progress = aggregateCacheProgress {
            OfflineCacheProgressBadge(progress: progress, tint: theme.accent)
        } else {
            ZStack {
                Circle()
                    .fill(Color.black.opacity(0.62))
                    .frame(width: 52, height: 52)
                Image(systemName: allCacheableFilesCached ? "checkmark.circle.fill" : (!isCacheSupportedForAnyFile ? "icloud.slash" : "arrow.down.circle.fill"))
                    .font(.system(size: 25, weight: .bold))
                    .foregroundColor(allCacheableFilesCached ? theme.accent : .white)
            }
        }
    }

    private var continueWatchingProgressRatio: Double? {
        guard isContinueWatchingContext else { return nil }
        let sortedFiles = movieFiles.enumerated().sorted {
            MediaNameParser.episodeSortKey(for: $0.element.fileName, fallbackIndex: $0.offset) <
            MediaNameParser.episodeSortKey(for: $1.element.fileName, fallbackIndex: $1.offset)
        }.map(\.element)
        let unfinishedFiles = sortedFiles.filter { file in
            guard file.duration > 0 else { return false }
            let ratio = file.playProgress / file.duration
            return file.playProgress > 5.0 && ratio < 0.95
        }
        guard let targetFile = unfinishedFiles.max(by: { lhs, rhs in
            (lhs.lastPlayedAt ?? 0) < (rhs.lastPlayedAt ?? 0)
        }) ?? unfinishedFiles.first else {
            return nil
        }
        let duration = targetFile.duration
        let progress = min(max(targetFile.playProgress, 0), duration)
        return min(max(progress / duration, 0), 1)
    }
    
    private func refreshCardState() {
        refreshTask?.cancel()
        refreshTask = Task {
            do {
                let snapshot = try await AppDatabase.shared.dbQueue.read { db -> (pairs: [(VideoFile, MediaSource?)], douban: DoubanMetadata?) in
                    let pairs = try VideoFile.fetchVisibleSourcePairs(movieId: movie.id, in: db)
                    let douban = try movie.id.flatMap { try DoubanMetadata.fetchOne(db, key: $0) }
                    return (pairs, douban)
                }
                if Task.isCancelled { return }
                let files = snapshot.pairs.map(\.0)
                let allFilesWatched = !files.isEmpty && files.allSatisfy { file in
                    file.duration > 0 && (file.playProgress / file.duration) >= 0.95
                }
                let missingFiles = evaluateMissingState(with: snapshot.pairs)
                await MainActor.run {
                    self.movieFiles = files
                    self.sourcePairs = snapshot.pairs
                    self.doubanMetadata = snapshot.douban?.isInvalidPlaceholder == true ? nil : snapshot.douban
                    self.isFullyWatched = allFilesWatched
                    self.hasMissingFiles = missingFiles
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
                        file.lastPlayedAt = nil
                        try file.update(db)
                    }
                }
                await MainActor.run { NotificationCenter.default.post(name: .libraryUpdated, object: nil) }
            } catch {}
        }
    }
    
    private func fetchFilesAndStartCache() {
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
                        cacheManager.cacheStatusMessage = "该媒体源暂不支持离线缓存"
                    } else {
                        cacheManager.startDownloads(files: cacheableFiles, groupTitle: movie.title)
                    }
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

}
