import SwiftUI
import GRDB

// 🌟 高级剧集菜单的数据模型
struct EpisodeItem: Identifiable {
    let id: String
    let file: VideoFile
    let season: Int
    let episode: Int
    let displayName: String
}

struct MovieDetailView: View {
    let movie: Movie
    @Environment(\.dismiss) var dismiss
    @AppStorage("seekDuration") var seekDuration: Int = 10
    
    @AppStorage("appTheme") var appTheme = ThemeType.appleLight.rawValue
    var theme: AppTheme { ThemeType(rawValue: appTheme)?.colors ?? ThemeType.appleLight.colors }
    @AppStorage("enableFastTooltip") var enableFastTooltip = true
    
    // 🌟 修复：在这里声明管家，让详情页全局都能认识它！
    @ObservedObject var cacheManager = OfflineCacheManager.shared
    
    @State private var videoFiles: [VideoFile] = []
    @State private var currentVideoFileId: String? = nil
    
    @State private var allEpisodes: [EpisodeItem] = []
    @State private var availableSeasons: [Int] = []
    @State private var selectedSeason: Int = 1
    @State private var isTVShow: Bool = false
    @State private var cacheSupportByFileId: [String: Bool] = [:]
    @State private var loadFileDetailsTask: Task<Void, Never>? = nil
    
    @State private var localPoster: NSImage? = nil
    @State private var displayMovieTitle: String
    @State private var showSeasonCacheAlert = false // 修复：整季缓存弹窗状态
    @State private var playbackAlertMessage = ""
    @State private var showPlaybackAlert = false
    
    init(movie: Movie) {
        self.movie = movie
        _displayMovieTitle = State(initialValue: movie.title)
    }
    
    // 删除了原有的 posterURLString 变量，因为我们现在有了超强的 CachedPosterView
    
    var mainFile: VideoFile? {
        videoFiles.first(where: { $0.id == currentVideoFileId }) ?? videoFiles.first
    }
    
    var allEpisodesForSelectedSeason: [EpisodeItem] {
        allEpisodes.filter { $0.season == selectedSeason }
    }
    
    private var episodeGridColumns: [GridItem] {
        [GridItem(.adaptive(minimum: 260, maximum: 320), spacing: 24, alignment: .top)]
    }
    
