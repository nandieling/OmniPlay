import SwiftUI
import GRDB
import AppKit
import UniformTypeIdentifiers

struct MovieEditModalView: View {
    let movie: Movie
    @Environment(\.dismiss) var dismiss
    
    @State private var title: String
    @State private var releaseDate: String
    @State private var tmdbVoteAverage: String
    @State private var doubanRating: String
    @State private var overview: String
    
    @State private var sourceFileName: String = "正在解析文件..."
    @State private var tempNewPosterPath: String? = nil
    
    init(movie: Movie) {
        self.movie = movie
        _title = State(initialValue: movie.title)
        _releaseDate = State(initialValue: movie.releaseDate ?? "")
        _tmdbVoteAverage = State(initialValue: movie.voteAverage != nil ? String(format: "%.1f", movie.voteAverage!) : "")
        _doubanRating = State(initialValue: "")
        _overview = State(initialValue: movie.overview ?? "")
    }

    var body: some View {
        VStack(spacing: 18) {
            Text("手动编辑资料")
                .font(.title2.bold())
                .padding(.top, 22)

            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    editorSection("源文件信息") {
                        pathRow(title: "文件", value: sourceFileName)
                    }

                    editorSection("基础信息") {
                        TextField("影视名称", text: $title)
                            .textFieldStyle(.roundedBorder)
                        TextField("上映时间 (如: 2025-01-01)", text: $releaseDate)
                            .textFieldStyle(.roundedBorder)
                        TextField("TMDB评分 (0.0 - 10.0)", text: $tmdbVoteAverage)
                            .textFieldStyle(.roundedBorder)
                        TextField("豆瓣评分 (0.0 - 10.0)", text: $doubanRating)
                            .textFieldStyle(.roundedBorder)
                    }

                    editorSection("剧情简介") {
                        OverviewTextView(text: $overview)
                        .frame(minHeight: 134)
                        .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                        .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color.gray.opacity(0.2)))
                    }

