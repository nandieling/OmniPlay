import SwiftUI

// ==========================================
// 🎨 1. 定义色彩模板 (语义化颜色)
// ==========================================
struct AppTheme {
    let name: String
    let accent: Color           // 强调色 (按钮、进度条、高亮文字)
    let background: Color       // 主背景色 (页面最底层的颜色)
    let surface: Color          // 卡片/弹窗底色 (比主背景稍微暗一点的层)
    let textPrimary: Color      // 主标题文字颜色
    let textSecondary: Color    // 副标题/描述文字颜色
}

// ==========================================
// 🗂 2. 浅色主题库 (重新调配：低饱和度、高级清爽)
// ==========================================
enum ThemeType: String, CaseIterable, Identifiable {
    case appleLight = "appleLight"
    case crystal = "crystal"
    case linen = "linen"
    case mint = "mint"
    case rose = "rose"
    
    var id: String { self.rawValue }
    
    // UI 显示的名称
    var displayName: String {
        switch self {
        case .appleLight: return "原生经典 (Apple Light)"
        case .crystal: return "晶透白 (Crystal)"
        case .linen: return "亚麻灰 (Linen)"
        case .mint: return "薄荷清 (Mint)"
        case .rose: return "北欧粉 (Nordic Rose)"
        }
    }
    
    // 主题对应的具体色值
    var colors: AppTheme {
        switch self {
        
        case .appleLight: // 🍎 苹果原生经典浅色 (图1、图2 风格)
                return AppTheme(
                    name: "Apple Light",
                    accent: Color(hex: "007AFF"),       // 苹果系统经典蓝
                    background: Color(hex: "F2F2F7"),   // 经典的极浅灰背景 (System Gray 6)
                    surface: Color(hex: "FFFFFF"),      // 纯白卡片
                    textPrimary: Color(hex: "000000"),  // 纯黑字
                    textSecondary: Color(hex: "8E8E93") // 经典灰字
                )
            
        case .crystal: // ✨ 默认浅色：极致清爽
            return AppTheme(
                name: "Crystal",
                accent: Color(hex: "007AFF"),       // 苹果蓝
                background: Color(hex: "FFFFFF"),   // 纯白
                surface: Color(hex: "F2F2F7"),      // 极浅灰卡片
                textPrimary: Color(hex: "000000"),  // 纯黑字
                textSecondary: Color(hex: "3C3C43") // 灰字
            )
        case .linen: // 亚麻灰：柔和护眼
            return AppTheme(
                name: "Linen",
                accent: Color(hex: "0D9488"),       // 蓝绿色
                background: Color(hex: "F9F9F9"),   // 带一点点灰的主背景
                surface: Color(hex: "F1F1F1"),      // 亚麻灰卡片
                textPrimary: Color(hex: "1F2937"),  // 深灰字
                textSecondary: Color(hex: "6B7280") // 灰字
            )
        case .mint: // 🍵 薄荷清：低饱和度自然风
            return AppTheme(
                name: "Mint",
                accent: Color(hex: "10B981"),       // 翡翠绿
                background: Color(hex: "F1FAF7"),   // 极淡薄荷绿
                surface: Color(hex: "E0F2F1"),      // 淡青色卡片
                textPrimary: Color(hex: "064E3B"),  // 暗绿字
                textSecondary: Color(hex: "689F88") // 绿灰字
            )
        case .rose: // 🌸 北欧粉：治愈系暖光
            return AppTheme(
                name: "Sakura",
                accent: Color(hex: "D946EF"),       // 紫红強調
                background: Color(hex: "FFF7FA"),   // 极淡粉
                surface: Color(hex: "FFE4EC"),      // 北欧粉卡片
                textPrimary: Color(hex: "701A75"),  // 暗紫红字
                textSecondary: Color(hex: "A855F7") // 紫灰字
            )
        }
    }
}

// ==========================================
// 🛠 3. 极其好用的 HEX 十六进制颜色扩展
// ==========================================
extension Color {
    init(hex: String) {
        let hex = hex.trimmingCharacters(in: CharacterSet.alphanumerics.inverted)
        var int: UInt64 = 0
        Scanner(string: hex).scanHexInt64(&int)
        let a, r, g, b: UInt64
        switch hex.count {
        case 3: // RGB (12-bit)
            (a, r, g, b) = (255, (int >> 8) * 17, (int >> 4 & 0xF) * 17, (int & 0xF) * 17)
        case 6: // RGB (24-bit)
            (a, r, g, b) = (255, int >> 16, int >> 8 & 0xFF, int & 0xFF)
        case 8: // ARGB (32-bit)
            (a, r, g, b) = (int >> 24, int >> 16 & 0xFF, int >> 8 & 0xFF, int & 0xFF)
        default:
            (a, r, g, b) = (255, 0, 0, 0)
        }
        self.init(
            .sRGB,
            red: Double(r) / 255,
            green: Double(g) / 255,
            blue:  Double(b) / 255,
            opacity: Double(a) / 255
        )
    }
}