    var body: some View {
        ZStack(alignment: .topLeading) {
            // 1. 毛玻璃背景
            GeometryReader { geo in
                Group {
                    if let img = localPoster {
                        Image(nsImage: img).resizable().aspectRatio(contentMode: .fill)
                    } else {
                        theme.background
                    }
                }
                .frame(width: geo.size.width, height: geo.size.height).clipped()
                .blur(radius: 80, opaque: true)
                .overlay(.regularMaterial)
                .overlay(theme.background.opacity(0.6))
            }.ignoresSafeArea()
            
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    // 2. 左右分栏的高级信息区
                    HStack(alignment: .top, spacing: 45) {
                        
                        // 🌟 核心升级：详情页的左侧海报也使用具有智能自愈功能的组件！
                                                CachedPosterView(posterPath: movie.posterPath)
                                                    .frame(width: 260, height: 390) // 🌟 修复：明确给定 2:3 比例的高度 (260 * 1.5)
                                                    .clipped() // 🌟 修复：防止内部的 fill 模式溢出
                                                    .cornerRadius(12)
                                                    .shadow(color: theme.textSecondary.opacity(0.15), radius: 25, y: 15)
                        
                        VStack(alignment: .leading, spacing: 16) {
                            Text(displayMovieTitle).font(.system(size: 46, weight: .heavy)).foregroundColor(theme.textPrimary).lineLimit(2)
                            
                            HStack(spacing: 16) {
                                if let date = movie.releaseDate, date.count >= 4 { Text(String(date.prefix(4))) }
                                if let vote = movie.voteAverage, vote > 0 { HStack(spacing: 4) { Image(systemName: "star.fill"); Text(String(format: "%.1f", vote)) }.foregroundColor(Color(hex: "FFAC00")) }
                                Text("TV-14").padding(.horizontal, 6).padding(.vertical, 2).background(theme.surface).cornerRadius(4)
                            }.font(.title3.bold()).foregroundColor(theme.textSecondary)
                            
                            Text(movie.overview ?? "暂无简介").font(.body).foregroundColor(theme.textPrimary.opacity(0.85)).lineSpacing(8).padding(.top, 10).padding(.bottom, 10)
                            
                            // 3. 播放控制条
                            if let file = mainFile {
                                let mainPlayProgress = file.playProgress
                                let mainVideoDuration = file.duration
                                let isWatched = mainVideoDuration > 0 && (mainPlayProgress / mainVideoDuration) >= 0.95
                                let currentBtnLabel = getPlayButtonLabel(fileId: file.id, progress: mainPlayProgress)
                                
                                HStack(spacing: 20) {
                                    Button(action: { attemptPlayback(for: file) }) {
                                        HStack(spacing: 8) {
                                            Image(systemName: "play.fill").font(.title3)
                                            Text(currentBtnLabel)
                                                .font(.title3.bold())
                                                .lineLimit(1)
                                                .fixedSize(horizontal: true, vertical: false)
                                        }
                                            .padding(.horizontal, 28).padding(.vertical, 12)
                                            .foregroundColor(theme.accent)
                                            .background(Capsule().fill(theme.accent.opacity(0.1)))
                                            .overlay(Capsule().stroke(theme.accent, lineWidth: 1.5))
                                    }.buttonStyle(.plain)
                                    
                                    Button(action: { toggleWatchedStatus(for: file.id) }) {
                                        HStack(spacing: 8) {
                                            Image(systemName: isWatched ? "checkmark.circle.fill" : "circle").font(.title3)
                                            Text(isWatched ? "已播" : "未播")
                                                .font(.title3.bold())
                                                .lineLimit(1)
                                                .fixedSize(horizontal: true, vertical: false)
                                        }
                                            .padding(.horizontal, 24).padding(.vertical, 12)
                                            .foregroundColor(theme.textPrimary)
                                            .background(Capsule().fill(theme.surface.opacity(0.5)))
                                            .overlay(Capsule().stroke(theme.textSecondary.opacity(0.2), lineWidth: 1))
                                    }.buttonStyle(.plain)
                                    
                                    if mainVideoDuration > 0 && mainPlayProgress > 0 && !isWatched {
                                        HStack(spacing: 12) {
                                            GeometryReader { geo in
                                                ZStack(alignment: .leading) {
                                                    Rectangle().fill(theme.surface).frame(height: 4).cornerRadius(2)
                                                    Rectangle().fill(theme.accent).frame(width: max(0, min(geo.size.width, geo.size.width * CGFloat(mainPlayProgress / mainVideoDuration))), height: 4).cornerRadius(2)
                                                }
                                            }.frame(width: 140, height: 4)
                                            Text("\(formatTime(mainPlayProgress)) / \(formatTime(mainVideoDuration))").font(.caption.monospacedDigit()).foregroundColor(theme.textSecondary).lineLimit(1).fixedSize()
                                        }.padding(.leading, 10)
                                    }
                                }
                            }
                        }
                    }
                    .padding(.top, 120).padding(.horizontal, 60)
                    
                    // 4. 分集选择器
                    if isTVShow && !allEpisodes.isEmpty {
                        VStack(alignment: .leading, spacing: 20) {
                            HStack(spacing: 15) {
                                Menu {
                                    ForEach(availableSeasons, id: \.self) { s in
                                        Button(s == 0 ? "特别篇" : "第 \(s) 季") { selectedSeason = s }
                                    }
                                } label: {
                                    HStack(spacing: 8) {
                                        Text(selectedSeason == 0 ? "特别篇" : "第 \(selectedSeason) 季").font(.title2.bold())
                                        Image(systemName: "chevron.down").font(.body.bold())
                                    }.foregroundColor(theme.textPrimary)
                                }
                                .menuStyle(.borderlessButton)
                                .menuIndicator(.hidden)
                                .fixedSize()
                                
                                // 整季缓存专属按钮
                                if cacheManager.isCacheModeActive {
                                    Button(action: { showSeasonCacheAlert = true }) {
                                        Image(systemName: "icloud.and.arrow.down")
                                            .font(.system(size: 14, weight: .bold))
                                            .foregroundColor(theme.accent)
                                            .padding(8)
                                            .background(theme.accent.opacity(0.1))
                                            .clipShape(Circle())
                                    }
                                    .buttonStyle(.plain)
                                    .conditionalHelp("缓存当前选择的整季", show: enableFastTooltip)
                                }
                            }
                            .padding(.horizontal, 60)
                            .alert("缓存第 \(selectedSeason) 季", isPresented: $showSeasonCacheAlert) {
                                Button("取消", role: .cancel) { }
                                Button("确定缓存") { cacheSelectedSeason() }
                            } message: {
                                Text("确定要将本季的所有剧集加入后台离线缓存队列吗？")
                            }
                            
                            LazyVGrid(columns: episodeGridColumns, alignment: .leading, spacing: 24) {
                                ForEach(allEpisodesForSelectedSeason) { ep in
                                    EpisodeCardView(
                                        movieId: movie.id,
                                        movieTitle: $displayMovieTitle,
                                        ep: ep,
                                        currentVideoFileId: $currentVideoFileId,
                                        localPoster: localPoster,
                                        isCacheSupported: cacheSupportByFileId[ep.file.id] ?? false
                                    )
                                }
                            }
                            .padding(.horizontal, 60)
                            .padding(.bottom, 60)
                        }
                        .padding(.top, 60)
                    } else {
                        Spacer().frame(height: 100) // 修复：完美闭合，解决大括号报错！
                    }
                }
            }
            
