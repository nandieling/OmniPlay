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
    
    // 🌟 新增：独立展示文件夹名
    @State private var sourceFolderName: String = "正在解析目录..."
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
        VStack(spacing: 20) {
            Text("手动编辑资料").font(.title2.bold())
            
            Form {
                // 🌟 完美分行展示：上方文件夹，下方文件名
                Section(header: Text("源文件信息")) {
                    Text("目录: \(sourceFolderName)")
                        .font(.body)
                        .fontWeight(.medium)
                        .foregroundColor(.primary)
                        .lineLimit(2)
                        
                    Text("文件: \(sourceFileName)")
                        .font(.caption)
                        .foregroundColor(.secondary)
                        .lineLimit(2)
                }
                
                Section(header: Text("基础信息")) {
                    TextField("影视名称", text: $title)
                    TextField("上映时间 (如: 2025-01-01)", text: $releaseDate)
                    TextField("评分 (0.0 - 10.0)", text: $voteAverage)
                }
                
                Section(header: Text("剧情简介")) {
                    TextEditor(text: $overview)
                        .frame(height: 80)
                        .overlay(RoundedRectangle(cornerRadius: 5).stroke(Color.gray.opacity(0.2)))
                }
                
                Section(header: Text("海报管理")) {
                    HStack {
                        Text(tempNewPosterPath ?? movie.posterPath ?? "暂无海报")
                            .font(.caption)
                            .foregroundColor(.secondary)
                            .lineLimit(1)
                            .truncationMode(.middle)
                        
                        Spacer()
                        
                        Button("选择本地图片...") {
                            selectLocalPoster()
                        }
                    }
                }
            }
            .padding()
            
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
        .frame(width: 450, height: 600)
        .onAppear(perform: loadSourceFile)
    }
    
    private func loadSourceFile() {
        Task {
            do {
                if let file = try await AppDatabase.shared.dbQueue.read({ db in
                    try movie.request(for: Movie.videoFiles).fetchOne(db)
                }) {
                    // 🌟 核心：Swift 动态反射提取底层路径，免疫报错！
                    let mirror = Mirror(reflecting: file)
                    var extractedFolder = movie.title // 兜底用电影名
                    
                    if let path = mirror.descendant("path") as? String {
                        extractedFolder = URL(fileURLWithPath: path).deletingLastPathComponent().lastPathComponent
                    } else if let url = mirror.descendant("url") as? String {
                        extractedFolder = URL(fileURLWithPath: url).deletingLastPathComponent().lastPathComponent
                    } else if let filePath = mirror.descendant("filePath") as? String {
                        extractedFolder = URL(fileURLWithPath: filePath).deletingLastPathComponent().lastPathComponent
                    }
                    
                    let finalFolder = extractedFolder
                    let finalFile = file.fileName
                    
                    await MainActor.run {
                        self.sourceFolderName = finalFolder
                        self.sourceFileName = finalFile
                    }
                } else {
                    await MainActor.run {
                        self.sourceFolderName = "未知目录"
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
