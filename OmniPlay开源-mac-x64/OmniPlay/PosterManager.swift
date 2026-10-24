import Foundation
import SwiftUI
import ImageIO

/// 海报位图的后台加载器：读盘与降采样都不碰主线程。
///
/// 海报目录在 iCloud 容器里时，未本地化的文件读一次要等云端下载；原先 `onAppear`
/// 里同步读整张 JPEG 再解码，整库刷新能把指针卡住几十秒。这里改成后台读 + 只解到
/// 显示尺寸 + 内存缓存，主线程只负责把成品贴上去。
nonisolated final class PosterImageLoader {
    static let shared = PosterImageLoader()

    /// 卡片与详情页的海报都不超过约 320pt，640px 够覆盖 Retina。
    static let displayMaxPixelSize: CGFloat = 640

    private let cache = NSCache<NSString, CGImage>()
    private let queue = DispatchQueue(label: "nan.OmniPlay.poster-image-loader", attributes: .concurrent)

    private init() {
        cache.countLimit = 240
        cache.totalCostLimit = 160 * 1024 * 1024
    }

    func cachedImage(for url: URL) -> CGImage? {
        cache.object(forKey: url.path as NSString)
    }

    func forget(_ url: URL) {
        cache.removeObject(forKey: url.path as NSString)
    }

    func forgetAll() {
        cache.removeAllObjects()
    }

    /// 后台读盘 + 降采样，已缓存时直接返回。读不到（例如云端还没下载完）返回 nil，
    /// 不动文件，等 `PosterUpdated` 广播再自愈。
    func image(
        at url: URL,
        maxPixelSize: CGFloat = PosterImageLoader.displayMaxPixelSize
    ) async -> CGImage? {
        let key = url.path as NSString
        if let cached = cache.object(forKey: key) { return cached }
        let image = await withCheckedContinuation { (continuation: CheckedContinuation<CGImage?, Never>) in
            queue.async {
                continuation.resume(returning: Self.downsampledImage(at: url, maxPixelSize: maxPixelSize))
            }
        }
        if let image {
            cache.setObject(image, forKey: key, cost: Self.byteCost(of: image))
        }
        return image
    }

    /// 只解析头部信息判断是不是真图片，比整图 NSImage(data:) 省一大截 CPU。
    static func isValidImage(_ data: Data) -> Bool {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil) else { return false }
        return CGImageSourceGetCount(source) > 0
    }

    private static func downsampledImage(at url: URL, maxPixelSize: CGFloat) -> CGImage? {
        // 先读成 Data：iCloud 未本地化的文件会在这一行触发下载，但那是后台线程的事。
        guard let data = try? Data(contentsOf: url) else { return nil }
        let readOptions: [CFString: Any] = [kCGImageSourceShouldCache: false]
        guard let source = CGImageSourceCreateWithData(data as CFData, readOptions as CFDictionary),
              CGImageSourceGetCount(source) > 0 else {
            // 文件确实在，但内容不是图片：删掉让上层重新下载
            try? FileManager.default.removeItem(at: url)
            return nil
        }
        let thumbnailOptions: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixelSize,
            kCGImageSourceShouldCacheImmediately: true
        ]
        return CGImageSourceCreateThumbnailAtIndex(source, 0, thumbnailOptions as CFDictionary)
    }

    private static func byteCost(of image: CGImage) -> Int {
        image.bytesPerRow * image.height
    }
}

class PosterManager {
    static let shared = PosterManager()
    let cacheDirectory: URL
    
    private init() {
        // 🌟 核心：海报永久存储。iCloud 容器可用时直接放容器内（两台 mac 原生同步），否则放本地 Application Support
        cacheDirectory = OmniPlayICloudSync.posterDirectory()
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
        if path.hasPrefix("/"), FileManager.default.fileExists(atPath: path) {
            return URL(fileURLWithPath: path)
        }
        let safeName = cacheFileName(for: path)
        let fileURL = cacheDirectory.appendingPathComponent(safeName)
        return FileManager.default.fileExists(atPath: fileURL.path) ? fileURL : nil
    }
    
    // 异步下载海报
    func downloadPoster(posterPath: String) {
        if posterPath.hasPrefix("/"), FileManager.default.fileExists(atPath: posterPath) {
            return
        }
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
        
        // 校验与落盘也放后台：刮削时几十张海报连着写，主线程一张都不该替它解码。
        Task.detached(priority: .utility) {
            do {
                let (data, response) = try await URLSession.shared.data(from: url)
                if let http = response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
                    throw URLError(.badServerResponse)
                }
                guard PosterImageLoader.isValidImage(data) else {
                    throw URLError(.cannotDecodeContentData)
                }
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
        PosterImageLoader.shared.forgetAll()
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
    @State private var loadedPosterPath: String? = nil
    
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
        .onChange(of: posterPath) { _, _ in
            nsImage = nil
            loadedPosterPath = nil
            loadImage()
        }
        // 监听下载完成的广播，实现“无缝自愈”
        .onReceive(NotificationCenter.default.publisher(for: NSNotification.Name("PosterUpdated_\(PosterManager.shared.cacheFileName(for: posterPath ?? ""))"))) { _ in
            loadImage()
        }
    }
    
    private func loadImage() {
        guard let path = posterPath, !path.isEmpty else {
            self.nsImage = nil
            self.loadedPosterPath = nil
            return
        }
        guard loadedPosterPath != path || nsImage == nil else { return }
        
        guard let localURL = PosterManager.shared.getLocalPosterURL(for: path) else {
            // 本地没有（比如沙盒迁移丢了），后台立刻静默补下！
            self.nsImage = nil
            self.loadedPosterPath = nil
            PosterManager.shared.downloadPoster(posterPath: path)
            return
        }
        if let cached = PosterImageLoader.shared.cachedImage(for: localURL) {
            self.nsImage = Self.posterImage(from: cached)
            self.loadedPosterPath = path
            return
        }
        // 读盘与解码交给后台队列：整库刷新时主线程只剩「把成品贴上来」这一件事。
        // @State 的写入是穿透到 box 的，所以这里拿一份视图副本就够用。
        let loaded = self
        Task {
            let cgImage = await PosterImageLoader.shared.image(at: localURL)
            loaded.nsImage = cgImage.map { Self.posterImage(from: $0) }
            loaded.loadedPosterPath = cgImage == nil ? nil : path
            if cgImage == nil {
                PosterManager.shared.downloadPoster(posterPath: path)
            }
        }
    }

    private static func posterImage(from cgImage: CGImage) -> NSImage {
        NSImage(cgImage: cgImage, size: NSSize(width: cgImage.width, height: cgImage.height))
    }
}