            // 返回按钮
            Button(action: { dismiss() }) {
                Image(systemName: "chevron.left").font(.body.bold()).padding(14).background(theme.background.opacity(0.8)).foregroundColor(theme.textPrimary).clipShape(Circle()).shadow(color: theme.textSecondary.opacity(0.1), radius: 5, y: 3)
            }
            .buttonStyle(.plain).padding(.top, 25).padding(.leading, 30)
            
            // 详情页右上角快捷切换缓存模式
            VStack {
                HStack {
                    Spacer()
                    Button(action: { withAnimation { cacheManager.isCacheModeActive.toggle() } }) {
                        Image(systemName: cacheManager.isCacheModeActive ? "icloud.fill" : "icloud")
                            .font(.title3.bold())
                            .padding(14)
                            .background(theme.background.opacity(0.8))
                            .foregroundColor(cacheManager.isCacheModeActive ? theme.accent : theme.textPrimary)
                            .clipShape(Circle())
                            .shadow(color: theme.textSecondary.opacity(0.1), radius: 5, y: 3)
                    }
                    .buttonStyle(.plain).padding(.top, 25).padding(.trailing, 30)
                }
                Spacer()
            }
        }
        .navigationBarBackButtonHidden(true)
        .alert("无法播放", isPresented: $showPlaybackAlert) {
            Button("知道了", role: .cancel) { }
        } message: {
            Text(playbackAlertMessage)
        }
        .onAppear {
            // 🌟 核心修复：使用新的无沙盒抓取逻辑作为毛玻璃背景
            if let path = movie.posterPath,
               let localURL = PosterManager.shared.getLocalPosterURL(for: path),
               let data = try? Data(contentsOf: localURL),
               let img = NSImage(data: data) {
                self.localPoster = img
            }
            loadFileDetails()
        }
        // 🌟 监听海报下载完成，刷新毛玻璃背景
        .onReceive(NotificationCenter.default.publisher(for: NSNotification.Name("PosterUpdated_\((movie.posterPath ?? "").replacingOccurrences(of: "/", with: ""))"))) { _ in
            if let path = movie.posterPath,
               let localURL = PosterManager.shared.getLocalPosterURL(for: path),
               let data = try? Data(contentsOf: localURL),
               let img = NSImage(data: data) {
                self.localPoster = img
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: .libraryUpdated)) { _ in
            loadFileDetails(preservingCurrentSelection: true)
        }
        .onDisappear {
            loadFileDetailsTask?.cancel()
            loadFileDetailsTask = nil
        }
    }
    
    private func getPlayButtonLabel(fileId: String, progress: Double) -> String {
        let prefix = progress > 5.0 ? "继续播放" : "开始播放"
        if isTVShow, let ep = allEpisodes.first(where: { $0.id == fileId }) {
            return "\(prefix) \(ep.displayName)"
        }
        return prefix
    }

    private func seasonSortPriority(_ season: Int) -> Int {
        season == 0 ? Int.max : season
    }
    
    private func loadFileDetails(preservingCurrentSelection: Bool = false) {
        let preservedSeason = preservingCurrentSelection ? selectedSeason : nil
        let preservedFileId = preservingCurrentSelection ? currentVideoFileId : nil
        loadFileDetailsTask?.cancel()
        loadFileDetailsTask = Task {
            do {
                let sourcePairs = try await AppDatabase.shared.dbQueue.read { db in
                    try VideoFile.fetchVisibleSourcePairs(movieId: movie.id, in: db)
                }
                if Task.isCancelled { return }
                let files = sourcePairs.map(\.0)
                let sortedFiles = files.enumerated().sorted {
                    let lhsDetailKey = MediaNameParser.episodeSortKey(for: $0.element.fileName, fallbackIndex: $0.offset).2
                    let rhsDetailKey = MediaNameParser.episodeSortKey(for: $1.element.fileName, fallbackIndex: $1.offset).2
                    let lhsResolved = EpisodeMetadataOverrideStore.shared.resolvedEpisodeInfo(
                        fileId: $0.element.id,
                        fileName: $0.element.fileName,
                        fallbackIndex: $0.offset
                    )
                    let rhsResolved = EpisodeMetadataOverrideStore.shared.resolvedEpisodeInfo(
                        fileId: $1.element.id,
                        fileName: $1.element.fileName,
                        fallbackIndex: $1.offset
                    )
                    return (seasonSortPriority(lhsResolved.season), lhsResolved.episode, lhsDetailKey) <
                        (seasonSortPriority(rhsResolved.season), rhsResolved.episode, rhsDetailKey)
                }.map(\.element)
                var episodes: [EpisodeItem] = []
                var isShow = false
                
                for (index, file) in sortedFiles.enumerated() {
                    let parsed = MediaNameParser.parseEpisodeDescriptor(from: file.fileName, fallbackIndex: index)
                    let resolvedInfo = EpisodeMetadataOverrideStore.shared.resolvedEpisodeInfo(
                        fileId: file.id,
                        fileName: file.fileName,
                        fallbackIndex: index
                    )
                    let s = resolvedInfo.season
                    let e = resolvedInfo.episode
                    var dName = resolvedInfo.displayName
                    if parsed.isTVShow {
                        isShow = true
                    } else if displayMovieTitle.contains("季") || displayMovieTitle.contains("集") {
                        isShow = true
                        dName = "第 \(e) 集"
                    } else {
                        dName = sortedFiles.count > 1 ? "部分 \(index + 1)" : "正片"
                    }
                    episodes.append(EpisodeItem(id: file.id, file: file, season: s, episode: e, displayName: dName))
                }
                
                let seasons = Array(Set(episodes.map { $0.season })).sorted {
                    seasonSortPriority($0) < seasonSortPriority($1)
                }
                let sortedEpisodes = episodes
                var nextUpEp = sortedEpisodes.first
                for ep in sortedEpisodes { let progress = ep.file.duration > 0 ? (ep.file.playProgress / ep.file.duration) : 0; if progress < 0.95 { nextUpEp = ep; break } }
                
                await MainActor.run {
                    guard !Task.isCancelled else { return }
                    self.videoFiles = sortedEpisodes.map(\.file); self.allEpisodes = sortedEpisodes; self.availableSeasons = seasons; self.isTVShow = isShow
                    self.cacheSupportByFileId = Dictionary(
                        uniqueKeysWithValues: sourcePairs.map { pair in
                            (pair.0.id, cacheManager.supportsCaching(mediaSource: pair.1))
                        }
                    )
                    if let preservedSeason, seasons.contains(preservedSeason) {
                        self.selectedSeason = preservedSeason
                        if let preservedFileId, sortedEpisodes.contains(where: { $0.id == preservedFileId }) {
                            self.currentVideoFileId = preservedFileId
                        }
                    } else if let preservedFileId, let preservedEpisode = sortedEpisodes.first(where: { $0.id == preservedFileId }) {
                        self.selectedSeason = preservedEpisode.season
                        self.currentVideoFileId = preservedEpisode.id
                    } else if let target = nextUpEp {
                        self.selectedSeason = target.season
                        self.currentVideoFileId = target.id
                    } else if let first = sortedEpisodes.first {
                        self.selectedSeason = first.season
                        self.currentVideoFileId = first.id
                    } else {
                        self.selectedSeason = 1
                        self.currentVideoFileId = nil
                    }
                }
            } catch { }
        }
    }
    
    private func toggleWatchedStatus(for fileId: String) {
        Task { do { try await AppDatabase.shared.dbQueue.write { db in if var file = try VideoFile.fetchOne(db, key: fileId) { let isWatched = file.duration > 0 && (file.playProgress / file.duration) >= 0.95; if isWatched { file.playProgress = 0 } else { file.playProgress = file.duration > 0 ? file.duration : 100; if file.duration == 0 { file.duration = 100 } }; try file.update(db) } }; DispatchQueue.main.async { NotificationCenter.default.post(name: .libraryUpdated, object: nil) } } catch { } }
    }
    
    private func cacheSelectedSeason() {
        let episodesToCache = allEpisodes.filter { $0.season == selectedSeason }
        var hasUnsupported = false
        for ep in episodesToCache {
            if !(cacheSupportByFileId[ep.file.id] ?? false) {
                hasUnsupported = true
                continue
            }
            if !OfflineCacheManager.shared.isCached(ep.file) {
                OfflineCacheManager.shared.startDownload(file: ep.file)
            }
        }
        if hasUnsupported {
            OfflineCacheManager.shared.cacheStatusMessage = "部分剧集来自远程源，已跳过离线缓存"
        }
    }
    
    private func attemptPlayback(for file: VideoFile) {
        Task {
            do {
                let source = try await AppDatabase.shared.dbQueue.read { db in
                    try file.request(for: VideoFile.mediaSource).fetchOne(db)
                }
                let isMissing = OfflineCacheManager.shared.hasMissingSource(for: file, mediaSource: source)
                await MainActor.run {
                    if source?.isEnabled == false {
                        playbackAlertMessage = "该媒体源已关闭，请重新开启后再播放。"
                        showPlaybackAlert = true
                    } else if isMissing {
                        playbackAlertMessage = "文件不存在。请重新连接外置硬盘/NAS，或先将该视频缓存到本机后再播放。"
                        showPlaybackAlert = true
                    } else {
                        DirectPlaybackWindowManager.shared.open(
                            .init(
                                movie: effectiveMovie,
                                fileId: file.id,
                                initialSourceBasePath: source?.baseUrl,
                                initialSourceProtocolType: source?.protocolType,
                                initialSourceAuthConfig: source?.authConfig,
                                initialPlaylistFiles: videoFiles
                            )
                        )
                    }
                }
            } catch {
                await MainActor.run {
                    playbackAlertMessage = "读取文件信息失败：\(error.localizedDescription)"
                    showPlaybackAlert = true
                }
            }
        }
    }
    
    private func formatTime(_ time: Double) -> String { if time.isNaN || time < 0 { return "00:00" }; let t = Int(time); return t / 3600 > 0 ? String(format: "%02d:%02d:%02d", t/3600, (t%3600)/60, t%60) : String(format: "%02d:%02d", (t%3600)/60, t%60) }

    private var effectiveMovie: Movie {
        var currentMovie = movie
        currentMovie.title = displayMovieTitle
        return currentMovie
    }
}

