//
//  HomeSortTests.swift
//  首页排序与分类分组的规则锁。这些断言在 mac 与 Windows 上必须给出同样的结果，
//  不一致就说明本机的 CLDR 排序不可信，见 docs/SPIKE.md。
//

import XCTest
@testable import OmniPlayCore

final class HomeSortTests: XCTestCase {

    private func item(_ id: Int64, _ title: String, date: String? = nil, rating: Double? = nil,
                      directors: [String] = [], casts: [String] = []) -> LibraryMovie {
        LibraryMovie(id: id, title: title, releaseDate: date, voteAverage: rating,
                    directors: directors, casts: casts)
    }

    private func titles(_ items: [LibraryMovie]) -> [String] { items.map(\.title) }

    // MARK: - 名称排序

    func testChineseTitlesSortByPinyin() {
        let items = [
            item(1, "肖申克的救赎"),
            item(2, "阿凡达"),
            item(3, "流浪地球"),
        ]
        XCTAssertEqual(titles(sortHomeItems(items, key: .name, ascending: true)),
                       ["阿凡达", "流浪地球", "肖申克的救赎"],
                       "中文名必须按 CLDR 拼音序，Windows 上失败说明 collation 没走对")
    }

    func testNumericFragmentsCompareAsNumbers() {
        let items = [item(1, "玩具总动员10"), item(2, "玩具总动员2"), item(3, "玩具总动员1")]
        XCTAssertEqual(titles(sortHomeItems(items, key: .name, ascending: true)),
                       ["玩具总动员1", "玩具总动员2", "玩具总动员10"],
                       "数字要按数值比，不能让 10 排在 2 前面")
    }

    func testLatinAndChineseKeepOneStableOrder() {
        let items = [item(1, "阿凡达"), item(2, "Avatar"), item(3, "2001: A Space Odyssey")]
        let ascending = sortHomeItems(items, key: .name, ascending: true)
        XCTAssertEqual(Set(ascending.map(\.id)), Set([1, 2, 3]), "排序不能吞条目")
        XCTAssertEqual(titles(sortHomeItems(items, key: .name, ascending: false)),
                       titles(sortHomeItems(items, key: .name, ascending: true)).reversed(),
                       "降序必须是升序的完全翻转，否则两个平台的分组顺序会打架")
    }

    // MARK: - 年份 / 评分

    func testMissingYearSortsAsOldest() {
        let items = [
            item(1, "缺年份", date: nil),
            item(2, "2019", date: "2019-05-01"),
            item(3, "1994", date: "1994-09-10"),
        ]
        XCTAssertEqual(titles(sortHomeItems(items, key: .year, ascending: true)),
                       ["缺年份", "1994", "2019"])
        XCTAssertEqual(titles(sortHomeItems(items, key: .year, ascending: false)),
                       ["2019", "1994", "缺年份"])
    }

    func testMissingRatingSortsAsZero() {
        let items = [
            item(1, "无评分", rating: nil),
            item(2, "低分", rating: 3.2),
            item(3, "高分", rating: 9.1),
        ]
        XCTAssertEqual(titles(sortHomeItems(items, key: .rating, ascending: true)),
                       ["无评分", "低分", "高分"])
    }

    // MARK: - 分类分组

    func testOnePersonHoldsEveryMovieTheyTookPartIn() {
        let items = [
            item(1, "片甲", directors: ["诺兰"]),
            item(2, "片乙", directors: ["诺兰", "维伦纽瓦"]),
            item(3, "片丙", directors: ["维伦纽瓦"]),
        ]
        let groups = buildCategoryGroups(items: items, mode: .director, ascending: true)
        XCTAssertEqual(groups.count, 2)
        let nolan = groups.first { $0.name == "诺兰" }
        XCTAssertEqual(nolan?.itemIDs, [1, 2], "共用条目必须同时留在两个分类里")
        XCTAssertEqual(nolan?.id, categoryFolderID(kind: "director", name: "诺兰"))
    }

    func testForeignNamesAlwaysFollowChineseNames() {
        let items = [
            item(1, "A", directors: ["Nolan"]),
            item(2, "B", directors: ["王家卫"]),
            item(3, "C", directors: ["Anderson"]),
            item(4, "D", directors: ["是枝裕和"]),
        ]
        let ascending = buildCategoryGroups(items: items, mode: .director, ascending: true)
            .map(\.name)
        XCTAssertEqual(ascending, ["是枝裕和", "王家卫", "Anderson", "Nolan"],
                       "中文名在前且按本地化序，没中文译名的外文名单独排后面")
    }

    func testUnlabeledGroupSitsAtTheEnd() {
        let items = [
            item(1, "有主演", casts: ["周润发"]),
            item(2, "谁演的不知道"),
        ]
        let groups = buildCategoryGroups(items: items, mode: .cast, ascending: true)
        XCTAssertEqual(groups.map(\.name), ["周润发", "未标注"])
        XCTAssertEqual(groups.last?.kind, "cast")
    }

    func testModeNoneYieldsNoGroups() {
        XCTAssertTrue(buildCategoryGroups(items: [item(1, "甲")], mode: .none, ascending: true).isEmpty)
    }
}
