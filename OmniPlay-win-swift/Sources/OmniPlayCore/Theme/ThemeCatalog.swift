import Foundation

/// 一份颜色：分量都是 0...1 的 sRGB，alpha 单独带。
/// 为什么不用平台颜色类型：mac 用 SwiftUI Color、WPF 用 SolidColorBrush，两边各算一遍
/// 混合与转色相迟早对不上。颜色数学只在这里算一次，两端拿到的都是同一串数值。
public struct RGBA: Equatable, Codable {
    public let r: Double
    public let g: Double
    public let b: Double
    public let a: Double

    public init(r: Double, g: Double, b: Double, a: Double = 1) {
        let clip: (Double) -> Double = { min(max($0, 0), 1) }
        self.r = clip(r); self.g = clip(g); self.b = clip(b); self.a = clip(a)
    }

    /// 认 `RRGGBB`、`#RRGGBB`、`AARRGGBB` 三种写法，与 mac 的 Color(hex:) 一致。
    public init?(hex: String) {
        // 自己判十六进制：Unicode.Scalar 上没有 isHexDigit，Windows 的 Foundation 也不保证有
        let cleaned = hex.unicodeScalars.filter { scalar in
            switch scalar.value {
            case 0x30...0x39, 0x41...0x46, 0x61...0x66: return true
            default: return false
            }
        }
        guard !cleaned.isEmpty else { return nil }
        var value: UInt64 = 0
        guard Scanner(string: String(cleaned)).scanHexInt64(&value) else { return nil }
        let digits = cleaned.count
        switch digits {
        case 3:
            let ri = (value >> 8) & 0xF, gi = (value >> 4) & 0xF, bi = value & 0xF
            self.init(r: Double(ri * 17) / 255, g: Double(gi * 17) / 255, b: Double(bi * 17) / 255)
        case 6:
            self.init(r: Double((value >> 16) & 0xFF) / 255,
                      g: Double((value >> 8) & 0xFF) / 255,
                      b: Double(value & 0xFF) / 255)
        case 8:
            self.init(r: Double((value >> 16) & 0xFF) / 255,
                      g: Double((value >> 8) & 0xFF) / 255,
                      b: Double(value & 0xFF) / 255,
                      a: Double((value >> 24) & 0xFF) / 255)
        default:
            return nil
        }
    }

    public func blended(with other: RGBA, fraction: Double) -> RGBA {
        let f = min(max(fraction, 0), 1)
        return RGBA(r: r + (other.r - r) * f,
                    g: g + (other.g - g) * f,
                    b: b + (other.b - b) * f,
                    a: a + (other.a - a) * f)
    }

    public func withAlpha(_ alpha: Double) -> RGBA {
        RGBA(r: r, g: g, b: b, a: alpha)
    }

    /// 色相平移，按需覆盖饱和/亮度。算法与 mac 的 Color.hueShifted 一致：纯灰没有色相，
    /// 直接原样返回（转了也是白转）。
    public func hueShifted(by delta: Double, saturation satOverride: Double? = nil,
                           brightness briOverride: Double? = nil) -> RGBA {
        let peak = max(r, g, b), floorValue = min(r, g, b), span = peak - floorValue
        guard span > 0 else { return self }
        var hue: Double
        if peak == r { hue = ((g - b) / span).truncatingRemainder(dividingBy: 6) }
        else if peak == g { hue = (b - r) / span + 2 }
        else { hue = (r - g) / span + 4 }
        hue /= 6
        if hue < 0 { hue += 1 }
        hue = (hue + delta).truncatingRemainder(dividingBy: 1)
        if hue < 0 { hue += 1 }
        let clip: (Double) -> Double = { min(max($0, 0), 1) }
        // HSV -> RGB
        let s = clip(satOverride ?? span / peak)
        let v = clip(briOverride ?? peak)
        let c = v * s
        let x = c * (1 - abs((hue * 6).truncatingRemainder(dividingBy: 2) - 1))
        let m = v - c
        let sector = Int(hue * 6) % 6
        let (rr, gg, bb): (Double, Double, Double)
        switch sector {
        case 0: (rr, gg, bb) = (c, x, 0)
        case 1: (rr, gg, bb) = (x, c, 0)
        case 2: (rr, gg, bb) = (0, c, x)
        case 3: (rr, gg, bb) = (0, x, c)
        case 4: (rr, gg, bb) = (x, 0, c)
        default: (rr, gg, bb) = (c, 0, x)
        }
        return RGBA(r: rr + m, g: gg + m, b: bb + m, a: a)
    }

    /// WPF 直接能吃的 `#AARRGGBB`。
    public var hexARGB: String {
        func byte(_ v: Double) -> String { String(format: "%02X", Int((v * 255).rounded())) }
        return "#" + byte(a) + byte(r) + byte(g) + byte(b)
    }

