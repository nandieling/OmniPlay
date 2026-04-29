import SwiftUI
import AppKit
import UniformTypeIdentifiers
import GRDB

struct EpisodeThumbnailEditModalView: View {
    let movieId: Int64?
    let episode: EpisodeItem
    @Binding var movieTitle: String

    @Environment(\.dismiss) private var dismiss

    @State private var sourceFileDisplayPath: String = "正在解析文件..."
    @State private var currentThumbnailDisplayPath: String = "暂无剧照"
    @State private var selectedImageURL: URL? = nil
    @State private var previewImage: NSImage? = nil
    @State private var errorMessage: String = ""
    @State private var editedMovieTitle: String
    @State private var editedSeason: String
    @State private var editedEpisode: String
    @State private var editedSubtitle: String

    init(movieId: Int64?, movieTitle: Binding<String>, episode: EpisodeItem) {
        self.movieId = movieId
        self._movieTitle = movieTitle
        self.episode = episode
        let override = EpisodeMetadataOverrideStore.shared.override(for: episode.file.id)
        _editedMovieTitle = State(initialValue: movieTitle.wrappedValue)
        _editedSeason = State(initialValue: String(episode.season))
        _editedEpisode = State(initialValue: String(episode.episode))
        _editedSubtitle = State(initialValue: override?.subtitle ?? "")
    }

    var body: some View {
        VStack(spacing: 20) {
            Text("自定义分集剧照")
                .font(.title2.bold())

            HStack(alignment: .top, spacing: 20) {
                Group {
                    if let image = previewImage {
                        Image(nsImage: image)
                            .resizable()
                            .scaledToFill()
                    } else {
                        RoundedRectangle(cornerRadius: 12)
                            .fill(Color.secondary.opacity(0.12))
                            .overlay(
                                Image(systemName: "photo")
                                    .font(.system(size: 28, weight: .medium))
                                    .foregroundColor(.secondary)
                            )
                    }
                }
                .frame(width: 220, height: 132)
                .clipShape(RoundedRectangle(cornerRadius: 12))

                Form {
                    Section(header: Text("分集信息")) {
                        TextField("影视名称", text: $editedMovieTitle)
                        TextField("第几季", text: $editedSeason)
                        TextField("第几集", text: $editedEpisode)
                        TextField("自定义副标题", text: $editedSubtitle)

                        Text("当前显示: \(effectiveEpisodeDisplayName)")
                            .font(.caption)
                            .foregroundColor(.secondary)
                            .lineLimit(2)

                        sourcePathBlock(title: "文件", value: sourceFileDisplayPath)
                    }

                    Section(header: Text("剧照管理")) {
                        Text(currentThumbnailDisplayPath)
                            .font(.caption)
                            .foregroundColor(.secondary)
                            .lineLimit(2)
                            .truncationMode(.middle)

                        Button("选择本地图片...") {
                            selectLocalThumbnail()
                        }
                    }

                    if !errorMessage.isEmpty {
                        Section {
                            Text(errorMessage)
                                .font(.caption)
                                .foregroundColor(.red)
                        }
                    }
                }
            }
            .padding(.horizontal)

            HStack(spacing: 15) {
                Button("取消") { dismiss() }
                    .keyboardShortcut(.cancelAction)

                Button("保存修改") {
                    saveChanges()
                }
                .buttonStyle(.borderedProminent)
                .tint(.blue)
                .disabled(normalizedMovieTitle.isEmpty || normalizedSeason == nil || normalizedEpisode == nil)
            }
            .padding(.bottom, 20)
        }
        .frame(width: 760, height: 560)
        .onAppear {
            loadCurrentThumbnail()
            loadSourcePath()
        }
    }

