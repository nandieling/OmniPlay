import SwiftUI

@main
struct OmniPlayApp: App {
    @NSApplicationDelegateAdaptor(OmniPlayAppDelegate.self) var appDelegate

    // 🌟 1. 监听你在设置里选中的语言
    @AppStorage("appLanguage") var appLanguage = "zh-Hans"
    
    // 初始化方法：App 启动的第一时间就会执行这里
    init() {
        // 🌟 终极封印：在 App 诞生的第一毫秒，物理封杀所有底层图形库和手柄扫描的啰嗦日志！
        // 必须放在数据库和任何视图加载之前执行！
        setenv("MVK_CONFIG_LOG_LEVEL", "0", 1)
        setenv("GC_DISABLE_GAMECONTROLLER", "1", 1)
        setenv("SDL_JOYSTICK_DISABLE", "1", 1)
        
        do {
            // 1. 找到 Mac 系统的“应用支持”文件夹 (专门用来存 App 数据的地方)
            let appSupportURL = try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
            
            // 2. 为我们的“觅影”创建一个专属文件夹
            let appDirectory = appSupportURL.appendingPathComponent("OmniPlay", isDirectory: true)
            if !FileManager.default.fileExists(atPath: appDirectory.path) {
                try FileManager.default.createDirectory(at: appDirectory, withIntermediateDirectories: true, attributes: nil)
            }
            
            // 3. 设定数据库文件的名字和路径
            let dbURL = appDirectory.appendingPathComponent("omniplay.sqlite")
            
            // 4. 【核心点火】让咱们的 GRDB 引擎启动并建表！
            try AppDatabase.shared.setup(databaseURL: dbURL)
            
            print("✅ GRDB 数据库引擎已成功启动！文件路径：\(dbURL.path)")
            
        } catch {
            print("❌ 致命错误：数据库初始化失败：\(error)")
            // 实际开发中，这里可以弹窗提示用户电脑磁盘满等问题
        }
    }
    
    var body: some Scene {
        WindowGroup {
            // 启动后直接显示咱们的主测试界面
            // (注意：如果你把主界面的名字改成了 PosterWallView，请把下面的 ContentView() 改成 PosterWallView() )
            ContentView()
                // 🌟 2. 神奇魔法：强制把整个 App 的 UI 环境语言，实时切换成你选中的语言！
                .environment(\.locale, .init(identifier: appLanguage))
                // 🌟 修复 2：加上这行“强制刷新大招”！
                // 它的作用是：只要 appLanguage 发生变化，立刻摧毁旧界面并用新语言重建整个 App！
                .id(appLanguage)
        }
    }
}