struct EpisodeCardView: View {
    let movieId: Int64?
    @Binding var movieTitle: String
    let ep: EpisodeItem
    @Binding var currentVideoFileId: String?
    let localPoster: NSImage?
    let isCacheSupported: Bool
    
    @AppStorage("appTheme") var appTheme = ThemeType.appleLight.rawValue
    var theme: AppTheme { ThemeType(rawValue: appTheme)?.colors ?? ThemeType.appleLight.colors }
    @ObservedObject var cacheManager = OfflineCacheManager.shared
    @State private var isHovering = false
    @State private var showEditModal = false
    
    private let cardWidth: CGFloat = 260
    private let thumbnailHeight: CGFloat = 156
    
    var body: some View {
        let duration = ep.file.duration
        let progress = duration > 0 ? (ep.file.playProgress / duration) : 0
        let isEpWatched = duration > 0 && progress >= 0.95
        let isPartiallyWatched = ep.file.playProgress > 5.0 && progress < 0.95
        let isSelected = currentVideoFileId == ep.file.id
        
        let maskOpacity: Double = isEpWatched ? 0.06 : (isSelected ? 0.12 : (isPartiallyWatched ? 0.2 : 0.3))
        let cardBrightness: Double = isEpWatched ? 0.12 : -0.12
        let isCached = cacheManager.isCached(ep.file)
        let downloadProgress = cacheManager.downloadProgress[ep.file.id]
        let isDownloading = downloadProgress != nil
        
        VStack(alignment: .leading, spacing: 10) {
            ZStack(alignment: .topTrailing) {
                Button(action: { currentVideoFileId = ep.file.id }) {
                    ZStack {
                        EpisodeThumbnailView(
                            fileId: ep.file.id,
                            fallbackImage: localPoster,
                            width: cardWidth,
                            height: thumbnailHeight
                        )
                            .brightness(cardBrightness)
                        Color.black.opacity(maskOpacity)
                        if isSelected { RoundedRectangle(cornerRadius: 10).stroke(theme.accent, lineWidth: 1.5) }
                    }
                    .frame(width: cardWidth, height: thumbnailHeight)
                    .clipShape(RoundedRectangle(cornerRadius: 10))
                }.buttonStyle(.plain)

                HStack(spacing: 8) {
                    if isHovering {
                        Button(action: { showEditModal = true }) {
                            Image(systemName: "pencil.circle.fill")
                                .font(.title2)
                                .foregroundColor(.white)
                                .shadow(radius: 3)
                        }
                        .buttonStyle(.plain)
                        .transition(.opacity)
                    }

                    if cacheManager.isCacheModeActive {
                        Button(action: {
                            if isCached { cacheManager.deleteCache(fileId: ep.file.id, fileName: ep.file.fileName) }
                            else if isCacheSupported && !isDownloading { cacheManager.startDownload(file: ep.file) }
                        }) {
                            ZStack {
                                Circle().fill(theme.surface.opacity(0.85)).frame(width: 28, height: 28).shadow(color: .black.opacity(0.15), radius: 3)
                                if isDownloading { ProgressView().progressViewStyle(CircularProgressViewStyle(tint: theme.accent)).scaleEffect(0.6) }
                                else if isCached { Image(systemName: "checkmark.icloud.fill").font(.system(size: 14, weight: .bold)).foregroundColor(theme.accent) }
                                else if !isCacheSupported { Image(systemName: "icloud.slash").font(.system(size: 14, weight: .bold)).foregroundColor(theme.textSecondary) }
                                else { Image(systemName: "icloud.and.arrow.down").font(.system(size: 14, weight: .bold)).foregroundColor(theme.textPrimary) }
                            }
                        }
                        .buttonStyle(.plain)
                        .disabled(!isCacheSupported)
                    }
                }
                .padding(8)
            }
            .onHover { isHovering = $0 }
            
            Text(ep.displayName)
                .font(.subheadline.bold())
                .foregroundColor(theme.textPrimary.opacity(isSelected ? 1.0 : 0.8))
                .lineLimit(2)
                .frame(width: cardWidth, alignment: .center)
                .multilineTextAlignment(.center)
            
            if duration > 0 && !isEpWatched && ep.file.playProgress > 0 {
                GeometryReader { geo in
                    ZStack(alignment: .leading) {
                        Rectangle().fill(theme.surface).frame(height: 4).cornerRadius(2)
                        Rectangle().fill(theme.accent).frame(width: max(0, min(geo.size.width, geo.size.width * CGFloat(progress))), height: 4).cornerRadius(2)
                    }
                }.frame(width: cardWidth, height: 4)
            } else { Spacer().frame(height: 4) }
        }
        .sheet(isPresented: $showEditModal) {
            EpisodeThumbnailEditModalView(movieId: movieId, movieTitle: $movieTitle, episode: ep)
        }
    }
}

