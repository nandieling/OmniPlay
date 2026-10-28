import SwiftUI
import GRDB
import AppKit

struct DoubanBindModalView: View {
    let movie: Movie
    let existingMetadata: DoubanMetadata?
    let onSaved: (DoubanMetadata) -> Void

    @Environment(\.dismiss) private var dismiss

    init(movie: Movie, existingMetadata: DoubanMetadata?, onSaved: @escaping (DoubanMetadata) -> Void) {
        self.movie = movie
        self.existingMetadata = existingMetadata
        self.onSaved = onSaved
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text("绑定豆瓣条目")
                    .font(.title2.bold())
                Spacer()
                Button(action: { dismiss() }) {
                    Image(systemName: "xmark.circle.fill")
                        .font(.title2)
                        .foregroundColor(.gray)
                }
                .buttonStyle(.plain)
            }
            .padding()
            Divider()

            DoubanBindingPanel(
                movie: movie,
                existingMetadata: existingMetadata,
                onSaved: { metadata in
                    onSaved(metadata)
                    dismiss()
                },
                onRemoved: {
                    NotificationCenter.default.post(name: .libraryUpdated, object: nil)
                    dismiss()
                },
                onCancel: { dismiss() },
                onHongguoBound: { dismiss() }
            )
            .padding(20)
        }
        // 弹层高度跟着内容走：没搜索时紧凑，出候选时把列表撑开
        .frame(width: 560)
        .fixedSize(horizontal: false, vertical: true)
    }
}

struct DoubanBindingPanel: View {
    let movie: Movie
    let existingMetadata: DoubanMetadata?
    let onSaved: (DoubanMetadata) -> Void
    let onRemoved: () -> Void
    var onCancel: (() -> Void)? = nil
    var onHongguoBound: (() -> Void)? = nil
    var showsTitle: Bool = true
    var usesFlexibleSpacer: Bool = true

    @AppStorage(AppThemeResolver.colorKey) var appTheme = ThemeType.appleLight.rawValue
    @AppStorage(AppThemeResolver.styleKey) var appThemeStyle = ThemeStyle.classic.rawValue
    var theme: AppTheme { AppThemeResolver.theme(colorRaw: appTheme, styleRaw: appThemeStyle) }

    @State private var subjectInput: String
    @State private var isFetching = false
    @State private var errorMessage = ""
    @State private var searchHits: [DoubanSearchHit] = []
    @State private var isSearching = false
    @State private var hongguoHits: [HongguoHit] = []
    @State private var isSearchingHongguo = false
    @State private var boundNotice = ""

