import SwiftUI
import GRDB
import AppKit
import UniformTypeIdentifiers

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
    
    @State private var localPoster: NSImage? = nil
    @State private var showSeasonCacheAlert = false // 修复：整季缓存弹窗状态
    @State private var playbackAlertMessage = ""
    @State private var showPlaybackAlert = false
    @State private var episodeToEdit: EpisodeItem? = nil
    
    init(movie: Movie) {
        self.movie = movie
    }
    
    // 删除了原有的 posterURLString 变量，因为我们现在有了超强的 CachedPosterView
    
    var mainFile: VideoFile? {
        videoFiles.first(where: { $0.id == currentVideoFileId }) ?? videoFiles.first
    }
    
    var allEpisodesForSelectedSeason: [EpisodeItem] {
        allEpisodes
            .filter { $0.season == selectedSeason }
            .sorted {
                if $0.episode != $1.episode { return $0.episode < $1.episode }
                return $0.file.fileName.localizedStandardCompare($1.file.fileName) == .orderedAscending
            }
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
                            Text(movie.title).font(.system(size: 46, weight: .heavy)).foregroundColor(theme.textPrimary).lineLimit(2)
                            
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
                            
                            ScrollView(.horizontal, showsIndicators: false) {
                                HStack(spacing: 24) {
                                    ForEach(allEpisodesForSelectedSeason) { ep in
                                        EpisodeCardView(
                                            movie: movie,
                                            ep: ep,
                                            currentVideoFileId: $currentVideoFileId,
                                            localPoster: localPoster,
                                            isCacheSupported: cacheSupportByFileId[ep.file.id] ?? false,
                                            onEdit: { episodeToEdit = ep }
                                        )
                                    }
                                }
                                .padding(.horizontal, 60).padding(.bottom, 60)
                            }
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
        .sheet(item: $episodeToEdit) { ep in
            EpisodeMetadataEditSheet(movie: movie, ep: ep)
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
        .onReceive(NotificationCenter.default.publisher(for: .libraryUpdated)) { _ in loadFileDetails() }
    }
    
    private func getPlayButtonLabel(fileId: String, progress: Double) -> String {
        let prefix = progress > 5.0 ? "继续播放" : "开始播放"
        if isTVShow, let ep = allEpisodes.first(where: { $0.id == fileId }) {
            let seasonText = ep.season == 0 ? "特别篇" : "第 \(ep.season) 季"
            return "\(prefix) \(seasonText) 第 \(ep.episode) 集"
        }
        return prefix
    }
    
    private func loadFileDetails() {
        Task {
            do {
                let sourcePairs: [(VideoFile, MediaSource)] = try await AppDatabase.shared.dbQueue.read { db -> [(VideoFile, MediaSource)] in
                    let files = try movie.request(for: Movie.videoFiles).fetchAll(db)
                    return try files.compactMap { file -> (VideoFile, MediaSource)? in
                        let source = try file.request(for: VideoFile.mediaSource).fetchOne(db)
                        guard let source else { return nil }
                        return (file, source)
                    }
                }
                let files = sourcePairs.map(\.0)
                var episodes: [EpisodeItem] = []
                var isShow = false
                
                for (index, file) in files.enumerated() {
                    let parsed = MediaNameParser.parseEpisodeInfo(from: file.fileName, fallbackIndex: index)
                    let s = parsed.season
                    let e = parsed.episode
                    var dName = parsed.displayName
                    if parsed.isTVShow {
                        isShow = true
                    } else if movie.title.contains("季") || movie.title.contains("集") {
                        isShow = true
                        dName = "第 \(e) 集"
                    } else {
                        dName = files.count > 1 ? "部分 \(index + 1)" : "正片"
                    }
                    if let customSubtitle = file.customSubtitle?.trimmingCharacters(in: .whitespacesAndNewlines), !customSubtitle.isEmpty {
                        dName = "第 \(e) 集 · \(customSubtitle)"
                    }
                    episodes.append(EpisodeItem(id: file.id, file: file, season: s, episode: e, displayName: dName))
                }
                
                let seasons = Array(Set(episodes.map { $0.season })).sorted { s1, s2 in if s1 == 0 { return false }; if s2 == 0 { return true }; return s1 < s2 }
                let sortedEpisodes = episodes.sorted { e1, e2 in if e1.season != e2.season { if e1.season == 0 { return false }; if e2.season == 0 { return true }; return e1.season < e2.season }; return e1.episode < e2.episode }
                var nextUpEp = sortedEpisodes.first
                for ep in sortedEpisodes { let progress = ep.file.duration > 0 ? (ep.file.playProgress / ep.file.duration) : 0; if progress < 0.95 { nextUpEp = ep; break } }
                
                await MainActor.run {
                    self.videoFiles = sortedEpisodes.map(\.file); self.allEpisodes = sortedEpisodes; self.availableSeasons = seasons; self.isTVShow = isShow
                    self.cacheSupportByFileId = Dictionary(
                        uniqueKeysWithValues: sourcePairs.map { pair in
                            (pair.0.id, cacheManager.supportsCaching(mediaSource: pair.1))
                        }
                    )
                    if let target = nextUpEp { self.selectedSeason = target.season; self.currentVideoFileId = target.id } else if let first = sortedEpisodes.first { self.selectedSeason = first.season; self.currentVideoFileId = first.id }
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
                    if isMissing {
                        playbackAlertMessage = "文件不存在。请重新连接外置硬盘/NAS，或先将该视频缓存到本机后再播放。"
                        showPlaybackAlert = true
                    } else {
                        DirectPlaybackWindowManager.shared.open(
                            .init(
                                movie: movie,
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
}

struct EpisodeCardView: View {
    let movie: Movie
    let ep: EpisodeItem
    @Binding var currentVideoFileId: String?
    let localPoster: NSImage?
    let isCacheSupported: Bool
    let onEdit: () -> Void
    
    @AppStorage("appTheme") var appTheme = ThemeType.appleLight.rawValue
    var theme: AppTheme { ThemeType(rawValue: appTheme)?.colors ?? ThemeType.appleLight.colors }
    @ObservedObject var cacheManager = OfflineCacheManager.shared
    @State private var isHoveringStill = false
    
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
                        EpisodeThumbnailView(fileId: ep.file.id, fallbackImage: localPoster)
                            .brightness(cardBrightness)
                            .frame(width: 260, height: 146)
                            .cornerRadius(10)
                        Color.black.opacity(maskOpacity).cornerRadius(10)
                        if isEpWatched {
                            RoundedRectangle(cornerRadius: 10)
                                .stroke(Color.white.opacity(0.45), lineWidth: 1)
                        }
                        if isSelected { RoundedRectangle(cornerRadius: 10).stroke(theme.accent, lineWidth: 1.5) }
                    }
                }.buttonStyle(.plain)
                
                VStack(spacing: 6) {
                    if isHoveringStill {
                        Button(action: onEdit) {
                            ZStack {
                                Circle().fill(theme.surface.opacity(0.9)).frame(width: 28, height: 28).shadow(color: .black.opacity(0.15), radius: 3)
                                Image(systemName: "pencil").font(.system(size: 13, weight: .bold)).foregroundColor(theme.textPrimary)
                            }
                        }
                        .buttonStyle(.plain)
                        .help("编辑分集信息和剧照")
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
            .onHover { hovering in
                withAnimation(.easeInOut(duration: 0.12)) {
                    isHoveringStill = hovering
                }
            }
            
            let prefixText = ep.season == 0 ? "特别篇" : "第 \(ep.season) 季"
            Text("\(ep.episode). \(movie.title) \(prefixText) \(ep.displayName)").font(.subheadline.bold()).foregroundColor(theme.textPrimary.opacity(isSelected ? 1.0 : 0.8)).lineLimit(1)
            
            if duration > 0 && !isEpWatched && ep.file.playProgress > 0 {
                GeometryReader { geo in
                    ZStack(alignment: .leading) {
                        Rectangle().fill(theme.surface).frame(height: 4).cornerRadius(2)
                        Rectangle().fill(theme.accent).frame(width: max(0, min(geo.size.width, geo.size.width * CGFloat(progress))), height: 4).cornerRadius(2)
                    }
                }.frame(width: 260, height: 4)
            } else { Spacer().frame(height: 4) }
        }
    }
}

struct EpisodeMetadataEditSheet: View {
    let movie: Movie
    let ep: EpisodeItem
    
    @Environment(\.dismiss) private var dismiss
    @State private var customSubtitle: String
    @State private var selectedStillURL: URL?
    @State private var statusMessage: String = ""
    @State private var statusIsError = false
    
    init(movie: Movie, ep: EpisodeItem) {
        self.movie = movie
        self.ep = ep
        _customSubtitle = State(initialValue: ep.file.customSubtitle ?? "")
    }
    
    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("编辑分集信息")
                .font(.title3.bold())
            
            VStack(alignment: .leading, spacing: 8) {
                metadataRow(title: "带路径原文件名", value: ep.file.relativePath)
                metadataRow(title: "刮削名称", value: movie.title)
                metadataRow(title: "分集", value: "\(ep.season == 0 ? "特别篇" : "第 \(ep.season) 季") 第 \(ep.episode) 集")
            }
            
            TextField("自定义副标题（默认留空）", text: $customSubtitle)
                .textFieldStyle(.roundedBorder)
            
            HStack(spacing: 10) {
                Button("选择自定义剧照") {
                    chooseStillImage()
                }
                Text(selectedStillURL?.lastPathComponent ?? "未选择图片")
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
            
            if !statusMessage.isEmpty {
                Text(statusMessage)
                    .font(.caption)
                    .foregroundColor(statusIsError ? .red : .green)
            }
            
            HStack {
                Spacer()
                Button("取消") { dismiss() }
                Button("保存") { saveChanges() }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(22)
        .frame(width: 520)
    }
    
    private func metadataRow(title: String, value: String) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(title)
                .font(.caption)
                .foregroundColor(.secondary)
            Text(value)
                .font(.body)
                .textSelection(.enabled)
                .lineLimit(2)
                .truncationMode(.middle)
        }
    }
    
    private func chooseStillImage() {
        let panel = NSOpenPanel()
        panel.message = "选择自定义分集剧照"
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.allowedContentTypes = [.image]
        if panel.runModal() == .OK {
            selectedStillURL = panel.url
        }
    }
    
    private func saveChanges() {
        let trimmedSubtitle = customSubtitle.trimmingCharacters(in: .whitespacesAndNewlines)
        do {
            try AppDatabase.shared.dbQueue.write { db in
                if var file = try VideoFile.fetchOne(db, key: ep.file.id) {
                    file.customSubtitle = trimmedSubtitle.isEmpty ? nil : trimmedSubtitle
                    try file.update(db)
                }
            }
            
            if let selectedStillURL {
                try saveCustomStill(from: selectedStillURL)
            }
            
            NotificationCenter.default.post(name: NSNotification.Name("ThumbnailGenerated_\(ep.file.id)"), object: nil)
            NotificationCenter.default.post(name: .libraryUpdated, object: nil)
            dismiss()
        } catch {
            statusMessage = error.localizedDescription
            statusIsError = true
        }
    }
    
    private func saveCustomStill(from url: URL) throws {
        let destinationURL = ThumbnailManager.shared.thumbDirectory.appendingPathComponent("\(ep.file.id).jpg")
        try? FileManager.default.removeItem(at: destinationURL)
        if let image = NSImage(contentsOf: url),
           let tiffData = image.tiffRepresentation,
           let bitmap = NSBitmapImageRep(data: tiffData),
           let jpegData = bitmap.representation(using: .jpeg, properties: [.compressionFactor: 0.9]) {
            try jpegData.write(to: destinationURL, options: .atomic)
            return
        }
        try FileManager.default.copyItem(at: url, to: destinationURL)
    }
}

struct EpisodeThumbnailView: View {
    let fileId: String
    let fallbackImage: NSImage?
    @State private var thumbnail: NSImage? = nil
    
    @AppStorage("appTheme") var appTheme = ThemeType.appleLight.rawValue
    var theme: AppTheme { ThemeType(rawValue: appTheme)?.colors ?? ThemeType.appleLight.colors }
    
    var body: some View {
        Group {
            if let img = thumbnail { Image(nsImage: img).resizable().aspectRatio(contentMode: .fill) }
            else if let img = fallbackImage { Image(nsImage: img).resizable().aspectRatio(contentMode: .fill).blur(radius: 10).overlay(theme.background.opacity(0.5)) }
            else { Rectangle().fill(theme.surface).overlay(Image(systemName: "photo").foregroundColor(theme.textSecondary.opacity(0.5))) }
        }
        .onAppear { loadThumbnail() }
        .onReceive(NotificationCenter.default.publisher(for: NSNotification.Name("ThumbnailGenerated_\(fileId)"))) { _ in loadThumbnail() }
    }
    
    private func loadThumbnail() {
        let url = ThumbnailManager.shared.thumbDirectory.appendingPathComponent("\(fileId).jpg")
        if let data = try? Data(contentsOf: url), let img = NSImage(data: data) {
            self.thumbnail = img
        } else {
            self.thumbnail = nil
        }
    }
}
