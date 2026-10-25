//
//  BridgeContractTests.swift
//  跨语言边界的契约锁。WPF 只会看 JSON，不会看 Swift 类型，所以这里逐字节钉住
//  「响应长什么样」，顺带验证手写的 JSON 读写在两个平台给出一样的字节。
//

import XCTest
@testable import OmniPlayCore

/// C ABI 回调不能捕获上下文，事件先落到这个进程级缓冲区再断言。
private nonisolated(unsafe) var pushedEvents: [String] = []

private func captureEvent(_ context: UnsafeMutableRawPointer?,
                          _ bytes: UnsafePointer<UInt8>?,
                          _ count: Int32) {
    guard let bytes, count > 0 else { return }
    pushedEvents.append(String(decoding: UnsafeBufferPointer(start: bytes, count: Int(count)), as: UTF8.self))
}

final class BridgeContractTests: XCTestCase {

    private var service = OmniPlayService()

    override func setUp() {
        super.setUp()
        service = OmniPlayService()
        pushedEvents = []
    }

    // MARK: - 助手

    private func call(_ method: String, _ params: JSONValue = .object([:])) throws -> JSONValue {
        let request = JSONValue.object(["method": .string(method), "params": params])
        let data = service.handle(requestJSON: request.serialized())
        let envelope = try XCTUnwrap(JSONValue.parse(data), "核心回了不是 JSON 的东西：\(String(decoding: data, as: UTF8.self))")
        XCTAssertEqual(envelope["ok"]?.boolValue, true, "调用 \(method) 失败：\(envelope["error"]?.stringValue ?? "?")")
        return envelope["result"] ?? .null
    }

    private func callError(_ method: String, _ params: JSONValue = .object([:])) throws -> String {
        let request = JSONValue.object(["method": .string(method), "params": params])
        let envelope = try XCTUnwrap(JSONValue.parse(service.handle(requestJSON: request.serialized())))
        XCTAssertEqual(envelope["ok"]?.boolValue, false)
        return envelope["error"]?.stringValue ?? ""
    }

    private func itemsJSON(_ entries: [JSONValue]) -> JSONValue { .array(entries) }

    private func entry(_ id: Int64, title: String, directors: [String] = [], casts: [String] = []) -> JSONValue {
        .object([
            "id": .int64(id),
            "title": .string(title),
            "directors": .array(directors.map { JSONValue.string($0) }),
            "casts": .array(casts.map { JSONValue.string($0) }),
        ])
    }

    // MARK: - 信封

    func testVersionDescribesTheBridge() throws {
        let result = try call("version")
        XCTAssertEqual(result["core"]?.stringValue, OmniPlayService.API.core)
        XCTAssertEqual(result["contract"]?.intValue, OmniPlayService.API.contract)
        XCTAssertEqual(result["platform"]?.stringValue, OmniPlayService.platformName)
        let features = try XCTUnwrap(result["features"]?.arrayValue).compactMap(\.stringValue)
        XCTAssertEqual(Set(features), Set(OmniPlayService.API.features))
        XCTAssertFalse(result["dataDirectory"]?.stringValue?.isEmpty ?? true, "宿主得知道库文件该放哪")
    }

    func testUnknownMethodGivesReadableChineseError() throws {
        let message = try callError("library.query")
        XCTAssertTrue(message.contains("library.query"), "错误里要带上没认出的方法名")
    }

    func testGarbageRequestComesBackAsAnErrorEnvelope() {
        for raw in ["", "{", "[]", "null", "not json at all"] {
            let envelope = JSONValue.parse(service.handle(requestJSON: Data(raw.utf8)))
            XCTAssertNotNil(envelope, "坏输入也要回一份 JSON：\(raw)")
            XCTAssertEqual(envelope?["ok"]?.boolValue, false, "\(raw) 应当被判为无效请求")
            XCTAssertFalse(envelope?["error"]?.stringValue?.isEmpty ?? true)
        }
    }

    func testEchoKeepsTypesIntact() throws {
        let params: JSONValue = .object([
            "flag": .bool(false),
            "count": .int(7),
            "ratio": .number(0.5),
            "text": .string("觅影 😀 \u{1F600} 换行\n制表\t完"),
            "list": .array([.int(1), .int(2)]),
            "nested": .object(["path": .string("D:\\媒体\\电影")]),
        ])
        XCTAssertEqual(try call("echo", params), params)
    }