                    editorSection("海报管理") {
                        VStack(alignment: .leading, spacing: 10) {
                            Text("当前海报")
                                .font(.caption)
                                .foregroundColor(.secondary)

                            Text(tempNewPosterPath ?? movie.posterPath ?? "暂无海报")
                                .font(.caption)
                                .foregroundColor(.secondary)
                                .lineLimit(nil)
                                .fixedSize(horizontal: false, vertical: true)
                                .truncationMode(.middle)
                                .textSelection(.enabled)

                            Button("选择本地图片...") {
                                selectLocalPoster()
                            }
                        }
                    }
                }
                .padding(.horizontal, 28)
                .padding(.bottom, 4)
            }
            
            HStack(spacing: 15) {
                Button("取消") { dismiss() }.keyboardShortcut(.cancelAction)
                
                Button("保存修改并锁定") {
                    saveChanges()
                }
                .buttonStyle(.borderedProminent)
                .tint(.blue)
            }
            .padding(.bottom, 20)
        }
        .frame(width: 640, height: 640)
        .onAppear {
            loadSourceFile()
            loadDoubanRating()
        }
    }

    private func editorSection<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(title)
                .font(.headline)
            content()
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color(nsColor: .controlBackgroundColor))
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
    }

    private func pathRow(title: String, value: String) -> some View {
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
    
    private func loadSourceFile() {
        Task {
            do {
                if let pair = try await AppDatabase.shared.dbQueue.read({ db -> (VideoFile, MediaSource?)? in
                    guard let file = try VideoFile.fetchVisibleFirstFile(movieId: movie.id, in: db) else {
                        return nil
                    }
                    let source = try file.request(for: VideoFile.mediaSource).fetchOne(db)
                    return (file, source)
                }) {
                    let (file, source) = pair
                    let finalFile = file.displayPath(mediaSource: source)
                    
                    await MainActor.run {
                        self.sourceFileName = finalFile
                    }
                } else {
                    await MainActor.run {
                        self.sourceFileName = "未能识别出绑定的底层视频文件"
                    }
                }
            } catch {}
        }
    }

    private func loadDoubanRating() {
        guard let movieId = movie.id else { return }
        Task {
            do {
                let rating = try await AppDatabase.shared.dbQueue.read { db in
                    try DoubanMetadata.fetchOne(db, key: movieId)?.rating
                }
                await MainActor.run {
                    self.doubanRating = rating != nil ? String(format: "%.1f", rating!) : ""
                }
            } catch {}
        }
    }
    
    private func selectLocalPoster() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.image]
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        
        if panel.runModal() == .OK, let url = panel.url {
            let newName = "custom_poster_\(UUID().uuidString.prefix(8)).jpg"
            do {
                let appSupportURL = try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
                let posterDirectory = appSupportURL.appendingPathComponent("OmniPlay/Posters", isDirectory: true)
                if !FileManager.default.fileExists(atPath: posterDirectory.path) { try FileManager.default.createDirectory(at: posterDirectory, withIntermediateDirectories: true, attributes: nil) }
                
                let destURL = posterDirectory.appendingPathComponent(newName)
                try FileManager.default.copyItem(at: url, to: destURL)
                self.tempNewPosterPath = "/" + newName
            } catch { print("海报拷贝失败: \(error)") }
        }
    }
    
    private func saveChanges() {
        let finalTitle = self.title
        let finalDate = self.releaseDate
        let finalTMDBVote = normalizedRatingValue(tmdbVoteAverage)
        let finalDoubanRating = normalizedRatingValue(doubanRating)
        let finalOverview = self.overview
        let finalPoster = self.tempNewPosterPath
        let targetId = self.movie.id
        
        Task {
            do {
                try await AppDatabase.shared.dbQueue.write { db in
                    if var m = try Movie.fetchOne(db, key: targetId) {
                        m.title = finalTitle; m.releaseDate = finalDate; m.voteAverage = finalTMDBVote; m.overview = finalOverview; m.isLocked = true
                        if let np = finalPoster { m.posterPath = np }
                        try m.update(db)

                        if let movieId = m.id {
                            if var douban = try DoubanMetadata.fetchOne(db, key: movieId) {
                                douban.rating = finalDoubanRating
                                douban.fetchedAt = Date().timeIntervalSince1970
                                try douban.update(db)
                            } else if let finalDoubanRating {
                                let now = Date()
                                let douban = DoubanMetadata(
                                    movieId: movieId,
                                    subjectId: "manual-\(movieId)",
                                    subjectURL: "",
                                    title: finalTitle,
                                    originalTitle: nil,
                                    year: String(finalDate.prefix(4)).isEmpty ? nil : String(finalDate.prefix(4)),
                                    rating: finalDoubanRating,
                                    ratingCount: nil,
                                    summary: nil,
                                    genres: nil,
                                    countries: nil,
                                    directors: nil,
                                    casts: nil,
                                    posterURL: nil,
                                    fetchedAt: now.timeIntervalSince1970,
                                    nextRefreshAt: now.addingTimeInterval(90 * 24 * 60 * 60).timeIntervalSince1970,
                                    lastError: nil
                                )
                                try douban.insert(db)
                            }
                        }
                    }
                }
                await MainActor.run { NotificationCenter.default.post(name: .libraryUpdated, object: nil); dismiss() }
            } catch { print("保存失败: \(error)") }
        }
    }

    private func normalizedRatingValue(_ value: String) -> Double? {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, let number = Double(trimmed), number > 0 else { return nil }
        return min(max(number, 0), 10)
    }
}

private struct OverviewTextView: NSViewRepresentable {
    @Binding var text: String

    func makeNSView(context: Context) -> NSScrollView {
        let scrollView = NSScrollView()
        scrollView.drawsBackground = true
        scrollView.backgroundColor = .textBackgroundColor
        scrollView.borderType = .noBorder
        scrollView.hasVerticalScroller = true
        scrollView.hasHorizontalScroller = false
        scrollView.autohidesScrollers = true

        let textView = NSTextView()
        textView.delegate = context.coordinator
        textView.string = text
        textView.font = .systemFont(ofSize: NSFont.systemFontSize)
        textView.textColor = .textColor
        textView.backgroundColor = .textBackgroundColor
        textView.drawsBackground = false
        textView.isEditable = true
        textView.isSelectable = true
        textView.isRichText = false
        textView.importsGraphics = false
        textView.allowsUndo = true
        textView.textContainerInset = NSSize(width: 12, height: 18)
        textView.textContainer?.lineFragmentPadding = 0
        textView.textContainer?.widthTracksTextView = true
        textView.textContainer?.heightTracksTextView = false
        textView.isVerticallyResizable = true
        textView.isHorizontallyResizable = false
        textView.autoresizingMask = [.width]
        scrollView.documentView = textView
        context.coordinator.textView = textView

        return scrollView
    }

    func updateNSView(_ nsView: NSScrollView, context: Context) {
        guard let textView = context.coordinator.textView else { return }
        if textView.string != text {
            textView.string = text
        }
    }

    func makeCoordinator() -> Coordinator {
        Coordinator(text: $text)
    }

    final class Coordinator: NSObject, NSTextViewDelegate {
        @Binding private var text: String
        weak var textView: NSTextView?

        init(text: Binding<String>) {
            _text = text
        }

        func textDidChange(_ notification: Notification) {
            guard let textView = notification.object as? NSTextView else { return }
            text = textView.string
        }
    }
}