    init(
        movie: Movie,
        existingMetadata: DoubanMetadata?,
        onSaved: @escaping (DoubanMetadata) -> Void,
        onRemoved: @escaping () -> Void,
        onCancel: (() -> Void)? = nil,
        onHongguoBound: (() -> Void)? = nil,
        showsTitle: Bool = true,
        usesFlexibleSpacer: Bool = true
    ) {
        self.movie = movie
        self.existingMetadata = existingMetadata
        self.onSaved = onSaved
        self.onRemoved = onRemoved
        self.onCancel = onCancel
        self.onHongguoBound = onHongguoBound
        self.showsTitle = showsTitle
        self.usesFlexibleSpacer = usesFlexibleSpacer
        _subjectInput = State(initialValue: existingMetadata?.subjectURL ?? "")
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            if showsTitle {
                Text(movie.title)
                    .font(.headline)
                    .lineLimit(1)
            }

            HStack(spacing: 8) {
                TextField("豆瓣链接或 subject ID，例如 https://movie.douban.com/subject/1292052/", text: $subjectInput)
                    .textFieldStyle(.roundedBorder)
                    .frame(maxWidth: 280)

                Button {
                    openDoubanSearch()
                } label: {
                    Label("在浏览器搜索豆瓣", systemImage: "safari")
                }
                .buttonStyle(.bordered)
                .disabled(isFetching)
                .help("在浏览器打开豆瓣搜索；找到条目后把 subject 链接粘回上面的输入框。")

                Button {
                    bindSubject()
                } label: {
                    if isFetching {
                        ProgressView().controlSize(.small)
                    } else {
                        Text("绑定并抓取")
                    }
                }
                .buttonStyle(.borderedProminent)
                .disabled(isFetching || subjectInput.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }

            // 一条关键词搜索就把候选列出来，省掉「开浏览器—找条目—复制链接—回来粘贴」这一圈
            HStack(spacing: 10) {
                Button {
                    searchDouban()
                } label: {
                    if isSearching {
                        ProgressView().controlSize(.small)
                    } else {
                        Label("搜索豆瓣", systemImage: "magnifyingglass")
                    }
                }
                .buttonStyle(.bordered)
                .disabled(isFetching || isSearching || doubanQuery.isEmpty)
                .help("用片名搜豆瓣候选；分季条目可以在输入框里补上季号或别名再搜一次。豆瓣有频控，一次请求可能要等十几秒。")

                if allowsHongguo {
                    Button {
                        searchHongguo()
                    } label: {
                        if isSearchingHongguo {
                            ProgressView().controlSize(.small)
                        } else {
                            Label("搜索红果", systemImage: "play.rectangle.on.rectangle")
                        }
                    }
                    .buttonStyle(.bordered)
                    .disabled(isFetching || isSearchingHongguo || doubanQuery.isEmpty)
                    .help("国产短剧在红果的库最全：一条搜索就给齐集数、简介与竖版封面。要绑某一季，就在输入框里写全片名带上季号再搜。")
                }

                Spacer(minLength: 8)

                Text(statusLine)
                    .font(.caption)
                    .foregroundColor(theme.textSecondary)
                    .lineLimit(1)
            }

            if !searchHits.isEmpty || !hongguoHits.isEmpty {
                // 候选区高度自己算：弹层是自适应高度的，滚动区不封顶会把整张弹层顶穿屏幕
                ScrollView {
                    VStack(alignment: .leading, spacing: 6) {
                        ForEach(searchHits) { hit in
                            doubanHitRow(hit)
                        }
                        ForEach(hongguoHits) { hit in
                            hongguoHitRow(hit)
                        }
                    }
                }
                .frame(height: candidatesHeight)
            }

            if let existingMetadata {
                HStack(spacing: 8) {
                    Text("当前绑定：\(existingMetadata.title)")
                    if let rating = existingMetadata.rating, rating > 0 {
                        Text(String(format: "豆瓣 %.1f", rating))
                            .foregroundColor(Color(hex: "00B51D"))
                    }
                }
                .font(.caption)
                .foregroundColor(theme.textSecondary)
                .lineLimit(1)
            }

            if !errorMessage.isEmpty {
                Text(errorMessage)
                    .font(.caption)
                    .foregroundColor(theme.dangerColor)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if usesFlexibleSpacer {
                Spacer()
            }

            // 「绑定并抓取」已经跟着输入框走，这里只剩解绑与取消，没东西可摆就不占位置
            if existingMetadata != nil || onCancel != nil {
                HStack {
                    if existingMetadata != nil {
                        Button("解除绑定", role: .destructive) {
                            removeBinding()
                        }
                        .disabled(isFetching)
                    }
                    Spacer()
                    if let onCancel {
                        Button("取消") { onCancel() }
                            .disabled(isFetching)
                    }
                }
            }
        }
    }

    private func openDoubanSearch() {
        let query = doubanQuery
        guard !query.isEmpty,
              let encoded = query.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed),
              let url = URL(string: "https://search.douban.com/movie/subject_search?search_text=\(encoded)") else {
            return
        }
        NSWorkspace.shared.open(url)
    }

