import SwiftUI
import GRDB
import AppKit

private struct OmniPlayDockerDoubanSyncTarget {
    let source: MediaSource
    let localFileIds: [String]
}

private enum OmniPlayDockerDoubanSyncError: LocalizedError {
    case remoteItemNotFound

    var errorDescription: String? {
        switch self {
        case .remoteItemNotFound:
            return "未在 OmniPlay Docker 中找到对应条目，豆瓣信息没有同步到 Docker 版。"
        }
    }
}

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
                onCancel: { dismiss() }
            )
            .padding(20)
        }
        .frame(width: 560, height: 300)
    }
}

struct DoubanBindingPanel: View {
    let movie: Movie
    let existingMetadata: DoubanMetadata?
    let onSaved: (DoubanMetadata) -> Void
    let onRemoved: () -> Void
    var onCancel: (() -> Void)? = nil
    var showsTitle: Bool = true
    var usesFlexibleSpacer: Bool = true

    @State private var subjectInput: String
    @State private var isFetching = false
    @State private var errorMessage = ""

    init(
        movie: Movie,
        existingMetadata: DoubanMetadata?,
        onSaved: @escaping (DoubanMetadata) -> Void,
        onRemoved: @escaping () -> Void,
        onCancel: (() -> Void)? = nil,
        showsTitle: Bool = true,
        usesFlexibleSpacer: Bool = true
    ) {
        self.movie = movie
        self.existingMetadata = existingMetadata
        self.onSaved = onSaved
        self.onRemoved = onRemoved
        self.onCancel = onCancel
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

            TextField("豆瓣链接或 subject ID，例如 https://movie.douban.com/subject/1292052/", text: $subjectInput)
                .textFieldStyle(.roundedBorder)

            HStack(spacing: 10) {
                Button {
                    openDoubanSearch()
                } label: {
                    Label("在浏览器搜索豆瓣", systemImage: "safari")
                }
                .buttonStyle(.bordered)
                .disabled(isFetching)

                Text("找到正确条目后复制 subject 链接回来绑定。")
                    .font(.caption)
                    .foregroundColor(.secondary)
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
                .foregroundColor(.secondary)
                .lineLimit(1)
            }

            Text("豆瓣仅支持手动绑定单个条目；不会自动搜索豆瓣，也不会参与批量扫描。刷新会走本地缓存和低频限速，遇到风控会自动暂停。")
                .font(.caption)
                .foregroundColor(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            if !errorMessage.isEmpty {
                Text(errorMessage)
                    .font(.caption)
                    .foregroundColor(.red)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if usesFlexibleSpacer {
                Spacer()
            }

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
                Button {
                    bindSubject()
                } label: {
                    if isFetching {
                        ProgressView()
                            .controlSize(.small)
                    } else {
                        Text("绑定并抓取")
                    }
                }
                .buttonStyle(.borderedProminent)
                .disabled(isFetching || subjectInput.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
    }

    private func openDoubanSearch() {
        let query = movie.title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty,
              let encoded = query.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed),
              let url = URL(string: "https://search.douban.com/movie/subject_search?search_text=\(encoded)") else {
            return
        }
        NSWorkspace.shared.open(url)
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
                    try await syncDoubanMetadataToOmniPlayDockerIfNeeded(cached, movieId: movieId)
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
                    PosterManager.shared.downloadPoster(posterPath: posterURL)
                }
                try await syncDoubanMetadataToOmniPlayDockerIfNeeded(metadata, movieId: movieId)
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
            PosterManager.shared.downloadPoster(posterPath: posterURL)
        }
    }

    private func syncDoubanMetadataToOmniPlayDockerIfNeeded(_ metadata: DoubanMetadata, movieId: Int64) async throws {
        do {
            let target = try await AppDatabase.shared.dbQueue.read { db -> OmniPlayDockerDoubanSyncTarget? in
                guard let source = try MediaSource.fetchOne(
                    db,
                    sql: """
                    SELECT mediaSource.*
                    FROM mediaSource
                    JOIN videoFile ON videoFile.sourceId = mediaSource.id
                    WHERE mediaSource.protocolType = ?
                      AND videoFile.movieId = ?
                      AND videoFile.id LIKE 'omniplay-docker:%'
                    LIMIT 1
                    """,
                    arguments: [MediaSourceProtocol.omniplayDocker.rawValue, movieId]
                ) else {
                    return nil
                }

                guard let sourceId = source.id else {
                    return nil
                }

                let localFileIds = try String.fetchAll(
                    db,
                    sql: """
                    SELECT id
                    FROM videoFile
                    WHERE movieId = ?
                      AND sourceId = ?
                      AND id LIKE 'omniplay-docker:%'
                    """,
                    arguments: [movieId, sourceId]
                )
                return OmniPlayDockerDoubanSyncTarget(source: source, localFileIds: localFileIds)
            }

            guard let target,
                  target.source.protocolKind == .omniplayDocker else {
                return
            }

            let config = OmniPlayDockerAuthConfig.decode(target.source.authConfig)
            let client = try OmniPlayDockerClient(baseURLString: target.source.baseUrl, sessionCookie: config?.sessionCookie)
            let remoteItems = try await client.libraryItems()
            let remoteItem = try await matchingOmniPlayDockerItem(
                movieId: movieId,
                localFileIds: target.localFileIds,
                remoteItems: remoteItems,
                client: client
            )
            guard let remoteItem else {
                throw OmniPlayDockerDoubanSyncError.remoteItemNotFound
            }
            _ = try await client.importDoubanMetadata(libraryItemId: remoteItem.id, metadata: metadata)
        } catch {
            print("⚠️ 同步豆瓣元数据到 OmniPlay Docker 失败：\(error.localizedDescription)")
            throw error
        }
    }

    private func matchingOmniPlayDockerItem(
        movieId: Int64,
        localFileIds: [String],
        remoteItems: [OmniPlayDockerLibraryItem],
        client: OmniPlayDockerClient
    ) async throws -> OmniPlayDockerLibraryItem? {
        if let item = remoteItems.first(where: { MediaLibraryManager.omniPlayDockerMovieId(for: $0.id) == movieId }) {
            return item
        }

        let remoteFileIds = Set(localFileIds.compactMap(omniPlayDockerRemoteFileId(from:)))
        guard !remoteFileIds.isEmpty else { return nil }

        for item in remoteItems {
            let detail = try await client.libraryDetail(id: item.id)
            let detailFileIds = Set(omniPlayDockerRemoteFileIds(from: detail))
            if !detailFileIds.isDisjoint(with: remoteFileIds) {
                return item
            }
        }
        return nil
    }

    private func omniPlayDockerRemoteFileId(from localFileId: String) -> String? {
        let parts = localFileId.split(separator: ":", maxSplits: 2, omittingEmptySubsequences: false).map(String.init)
        guard parts.count == 3, parts[0] == "omniplay-docker", !parts[2].isEmpty else { return nil }
        return parts[2].removingPercentEncoding ?? parts[2]
    }

    private func omniPlayDockerRemoteFileIds(from detail: OmniPlayDockerLibraryDetail) -> [String] {
        var ids = detail.videoFiles.map(\.id)
        for season in detail.seasons {
            ids.append(contentsOf: season.episodes.compactMap(\.videoFile?.id))
        }
        return ids
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
