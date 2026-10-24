import AppKit
import SwiftUI

// ==========================================
// 🎨 1. 定义色彩模板 (语义化颜色)
// ==========================================
/// 页面底的彩色光斑。玻璃本身是透明的，浅色底全靠这些柔光提供可折射的颜色与层次。
/// 半径用容器比例而不是固定点值，窗口拉大拉小光斑都跟着铺满，不会缩成一团。
struct AppGlassGlow {
    let color: Color
    let center: UnitPoint
    let spread: CGFloat     // 椭圆渐变的外半径占容器尺寸的比例
    let opacity: Double
}

/// 液态玻璃主题的材质参数。只放不受系统版本限制的值，
/// 真正的系统玻璃（glassEffect）在 ThemeSurfaceModifier 里按版本选用。
struct AppGlassStyle {
    let material: Material      // macOS 14 / 15 的兜底材质
    let tint: Color             // 玻璃染色
    let sheen: LinearGradient   // 顶部镜面高光，玻璃那点「湿亮」全靠它
    let border: Color           // 边缘高光
    let edge: Color             // 玻璃轮廓：浅色底上只给高光会糊成一片
    let shadow: Color           // 玻璃片投影
    let glows: [AppGlassGlow]   // 页面底的光斑
    let cornerRadius: CGFloat
}

struct AppTheme {
    let name: String
    let accent: Color           // 强调色 (按钮、进度条、高亮文字)
    let background: Color       // 主背景色 (页面最底层的颜色)
    let surface: Color          // 卡片/弹窗底色 (比主背景稍微暗一点的层)
    let textPrimary: Color      // 主标题文字颜色
    let textSecondary: Color    // 副标题/描述文字颜色
    var glass: AppGlassStyle? = nil     // 有值即为液态玻璃主题
    var backdrop: [Color] = []          // 页面底色渐变，玻璃需要可折射的景深

    var isLiquidGlass: Bool { glass != nil }

    /// 进度条底槽、缩略图占位这类「比背景深一点的实心垫层」
    var subtleFill: Color { isLiquidGlass ? textPrimary.opacity(0.07) : surface }

    /// 胶囊按钮、列表行这类半透明垫层；玻璃主题由玻璃本身充当
    var chipFill: Color { isLiquidGlass ? Color.clear : surface.opacity(0.5) }

    /// 详情页把海报冲淡多少：玻璃主题略少于纯色主题，让那张海报的颜色透上来给玻璃折射，
    /// 又保证深色标题压在暗色海报上仍读得清
    var heroDimming: Double { isLiquidGlass ? 0.55 : 0.6 }

    /// 缩略图 / 海报占位块
    var placeholderFill: Color { isLiquidGlass ? textPrimary.opacity(0.055) : surface.opacity(0.32) }

    /// 列表行垫层（选中态给强调色，未选中给半透明）
    func rowFill(selected: Bool) -> Color {
        if isLiquidGlass { return selected ? accent.opacity(0.16) : Color.clear }
        return surface.opacity(selected ? 0.9 : 0.55)
    }

    /// 整窗（含系统控件、弹层、滚动条）跟着主题走才不违和
    var preferredScheme: ColorScheme? { isLiquidGlass ? .light : nil }

    /// 状态提示色：几套浅色配色上都压得住的绿 / 橙 / 红，设置弹窗与详情页用同一组
    var successColor: Color { Color(hex: "1E8E4A") }
    var warningColor: Color { Color(hex: "B25E09") }
    var dangerColor: Color { Color(hex: "D0021B") }
}