    /// 输入框里通常装着已绑定的链接，那种时候用片名去搜更合理。
    private var doubanQuery: String {
        let typed = subjectInput.trimmingCharacters(in: .whitespacesAndNewlines)
        if !typed.isEmpty, DoubanService.shared.normalizedSubject(input: typed) == nil {
            return typed
        }
        return movie.title.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    @ViewBuilder
    private func doubanHitRow(_ hit: DoubanSearchHit) -> some View {
        HStack(spacing: 8) {
            CachedPosterView(posterPath: hit.coverURL)
                .frame(width: 34, height: 50)
                .cornerRadius(4)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(hit.title).font(.subheadline).fontWeight(.medium).lineLimit(1)
                    if let year = hit.year {
                        Text(String(year)).font(.caption).foregroundColor(theme.textSecondary)
                    }
                    if hit.looksLikeShortDrama {
                        Text("短剧")
                            .font(.caption2)
                            .padding(.horizontal, 4).padding(.vertical, 1)
                            .background(theme.accent.opacity(0.15))
                            .cornerRadius(3)
                    }
                }
                HStack(spacing: 6) {
                    if let rating = hit.rating, rating > 0 {
                        Text(String(format: "豆瓣 %.1f", rating))
                            .font(.caption)
                            .foregroundColor(Color(hex: "00B51D"))
                    }
                    if let minutes = hit.episodeMinutes {
                        Text("单集 \(minutes) 分钟").font(.caption).foregroundColor(theme.textSecondary)
                    }
                    if !hit.genres.isEmpty {
                        Text(hit.genres.prefix(3).joined(separator: " / "))
                            .font(.caption)
                            .foregroundColor(theme.textSecondary)
                            .lineLimit(1)
                    }
                }
            }
            Spacer(minLength: 8)
            Button("绑定") { bind(hit) }
                .disabled(isFetching || isSearching)
        }
        .padding(6)
        .background(theme.surface)
        .cornerRadius(6)
    }

    private func bind(_ hit: DoubanSearchHit) {
        subjectInput = hit.subjectURL
        searchHits = []
        bindSubject()
    }

    /// 短剧总开关关掉、或用户不想碰红果时，整块红果入口都不出现。
    private var allowsHongguo: Bool {
        movie.id != nil && ShortDrama.isEnabled() && ShortDrama.hongguoEnabled()
    }

    private var statusLine: String {
        if isSearching { return "正在请求豆瓣，豆瓣闸门最少 12 秒一次…" }
        if isSearchingHongguo { return "正在查询红果…" }
        if !boundNotice.isEmpty { return boundNotice }
        return "豆瓣有频控，请求之间会自动排队。"
    }

    /// 一行候选约 62pt（竖版封面加上下内边距），再加 6pt 间距；超过 320pt 就交给滚动条。
    private var candidatesHeight: CGFloat {
        min(320, CGFloat(searchHits.count + hongguoHits.count) * 68 + 6)
    }

    @ViewBuilder
    private func hongguoHitRow(_ hit: HongguoHit) -> some View {
        HStack(spacing: 8) {
            CachedPosterView(posterPath: hit.coverURL)
                .frame(width: 34, height: 50)
                .cornerRadius(4)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(hit.title).font(.subheadline).fontWeight(.medium).lineLimit(1)
                    if let year = hit.year {
                        Text(String(year)).font(.caption).foregroundColor(theme.textSecondary)
                    }
                    if let count = hit.episodeCount {
                        Text("共 \(count) 集")
                            .font(.caption2)
                            .padding(.horizontal, 4).padding(.vertical, 1)
                            .background(theme.accent.opacity(0.15))
                            .cornerRadius(3)
                    }
                }
                HStack(spacing: 6) {
                    Text("红果").font(.caption).foregroundColor(theme.textSecondary)
                    if let heat = hit.heatText, !heat.isEmpty {
                        Text(heat).font(.caption).foregroundColor(theme.textSecondary)
                    }
                    if !hit.categories.isEmpty {
                        Text(hit.categories.prefix(3).joined(separator: " / "))
                            .font(.caption)
                            .foregroundColor(theme.textSecondary)
                            .lineLimit(1)
                    }
                }
            }
            Spacer(minLength: 8)
            Button("绑定") { bind(hit) }
                .disabled(isFetching || isSearchingHongguo)
        }
        .padding(6)
        .background(theme.surface)
        .cornerRadius(6)
    }

    private func searchHongguo() {
        let query = doubanQuery
        guard !query.isEmpty else { return }
        isSearchingHongguo = true
        errorMessage = ""
        Task {
            do {
                let hits = try await HongguoService.shared.search(query: query)
                let ranked = HongguoService.rankedHits(hits, query: query)
                await MainActor.run {
                    // 同豆瓣那边：对不上片名的候选也留着排后面，手动匹配要能挑冷门条目
                    hongguoHits = Array((ranked + hits.filter { !ranked.contains($0) }).prefix(6))
                    isSearchingHongguo = false
                    if hongguoHits.isEmpty {
                        errorMessage = "红果没有返回候选：可能已下架，或确实没有同名条目。"
                    }
                }
            } catch {
                await MainActor.run {
                    errorMessage = "红果搜索失败：\(error.localizedDescription)"
                    hongguoHits = []
                    isSearchingHongguo = false
                }
            }
        }
    }

    private func bind(_ hit: HongguoHit) {
        guard let movieId = movie.id else { return }
        isFetching = true
        errorMessage = ""
        boundNotice = ""
        Task {
            do {
                try await HongguoBinding.bind(hit, movieId: movieId)
                await MainActor.run {
                    hongguoHits = []
                    searchHits = []
                    boundNotice = "已绑定红果条目：\(hit.title)"
                    isFetching = false
                    NotificationCenter.default.post(name: .libraryUpdated, object: nil)
                    onHongguoBound?()
                }
            } catch {
                await MainActor.run {
                    errorMessage = "绑定红果失败：\(error.localizedDescription)"
                    isFetching = false
                }
            }
        }
    }

    private func searchDouban() {
        let query = doubanQuery
        guard !query.isEmpty else { return }
        isSearching = true
        errorMessage = ""
        Task {
            do {
                let hits = try await DoubanService.shared.searchSubjects(query: query)
                let ranked = DoubanService.rankedShortDramaHits(hits, query: query)
                await MainActor.run {
                    // 对不上片名的候选也留着（手动匹配要能挑），只是排在后面
                    // 分季条目（「持械入宋：第七季」）对不上检索词只能排后面，
                    // 所以多留两条，别把用户要的那季挤掉
                    let merged = ranked + hits.filter { !ranked.contains($0) }
                    searchHits = Array(merged.prefix(6))
                    isSearching = false
                    if searchHits.isEmpty {
                        errorMessage = "豆瓣没有返回候选：可能被风控，或确实没有同名条目。"
                    }
                }
            } catch {
                await MainActor.run {
                    errorMessage = "豆瓣搜索失败：\(error.localizedDescription)"
                    searchHits = []
                    isSearching = false
                }
            }
        }
    }

    private func bindSubject() {
        isFetching = true
        errorMessage = ""
        let input = subjectInput
        let movieId = movie.id

        Task {
            do {
                guard let movieId else {
                    throw DoubanServiceError.invalidSubject
                }
                if let cached = try await cachedMetadataIfFresh(movieId: movieId, input: input) {
                    try await applyDoubanPosterIfAvailable(cached, movieId: movieId)
                    await MainActor.run {
                        onSaved(cached)
                        NotificationCenter.default.post(name: .libraryUpdated, object: nil)
                        isFetching = false
                    }
                    return
                }
                let payload = try await DoubanService.shared.fetchSubject(input: input)
                let metadata = payload.metadata(movieId: movieId, fetchedAt: Date(), cacheDays: 90)
                try await AppDatabase.shared.dbQueue.write { db in
                    try metadata.save(db)
                    try metadata.applyPosterToMovie(db)
                    if let posterURL = metadata.posterURL,
                       !DoubanMetadata.isUsablePosterURL(posterURL) {
                        try DoubanMetadata.clearUnusableDoubanPosterIfNeeded(movieId: movieId, db: db)
                    }
                }
                if let posterURL = metadata.posterURL, DoubanMetadata.isUsablePosterURL(posterURL) {
                    // 用户点了「绑定并抓取」，绕过自动刮削失败账本强行重下
                    PosterManager.shared.downloadPoster(posterPath: posterURL, force: true)
                }
                await MainActor.run {
                    onSaved(metadata)
                    NotificationCenter.default.post(name: .libraryUpdated, object: nil)
                    isFetching = false
                }
            } catch {
                await MainActor.run {
                    errorMessage = error.localizedDescription
                    isFetching = false
                }
            }
        }
    }

    private func applyDoubanPosterIfAvailable(_ metadata: DoubanMetadata, movieId: Int64) async throws {
        try await AppDatabase.shared.dbQueue.write { db in
            try metadata.applyPosterToMovie(db)
            if let posterURL = metadata.posterURL,
               !DoubanMetadata.isUsablePosterURL(posterURL) {
                try DoubanMetadata.clearUnusableDoubanPosterIfNeeded(movieId: movieId, db: db)
            }
        }
        if let posterURL = metadata.posterURL, DoubanMetadata.isUsablePosterURL(posterURL) {
            PosterManager.shared.downloadPoster(posterPath: posterURL, force: true)
        }
    }

    private func cachedMetadataIfFresh(movieId: Int64, input: String) async throws -> DoubanMetadata? {
        guard let subject = DoubanService.shared.normalizedSubject(input: input) else {
            throw DoubanServiceError.invalidSubject
        }
        return try await AppDatabase.shared.dbQueue.read { db in
            guard let cached = try DoubanMetadata.fetchOne(db, key: movieId),
                  cached.subjectId == subject.id,
                  cached.nextRefreshAt > Date().timeIntervalSince1970,
                  cached.rating != nil,
                  cached.lastError == nil else {
                return nil
            }
            return cached
        }
    }

    private func removeBinding() {
        guard let movieId = movie.id else { return }
        isFetching = true
        Task {
            do {
                try await AppDatabase.shared.dbQueue.write { db in
                    _ = try DoubanMetadata.deleteOne(db, key: movieId)
                    try DoubanMetadata.clearUnusableDoubanPosterIfNeeded(movieId: movieId, db: db)
                }
                await MainActor.run {
                    onRemoved()
                    NotificationCenter.default.post(name: .libraryUpdated, object: nil)
                    isFetching = false
                }
            } catch {
                await MainActor.run {
                    errorMessage = "解除绑定失败：\(error.localizedDescription)"
                    isFetching = false
                }
            }
        }
    }
}
