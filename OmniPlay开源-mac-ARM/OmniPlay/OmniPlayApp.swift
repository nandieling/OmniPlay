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
            // 1. 打开本地媒体库；若 iCloud 容器中有更新的云端库，先拉取覆盖
            let (dbURL, container) = try OmniPlayICloudSync.openDatabase()
            
            // 2. 【核心点火】让咱们的 GRDB 引擎启动并建表！
            try AppDatabase.shared.setup(databaseURL: dbURL, icloudContainerURL: container)
            
            print("✅ GRDB 数据库引擎已成功启动！文件路径：\(dbURL.path)"
                + (container != nil ? "（iCloud 同步已启用）" : "（iCloud 不可用，纯本地模式）"))
            
            // 3. 启动后台定时同步（本地库 -> iCloud，每 60 秒 + 退出前）
            if container != nil {
                OmniPlayICloudSync.startBackgroundSync()
            }
            
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
