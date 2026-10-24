import AppKit
import Testing
import SwiftUI
@testable import OmniPlay

// 主题是「风格 × 颜色」两个维度拼出来的，最怕的是某套配色在某个风格下取不到颜色、
// 或者玻璃字段漏给了经典风格，所以这里按语义逐条断言。
@Suite("主题配色")
struct ThemeManagerTests {
    private static let glassRaw = ThemeStyle.liquidGlass.rawValue
    private static let classicRaw = ThemeStyle.classic.rawValue

    @Test("每套主题颜色都能取到成套配色，经典风格不带玻璃字段")
    func everyColorResolves() {
        for type in ThemeType.allCases {
            let colors = type.colors
            #expect(!colors.name.isEmpty)
            #expect(type.displayName.contains("("))
            // 底色和文字不能撞色，否则整页会糊成一片
            #expect(colors.background != colors.textPrimary)
            #expect(colors.surface != colors.textSecondary)
            #expect(!colors.isLiquidGlass, "\(type.rawValue) 是纯配色，不该带玻璃")
            #expect(colors.backdrop.isEmpty)
        }
    }

    @Test("rawValue 可以往返，旧设置里的主题不会丢")
    func rawValuesRoundTrip() {
        for type in ThemeType.allCases {
            #expect(ThemeType(rawValue: type.rawValue) == type)
        }
        for style in ThemeStyle.allCases {
            #expect(ThemeStyle(rawValue: style.rawValue) == style)
        }
        #expect(ThemeType(rawValue: "no-such-theme") == nil)
        #expect(ThemeStyle(rawValue: "no-such-style") == nil)
    }

    @Test("5 套配色都吃两种风格，玻璃只由风格决定")
    func everyColorSupportsBothStyles() {
        for type in ThemeType.allCases {
            let classic = AppThemeResolver.theme(colorRaw: type.rawValue, styleRaw: Self.classicRaw)
            #expect(!classic.isLiquidGlass)
            #expect(classic.preferredScheme == nil)

            let glass = AppThemeResolver.theme(colorRaw: type.rawValue, styleRaw: Self.glassRaw)
            #expect(glass.isLiquidGlass, "\(type.rawValue) 在玻璃风格下必须有玻璃层")
            #expect(glass.glass?.cornerRadius ?? 0 > 0)
            #expect(!glass.backdrop.isEmpty)
            #expect((glass.glass?.glows.count ?? 0) >= 3, "浅色玻璃至少要有三团光斑才折射得出来")
            #expect(glass.preferredScheme == .light)
            // 玻璃化只换底和材质，字色与卡片色沿用这套配色本身
            #expect(glass.textPrimary == type.colors.textPrimary)
            #expect(glass.textSecondary == type.colors.textSecondary)
            #expect(glass.surface == type.colors.surface)
        }
    }

    @Test("玻璃主题在每套配色下都是浅色：底色亮、文字深")
    func glassStaysLightForEveryColor() {
        for type in ThemeType.allCases {
            let glass = AppThemeResolver.theme(colorRaw: type.rawValue, styleRaw: Self.glassRaw)
            let stops = glass.backdrop.compactMap(luminance)
            #expect(stops.count == glass.backdrop.count, "渐变色得都能解析成 sRGB")
            #expect(stops.allSatisfy { $0 > 0.70 }, "\(type.rawValue) 的页面底每一档都必须是浅色")
            #expect((luminance(glass.textPrimary) ?? 1) < 0.25)
        }
    }

    @Test("玻璃风格的垫层交给玻璃本身，经典风格保持半透明纯色")
    func translucentFillsDiffer() {
        let glass = AppThemeResolver.theme(colorRaw: ThemeType.crystal.rawValue, styleRaw: Self.glassRaw)
        let light = ThemeType.crystal.colors
        #expect(glass.chipFill == Color.clear)
        #expect(glass.rowFill(selected: true) != glass.rowFill(selected: false))
        #expect(glass.heroDimming != light.heroDimming)
        #expect(glass.heroDimming > 0.45 && glass.heroDimming < 0.6)
        #expect(light.chipFill != Color.clear)
        #expect(light.subtleFill == light.surface)
    }

    @Test("标题栏只在玻璃风格下染色，取页面顶色")
    func windowTintOnlyForGlass() {
        #expect(ThemeType.rose.colors.windowTint == nil)
        let glass = AppThemeResolver.theme(colorRaw: ThemeType.rose.rawValue, styleRaw: Self.glassRaw)
        #expect(glass.windowTint == glass.backdrop.first)
    }

    @Test("旧的「液态玻璃」配色值等价于晶透白 + 玻璃风格")
    func legacyGlassSelectionStillResolves() {
        let (color, style) = AppThemeResolver.selection(colorRaw: "liquidGlass", styleRaw: "")
        #expect(color == .crystal)
        #expect(style == .liquidGlass)

        // 新键里写了风格就以新键为准
        let (mint, classic) = AppThemeResolver.selection(colorRaw: ThemeType.mint.rawValue, styleRaw: Self.classicRaw)
        #expect(mint == .mint)
        #expect(classic == .classic)

        // 两个键都取不到时退回默认配色 + 经典
        let (fallbackColor, fallbackStyle) = AppThemeResolver.selection(colorRaw: "no-such-theme", styleRaw: "no-such-style")
        #expect(fallbackColor == .crystal)
        #expect(fallbackStyle == .classic)
    }
}

// 用 sRGB 相对亮度判断一个颜色偏白还是偏黑，比逐处比对十六进制稳。
private func luminance(_ color: Color) -> Double? {
    guard let ns = NSColor(color).usingColorSpace(.sRGB), ns.numberOfComponents >= 3 else { return nil }
    let channels = [ns.redComponent, ns.greenComponent, ns.blueComponent].map(Double.init)
    let linear = channels.map { $0 <= 0.04045 ? $0 / 12.92 : pow(($0 + 0.055) / 1.055, 2.4) }
    return 0.2126 * linear[0] + 0.7152 * linear[1] + 0.0722 * linear[2]
}
