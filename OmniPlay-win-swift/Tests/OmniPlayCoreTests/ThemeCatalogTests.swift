//
//  ThemeCatalogTests.swift
//  主题配方是「算一次、两个界面共用」的那份唯一真源，WPF 只做翻译，
//  所以配方本身要有回归锁：改配色必须让测试先叫出来。
//

import XCTest
@testable import OmniPlayCore

final class ThemeCatalogTests: XCTestCase {

    /// #AARRGGBB：九个字符、首字符是 #、其余全是十六进制
    private func isArgbHex(_ value: String) -> Bool {
        value.count == 9 && value.hasPrefix("#") && value.dropFirst().allSatisfy(\.isHexDigit)
    }

    func testAllPalettesAreExposed() {
        XCTAssertEqual(ThemeCatalog.palettes.map(\.id),
                       ["appleLight", "crystal", "linen", "mint", "rose"])
        for palette in ThemeCatalog.palettes {
            XCTAssertFalse(palette.displayName.isEmpty)
            // 五套配色都是浅底，正文必须比背景与卡面暗，否则文字直接糊住
            XCTAssertGreaterThan(palette.background.luminance, palette.textPrimary.luminance,
                                 "\(palette.id) 背景应比正文亮")
            XCTAssertGreaterThan(palette.surface.luminance, palette.textPrimary.luminance,
                                 "\(palette.id) 卡面应比正文亮")
        }
    }

    func testUnknownColorFallsBackInsteadOfCrashing() {
        let spec = ThemeCatalog.resolve(color: "已删除的旧配色", style: "classic")
        XCTAssertEqual(spec.id, "crystal")
        XCTAssertNil(spec.glass, "经典风格不该带玻璃配方")
    }

    func testLegacyLiquidGlassColorStillMeansGlass() {
        let spec = ThemeCatalog.resolve(color: "liquidGlass", style: "classic")
        XCTAssertEqual(spec.id, "crystal")
        XCTAssertEqual(spec.style, "liquidGlass")
        XCTAssertNotNil(spec.glass, "老版本存的颜色值叫 liquidGlass，要照样出玻璃")
    }

    func testGlassBackdropGoesLightToDark() {
        let spec = ThemeCatalog.resolve(color: "mint", style: "liquidGlass")
        XCTAssertEqual(spec.backdrop.count, 3)
        let stops = spec.backdrop.map(\.luminance)
        XCTAssertGreaterThan(stops[0], stops[2], "左上必须比右下浅")
        XCTAssertEqual(stops[0], stops.max(), "左上必须是三段里最浅的一档")
        // 中段是主题色的晕染，允许比两端都深；它保证五套主题都带得出那层颜色
        XCTAssertLessThan(stops[1], stops[0])
        XCTAssertEqual(spec.backdropDirection, .topLeftToBottomRight)
    }

    func testGlassRecipeFollowsAccent() {
        let crystal = ThemeCatalog.resolve(color: "crystal", style: "liquidGlass")
        let rose = ThemeCatalog.resolve(color: "rose", style: "liquidGlass")
        XCTAssertNotEqual(crystal.glass, rose.glass, "染色与光斑必须跟着主题色走")
        XCTAssertEqual(crystal.glass?.cornerRadius, rose.glass?.cornerRadius)
        XCTAssertEqual(crystal.glass?.glows.count, 5)
    }

    func testEveryColorSerializesAsArgb() throws {
        let json = themeSpecJSON(ThemeCatalog.resolve(color: "rose", style: "liquidGlass"))
        let data = JSONValue.object(["ok": .bool(true), "result": json]).serialized()
        let back = try XCTUnwrap(JSONValue.parse(data))
        for key in ["accent", "background", "surface", "textPrimary", "textSecondary",
                    "success", "warning", "danger"] {
            let argb = try XCTUnwrap(back["result"]?[key]?["argb"]?.stringValue)
            XCTAssertTrue(isArgbHex(argb), "\(key) 必须是 #AARRGGBB，实际是 \(argb)")
        }
        let glass = try XCTUnwrap(back["result"]?["glass"]?.objectValue)
        for key in ["tint", "sheenTop", "sheenBottom", "border", "edge", "shadow"] {
            let argb = try XCTUnwrap(glass[key]?["argb"]?.stringValue)
            XCTAssertTrue(isArgbHex(argb), "玻璃配方 \(key) 格式不对：\(argb)")
        }
    }

    func testColorMathIsBounded() {
        let base = RGBA(hex: "007AFF")!
        XCTAssertEqual(base.blended(with: .white, fraction: 0), base)
        let full = base.blended(with: .white, fraction: 1)
        XCTAssertEqual(full.r, 1, accuracy: 1e-12)
        XCTAssertEqual(full.g, 1, accuracy: 1e-12)
        XCTAssertEqual(full.b, 1, accuracy: 1e-12)
        let shifted = base.hueShifted(by: 0.4, saturation: 2.0, brightness: 3.0)
        for channel in [shifted.r, shifted.g, shifted.b, shifted.a] {
            XCTAssertGreaterThanOrEqual(channel, 0)
            XCTAssertLessThanOrEqual(channel, 1, "分量越界会在 WPF 里直接抛异常")
        }
        // 纯灰没有色相，移色相应该原样返回
        let gray = RGBA(r: 0.5, g: 0.5, b: 0.5)
        XCTAssertEqual(gray.hueShifted(by: 0.3), gray)
    }

    func testHexAcceptsWhatMacWrites() {
        XCTAssertNotNil(RGBA(hex: "#007AFF"))
        XCTAssertNotNil(RGBA(hex: "007AFF"))
        XCTAssertNotNil(RGBA(hex: "80007AFF"))
        XCTAssertNotNil(RGBA(hex: "F7A"))
        XCTAssertNil(RGBA(hex: "zzzzzz"))
        XCTAssertNil(RGBA(hex: ""))
    }
}

private extension RGBA {
    /// 简单感知亮度，只用来做「深浅关系」这类粗判。
    var luminance: Double { 0.2126 * r + 0.7152 * g + 0.0722 * b }
}
