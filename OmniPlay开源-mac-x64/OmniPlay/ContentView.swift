import SwiftUI

struct ContentView: View {
    @AppStorage(AppThemeResolver.colorKey) var appTheme = ThemeType.appleLight.rawValue
    @AppStorage(AppThemeResolver.styleKey) var appThemeStyle = ThemeStyle.classic.rawValue

    init() {
        // 旧版本把液态玻璃当成一种配色，启动时改写成「颜色 + 风格」两个键
        AppThemeResolver.migrateLegacySelection()
    }

    var body: some View {
        // 直接显示咱们全新打造的海报墙界面！
        PosterWallView()
            .frame(minWidth: 800, minHeight: 600) // 给窗口一个更宽阔的初始尺寸
            .task { await NeodbSyncService.shared.runLaunchSyncIfNeeded() }
            .background(ThemeWindowStyler(topColor: AppThemeResolver.theme(colorRaw: appTheme, styleRaw: appThemeStyle).windowTint))
    }
}
