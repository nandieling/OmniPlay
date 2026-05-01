import SwiftUI

struct ContentView: View {
    var body: some View {
        // 直接显示咱们全新打造的海报墙界面！
        PosterWallView()
            .frame(minWidth: 800, minHeight: 600) // 给窗口一个更宽阔的初始尺寸
    }
}
