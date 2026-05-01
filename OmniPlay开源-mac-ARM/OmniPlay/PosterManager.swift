import Foundation
import SwiftUI

class PosterManager {
    static let shared = PosterManager()
    let cacheDirectory: URL
    
    private init() {
        // 🌟 核心：使用 Application Support，海报将永久安全存储，再也不会莫名其妙消失
        let paths = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)
        cacheDirectory = paths[0].appendingPathComponent("OmniPlay/Posters", isDirectory: true)
        
        if !FileManager.default.fileExists(atPath: cacheDirectory.path) {
            try? FileManager.default.createDirectory(at: cacheDirectory, withIntermediateDirectories: true)
        }
    }
    
    func cacheFileName(for path: String) -> String {
        if path.hasPrefix("/") {
            return path.replacingOccurrences(of: "/", with: "")
        }
        let encoded = path.addingPercentEncoding(withAllowedCharacters: .alphanumerics)
            ?? path.replacingOccurrences(of: "/", with: "_")
        return "ext_\(encoded)"
    }

    // 获取本地海报路径
    func getLocalPosterURL(for path: String?) -> URL? {
        guard let path = path, !path.isEmpty else { return nil }
        let safeName = cacheFileName(for: path)
        let fileURL = cacheDirectory.appendingPathComponent(safeName)
        return FileManager.default.fileExists(atPath: fileURL.path) ? fileURL : nil
    }
    
    // 异步下载海报
    func downloadPoster(posterPath: String) {
        let safeName = cacheFileName(for: posterPath)
        let destinationURL = cacheDirectory.appendingPathComponent(safeName)
        
        // 防重复拦截：如果本地已经有了，直接跳过
        if FileManager.default.fileExists(atPath: destinationURL.path) { return }
        
        let remoteURL: URL?
        if posterPath.hasPrefix("http://") || posterPath.hasPrefix("https://") {
            remoteURL = URL(string: posterPath)
        } else {
            remoteURL = URL(string: "https://image.tmdb.org/t/p/w500\(posterPath)")
        }
        guard let url = remoteURL else { return }
        
        Task {
            do {
                let (data, _) = try await URLSession.shared.data(from: url)
                try data.write(to: destinationURL)
                
                // 🌟 魔法：下载完成后，精准通知 UI 瞬间刷新这张海报
                await MainActor.run {
                    NotificationCenter.default.post(name: NSNotification.Name("PosterUpdated_\(safeName)"), object: nil)
                }
            } catch {
                print("❌ 下载海报失败: \(error)")
            }
        }
    }
    
    func clearCache() {
        try? FileManager.default.removeItem(at: cacheDirectory)
        try? FileManager.default.createDirectory(at: cacheDirectory, withIntermediateDirectories: true)
    }
}

// ==========================================
// 🌟 神级 UI 组件：智能缓存海报视图 (可直接放在这里)
// ==========================================
struct CachedPosterView: View {
    let posterPath: String?
    @State private var nsImage: NSImage? = nil
    
    var body: some View {
        Group {
            if let img = nsImage {
                Image(nsImage: img)
                    .resizable()
                    .aspectRatio(contentMode: .fill)
            } else {
                ZStack {
                    Color.gray.opacity(0.15)
                    Image(systemName: "photo")
                        .font(.largeTitle)
                        .foregroundColor(.gray.opacity(0.5))
                }
            }
        }
        .onAppear(perform: loadImage)
        // 监听下载完成的广播，实现“无缝自愈”
        .onReceive(NotificationCenter.default.publisher(for: NSNotification.Name("PosterUpdated_\(PosterManager.shared.cacheFileName(for: posterPath ?? ""))"))) { _ in
            loadImage()
        }
    }
    
    private func loadImage() {
        guard let path = posterPath, !path.isEmpty else { return }
        
        // 1. 本地有，直接秒开读取
        if let localURL = PosterManager.shared.getLocalPosterURL(for: path),
           let data = try? Data(contentsOf: localURL),
           let image = NSImage(data: data) {
            self.nsImage = image
        } else {
            // 2. 本地没有（比如沙盒迁移丢了），后台立刻静默补下！
            PosterManager.shared.downloadPoster(posterPath: path)
        }
    }
}