struct EpisodeThumbnailView: View {
    let fileId: String
    let fallbackImage: NSImage?
    let width: CGFloat
    let height: CGFloat
    @State private var thumbnail: NSImage? = nil
    
    @AppStorage("appTheme") var appTheme = ThemeType.appleLight.rawValue
    var theme: AppTheme { ThemeType(rawValue: appTheme)?.colors ?? ThemeType.appleLight.colors }
    
    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 10)
                .fill(theme.surface.opacity(0.32))

            if let img = thumbnail {
                Image(nsImage: img)
                    .resizable()
                    .scaledToFill()
                    .frame(width: width, height: height)
                    .clipped()
            }
            else if let img = fallbackImage {
                Image(nsImage: img)
                    .resizable()
                    .scaledToFill()
                    .frame(width: width, height: height)
                    .blur(radius: 10)
                    .overlay(theme.background.opacity(0.5))
                    .clipped()
            }
            else { Rectangle().fill(theme.surface).overlay(Image(systemName: "photo").foregroundColor(theme.textSecondary.opacity(0.5))) }
        }
        .frame(width: width, height: height)
        .clipped()
        .onAppear { loadThumbnail() }
        .onReceive(NotificationCenter.default.publisher(for: NSNotification.Name("ThumbnailGenerated_\(fileId)"))) { _ in loadThumbnail() }
    }
    
    private func loadThumbnail() {
        let url = ThumbnailManager.shared.thumbnailURL(for: fileId)
        if let img = NSImage(contentsOf: url) { self.thumbnail = img }
    }
}
