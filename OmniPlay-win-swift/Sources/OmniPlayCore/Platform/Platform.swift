import Foundation

/// 平台接缝。核心只认协议，宿主在启动时把实现注进来；没注入就退回 macOS 的老行为，
/// 这样 mac 版可以一个字不改地继续跑，Windows 版逐个补钩子。
public struct PlatformHooks {
    /// 机密读写（mac: Keychain；Windows: 凭据管理器）。返回 nil 表示没有存过。
    public var secretRead: (_ service: String, _ account: String) -> String?
    public var secretWrite: (_ service: String, _ account: String, _ value: String) -> Bool
    public var secretDelete: (_ service: String, _ account: String) -> Bool

    /// 应用数据目录（mac: ~/Library/Application Support/OmniPlay；Windows: %APPDATA%\OmniPlay）。
    public var dataDirectory: () -> URL

    /// 海报/剧照解码后的降采样字节（mac: ImageIO；Windows: 宿主用 WPF 自己解，钩子可留空）。
    public var imageDownscale: ((_ url: URL, _ maxPixelSize: Int) -> Data?)?

    /// 给用户看的一条提示（mac: UNUserNotification；Windows: Toast）。
    public var notify: ((_ title: String, _ body: String) -> Void)?

    public init(
        secretRead: @escaping (String, String) -> String? = { _, _ in nil },
        secretWrite: @escaping (String, String, String) -> Bool = { _, _, _ in false },
        secretDelete: @escaping (String, String) -> Bool = { _, _ in false },
        dataDirectory: @escaping () -> URL = PlatformHooks.defaultDataDirectory,
        imageDownscale: ((URL, Int) -> Data?)? = nil,
        notify: ((String, String) -> Void)? = nil
    ) {
        self.secretRead = secretRead
        self.secretWrite = secretWrite
        self.secretDelete = secretDelete
        self.dataDirectory = dataDirectory
        self.imageDownscale = imageDownscale
        self.notify = notify
    }

    public static func defaultDataDirectory() -> URL {
        #if os(Windows)
        // Windows 的 Foundation 没有 appDataDirectory 这个搜索路径，直接认 %APPDATA%
        let appData = ProcessInfo.processInfo.environment["APPDATA"]
        let base = (appData?.isEmpty == false) ? URL(fileURLWithPath: appData!)
            : URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
        #else
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
        #endif
        return base.appendingPathComponent("OmniPlay", isDirectory: true)
    }
}

public enum Platform {
    nonisolated(unsafe) public static var hooks = PlatformHooks()
}