    func testErrorsCarryNoResultKey() throws {
        let envelope = try XCTUnwrap(JSONValue.parse(service.handle(requestJSON:
            Data(#"{"method":"error.demo"}"#.utf8))))
        XCTAssertNil(envelope["result"], "出错时不要把空 result 一起甩给界面")
        XCTAssertEqual(envelope["error"]?.stringValue, "这是一条给界面看的错误说明")
    }

    // MARK: - JSON 读写

    func testIntegersNeverLeakDecimalPoint() {
        let json = JSONValue.object([
            "year": .int(2019),
            "id": .int64(9007199254740991),   // 2^53-1，Double 还能精确表示的上限
            "ratio": .number(0.5),
        ]).serializedString()
        XCTAssertEqual(json, #"{"id":9007199254740991,"ratio":0.5,"year":2019}"#,
                       "整数不能写成 2019.0，但真小数要照常保留")
    }

    func testEscapingRoundTrips() throws {
        let nasty = "引号\" 反斜杠\\ 换行\n 制表\t 控制符\u{1} 中文😀 emoji"
        let encoded = JSONValue.string(nasty).serializedString()
        XCTAssertEqual(try XCTUnwrap(JSONValue.parse(encoded)).stringValue, nasty)
    }

    func testSurrogatePairsDecodeToEmoji() throws {
        let decoded = try XCTUnwrap(JSONValue.parse(#""\ud83d\ude00""#))
        XCTAssertEqual(decoded.stringValue, "😀")
    }

    func testParserRejectsBrokenJSON() {
        for raw in ["{\"a\":}", "[1,2", "{\"a\":1,,\"b\":2}", "\"unclosed", "01", "{\"a\":1} trailing"] {
            XCTAssertNil(JSONValue.parse(raw), "\(raw) 应该解析失败")
        }
    }

    func testParserAcceptsWhitespaceAndEmptyContainers() throws {
        let value = try XCTUnwrap(JSONValue.parse("  { \"a\" : [ ] , \"b\": { } , \"c\": -1.5e2 }  "))
        XCTAssertEqual(value["a"]?.arrayValue, [])
        XCTAssertEqual(value["b"]?.objectValue, [:])
        XCTAssertEqual(value["c"]?.doubleValue, -150)
    }

    func testKeysComeOutSorted() {
        let json = JSONValue.object(["z": .int(1), "a": .int(2), "m": .int(3)]).serializedString()
        XCTAssertEqual(json, #"{"a":2,"m":3,"z":1}"#)
    }

    // MARK: - 业务方法

    func testThemeListCoversEveryPalette() throws {
        let result = try call("theme.list")
        let themes = try XCTUnwrap(result["themes"]?.arrayValue)
        XCTAssertEqual(themes.count, ThemeCatalog.palettes.count)
        XCTAssertEqual(themes.compactMap { $0["id"]?.stringValue }, ThemeCatalog.palettes.map(\.id))
        XCTAssertEqual(result["styles"]?.arrayValue?.compactMap(\.stringValue), ["classic", "liquidGlass"])
    }

    func testThemeGetAgreesWithCoreSpec() throws {
        let params: JSONValue = .object([
            "color": .string("linen"),
            "style": .string("liquidGlass"),
            "ignoreMe": .bool(true),   // 未知字段必须安静忽略
        ])
        let result = try call("theme.get", params)
        XCTAssertEqual(result["id"]?.stringValue, "linen")
        let expected = ThemeCatalog.resolve(color: "linen", style: "liquidGlass")
        XCTAssertEqual(result["glass"], themeSpecJSON(expected)["glass"],
                       "界面上屏的每一笔颜色都来自这份配方，不能漂")
    }

    func testHomeSortRoundTrip() throws {
        let params: JSONValue = .object([
            "key": .string("name"),
            "ascending": .bool(true),
            "items": itemsJSON([
                entry(11, title: "肖申克的救赎"),
                entry(12, title: "阿凡达"),
                entry(13, title: "流浪地球"),
            ]),
        ])
        let ids = try XCTUnwrap(call("home.sort", params)["ids"]?.arrayValue).compactMap(\.int64Value)
        XCTAssertEqual(ids, [12, 13, 11])
    }

    func testCategoryGroupsRoundTrip() throws {
        let params: JSONValue = .object([
            "mode": .string("cast"),
            "ascending": .bool(true),
            "items": itemsJSON([
                entry(1, title: "甲", casts: ["张国荣"]),
                entry(2, title: "乙", casts: ["张国荣", "梁朝伟"]),
                entry(3, title: "丙"),
            ]),
        ])
        let groups = try XCTUnwrap(call("category.groups", params)["groups"]?.arrayValue)
        XCTAssertEqual(groups.compactMap { $0["name"]?.stringValue }, ["梁朝伟", "张国荣", "未标注"],
                       "中文名按本地化序，没收主演信息的收进末尾的未标注")
        let Leslie = groups.first { $0["name"]?.stringValue == "张国荣" }
        XCTAssertEqual(Leslie?["count"]?.intValue, 2)
        XCTAssertEqual(Leslie?["id"]?.stringValue, categoryFolderID(kind: "cast", name: "张国荣"))
    }

    func testMissingItemsIsADescribedError() throws {
        let message = try callError("home.sort", .object(["key": .string("name")]))
        XCTAssertTrue(message.contains("items"))
    }

    // MARK: - 事件

    func testPushedEventsReachBothChannels() throws {
        service.push(topic: "no.listener.yet", payload: .object([:]))
        service.setEventCallback(context: nil, function: captureEvent)
        service.push(topic: "library.changed", payload: .object(["count": .int(42)]))

        XCTAssertEqual(pushedEvents.count, 1, "注册之前的推送不该被硬塞给宿主")
        let pushed = try XCTUnwrap(JSONValue.parse(Data(pushedEvents[0].utf8)))
        XCTAssertEqual(pushed["topic"]?.stringValue, "library.changed")
        XCTAssertEqual(pushed["payload"]?["count"]?.intValue, 42)

        let polled = try call("events.poll", .object(["since": .int(0)]))
        XCTAssertEqual(polled["cursor"]?.intValue, 2)
        XCTAssertEqual(polled["events"]?.arrayValue?.count, 2)
    }

    func testEventBufferStaysBounded() {
        for _ in 0..<600 { service.push(topic: "scan.progress", payload: .object([:])) }
        let (head, events) = service.eventsSince(0)
        XCTAssertEqual(head, 600)
        XCTAssertEqual(events.count, 512, "环形缓冲封顶，长时间刮削不能把内存吃光")
        XCTAssertEqual(events.first?.cursor, 89)
    }
}
