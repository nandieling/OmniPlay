import SwiftUI
import GRDB
import AppKit
import UniformTypeIdentifiers

struct MovieEditModalView: View {
    let movie: Movie
    @Environment(\.dismiss) var dismiss
    
    @State private var title: String
    @State private var releaseDate: String
    @State private var voteAverage: String
    @State private var overview: String
    
    @State private var sourceFileName: String = "正在解析文件..."
    @State private var tempNewPosterPath: String? = nil
    
    init(movie: Movie) {
        self.movie = movie
        _title = State(initialValue: movie.title)
        _releaseDate = State(initialValue: movie.releaseDate ?? "")
        _voteAverage = State(initialValue: movie.voteAverage != nil ? String(format: "%.1f", movie.voteAverage!) : "")
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
                        TextField("评分 (0.0 - 10.0)", text: $voteAverage)
                            .textFieldStyle(.roundedBorder)
                    }

                    editorSection("剧情简介") {
                        TextEditor(text: $overview)
                            .frame(minHeight: 110)
                            .padding(6)
                            .background(Color(nsColor: .textBackgroundColor))
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
        .onAppear(perform: loadSourceFile)
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
        let finalTitle = self.title; let finalDate = self.releaseDate; let finalVote = Double(self.voteAverage) ?? 0.0; let finalOverview = self.overview; let finalPoster = self.tempNewPosterPath; let targetId = self.movie.id
        
        Task {
            do {
                try await AppDatabase.shared.dbQueue.write { db in
                    if var m = try Movie.fetchOne(db, key: targetId) {
                        m.title = finalTitle; m.releaseDate = finalDate; m.voteAverage = finalVote; m.overview = finalOverview; m.isLocked = true
                        if let np = finalPoster { m.posterPath = np }
                        try m.update(db)
                    }
                }
                await MainActor.run { NotificationCenter.default.post(name: .libraryUpdated, object: nil); dismiss() }
            } catch { print("保存失败: \(error)") }
        }
    }
}