// ==========================================
// 🗂 2. 主题颜色库 (重新调配：低饱和度、高级清爽)；风格见下面的 ThemeStyle
// ==========================================
/// 主题风格。经典 = 纯色底 + 实心卡片；液态玻璃 = 彩色光斑底 + 系统玻璃层。
/// 两种风格共用下面 5 套主题颜色，玻璃的染色与光斑全部从对应强调色推导。
enum ThemeStyle: String, CaseIterable, Identifiable {
    case classic = "classic"
    case liquidGlass = "liquidGlass"

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .classic: return "经典 (Classic)"
        case .liquidGlass: return "液态玻璃 (Liquid Glass)"
        }
    }
}

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
                accent: Color(hex: "2F7CF6"),       // 清爽蓝，比系统蓝收敛一点
                background: Color(hex: "F4F6FA"),   // 冷调浅灰底，白卡片才浮得起来
                surface: Color(hex: "FFFFFF"),      // 纯白卡片
                textPrimary: Color(hex: "1B2330"),  // 深墨蓝灰字，比纯黑柔和
                textSecondary: Color(hex: "6E7787") // 中性灰字
            )
        case .linen: // 亚麻灰：柔和护眼
            return AppTheme(
                name: "Linen",
                accent: Color(hex: "0D9488"),       // 蓝绿色
                background: Color(hex: "F5F4F0"),   // 带一点暖调的亚麻底
                surface: Color(hex: "FCFBF8"),      // 米白卡片
                textPrimary: Color(hex: "2B2A26"),  // 暖深灰字
                textSecondary: Color(hex: "77756C") // 亚麻灰字
            )
        case .mint: // 🍵 薄荷清：低饱和度自然风
            return AppTheme(
                name: "Mint",
                accent: Color(hex: "10B981"),       // 翡翠绿
                background: Color(hex: "F1F8F4"),   // 极淡薄荷绿
                surface: Color(hex: "FBFDFC"),      // 近白卡片，淡青只做打底
                textPrimary: Color(hex: "143D31"),  // 暗绿字
                textSecondary: Color(hex: "62847A") // 绿灰字
            )
        case .rose: // 🌸 北欧粉：治愈系暖光
            return AppTheme(
                name: "Sakura",
                accent: Color(hex: "E0559C"),       // 北欧玫瑰，比紫红耐看
                background: Color(hex: "FBF5F6"),   // 若隐若现的粉底
                surface: Color(hex: "FEFBFC"),      // 近白卡片
                textPrimary: Color(hex: "3C2531"),  // 暗玫瑰灰字
                textSecondary: Color(hex: "967B87") // 藕荷灰字
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

// ==========================================
// 🧩 3.5 主题 = 风格 × 颜色
// ==========================================
/// 设置页存的是「风格 + 颜色」两个键，这里负责把它们合成一份 AppTheme。
enum AppThemeResolver {
    static let colorKey = "appTheme"
    static let styleKey = "appThemeStyle"
    /// 旧版本把液态玻璃当成一种「颜色」存进 appTheme
    private static let legacyGlassRaw = "liquidGlass"

    static func theme(colorRaw: String, styleRaw: String) -> AppTheme {
        let (color, style) = selection(colorRaw: colorRaw, styleRaw: styleRaw)
        let base = color.colors
        return style == .liquidGlass ? base.applyingLiquidGlass() : base
    }

    /// 当前选中的颜色与风格。读到旧的「液态玻璃」配色值时按「晶透白 + 玻璃风格」解释，
    /// 不改写 UserDefaults 也能立刻渲染出正确主题。
    static func selection(colorRaw: String, styleRaw: String) -> (ThemeType, ThemeStyle) {
        let isLegacyGlass = colorRaw == legacyGlassRaw
        let color = ThemeType(rawValue: isLegacyGlass ? ThemeType.crystal.rawValue : colorRaw) ?? .crystal
        let style: ThemeStyle = isLegacyGlass ? .liquidGlass : (ThemeStyle(rawValue: styleRaw) ?? .classic)
        return (color, style)
    }

    /// 把旧的单键选择写回成新的两个键，这样两个下拉都能正确回显选中项
    @discardableResult
    static func migrateLegacySelection() -> Bool {
        let defaults = UserDefaults.standard
        guard defaults.string(forKey: colorKey) == legacyGlassRaw else { return false }
        defaults.set(ThemeType.crystal.rawValue, forKey: colorKey)
        defaults.set(ThemeStyle.liquidGlass.rawValue, forKey: styleKey)
        return true
    }
}

extension Color {
    /// 与另一个颜色线性混合：fraction = 0 全靠自己，1 全靠对方
    func blended(with other: Color, fraction: Double) -> Color {
        let f = min(max(fraction, 0), 1)
        guard let from = NSColor(self).usingColorSpace(.sRGB),
              let to = NSColor(other).usingColorSpace(.sRGB) else { return self }
        guard let mixed = from.blended(withFraction: f, of: to) else { return self }
        return Color(nsColor: mixed)
    }

    /// 在色相环上平移，按需改写饱和与亮度：光斑要的是「带颜色的雾」，不是实心强调色。
    /// HSB 自己算，NSColorSpace 没有现成的 HSV 空间可用。
    func hueShifted(by delta: Double, saturation satOverride: Double? = nil, brightness briOverride: Double? = nil) -> Color {
        guard let rgb = NSColor(self).usingColorSpace(.sRGB) else { return self }
        let r = Double(rgb.redComponent), g = Double(rgb.greenComponent), b = Double(rgb.blueComponent)
        let peak = max(r, g, b), floor_ = min(r, g, b), span = peak - floor_
        guard span > 0 else { return self }        // 纯灰没有色相，转了也是白转
        var hue: Double
        if peak == r { hue = ((g - b) / span).truncatingRemainder(dividingBy: 6) }
        else if peak == g { hue = (b - r) / span + 2 }
        else { hue = (r - g) / span + 4 }
        hue /= 6
        if hue < 0 { hue += 1 }
        hue = (hue + delta).truncatingRemainder(dividingBy: 1)
        if hue < 0 { hue += 1 }
        let clip: (Double) -> Double = { min(max($0, 0), 1) }
        return Color(hue: hue,
                     saturation: clip(satOverride ?? span / peak),
                     brightness: clip(briOverride ?? peak),
                     opacity: Double(rgb.alphaComponent))
    }
}

extension AppGlassStyle {
    /// 一份通用玻璃配方：染色与五团光斑全部从这套配色的强调色派生，
    /// 换一套主题颜色，玻璃折射出来的就是那一套的颜色。
    static func liquidGlass(accent: Color, textPrimary: Color) -> AppGlassStyle {
        AppGlassStyle(
            material: .regularMaterial,
            tint: accent.blended(with: .white, fraction: 0.58).opacity(0.44),
            sheen: LinearGradient(
                colors: [Color.white.opacity(0.75), Color.white.opacity(0.06)],
                startPoint: .top,
                endPoint: UnitPoint(x: 0.5, y: 0.34)
            ),
            border: Color(hex: "FFFFFF").opacity(0.92),
            edge: textPrimary.opacity(0.13),
            shadow: textPrimary.opacity(0.15),
            // 一团强调色铺底 + 四角四团同明度陪衬色，保证玻璃挪到哪里都折射得出颜色
            glows: [
                AppGlassGlow(color: accent, center: UnitPoint(x: 0.50, y: 0.42), spread: 1.00, opacity: 0.12),
                AppGlassGlow(color: accent.hueShifted(by: 0.06, brightness: 0.98), center: UnitPoint(x: 0.12, y: 0.02), spread: 0.62, opacity: 0.40),
                AppGlassGlow(color: accent.hueShifted(by: -0.10, brightness: 0.96), center: UnitPoint(x: 0.97, y: 0.16), spread: 0.55, opacity: 0.34),
                AppGlassGlow(color: accent.hueShifted(by: 0.48, saturation: 0.55), center: UnitPoint(x: 0.05, y: 0.99), spread: 0.60, opacity: 0.30),
                AppGlassGlow(color: accent.hueShifted(by: -0.42, saturation: 0.50), center: UnitPoint(x: 0.93, y: 0.95), spread: 0.52, opacity: 0.28)
            ],
            cornerRadius: 14
        )
    }
}

extension AppTheme {
    /// 把一套经典配色改写成液态玻璃版：文字与卡片底色沿用配色本身，
    /// 只补上玻璃材质与向白色靠拢的三段底渐变，所以 5 套主题颜色都能玻璃化。
    func applyingLiquidGlass() -> AppTheme {
        var glassy = self
        glassy.glass = AppGlassStyle.liquidGlass(accent: accent, textPrimary: textPrimary)
        // 三段底：左上最浅、右下最深，配下面 backdropLayer 的斜向渐变
        glassy.backdrop = [
            background.blended(with: .white, fraction: 0.55),
            accent.blended(with: .white, fraction: 0.86),
            background.blended(with: .white, fraction: 0.30)
        ]
        return glassy
    }
}

// ==========================================
// 🧊 4. 液态玻璃：主题化底与背景层
// ==========================================
extension AppTheme {
    /// 页面最底层。玻璃主题画浅色渐变加几团彩色光斑，让上层玻璃有颜色可折射；
    /// 其他主题还是那一片纯色。
    @ViewBuilder
    var backdropLayer: some View {
        if backdrop.isEmpty {
            background
        } else {
            ZStack {
                // 左浅右深、上浅下深：从左上到右下的斜向渐变
                LinearGradient(colors: backdrop, startPoint: .topLeading, endPoint: .bottomTrailing)
                ForEach(Array((glass?.glows ?? []).enumerated()), id: \.offset) { _, glow in
                    EllipticalGradient(
                        colors: [glow.color.opacity(glow.opacity), Color.clear],
                        center: glow.center,
                        startRadiusFraction: 0,
                        endRadiusFraction: glow.spread
                    )
                }
                // 顶部一道白色反光：玻璃带压在下面才分得出前后层次
                LinearGradient(
                    colors: [Color.white.opacity(0.5), Color.clear],
                    startPoint: .top,
                    endPoint: UnitPoint(x: 0.5, y: 0.26)
                )
            }
        }
    }

    /// 标题栏该染成什么颜色。玻璃主题的页面底是渐变，标题栏不同色就会硬生生出一条白带；
    /// 其他主题交还系统默认。
    var windowTint: Color? { isLiquidGlass ? (backdrop.first ?? background) : nil }

}

/// 主题化底。三种落点共用一套实现，非玻璃主题各自退回原来的观感。
struct ThemeSurfaceModifier: ViewModifier {
    enum Mode {
        case card       // 实心卡片：玻璃主题上玻璃，其他主题纯色 surface + 淡阴影
        case vibrant    // 半透明控件：玻璃主题上玻璃，其他主题沿用 ultraThinMaterial
        case glassOnly  // 只在玻璃主题上加玻璃背板，其他主题一律原样
    }

    let theme: AppTheme
    let shape: AnyShape
    let mode: Mode
    /// 选中态染色：玻璃主题用来染玻璃，其他主题直接当填充色
    var tint: Color? = nil
    /// 要不要投影。海报玻璃垫这种满屏重复出现的元件走 false：一张投影就是一次离屏高斯模糊，
    /// 几十张同时在滚时省下来的就是那几帧。
    var elevated: Bool = true
    /// 滚动进行中：改用纯染色底代替系统玻璃。每块玻璃都要重新采样一次背后的内容，
    /// 整屏海报同时移动时这笔开销直接把帧率拖下去；手一停立刻换回真玻璃。
    var simplified: Bool = false
    /// 要不要磨砂。压在视频这种正在动的真实画面上时走 false：只折射、不糊一层毛玻璃上去。
    var frosted: Bool = true

    @ViewBuilder
    func body(content: Content) -> some View {
        if let glass = theme.glass {
            content
                .modifier(GlassPlateModifier(glass: glass, tintOverride: tint, shape: shape, simplified: simplified, frosted: frosted))
                // 高光与描边只是装饰，必须让点击穿过去，否则盖在底下的按钮全点不动
                .overlay {
                    ZStack {
                        shape.fill(glass.sheen)
                        shape.stroke(glass.border, lineWidth: 1).clipShape(shape)
                        shape.stroke(glass.edge, lineWidth: 1).clipShape(shape)
                    }
                    .allowsHitTesting(false)
                }
                .modifier(GlassDropShadowModifier(shadow: glass.shadow, elevated: elevated))
        } else {
            switch mode {
            case .card:
                content
                    .background(shape.fill(tint ?? theme.surface))
                    .shadow(color: Color.black.opacity(0.08), radius: 6, y: 3)
            case .vibrant:
                if let tint {
                    content.background(shape.fill(tint))
                } else {
                    content.background(shape.fill(.ultraThinMaterial))
                }
            case .glassOnly:
                content
            }
        }
    }
}

/// 玻璃片的投影。只给少量大块玻璃用，重复几十次的元件宁可不要。
private struct GlassDropShadowModifier: ViewModifier {
    let shadow: Color
    let elevated: Bool

    @ViewBuilder
    func body(content: Content) -> some View {
        if elevated {
            content.shadow(color: shadow, radius: 14, y: 7)
        } else {
            content
        }
    }
}

private struct GlassPlateModifier: ViewModifier {
    let glass: AppGlassStyle
    var tintOverride: Color? = nil
    let shape: AnyShape
    var simplified: Bool = false
    var frosted: Bool = true

    @ViewBuilder
    func body(content: Content) -> some View {
        let tint = tintOverride ?? glass.tint
        if #available(macOS 26.0, *), !simplified, frosted {
            // macOS 26 起用系统 Liquid Glass，折射与高光交给系统
            content.glassEffect(Glass.regular.tint(tint), in: shape)
        } else if #available(macOS 26.0, *), !simplified {
            // 净玻璃：只折射不磨砂，画面该是什么样还是什么样
            content.glassEffect(Glass.clear.tint(tint), in: shape)
        } else if simplified || !frosted {
            // 滚动中的临时底 / 净玻璃在旧系统上的兜底：只有一层带透明度的染色，不折射也不做背景模糊
            content.background(shape.fill(tint))
        } else {
            content
                .background(shape.fill(glass.material))
                .background(shape.fill(tint))
        }
    }
}

