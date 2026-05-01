import Cocoa
import Foundation
import GRDB

class LocalFolderManager {
    static let shared = LocalFolderManager()
    
    private init() {}
    
    /// 1. 弹出面板让用户选择文件夹，并生成安全书签存入数据库
    func promptAndSaveLocalFolder() {
        let panel = NSOpenPanel()
        panel.title = "添加本地视频库"
        panel.message = "请授权“觅影”访问您的本地视频文件夹"
        panel.prompt = "授权访问" // 按钮上的文字
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        panel.canCreateDirectories = true
        
        // 当用户点击“授权访问”后
        if panel.runModal() == .OK, let selectedURL = panel.url {
            saveBookmarkToDatabase(for: selectedURL)
        }
    }
    
    /// 2. 生成安全书签并写入 GRDB
    private func saveBookmarkToDatabase(for url: URL) {
        do {
            // 【核心】请求生成带有安全作用域的书签数据
            let bookmarkData = try url.bookmarkData(
                options: .withSecurityScope,
                includingResourceValuesForKeys: nil,
                relativeTo: nil
            )
            
            // 将 Data 转为 Base64 字符串，方便存入 SQLite
            let bookmarkString = bookmarkData.base64EncodedString()
            
            // 组装数据库模型 (使用我们之前定义的 MediaSource)
            let newSource = MediaSource(
                name: url.lastPathComponent, // 默认用文件夹名作为库的名称
                protocolType: MediaSourceProtocol.local.rawValue,
                baseUrl: MediaSourceProtocol.local.normalizedBaseURL(url.path),
                authConfig: bookmarkString   // 将书签存在这里！
            )
            
            // 写入数据库
            try AppDatabase.shared.dbQueue.write { db in
                try newSource.insert(db)
            }
            
            print("✅ 成功添加本地文件夹并保存权限书签：\(url.path)")
            
            // TODO: 这里可以发一个 Notification 或者回调，通知 UI 刷新媒体库列表，并触发后台扫描器去扫描里面的 .mp4/.mkv 文件
            
        } catch {
            print("❌ 生成安全书签失败: \(error.localizedDescription)")
        }
    }
    
    /// 3. 【极其重要】使用书签安全地访问文件
    /// 以后每次扫描该文件夹，或者播放该文件夹里的视频时，都必须调用此方法包裹你的逻辑
    func accessSecurityScopedResource(from source: MediaSource, action: (URL) throws -> Void) throws {
        guard source.protocolKind == .local, let authConfig = source.authConfig,
              let bookmarkData = Data(base64Encoded: authConfig) else {
            return
        }
        
        var isStale = false // 书签是否过期（比如用户移动了文件夹）
        let url = try URL(
            resolvingBookmarkData: bookmarkData,
            options: .withSecurityScope,
            relativeTo: nil,
            bookmarkDataIsStale: &isStale
        )
        
        if isStale {
            print("⚠️ 警告：安全书签已过期（可能文件夹被移动或重命名），需要提示用户重新授权。")
            // 实际开发中，这里可以把这个 source 标记为无效，并在 UI 上提示用户修复
        }
        
        // 【核心】开始访问权限！
        let accessGranted = url.startAccessingSecurityScopedResource()
        
        guard accessGranted else {
            print("❌ 无法获取沙盒访问权限")
            return
        }
        
        // 【核心】无论操作成功与否，用 defer 确保离开作用域时归还权限，否则会导致内核资源泄漏！
        defer {
            url.stopAccessingSecurityScopedResource()
        }
        
        // 权限兑换成功，执行你传进来的闭包（比如在里面遍历文件，或者让 VLC 开始播放）
        try action(url)
    }
}