    private func sourcePathBlock(title: String, value: String) -> some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(title)
                .font(.caption)
                .foregroundColor(.secondary)
            Text(verbatim: value)
                .font(.caption)
                .foregroundColor(.secondary)
                .lineLimit(nil)
                .fixedSize(horizontal: false, vertical: true)
                .textSelection(.enabled)
        }
    }

    private var normalizedMovieTitle: String {
        editedMovieTitle.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private var normalizedSeason: Int? {
        Int(editedSeason.trimmingCharacters(in: .whitespacesAndNewlines))
    }

    private var normalizedEpisode: Int? {
        Int(editedEpisode.trimmingCharacters(in: .whitespacesAndNewlines))
    }

    private var normalizedSubtitle: String? {
        let value = editedSubtitle.trimmingCharacters(in: .whitespacesAndNewlines)
        return value.isEmpty ? nil : value
    }

    private var effectiveEpisodeDisplayName: String {
        guard let season = normalizedSeason, let episodeNumber = normalizedEpisode, season >= 0, episodeNumber > 0 else {
            return episode.displayName
        }
        var displayName = season == 0 ? "特别篇 第 \(episodeNumber) 集" : "第 \(season) 季 第 \(episodeNumber) 集"
        if let subtitle = normalizedSubtitle {
            displayName += " · \(subtitle)"
        }
        return displayName
    }

    private func loadCurrentThumbnail() {
        let currentURL = ThumbnailManager.shared.thumbnailURL(for: episode.file.id)
        if let image = NSImage(contentsOf: currentURL) {
            previewImage = image
            currentThumbnailDisplayPath = currentURL.path
        } else {
            previewImage = nil
            currentThumbnailDisplayPath = "当前未发现本地剧照，可只保存名称/季集信息，也可同时替换剧照"
        }
    }

    private func loadSourcePath() {
        Task {
            do {
                let source = try await AppDatabase.shared.dbQueue.read { db in
                    try episode.file.request(for: VideoFile.mediaSource).fetchOne(db)
                }
                let filePath = episode.file.displayPath(mediaSource: source)
                await MainActor.run {
                    sourceFileDisplayPath = filePath
                }
            } catch {
                await MainActor.run {
                    let relativePath = episode.file.relativePath.trimmingCharacters(in: .whitespacesAndNewlines)
                    let fallbackPath = relativePath.isEmpty ? episode.file.fileName : relativePath
                    sourceFileDisplayPath = fallbackPath
                }
            }
        }
    }

    private func selectLocalThumbnail() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.image]
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false

        if panel.runModal() == .OK, let url = panel.url {
            selectedImageURL = url
            previewImage = NSImage(contentsOf: url)
            currentThumbnailDisplayPath = url.path
            errorMessage = ""
        }
    }

    private func saveChanges() {
        guard let season = normalizedSeason,
              let episodeNumber = normalizedEpisode,
              season >= 0,
              episodeNumber > 0 else {
            errorMessage = "请填写有效的影视名称、季数和集数。"
            return
        }

        let finalMovieTitle = normalizedMovieTitle
        let targetMovieId = movieId
        let fileId = episode.file.id
        let thumbnailURL = selectedImageURL

        Task {
            do {
                if let targetMovieId {
                    try await AppDatabase.shared.dbQueue.write { db in
                        if var movie = try Movie.fetchOne(db, key: targetMovieId) {
                            movie.title = finalMovieTitle
                            try movie.update(db)
                        }
                    }
                }

                EpisodeMetadataOverrideStore.shared.saveOverride(
                    fileId: fileId,
                    season: season,
                    episode: episodeNumber,
                    subtitle: normalizedSubtitle
                )

                if let thumbnailURL {
                    try ThumbnailManager.shared.replaceThumbnail(fileId: fileId, with: thumbnailURL)
                }

                await MainActor.run {
                    movieTitle = finalMovieTitle
                    NotificationCenter.default.post(name: .libraryUpdated, object: nil)
                    dismiss()
                }
            } catch {
                await MainActor.run {
                    errorMessage = "保存失败：\(error.localizedDescription)"
                }
            }
        }
    }

}