extension View {
    /// 卡片 / 面板底
    func themeSurface(_ theme: AppTheme, cornerRadius: CGFloat? = nil) -> some View {
        themeSurfaceModifier(theme, cornerRadius: cornerRadius, mode: .card)
    }

    /// 弹窗与浮层的统一底：底色铺主题背景（玻璃主题即左浅右深、上浅下深的渐变），
    /// 按钮、开关、进度条等控件的强调色也换成主题色
    func themeSheetChrome(_ theme: AppTheme) -> some View {
        tint(theme.accent).background(theme.backdropLayer)
    }

    /// 胶囊、列表行等半透明控件底；tint 用于选中态染色
    func themeVibrant(_ theme: AppTheme, capsule: Bool = false, cornerRadius: CGFloat? = nil, tint: Color? = nil) -> some View {
        let shape: AnyShape = capsule
            ? AnyShape(Capsule())
            : AnyShape(RoundedRectangle(
                cornerRadius: cornerRadius ?? theme.glass?.cornerRadius ?? 10,
                style: .continuous
            ))
        return modifier(ThemeSurfaceModifier(theme: theme, shape: shape, mode: .vibrant, tint: tint))
    }

    /// 自带背景的浮层（顶栏、海报卡）：只在玻璃主题下补一层玻璃。
    /// tint 传 .clear 得到不带染色的净玻璃：压在视频这类真实画面上时只折射，不糊一层颜色上去。
    func themeGlassPlate(_ theme: AppTheme, cornerRadius: CGFloat = 18, elevated: Bool = true, simplified: Bool = false, tint: Color? = nil, frosted: Bool = true) -> some View {
        themeSurfaceModifier(theme, cornerRadius: cornerRadius, mode: .glassOnly, tint: tint, elevated: elevated, simplified: simplified, frosted: frosted)
    }