    public static let white = RGBA(r: 1, g: 1, b: 1)
}

public struct GlowSpec: Equatable {
    public let color: RGBA
    /// 光斑中心与半径都用 0...1 的窗口比例，两端各自换算成自己的坐标系。
    public let x: Double
    public let y: Double
    public let spread: Double
    public let opacity: Double

    public init(color: RGBA, x: Double, y: Double, spread: Double, opacity: Double) {
        self.color = color; self.x = x; self.y = y; self.spread = spread; self.opacity = opacity
    }
}

public struct GlassSpec: Equatable {
    public let tint: RGBA
    public let sheenTop: RGBA
    public let sheenBottom: RGBA
    /// sheen 的终点（0.5, 0.34）：高光只铺上半截，玻璃才有「湿亮」而不是一块白板
    public let sheenEndX: Double
    public let sheenEndY: Double
    public let border: RGBA
    public let edge: RGBA
    public let shadow: RGBA
    public let glows: [GlowSpec]
    public let cornerRadius: Double
}

/// 渐变方向用结构体而不是元组：元组不遵守协议，会让 ThemeSpec 的 Equatable 合成失败。
public struct GradientDirection: Equatable {
    public let fromX: Double
    public let fromY: Double
    public let toX: Double
    public let toY: Double

    public init(fromX: Double, fromY: Double, toX: Double, toY: Double) {
        self.fromX = fromX; self.fromY = fromY; self.toX = toX; self.toY = toY
    }

    public static let topLeftToBottomRight = GradientDirection(fromX: 0, fromY: 0, toX: 1, toY: 1)
}

public struct ThemeSpec: Equatable {
    public let id: String
    public let displayName: String
    public let style: String          // classic | liquidGlass
    public let accent: RGBA
    public let background: RGBA
    public let surface: RGBA
    public let textPrimary: RGBA
    public let textSecondary: RGBA
    public let backdrop: [RGBA]       // 经典主题为空；玻璃主题为左上最浅、右下最深的三段
    public let backdropDirection: GradientDirection
    public let glass: GlassSpec?
    public let success: RGBA
    public let warning: RGBA
    public let danger: RGBA
}

/// 主题目录。色值表从 mac 的 ThemeManager 原样搬过来，混合与色相数学也由这份实现统一算，
/// 两端只是把同一串数字画出来。
public enum ThemeCatalog {
    public struct Palette {
        public let id: String
        public let displayName: String
        public let accent: RGBA
        public let background: RGBA
        public let surface: RGBA
        public let textPrimary: RGBA
        public let textSecondary: RGBA
    }

    public static let palettes: [Palette] = [
        Palette(id: "appleLight", displayName: "原生经典 (Apple Light)",
                accent: RGBA(hex: "007AFF")!, background: RGBA(hex: "F2F2F7")!,
                surface: RGBA(hex: "FFFFFF")!, textPrimary: RGBA(hex: "000000")!,
                textSecondary: RGBA(hex: "8E8E93")!),
        Palette(id: "crystal", displayName: "晶透白 (Crystal)",
                accent: RGBA(hex: "2F7CF6")!, background: RGBA(hex: "F4F6FA")!,
                surface: RGBA(hex: "FFFFFF")!, textPrimary: RGBA(hex: "1B2330")!,
                textSecondary: RGBA(hex: "6E7787")!),
        Palette(id: "linen", displayName: "亚麻灰 (Linen)",
                accent: RGBA(hex: "0D9488")!, background: RGBA(hex: "F5F4F0")!,
                surface: RGBA(hex: "FCFBF8")!, textPrimary: RGBA(hex: "2B2A26")!,
                textSecondary: RGBA(hex: "77756C")!),
        Palette(id: "mint", displayName: "薄荷清 (Mint)",
                accent: RGBA(hex: "10B981")!, background: RGBA(hex: "F1F8F4")!,
                surface: RGBA(hex: "FBFDFC")!, textPrimary: RGBA(hex: "143D31")!,
                textSecondary: RGBA(hex: "62847A")!),
        Palette(id: "rose", displayName: "北欧粉 (Nordic Rose)",
                accent: RGBA(hex: "E0559C")!, background: RGBA(hex: "FBF5F6")!,
                surface: RGBA(hex: "FEFBFC")!, textPrimary: RGBA(hex: "3C2531")!,
                textSecondary: RGBA(hex: "967B87")!),
    ]

    /// 旧版本把「液态玻璃」当成一种颜色存过，读到就按「晶透白 + 玻璃风格」解释。
    public static func resolve(color: String, style: String) -> ThemeSpec {
        let legacyGlass = color == "liquidGlass"
        let colorID = legacyGlass ? "crystal" : color
        let palette = palettes.first { $0.id == colorID } ?? palettes[1]
        let styleID = legacyGlass ? "liquidGlass" : (style == "liquidGlass" ? "liquidGlass" : "classic")
        return spec(palette: palette, glass: styleID == "liquidGlass")
    }

