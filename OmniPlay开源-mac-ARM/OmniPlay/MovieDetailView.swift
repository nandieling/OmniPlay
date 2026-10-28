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
    @State private var displayedMovie: Movie
    @Environment(\.dismiss) var dismiss
    @AppStorage("seekDuration") var seekDuration: Int = 10
    
    @AppStorage(AppThemeResolver.colorKey) var appTheme = ThemeType.appleLight.rawValue
    @AppStorage(AppThemeResolver.styleKey) var appThemeStyle = ThemeStyle.classic.rawValue
    var theme: AppTheme { AppThemeResolver.theme(colorRaw: appTheme, styleRaw: appThemeStyle) }
    @AppStorage("enableFastTooltip") var enableFastTooltip = true
    
    // 🌟 修复：在这里声明管家，让详情页全局都能认识它！
    @ObservedObject var cacheManager = OfflineCacheManager.shared
    
    @State private var videoFiles: [VideoFile] = []
    @State private var currentVideoFileId: String? = nil
    
    @State private var allEpisodes: [EpisodeItem] = []
    @State private var availableSeasons: [Int] = []
    @State private var selectedSeason: Int = 1
    @State private var isTVShow: Bool = false
    @State private var isDetailCacheModeActive = false
    @State private var cacheSupportByFileId: [String: Bool] = [:]
    @State private var pendingCacheCancellation: [VideoFile] = []
    @State private var showCancelCacheConfirmation = false
    @State private var loadFileDetailsTask: Task<Void, Never>? = nil
    
    @State private var localPoster: NSImage? = nil
    @State private var displayMovieTitle: String
    @State private var playbackAlertMessage = ""
    @State private var showPlaybackAlert = false
    @State private var doubanMetadata: DoubanMetadata? = nil
    @State private var isShowingNeodbMark = false
    
    init(movie: Movie) {
        self.movie = movie
        _displayedMovie = State(initialValue: movie)
        _displayMovieTitle = State(initialValue: movie.title)
    }

    private var activeMovieId: Int64? { displayedMovie.id ?? movie.id }
    
    // 删除了原有的 posterURLString 变量，因为我们现在有了超强的 CachedPosterView
    
    var mainFile: VideoFile? {
        if let currentVideoFileId,
           let selected = videoFiles.first(where: { $0.id == currentVideoFileId }) {
            return selected
        }

        let unfinished = videoFiles
            .filter { file in
                guard file.playProgress > 0 else { return false }
                return file.duration <= 0 || file.playProgress / file.duration < 0.95
            }
            .max { lhs, rhs in
                (lhs.lastPlayedAt ?? 0) < (rhs.lastPlayedAt ?? 0)
            }
        return unfinished ?? videoFiles.first
    }

    private var isMultipartMovie: Bool {
        !isTVShow && videoFiles.count > 1
    }

    private var multipartMovieTimeline: (progress: Double, duration: Double) {
        var progress = 0.0
        var duration = 0.0
        for file in videoFiles {
            guard file.duration > 0 else { continue }
            duration += file.duration
            let watched = file.playProgress / file.duration >= 0.95
            progress += watched ? file.duration : min(max(file.playProgress, 0), file.duration)
        }
        return (progress, duration)
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
                // 冲淡海报的一层跟随主题：玻璃主题下就是那层斜向渐变底，换主题底色跟着变
                .overlay { theme.backdropLayer.opacity(theme.heroDimming) }
            }.ignoresSafeArea()
            
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    // 2. 左右分栏的高级信息区
                    HStack(alignment: .top, spacing: 45) {
                        
                        ZStack {
                            // 🌟 核心升级：详情页的左侧海报也使用具有智能自愈功能的组件！
                            CachedPosterView(posterPath: displayedMovie.posterPath)
                                .frame(width: 260, height: 390) // 🌟 修复：明确给定 2:3 比例的高度 (260 * 1.5)
                                .clipped() // 🌟 修复：防止内部的 fill 模式溢出
                                .cornerRadius(12)
                                .shadow(color: theme.textSecondary.opacity(0.15), radius: 25, y: 15)

                            if isDetailCacheModeActive {
                                Button(action: {
                                    if aggregateCacheProgress(for: videoFiles) != nil {
                                        requestCacheCancellation(for: videoFiles)
                                    } else {
                                        cacheEntireTitle()
                                    }
                                }) {
                                    cacheOverlayContent(for: videoFiles, downloadTitle: "离线缓存整部影片或整部剧集")
                                }
                                .buttonStyle(.plain)
                                .disabled(isCacheActionDisabled(for: videoFiles))
                                .conditionalHelp(cacheHelpText(for: videoFiles, defaultText: "离线缓存整部影片或整部剧集"), show: enableFastTooltip)
                            }
                        }
                        
                        VStack(alignment: .leading, spacing: 16) {
                            HStack(alignment: .firstTextBaseline, spacing: 14) {
                                Text(displayMovieTitle).font(.system(size: 46, weight: .heavy)).foregroundColor(theme.textPrimary).lineLimit(2)

                                // NeoDB 标记按钮挪到下面「开始播放 / 已播未播」那一排的末尾
                            }
                            
                            HStack(spacing: 16) {
                                if let year = preferredYear { Text(year) }
                                if let vote = displayedMovie.voteAverage, vote > 0 {
                                    HStack(spacing: 4) {
                                        Image(systemName: "star.fill")
                                        Text(String(format: "%.1f", vote))
                                    }
                                        .foregroundColor(Color(hex: "FFAC00"))
                                }
                                if let rating = doubanMetadata?.rating, rating > 0 {
                                    HStack(spacing: 4) {
                                        Image(systemName: "star.fill")
                                        Text(String(format: "%.1f", rating))
                                    }
                                        .foregroundColor(Color(hex: "00B51D"))
                                }
                                ForEach(fusedTextMetadataParts, id: \.self) { part in
                                    Text(part)
                                }
                            }
                            .font(.title3.bold())
                            .foregroundColor(theme.textSecondary)
                            
                            VStack(alignment: .leading, spacing: 8) {
                                if !fusedDirectorLine.isEmpty {
                                    HStack(alignment: .top, spacing: 10) {
                                        Text("导演")
                                            .font(.subheadline.weight(.semibold))
                                            .foregroundColor(theme.textSecondary)
                                            .frame(width: 34, alignment: .leading)
                                        Text(fusedDirectorLine)
                                            .font(.subheadline)
                                            .foregroundColor(theme.textPrimary)
                                    }
                                }
                                if !fusedCastLine.isEmpty {
                                    HStack(alignment: .top, spacing: 10) {
                                        Text("主演")
                                            .font(.subheadline.weight(.semibold))
                                            .foregroundColor(theme.textSecondary)
                                            .frame(width: 34, alignment: .leading)
                                        Text(fusedCastLine)
                                            .font(.subheadline)
                                            .foregroundColor(theme.textPrimary)
                                    }
                                }
                            }
                            
                            Text(fusedOverview)
                                .font(.body)
                                .foregroundColor(theme.textPrimary.opacity(0.85))
                                .lineSpacing(8)
                                .fixedSize(horizontal: false, vertical: true)
                                .padding(.top, 10)
                                .padding(.bottom, 10)
                            
                            // 3. 播放控制条
                            if let file = mainFile {
                                let timeline = isMultipartMovie ? multipartMovieTimeline : (progress: file.playProgress, duration: file.duration)
                                let mainPlayProgress = timeline.progress
                                let mainVideoDuration = timeline.duration
                                let currentBtnLabel = getPlayButtonLabel(fileId: file.id, progress: mainPlayProgress)
                                
                                mainPlaybackControls(
                                    file: file,
                                    buttonLabel: currentBtnLabel,
                                    progress: mainPlayProgress,
                                    duration: mainVideoDuration
                                )
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
                                if isDetailCacheModeActive {
                                    HStack(spacing: 10) {
                                        Button(action: {
                                            let seasonFiles = allEpisodesForSelectedSeason.map(\.file)
                                            if aggregateCacheProgress(for: seasonFiles) != nil {
                                                requestCacheCancellation(for: seasonFiles)
                                            } else {
                                                cacheSelectedSeason()
                                            }
                                        }) {
                                            cacheOverlayContent(for: allEpisodesForSelectedSeason.map(\.file), downloadTitle: "缓存当前选择的整季", compact: true)
                                        }
                                        .buttonStyle(.plain)
                                        .disabled(isCacheActionDisabled(for: allEpisodesForSelectedSeason.map(\.file)))
                                        .conditionalHelp(cacheHelpText(for: allEpisodesForSelectedSeason.map(\.file), defaultText: "缓存当前选择的整季"), show: enableFastTooltip)

                                        if let progress = aggregateCacheProgress(for: allEpisodesForSelectedSeason.map(\.file)) {
                                            ProgressView(value: progress)
                                                .progressViewStyle(.linear)
                                                .tint(theme.accent)
                                                .frame(width: 120)
                                        }
                                    }
                                }
                            }
                            .padding(.horizontal, 60)
                            LazyVGrid(columns: episodeGridColumns, alignment: .leading, spacing: 24) {
                                ForEach(allEpisodesForSelectedSeason) { ep in
                                    EpisodeCardView(
                                        movieId: activeMovieId,
                                        movieTitle: $displayMovieTitle,
                                        ep: ep,
                                        currentVideoFileId: $currentVideoFileId,
                                        localPoster: localPoster,
                                        isCacheSupported: cacheSupportByFileId[ep.file.id] ?? false,
                                        isDetailCacheModeActive: isDetailCacheModeActive
                                    )
                                }
                            }
                            .padding(.horizontal, 60)
                            .padding(.bottom, 60)
                        }
                        .padding(.top, 60)
                    } else {
                        if versionGroups.count > 1 {
                            movieVersionList
                                .padding(.horizontal, 60)
                                .padding(.top, 44)
                        }
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
                    Button(action: { withAnimation { isDetailCacheModeActive.toggle() } }) {
                        Image(systemName: isDetailCacheModeActive ? "icloud.fill" : "icloud")
                            .font(.title3.bold())
                            .padding(14)
                            .background(theme.background.opacity(0.8))
                            .foregroundColor(isDetailCacheModeActive ? theme.accent : theme.textPrimary)
                            .clipShape(Circle())
                            .shadow(color: theme.textSecondary.opacity(0.1), radius: 5, y: 3)
                    }
                    .buttonStyle(.plain).padding(.top, 25).padding(.trailing, 30)
                }
                Spacer()
            }
        }
        .sheet(isPresented: $isShowingNeodbMark) {
            NeodbMarkSheet(localId: activeMovieId ?? 0, title: displayMovieTitle)
        }
        .navigationBarBackButtonHidden(true)
        .modifier(RemoveNavTitleModifier())
        .alert("无法播放", isPresented: $showPlaybackAlert) {
            Button("知道了", role: .cancel) { }
        } message: {
            Text(playbackAlertMessage)
        }
        .confirmationDialog("取消离线缓存？", isPresented: $showCancelCacheConfirmation, titleVisibility: .visible) {
            Button("取消缓存", role: .destructive) {
                cacheManager.cancelDownloads(files: pendingCacheCancellation)
                pendingCacheCancellation = []
            }
            Button("继续缓存", role: .cancel) {
                pendingCacheCancellation = []
            }
        } message: {
            Text("已缓存的内容会保留，未完成的文件将被删除。")
        }
        .onAppear {
            // 🌟 核心修复：使用新的无沙盒抓取逻辑作为毛玻璃背景
            refreshLocalPoster()
            refreshMovieMetadata(preferredFileId: nil, followFileAssociation: false)
        }
        // 🌟 监听海报下载完成，刷新毛玻璃背景
        .onReceive(NotificationCenter.default.publisher(for: NSNotification.Name("PosterUpdated_\(PosterManager.shared.cacheFileName(for: displayedMovie.posterPath ?? ""))"))) { _ in
            refreshLocalPoster()
        }
        .onReceive(NotificationCenter.default.publisher(for: .libraryUpdated)) { notification in
            let preferredFileId = notification.userInfo?[LibraryUpdateUserInfoKey.preferredVideoFileId] as? String
            let replacedMovieId = notification.userInfo?[LibraryUpdateUserInfoKey.replacedMovieId] as? Int64
            let updatedMovieId = notification.userInfo?[LibraryUpdateUserInfoKey.updatedMovieId] as? Int64
            let followsCurrentScrape = replacedMovieId == activeMovieId
                || updatedMovieId == activeMovieId
                || preferredFileId.map { id in videoFiles.contains(where: { $0.id == id }) } == true
            refreshMovieMetadata(
                preferredFileId: preferredFileId,
                followFileAssociation: followsCurrentScrape,
                preservingCurrentSelection: true
            )
        }
        .onDisappear {
            loadFileDetailsTask?.cancel()
            loadFileDetailsTask = nil
        }
    }
    
    private func getPlayButtonLabel(fileId: String, progress: Double) -> String {
        let duration = isMultipartMovie ? multipartMovieTimeline.duration : (videoFiles.first { $0.id == fileId }?.duration ?? 0)
        let state = PlaybackWatchState(progress: progress, duration: duration)
        let prefix = state.hasResumeProgress ? "继续播放" : "开始播放"
        if isTVShow, let ep = allEpisodes.first(where: { $0.id == fileId }) {
            return "\(prefix) \(ep.displayName)"
        }
        return prefix
    }

    private var fusedTextMetadataParts: [String] {
        var parts: [String] = []
        let genres = Array(doubanMetadata?.genreList.prefix(3) ?? [])
        if !genres.isEmpty { parts.append(genres.joined(separator: " / ")) }
        let countries = Array(doubanMetadata?.countryList.prefix(2) ?? [])
        if !countries.isEmpty { parts.append(countries.joined(separator: " / ")) }
        return parts
    }

    private var fusedDirectorLine: String {
        let fromMovie = displayedMovie.directorList
        if !fromMovie.isEmpty { return fromMovie.joined(separator: "、") }
        return (doubanMetadata?.directorList ?? []).joined(separator: "、")
    }

    private var fusedCastLine: String {
        let fromMovie = displayedMovie.castList
        let list = fromMovie.isEmpty ? (doubanMetadata?.castList ?? []) : fromMovie
        return list.prefix(6).joined(separator: "、")
    }

    private var preferredYear: String? {
        if let year = doubanMetadata?.year, !year.isEmpty { return year }
        if let date = displayedMovie.releaseDate, date.count >= 4 { return String(date.prefix(4)) }
        return nil
    }

    private var fusedOverview: String {
        if let overview = displayedMovie.overview?.trimmingCharacters(in: .whitespacesAndNewlines), !overview.isEmpty {
            return overview
        }
        if let summary = doubanMetadata?.summary?.trimmingCharacters(in: .whitespacesAndNewlines), !summary.isEmpty {
            return summary
        }
        return "暂无简介"
    }

    private func loadDoubanMetadata() {
        guard let movieId = activeMovieId else { return }
        Task {
            let metadata = try? await AppDatabase.shared.dbQueue.read { db in
                try DoubanMetadata.fetchOne(db, key: movieId)
            }
            await MainActor.run {
                self.doubanMetadata = metadata?.isInvalidPlaceholder == true ? nil : metadata
            }
        }
    }

    private func refreshMovieMetadata(
        preferredFileId: String?,
        followFileAssociation: Bool,
        preservingCurrentSelection: Bool = false
    ) {
        let currentMovieId = activeMovieId
        Task {
            let refreshed = try? await AppDatabase.shared.dbQueue.read { db -> Movie? in
                if followFileAssociation,
                   let preferredFileId,
                   let associatedMovieId = try VideoFile.fetchOne(db, key: preferredFileId)?.movieId,
                   let associatedMovie = try Movie.fetchOne(db, key: associatedMovieId) {
                    return associatedMovie
                }
                guard let currentMovieId else { return nil }
                return try Movie.fetchOne(db, key: currentMovieId)
            }
            await MainActor.run {
                if let refreshed {
                    displayedMovie = refreshed
                    displayMovieTitle = refreshed.title
                }
                refreshLocalPoster()
                loadFileDetails(
                    preservingCurrentSelection: preservingCurrentSelection,
                    preferredFileId: followFileAssociation ? preferredFileId : nil
                )
                loadDoubanMetadata()
            }
        }
    }

    private func refreshLocalPoster() {
        guard let path = displayedMovie.posterPath,
              let localURL = PosterManager.shared.getLocalPosterURL(for: path),
              let data = try? Data(contentsOf: localURL),
              let image = NSImage(data: data) else {
            localPoster = nil
            return
        }
        localPoster = image
    }

    private func seasonSortPriority(_ season: Int) -> Int {
        season == 0 ? Int.max : season
    }

    private func episodeBaseDisplayName(season: Int, episode: Int) -> String {
        season == 0 ? "特别篇第\(episode)集" : "第\(season)季第\(episode)集"
    }

    private func applyingDuplicateEpisodeSuffixes(
        to episodes: [EpisodeItem],
        eligibleEpisodeIDs: Set<String>,
        explicitSubtitleFileIDs: Set<String>
    ) -> [EpisodeItem] {
        let duplicateGroups = Dictionary(
            grouping: episodes.filter { eligibleEpisodeIDs.contains($0.id) },
            by: { "\($0.season)#\($0.episode)" }
        ).filter { $0.value.count > 1 }

        var suffixesByFileID: [String: String] = [:]
        for group in duplicateGroups.values {
            let candidates = group.filter { !explicitSubtitleFileIDs.contains($0.id) }
            let suffixes = episodePrefixDisambiguationSuffixes(for: candidates)
            suffixesByFileID.merge(suffixes) { current, _ in current }
        }

        return episodes.map { episode in
            guard let suffix = suffixesByFileID[episode.id],
                  !suffix.isEmpty else {
                return episode
            }
            return EpisodeItem(
                id: episode.id,
                file: episode.file,
                season: episode.season,
                episode: episode.episode,
                displayName: "\(episodeBaseDisplayName(season: episode.season, episode: episode.episode)) · \(suffix)"
            )
        }
    }

    private func episodePrefixDisambiguationSuffixes(for episodes: [EpisodeItem]) -> [String: String] {
        guard episodes.count > 1 else { return [:] }
        let tokenSets = episodes.map { episode in
            (id: episode.id, tokens: episodePrefixTokens(from: episode.file.fileName))
        }
        guard tokenSets.contains(where: { !$0.tokens.isEmpty }) else { return [:] }

        let allTokens = tokenSets.map(\.tokens)
        let prefixCount = commonPrefixTokenCount(allTokens)
        let suffixCount = commonSuffixTokenCount(allTokens, afterCommonPrefix: prefixCount)

        var result: [String: String] = [:]
        for item in tokenSets {
            let end = max(prefixCount, item.tokens.count - suffixCount)
            guard prefixCount < end else { continue }
            let differenceTokens = Array(item.tokens[prefixCount..<end])
            if let title = preferredEpisodeDifferenceTitle(from: differenceTokens) {
                result[item.id] = title
            }
        }
        return result
    }

    private func episodePrefixTokens(from fileName: String) -> [String] {
        let stem = ((fileName as NSString).lastPathComponent as NSString).deletingPathExtension
        guard let markerRange = episodeMarkerRange(in: stem) else { return [] }
        let prefix = String(stem[..<markerRange.lowerBound])
        let normalized = prefix
            .replacingOccurrences(of: #"[\\[\\]\\(\\)\\{\\}【】（）《》「」『』〔〕〖〗,:：]+"#, with: " ", options: .regularExpression)
            .replacingOccurrences(of: #"[._\-–—]+"#, with: " ", options: .regularExpression)
            .replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !normalized.isEmpty else { return [] }
        return normalized
            .split(separator: " ")
            .map(String.init)
            .filter { !isEpisodePrefixNoiseToken($0) }
    }

    private func episodeMarkerRange(in stem: String) -> Range<String.Index>? {
        let patterns = [
            #"[sS]\d{1,2}[eE][pP]?\d{1,3}"#,
            #"[eE][pP]?\d{1,3}"#,
            #"第\s*\d{1,3}\s*[集话]"#
        ]
        for pattern in patterns {
            if let range = stem.range(of: pattern, options: .regularExpression) {
                return range
            }
        }
        return nil
    }

    private func commonPrefixTokenCount(_ tokenSets: [[String]]) -> Int {
        guard let first = tokenSets.first, !first.isEmpty else { return 0 }
        var count = 0
        while count < first.count {
            let token = normalizedEpisodeDifferenceToken(first[count])
            guard tokenSets.allSatisfy({ $0.count > count && normalizedEpisodeDifferenceToken($0[count]) == token }) else {
                break
            }
            count += 1
        }
        return count
    }

    private func commonSuffixTokenCount(_ tokenSets: [[String]], afterCommonPrefix prefixCount: Int) -> Int {
        var count = 0
        while true {
            var candidate: String?
            for tokens in tokenSets {
                let index = tokens.count - count - 1
                guard index >= prefixCount else { return count }
                let token = normalizedEpisodeDifferenceToken(tokens[index])
                if let candidate {
                    guard candidate == token else { return count }
                } else {
                    candidate = token
                }
            }
            count += 1
        }
    }

    private func preferredEpisodeDifferenceTitle(from tokens: [String]) -> String? {
        let chineseTokens = tokens.filter { $0.range(of: #"\p{Han}"#, options: .regularExpression) != nil }
        if !chineseTokens.isEmpty {
            let title = chineseTokens.joined().trimmingCharacters(in: .whitespacesAndNewlines)
            return title.isEmpty ? nil : title
        }

        let englishTokens = tokens.filter {
            $0.range(of: #"[A-Za-z]"#, options: .regularExpression) != nil &&
            !isEpisodePrefixNoiseToken($0)
        }
        let title = englishTokens.joined(separator: " ").trimmingCharacters(in: .whitespacesAndNewlines)
        return title.isEmpty ? nil : title
    }

    private func normalizedEpisodeDifferenceToken(_ token: String) -> String {
        token.lowercased().trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private func isEpisodePrefixNoiseToken(_ token: String) -> Bool {
        let lower = token.lowercased().trimmingCharacters(in: .whitespacesAndNewlines)
        if lower.isEmpty { return true }
        if lower.range(of: #"^(19|20)\d{2}$"#, options: .regularExpression) != nil { return true }
        if lower.range(of: #"^\d+$"#, options: .regularExpression) != nil { return true }
        if lower.range(of: #"^\d+(\.\d+)?$"#, options: .regularExpression) != nil { return true }
        if lower.range(of: #"^(\d{3,4}p|[48]k|uhd|fhd|hd|sd)$"#, options: .regularExpression) != nil { return true }
        if lower.range(of: #"^(8|10|12)bit$"#, options: .regularExpression) != nil { return true }
        if lower.range(of: #"^(x|h)?26[45]$"#, options: .regularExpression) != nil { return true }
        if lower.range(of: #"^h\.?(264|265)$"#, options: .regularExpression) != nil { return true }
        if lower.range(of: #"^(aac|ac3|eac3|ddp|dts|truehd|lpcm|flac|mp3|opus)\d*(\.\d+)?$"#, options: .regularExpression) != nil { return true }
        let releaseTokens: Set<String> = [
            "blu", "ray", "bluray", "bdrip", "web", "dl", "webdl", "webrip", "hdtv", "uhdtv",
            "remux", "avc", "hevc", "hdr", "dv", "dovi", "sdr", "hdr10", "hdr10plus",
            "atmos", "nf", "netflix", "amzn", "amazon", "dsnp", "disney", "hulu", "atvp",
            "max", "proper", "repack", "internal"
        ]
        return releaseTokens.contains(lower)
    }

    private func hasUnfinishedPlaybackProgress(_ file: VideoFile) -> Bool {
        PlaybackWatchState(progress: file.playProgress, duration: file.duration) == .inProgress
    }

    private func isNotFullyWatched(_ file: VideoFile) -> Bool {
        guard file.duration > 0 else { return true }
        return (file.playProgress / file.duration) < 0.95
    }

    private func mostRecentUnfinishedEpisode(in episodes: [EpisodeItem]) -> EpisodeItem? {
        let unfinishedEpisodes = episodes.filter { hasUnfinishedPlaybackProgress($0.file) }
        return unfinishedEpisodes.max { lhs, rhs in
            (lhs.file.lastPlayedAt ?? 0) < (rhs.file.lastPlayedAt ?? 0)
        }
    }

    private func nextUpEpisode(in season: Int, from episodes: [EpisodeItem]) -> EpisodeItem? {
        let seasonEpisodes = episodes.filter { $0.season == season }
        return mostRecentUnfinishedEpisode(in: seasonEpisodes)
            ?? seasonEpisodes.first { isNotFullyWatched($0.file) }
            ?? seasonEpisodes.first
    }
    
    private func loadFileDetails(preservingCurrentSelection: Bool = false, preferredFileId: String? = nil) {
        guard let movieId = activeMovieId else { return }
        let preservedSeason = preservingCurrentSelection ? selectedSeason : nil
        let preservedFileId = preservingCurrentSelection ? currentVideoFileId : nil
        let explicitPreferredFileId = preferredFileId?.trimmingCharacters(in: .whitespacesAndNewlines)
        loadFileDetailsTask?.cancel()
        loadFileDetailsTask = Task {
            do {
                let sourcePairs = try await AppDatabase.shared.dbQueue.read { db in
                    try VideoFile.fetchVisibleSourcePairs(movieId: movieId, in: db)
                }
                if Task.isCancelled { return }
                let files = sourcePairs.map(\.0)
                let sortedFiles = files.enumerated().sorted {
                    let lhsParsed = MediaNameParser.parseEpisodeInfo(from: $0.element.fileName, fallbackIndex: $0.offset)
                    let rhsParsed = MediaNameParser.parseEpisodeInfo(from: $1.element.fileName, fallbackIndex: $1.offset)
                    if lhsParsed.isTVShow || rhsParsed.isTVShow {
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
                    }
                    return MediaNameParser.playbackSortPrecedes(
                        lhsRelativePath: $0.element.relativePath,
                        lhsFileName: $0.element.fileName,
                        lhsFallbackIndex: $0.offset,
                        rhsRelativePath: $1.element.relativePath,
                        rhsFileName: $1.element.fileName,
                        rhsFallbackIndex: $1.offset
                    )
                }.map(\.element)
                var episodes: [EpisodeItem] = []
                var eligibleEpisodeIDs = Set<String>()
                var explicitSubtitleFileIDs = Set<String>()
                var isShow = false
                // 短剧分集标题用「第 N 集」，集号优先按文件名里的数字认；
                // 目录带季号的多季短剧（S07 / 第7季）保留季号，不然分季信息就没了
                let isShortDramaMovie = sortedFiles.contains { $0.mediaKind == ShortDrama.kindValue }
                
                for (index, file) in sortedFiles.enumerated() {
                    let parsed = MediaNameParser.parseEpisodeDescriptor(from: file.fileName, fallbackIndex: index)
                    let override = EpisodeMetadataOverrideStore.shared.override(for: file.id)
                    let preferredSeason = override == nil
                        ? MediaNameParser.resolvePreferredSeason(
                            from: file.relativePath,
                            fileName: file.fileName,
                            fallbackIndex: index
                        )
                        : nil
                    var s = override?.season ?? preferredSeason ?? parsed.season
                    var e = override?.episode ?? parsed.episode
                    var dName = episodeBaseDisplayName(season: s, episode: e)
                    if isShortDramaMovie {
                        isShow = true
                        eligibleEpisodeIDs.insert(file.id)
                    }
                    if isShortDramaMovie, let numbered = ShortDramaClassifier.episodeNumber(from: file.fileName) {
                        e = numbered
                        if let shortSeason = ShortDramaClassifier.seasonNumber(inPath: file.relativePath) {
                            s = shortSeason
                            dName = episodeBaseDisplayName(season: shortSeason, episode: numbered)
                        } else {
                            dName = ShortDramaClassifier.episodeDisplayName(episode: numbered)
                        }
                    }
                    if parsed.isTVShow || isShortDramaMovie {
                        isShow = true
                        eligibleEpisodeIDs.insert(file.id)
                        if let subtitle = override?.subtitle?.trimmingCharacters(in: .whitespacesAndNewlines), !subtitle.isEmpty {
                            dName += " · \(subtitle)"
                            explicitSubtitleFileIDs.insert(file.id)
                        }
                    } else if displayMovieTitle.contains("季") || displayMovieTitle.contains("集") {
                        isShow = true
                        eligibleEpisodeIDs.insert(file.id)
                    } else {
                        dName = sortedFiles.count > 1 ? "部分 \(index + 1)" : "正片"
                    }
                    episodes.append(EpisodeItem(id: file.id, file: file, season: s, episode: e, displayName: dName))
                }
                
                let seasons = Array(Set(episodes.map { $0.season })).sorted {
                    seasonSortPriority($0) < seasonSortPriority($1)
                }
                let sortedEpisodes = applyingDuplicateEpisodeSuffixes(
                    to: episodes,
                    eligibleEpisodeIDs: eligibleEpisodeIDs,
                    explicitSubtitleFileIDs: explicitSubtitleFileIDs
                )
                let resumeEp = mostRecentUnfinishedEpisode(in: sortedEpisodes)
                let nextUnwatchedEp = sortedEpisodes.first { isNotFullyWatched($0.file) }
                let nextUpEp = resumeEp ?? nextUnwatchedEp ?? sortedEpisodes.first
                
                await MainActor.run {
                    guard !Task.isCancelled else { return }
                    self.videoFiles = sortedEpisodes.map(\.file); self.allEpisodes = sortedEpisodes; self.availableSeasons = seasons; self.isTVShow = isShow
                    self.cacheSupportByFileId = Dictionary(
                        uniqueKeysWithValues: sourcePairs.map { pair in
                            (pair.0.id, cacheManager.supportsCaching(mediaSource: pair.1))
                        }
                    )
                    if let explicitPreferredFileId,
                       !explicitPreferredFileId.isEmpty,
                       let preferredEpisode = sortedEpisodes.first(where: { $0.id == explicitPreferredFileId }) {
                        self.selectedSeason = preferredEpisode.season
                        self.currentVideoFileId = preferredEpisode.id
                    } else if let preservedSeason, seasons.contains(preservedSeason) {
                        self.selectedSeason = preservedSeason
                        if let preservedFileId,
                           let preservedEpisode = sortedEpisodes.first(where: { $0.id == preservedFileId && $0.season == preservedSeason }),
                           (hasUnfinishedPlaybackProgress(preservedEpisode.file)
                            || nextUpEpisode(in: preservedSeason, from: sortedEpisodes) == nil) {
                            self.currentVideoFileId = preservedEpisode.id
                        } else {
                            self.currentVideoFileId = nextUpEpisode(in: preservedSeason, from: sortedEpisodes)?.id
                        }
                    } else if let preservedFileId, let preservedEpisode = sortedEpisodes.first(where: { $0.id == preservedFileId }) {
                        let nextUp = nextUpEp
                        if hasUnfinishedPlaybackProgress(preservedEpisode.file) || nextUp == nil {
                            self.selectedSeason = preservedEpisode.season
                            self.currentVideoFileId = preservedEpisode.id
                        } else {
                            self.selectedSeason = nextUp?.season ?? preservedEpisode.season
                            self.currentVideoFileId = nextUp?.id ?? preservedEpisode.id
                        }
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
        Task { do { try await AppDatabase.shared.dbQueue.write { db in if var file = try VideoFile.fetchOne(db, key: fileId) { let isWatched = file.duration > 0 && (file.playProgress / file.duration) >= 0.95; if isWatched { file.playProgress = 0 } else { file.playProgress = file.duration > 0 ? file.duration : 100; if file.duration == 0 { file.duration = 100 } }; file.lastPlayedAt = nil; try file.update(db) } }; DispatchQueue.main.async { NotificationCenter.default.post(name: .libraryUpdated, object: nil) } } catch { } }
    }
    
    private func cacheSelectedSeason() {
        let episodesToCache = allEpisodes.filter { $0.season == selectedSeason }
        let supportedFiles = episodesToCache.map(\.file).filter { cacheSupportByFileId[$0.id] ?? false }
        var hasUnsupported = false
        for ep in episodesToCache {
            if !(cacheSupportByFileId[ep.file.id] ?? false) {
                hasUnsupported = true
            }
        }
        let filesToCache = supportedFiles.filter { !OfflineCacheManager.shared.isCached($0) }
        if !filesToCache.isEmpty {
            OfflineCacheManager.shared.startDownloads(files: filesToCache, groupTitle: "\(displayedMovie.title) 第 \(selectedSeason) 季")
        }
        if hasUnsupported {
            OfflineCacheManager.shared.cacheStatusMessage = "部分剧集的媒体源暂不支持离线缓存，已跳过"
        }
    }

    private func cacheEntireTitle() {
        let supportedFiles = videoFiles.filter { cacheSupportByFileId[$0.id] ?? false }
        let filesToCache = supportedFiles.filter { !OfflineCacheManager.shared.isCached($0) }
        guard !filesToCache.isEmpty else {
            OfflineCacheManager.shared.cacheStatusMessage = supportedFiles.isEmpty ? "该媒体源暂不支持离线缓存" : "所选内容已在本地缓存中"
            return
        }
        OfflineCacheManager.shared.startDownloads(files: filesToCache, groupTitle: displayedMovie.title)
    }

    private func requestCacheCancellation(for files: [VideoFile]) {
        let activeFiles = files.filter { cacheManager.progress(for: $0) != nil }
        guard !activeFiles.isEmpty else { return }
        pendingCacheCancellation = activeFiles
        showCancelCacheConfirmation = true
    }
    
    private func attemptPlayback(for file: VideoFile) {
        guard let movieId = activeMovieId else { return }
        Task {
            do {
                let snapshot = try await AppDatabase.shared.dbQueue.read { db -> (VideoFile, MediaSource?, [VideoFile]) in
                    let current = try VideoFile.fetchOne(db, key: file.id) ?? file
                    let source = try current.request(for: VideoFile.mediaSource).fetchOne(db)
                    let files = try VideoFile.fetchVisibleFiles(movieId: movieId, in: db)
                    return (current, source, files)
                }

                let selectedFile = snapshot.0

                let selectedSourceId = selectedFile.sourceId
                let source = try await AppDatabase.shared.dbQueue.read { db in
                    try MediaSource.fetchOne(db, key: selectedSourceId)
                }
                let playlistFiles = try await AppDatabase.shared.dbQueue.read { db in
                    try VideoFile.fetchVisibleFiles(movieId: movieId, in: db)
                        .filter { $0.sourceId == selectedSourceId }
                }
                let isMissing = OfflineCacheManager.shared.hasMissingSource(for: selectedFile, mediaSource: source)
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
                                fileId: selectedFile.id,
                                initialSourceBasePath: source?.baseUrl,
                                initialSourceProtocolType: source?.protocolType,
                                initialSourceAuthConfig: source?.authConfig,
                                initialPlaylistFiles: playlistFiles
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

    /// 版本列表第一行的文件名：取路径最后一段，拿不到时退回记录里的文件名
    private func versionRowTitle(for file: VideoFile) -> String {
        let name = (file.relativePath as NSString).lastPathComponent
        if !name.isEmpty { return name }
        return file.fileName.isEmpty ? "未知文件" : file.fileName
    }

    private func formatTime(_ time: Double) -> String { if time.isNaN || time < 0 { return "00:00" }; let t = Int(time); return t / 3600 > 0 ? String(format: "%02d:%02d:%02d", t/3600, (t%3600)/60, t%60) : String(format: "%02d:%02d", (t%3600)/60, t%60) }

    /// 详情页版本行：同一张蓝光盘（同一 BDMV 下的多条 STREAM）算一个版本
    private struct MovieVersionGroup {
        let title: String
        let files: [VideoFile]
        let isBluRayPackage: Bool

        var primary: VideoFile { files[0] }
        var segmentCount: Int { files.count }

        var timeline: (progress: Double, duration: Double, played: Bool) {
            var progress = 0.0
            var duration = 0.0
            for file in files where file.duration > 0 {
                duration += file.duration
                let watched = file.playProgress / file.duration >= 0.95
                progress += watched ? file.duration : min(max(file.playProgress, 0), file.duration)
            }
            if files.count == 1, duration == 0, let only = files.first {
                return (only.playProgress, only.duration, only.playProgress > 5)
            }
            return (progress, duration, files.contains { $0.playProgress > 5 })
        }
    }

    private var versionGroups: [MovieVersionGroup] {
        var keys: [String] = []
        var buckets: [String: [VideoFile]] = [:]
        for file in videoFiles {
            let key = MediaNameParser.bluRayPackageKey(relativePath: file.relativePath).map { "bdmv:\($0)" } ?? "file:\(file.id)"
            if buckets[key] == nil { keys.append(key) }
            buckets[key, default: []].append(file)
        }
        return keys.compactMap { key -> MovieVersionGroup? in
            guard let bucket = buckets[key], !bucket.isEmpty else { return nil }
            let isPackage = key.hasPrefix("bdmv:")
            let files = isPackage ? bucket.sorted { lhs, rhs in
                let lhsKey = MediaNameParser.bluRayStreamPlaybackSortKey(for: lhs.fileName)
                let rhsKey = MediaNameParser.bluRayStreamPlaybackSortKey(for: rhs.fileName)
                if lhsKey.number != rhsKey.number { return lhsKey.number < rhsKey.number }
                return lhsKey.name < rhsKey.name
            } : bucket
            guard let head = files.first else { return nil }
            let title = isPackage
                ? (MediaNameParser.bluRayPackageTitle(relativePath: head.relativePath) ?? versionRowTitle(for: head))
                : versionRowTitle(for: head)
            return MovieVersionGroup(title: title, files: files, isBluRayPackage: isPackage)
        }
    }

    private var movieVersionList: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("版本 / 文件（\(versionGroups.count)）")
                .font(.title2.bold())
                .foregroundColor(theme.textPrimary)

            ForEach(versionGroups, id: \.primary.id) { group in
                let isSelected = group.files.contains { $0.id == currentVideoFileId }
                let timeline = group.timeline
                Button(action: { currentVideoFileId = group.primary.id }) {
                    HStack(alignment: .top, spacing: 14) {
                        Image(systemName: isSelected ? "checkmark.circle.fill" : "circle")
                            .foregroundColor(isSelected ? theme.accent : theme.textSecondary)
                            .font(.title3)
                        VStack(alignment: .leading, spacing: 5) {
                            // 蓝光包显示盘名，散片显示文件名
                            Text(group.title)
                                .font(.headline)
                                .foregroundColor(theme.textPrimary)
                                .lineLimit(2)
                            Text("\(formatTime(timeline.progress)) / \(timeline.duration > 0 ? formatTime(timeline.duration) : "--:--") · \(timeline.played ? "已播放" : "未播放")\(group.segmentCount > 1 ? " · 蓝光 \(group.segmentCount) 段" : "")")
                                .font(.caption.monospacedDigit())
                                .foregroundColor(theme.textSecondary)
                        }
                        Spacer(minLength: 0)
                    }
                    .padding(.horizontal, 16)
                    .padding(.vertical, 12)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(theme.rowFill(selected: isSelected))
                    .themeGlassPlate(theme, cornerRadius: 10)
                    .overlay(RoundedRectangle(cornerRadius: 10).stroke(isSelected ? theme.accent : theme.textSecondary.opacity(0.16), lineWidth: isSelected ? 1.5 : 1))
                    .cornerRadius(10)
                }
                .buttonStyle(.plain)
            }
        }
        .frame(maxWidth: 900, alignment: .leading)
    }

    @ViewBuilder
    private func mainPlaybackControls(
        file: VideoFile,
        buttonLabel: String,
        progress: Double,
        duration: Double
    ) -> some View {
        let state = PlaybackWatchState(progress: progress, duration: duration)
        let hasProgressBar = state.hasResumeProgress && duration > 0
        if hasProgressBar {
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 12) {
                    mainPlaybackButtons(file: file, state: state, buttonLabel: buttonLabel)
                    mainPlaybackProgress(progress: progress, duration: duration)
                        .frame(width: 320, alignment: .leading)
                }
                VStack(alignment: .leading, spacing: 12) {
                    mainPlaybackButtons(file: file, state: state, buttonLabel: buttonLabel)
                    mainPlaybackProgress(progress: progress, duration: duration)
                        .frame(maxWidth: 360, alignment: .leading)
                }
            }
        } else {
            mainPlaybackButtons(file: file, state: state, buttonLabel: buttonLabel)
        }
    }

    private func mainPlaybackButtons(file: VideoFile, state: PlaybackWatchState, buttonLabel: String) -> some View {
        HStack(spacing: 12) {
            Button(action: { attemptPlayback(for: file) }) {
                HStack(spacing: 8) {
                    Image(systemName: "play.fill").font(.title3)
                    Text(buttonLabel)
                        .font(.title3.bold())
                        .lineLimit(1)
                        .minimumScaleFactor(0.86)
                }
                .padding(.horizontal, 24)
                .padding(.vertical, 12)
                .foregroundColor(theme.textPrimary)
                .themeGlassPlate(theme, cornerRadius: 999)
                .overlay(Capsule().stroke(theme.textSecondary.opacity(0.2), lineWidth: 1))
            }
            .buttonStyle(.plain)

            Button(action: { toggleWatchedStatus(for: file.id) }) {
                HStack(spacing: 8) {
                    Image(systemName: state == .watched ? "checkmark.circle.fill" : (state == .inProgress ? "circle.lefthalf.filled" : "circle")).font(.title3)
                    Text(state.statusLabel)
                        .font(.title3.bold())
                        .lineLimit(1)
                        .minimumScaleFactor(0.86)
                }
                .padding(.horizontal, 20)
                .padding(.vertical, 12)
                .foregroundColor(theme.textPrimary)
                .background(Capsule().fill(theme.chipFill))
                .themeGlassPlate(theme, cornerRadius: 999)
                .overlay(Capsule().stroke(theme.textSecondary.opacity(0.2), lineWidth: 1))
            }
            .buttonStyle(.plain)

            // NeoDB 标记：样式与左边的已播/未播同款，按钮上只有文字
            if let movieId = activeMovieId, movieId > 0 {
                NeodbMarkStatusButton(localId: movieId, theme: theme) { isShowingNeodbMark = true }
            }
        }
        .fixedSize(horizontal: true, vertical: false)
    }

    private func mainPlaybackProgress(progress: Double, duration: Double) -> some View {
        HStack(spacing: 12) {
            GeometryReader { geo in
                ZStack(alignment: .leading) {
                    Rectangle()
                        .fill(theme.subtleFill)
                        .frame(height: 4)
                        .cornerRadius(2)
                    Rectangle()
                        .fill(theme.accent)
                        .frame(width: max(0, min(geo.size.width, geo.size.width * CGFloat(progress / duration))), height: 4)
                        .cornerRadius(2)
                }
            }
            .frame(minWidth: 120, maxWidth: 220, minHeight: 4, maxHeight: 4)
            Text("\(formatTime(progress)) / \(formatTime(duration))")
                .font(.caption.monospacedDigit())
                .foregroundColor(theme.textSecondary)
                .lineLimit(1)
                .minimumScaleFactor(0.86)
        }
    }

    private func aggregateCacheProgress(for files: [VideoFile]) -> Double? {
        let cacheableFiles = files.filter { cacheSupportByFileId[$0.id] ?? false }
        guard !cacheableFiles.isEmpty else { return nil }
        guard cacheableFiles.contains(where: { cacheManager.progress(for: $0) != nil }) else { return nil }
        let total = cacheableFiles.reduce(0.0) { partial, file in
            if cacheManager.isCached(file) { return partial + 1.0 }
            return partial + (cacheManager.progress(for: file) ?? 0.0)
        }
        return total / Double(cacheableFiles.count)
    }

    private func allCacheableFilesCached(_ files: [VideoFile]) -> Bool {
        let cacheableFiles = files.filter { cacheSupportByFileId[$0.id] ?? false }
        return !cacheableFiles.isEmpty && cacheableFiles.allSatisfy { cacheManager.isCached($0) }
    }

    private func isCacheActionDisabled(for files: [VideoFile]) -> Bool {
        let cacheableFiles = files.filter { cacheSupportByFileId[$0.id] ?? false }
        return cacheableFiles.isEmpty ||
            cacheableFiles.allSatisfy { cacheManager.isCached($0) }
    }

    private func cacheHelpText(for files: [VideoFile], defaultText: String) -> String {
        let cacheableFiles = files.filter { cacheSupportByFileId[$0.id] ?? false }
        if cacheableFiles.isEmpty { return "该媒体源暂不支持离线缓存" }
        if cacheableFiles.contains(where: { cacheManager.progress(for: $0) != nil }) { return "正在离线缓存" }
        if cacheableFiles.allSatisfy({ cacheManager.isCached($0) }) { return "已缓存到本地" }
        return defaultText
    }

    @ViewBuilder
    private func cacheOverlayContent(for files: [VideoFile], downloadTitle: String, compact: Bool = false) -> some View {
        if let progress = aggregateCacheProgress(for: files) {
            OfflineCacheProgressBadge(progress: progress, tint: theme.accent)
                .scaleEffect(compact ? 0.72 : 1.0)
        } else {
            let cacheableFiles = files.filter { cacheSupportByFileId[$0.id] ?? false }
            let allCached = !cacheableFiles.isEmpty && cacheableFiles.allSatisfy { cacheManager.isCached($0) }
            let unsupported = cacheableFiles.isEmpty
            ZStack {
                Circle()
                    .fill(Color.black.opacity(compact ? 0.0 : 0.62))
                    .frame(width: compact ? 34 : 56, height: compact ? 34 : 56)
                Image(systemName: allCached ? "checkmark.circle.fill" : (unsupported ? "icloud.slash" : "arrow.down.circle.fill"))
                    .font(.system(size: compact ? 24 : 28, weight: .bold))
                    .foregroundColor(allCached ? theme.accent : (compact ? theme.accent : .white))
                    .accessibilityLabel(downloadTitle)
            }
        }
    }

    private var effectiveMovie: Movie {
        var currentMovie = displayedMovie
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
    let isDetailCacheModeActive: Bool
    
    @AppStorage(AppThemeResolver.colorKey) var appTheme = ThemeType.appleLight.rawValue
    @AppStorage(AppThemeResolver.styleKey) var appThemeStyle = ThemeStyle.classic.rawValue
    var theme: AppTheme { AppThemeResolver.theme(colorRaw: appTheme, styleRaw: appThemeStyle) }
    @ObservedObject var cacheManager = OfflineCacheManager.shared
    @State private var isHovering = false
    @State private var showEditModal = false
    @State private var showCancelCacheConfirmation = false
    
    private let cardWidth: CGFloat = 260
    private let thumbnailHeight: CGFloat = 156
    
    var body: some View {
        let duration = ep.file.duration
        let progress = duration > 0 ? (ep.file.playProgress / duration) : 0
        let watchState = PlaybackWatchState(progress: ep.file.playProgress, duration: duration)
        let isSelected = currentVideoFileId == ep.file.id
        let isCached = cacheManager.isCached(ep.file)
        
        VStack(alignment: .leading, spacing: 10) {
            ZStack {
                Button(action: { currentVideoFileId = ep.file.id }) {
                    ZStack {
                        EpisodeThumbnailView(
                            fileId: ep.file.id,
                            fallbackImage: localPoster,
                            width: cardWidth,
                            height: thumbnailHeight
                        )
                        if isSelected { RoundedRectangle(cornerRadius: 10).stroke(theme.accent, lineWidth: 1.5) }
                    }
                    .frame(width: cardWidth, height: thumbnailHeight)
                    .clipShape(RoundedRectangle(cornerRadius: 10))
                }.buttonStyle(.plain)

                if isDetailCacheModeActive {
                    Button(action: { cacheEpisode() }) {
                        episodeCacheOverlayContent
                    }
                    .buttonStyle(.plain)
                    .disabled(!isCacheSupported || isCached)
                }

                VStack {
                    HStack(spacing: 8) {
                        Spacer()
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
                    }
                    .padding(8)
                    Spacer()
                }
                .frame(width: cardWidth, height: thumbnailHeight)
            }
            .onHover { isHovering = $0 }
            
            HStack(spacing: 6) {
                Image(systemName: watchState == .watched ? "checkmark.circle.fill" : "circle")
                    .foregroundColor(watchState == .watched ? theme.accent : theme.textSecondary)
                    .accessibilityLabel(watchState == .watched ? "已看" : "未看")
                Text(ep.displayName)
                    .font(.subheadline.bold())
                    .foregroundColor(theme.textPrimary.opacity(isSelected ? 1.0 : 0.8))
                    .lineLimit(2)
            }
            .frame(width: cardWidth, alignment: .center)
            .multilineTextAlignment(.center)
            
            if duration > 0 && watchState == .inProgress {
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
        .confirmationDialog("取消这一集的离线缓存？", isPresented: $showCancelCacheConfirmation, titleVisibility: .visible) {
            Button("取消缓存", role: .destructive) {
                cacheManager.cancelDownloads(files: [ep.file])
            }
            Button("继续缓存", role: .cancel) { }
        } message: {
            Text("未完成的文件将被删除。")
        }
    }

    @ViewBuilder
    private var episodeCacheOverlayContent: some View {
        if let progress = cacheManager.progress(for: ep.file) {
            OfflineCacheProgressBadge(progress: progress, tint: theme.accent)
        } else {
            ZStack {
                Circle()
                    .fill(Color.black.opacity(0.62))
                    .frame(width: 52, height: 52)
                Image(systemName: cacheManager.isCached(ep.file) ? "checkmark.circle.fill" : (!isCacheSupported ? "icloud.slash" : "arrow.down.circle.fill"))
                    .font(.system(size: 25, weight: .bold))
                    .foregroundColor(cacheManager.isCached(ep.file) ? theme.accent : .white)
            }
        }
    }

    private func cacheEpisode() {
        if cacheManager.progress(for: ep.file) != nil {
            showCancelCacheConfirmation = true
            return
        }
        guard isCacheSupported else {
            cacheManager.cacheStatusMessage = "该媒体源暂不支持离线缓存"
            return
        }
        guard !cacheManager.isCached(ep.file) else {
            cacheManager.cacheStatusMessage = "《\(ep.file.fileName)》已在本地缓存中"
            return
        }
        cacheManager.startDownload(file: ep.file)
    }
}

struct EpisodeThumbnailView: View {
    let fileId: String
    let fallbackImage: NSImage?
    let width: CGFloat
    let height: CGFloat
    @State private var thumbnail: NSImage? = nil
    
    @AppStorage(AppThemeResolver.colorKey) var appTheme = ThemeType.appleLight.rawValue
    @AppStorage(AppThemeResolver.styleKey) var appThemeStyle = ThemeStyle.classic.rawValue
    var theme: AppTheme { AppThemeResolver.theme(colorRaw: appTheme, styleRaw: appThemeStyle) }
    
    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 10)
                .fill(theme.placeholderFill)

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
        guard FileManager.default.fileExists(atPath: url.path) else { return }
        // 一百格的分集墙滚动时是每格一次磁盘解码，压在主线线程上就是掉帧，
        // 解码丢给后台执行器，回来只赋个值。
        Task(priority: .userInitiated) {
            let image = await Task.detached(priority: .userInitiated) { NSImage(contentsOf: url) }.value
            self.thumbnail = image
        }
    }
}