    /// 顶栏按钮簇背板：玻璃主题下只给右上角那一簇按钮垫一块圆角玻璃，
    /// 玻璃贴着按钮外沿收口，不再横贯整条顶栏；其他主题原样不动。
    @ViewBuilder
    func themeTopBarCluster(_ theme: AppTheme, pad: CGFloat = 10, tint: Color? = nil, frosted: Bool = true) -> some View {
        if theme.isLiquidGlass {
            padding(EdgeInsets(top: pad * 0.6, leading: pad, bottom: pad * 0.6, trailing: pad))
                .themeGlassPlate(theme, cornerRadius: theme.glass?.cornerRadius ?? 14, tint: tint, frosted: frosted)
        } else {
            self
        }
    }

    /// 页眉胶囊：玻璃主题下给标题行补内边距并垫一颗玻璃胶囊，其他主题原样不动
    @ViewBuilder
    func themeGlassPill(_ theme: AppTheme, pad: CGFloat = 12, tint: Color? = nil, frosted: Bool = true) -> some View {
        if theme.isLiquidGlass {
            padding(EdgeInsets(top: pad * 0.7, leading: pad, bottom: pad * 0.7, trailing: pad))
                .themeGlassPlate(theme, cornerRadius: 999, tint: tint, frosted: frosted)
        } else {
            self
        }
    }