    static func spec(palette: Palette, glass: Bool) -> ThemeSpec {
        var backdrop: [RGBA] = []
        var glassSpec: GlassSpec? = nil
        if glass {
            glassSpec = liquidGlass(accent: palette.accent, textPrimary: palette.textPrimary)
            // 三段底：左上最浅、右下最深，配斜向渐变（左浅右深、上浅下深）
            backdrop = [
                palette.background.blended(with: .white, fraction: 0.55),
                palette.accent.blended(with: .white, fraction: 0.86),
                palette.background.blended(with: .white, fraction: 0.30),
            ]
        }
        return ThemeSpec(
            id: palette.id, displayName: palette.displayName, style: glass ? "liquidGlass" : "classic",
            accent: palette.accent, background: palette.background, surface: palette.surface,
            textPrimary: palette.textPrimary, textSecondary: palette.textSecondary,
            backdrop: backdrop,
            backdropDirection: .topLeftToBottomRight,
            glass: glassSpec,
            success: RGBA(hex: "1E8E4A")!, warning: RGBA(hex: "B25E09")!, danger: RGBA(hex: "D0021B")!
        )
    }

    /// 玻璃配方：染色与五团光斑全从强调色推导，换一套主题颜色，玻璃折射出来就是那一套颜色。
    static func liquidGlass(accent: RGBA, textPrimary: RGBA) -> GlassSpec {
        GlassSpec(
            tint: accent.blended(with: .white, fraction: 0.58).withAlpha(0.44),
            sheenTop: RGBA.white.withAlpha(0.75),
            sheenBottom: RGBA.white.withAlpha(0.06),
            sheenEndX: 0.5, sheenEndY: 0.34,
            border: RGBA.white.withAlpha(0.92),
            edge: textPrimary.withAlpha(0.13),
            shadow: textPrimary.withAlpha(0.15),
            glows: [
                GlowSpec(color: accent, x: 0.50, y: 0.42, spread: 1.00, opacity: 0.12),
                GlowSpec(color: accent.hueShifted(by: 0.06, brightness: 0.98), x: 0.12, y: 0.02, spread: 0.62, opacity: 0.40),
                GlowSpec(color: accent.hueShifted(by: -0.10, brightness: 0.96), x: 0.97, y: 0.16, spread: 0.55, opacity: 0.34),
                GlowSpec(color: accent.hueShifted(by: 0.48, saturation: 0.55), x: 0.05, y: 0.99, spread: 0.60, opacity: 0.30),
                GlowSpec(color: accent.hueShifted(by: -0.42, saturation: 0.50), x: 0.93, y: 0.95, spread: 0.52, opacity: 0.28),
            ],
            cornerRadius: 14
        )
    }
}

public func themeSpecJSON(_ spec: ThemeSpec) -> JSONValue {
    func color(_ c: RGBA) -> JSONValue {
        .object([
            "argb": .string(c.hexARGB),
            "r": .number(c.r), "g": .number(c.g), "b": .number(c.b), "a": .number(c.a),
        ])
    }
    var payload: [String: JSONValue] = [
        "id": .string(spec.id),
        "displayName": .string(spec.displayName),
        "style": .string(spec.style),
        "accent": color(spec.accent),
        "background": color(spec.background),
        "surface": color(spec.surface),
        "textPrimary": color(spec.textPrimary),
        "textSecondary": color(spec.textSecondary),
        "success": color(spec.success),
        "warning": color(spec.warning),
        "danger": color(spec.danger),
        "backdrop": .array(spec.backdrop.map(color)),
        "backdropFrom": .array([.number(spec.backdropDirection.fromX), .number(spec.backdropDirection.fromY)]),
        "backdropTo": .array([.number(spec.backdropDirection.toX), .number(spec.backdropDirection.toY)]),
    ]
    if let glass = spec.glass {
        payload["glass"] = .object([
            "tint": color(glass.tint),
            "sheenTop": color(glass.sheenTop),
            "sheenBottom": color(glass.sheenBottom),
            "sheenEnd": .array([.number(glass.sheenEndX), .number(glass.sheenEndY)]),
            "border": color(glass.border),
            "edge": color(glass.edge),
            "shadow": color(glass.shadow),
            "cornerRadius": .number(glass.cornerRadius),
            "glows": .array(glass.glows.map {
                .object([
                    "color": color($0.color.withAlpha($0.opacity)),
                    "center": .array([.number($0.x), .number($0.y)]),
                    "spread": .number($0.spread),
                ])
            }),
        ])
    } else {
        payload["glass"] = .null
    }
    return .object(payload)
}