    /// 海报玻璃垫：玻璃砖严格对齐海报、只向外扩出一圈 pad，用 background 画所以不占布局，
    /// 片名与年份的排布、卡片尺寸都不受影响；其他主题什么都没有。
    @ViewBuilder
    func themeGlassMat(_ theme: AppTheme, pad: CGFloat = 8, cornerRadius: CGFloat = 16, simplified: Bool = false) -> some View {
        if theme.isLiquidGlass {
            background(
                Rectangle()
                    .fill(.clear)
                    .padding(-pad)
                    .themeGlassPlate(theme, cornerRadius: cornerRadius, elevated: false, simplified: simplified)
            )
        } else {
            self
        }
    }

    private func themeSurfaceModifier(_ theme: AppTheme, cornerRadius: CGFloat?, mode: ThemeSurfaceModifier.Mode, tint: Color? = nil, elevated: Bool = true, simplified: Bool = false, frosted: Bool = true) -> some View {
        modifier(ThemeSurfaceModifier(
            theme: theme,
            shape: AnyShape(RoundedRectangle(
                cornerRadius: cornerRadius ?? theme.glass?.cornerRadius ?? 12,
                style: .continuous
            )),
            mode: mode,
            tint: tint,
            elevated: elevated,
            simplified: simplified,
            frosted: frosted
        ))
    }
}


// ==========================================
// 🪟 5. 系统标题栏：跟着主题底走
// ==========================================
/// 把标题栏染成页面顶色并去掉分隔线。玻璃主题的页面底铺到内容区上沿，
/// 标题栏若还是系统默认就是一条硬白带，玻璃带压在下面很违和；非玻璃主题交还系统。
struct ThemeWindowStyler: NSViewRepresentable {
    /// 标题栏要染成的颜色，nil 表示用系统默认
    let topColor: Color?

    func makeNSView(context: Context) -> NSView {
        let view = NSView(frame: .zero)
        DispatchQueue.main.async { [topColor] in self.apply(topColor, to: view.window) }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        DispatchQueue.main.async { [topColor] in self.apply(topColor, to: nsView.window) }
    }

    private func apply(_ color: Color?, to window: NSWindow?) {
        guard let window else { return }
        window.titlebarAppearsTransparent = color != nil
        window.titlebarSeparatorStyle = color == nil ? .automatic : .none
        window.backgroundColor = color.map { NSColor($0) }
    }
}
