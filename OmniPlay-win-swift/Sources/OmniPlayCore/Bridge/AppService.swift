import Foundation

/// 边界契约：请求 `{ "method": "...", "params": { ... } }`，
/// 响应 `{ "ok": true, "result": ... }` 或 `{ "ok": false, "error": "给人看的一句话" }`。
/// 耗时动作先立即回「已受理」，进度靠 `events.poll` 与宿主注册的事件回调推。
public final class OmniPlayService {
    public struct API {
        public static let core = "0.1.0"
        public static let contract = 1
        public static let features = [
            "version", "echo", "error.demo",
            "theme.list", "theme.get",
            "home.sort", "category.groups",
            "events.poll", "events.push",
            "player.status", "player.start", "player.load", "player.pause", "player.stop",
        ]
    }

    public struct Event {
        public let cursor: Int
        public let topic: String
        public let payload: JSONValue
    }

    private let lock = NSLock()
    private var events: [Event] = []
    private var cursor = 0
    private var hostCallback: (context: UnsafeMutableRawPointer?,
                              function: (@convention(c) (UnsafeMutableRawPointer?, UnsafePointer<UInt8>?, Int32) -> Void)?)?

    /// 宿主可以在启动时注入的钩子集合，界面按平台各自实现（见 PlatformHooks）。
    public var hooks: PlatformHooks

    /// 播放器实例。界面只通过 player.* 方法驱动它，渲染表面（HWND / CAMetalLayer 指针）
    /// 由宿主在 player.start 里传进来。
    public let player: PlayerCore

    public init(hooks: PlatformHooks = PlatformHooks(), player: PlayerCore = PlayerCore()) {
        self.hooks = hooks
        self.player = player
    }

    // MARK: - 事件

    public func push(topic: String, payload: JSONValue) {
        lock.lock()
        cursor += 1
        let event = Event(cursor: cursor, topic: topic, payload: payload)
        events.append(event)
        if events.count > 512 { events.removeFirst(events.count - 512) }
        let callback = hostCallback
        lock.unlock()
        guard let callback, let function = callback.function else { return }
        // 事件走推送，界面不必轮询；回调在核心线程上触发，宿主自己 marshal 到 UI 线程。
        let data = JSONValue.object(["cursor": .int(event.cursor),
                                     "topic": .string(event.topic),
                                     "payload": event.payload]).serialized()
        data.withUnsafeBytes { raw in
            function(callback.context, raw.bindMemory(to: UInt8.self).baseAddress, Int32(data.count))
        }
    }

    public func setEventCallback(context: UnsafeMutableRawPointer?,
                                 function: (@convention(c) (UnsafeMutableRawPointer?, UnsafePointer<UInt8>?, Int32) -> Void)?) {
        lock.lock()
        hostCallback = function.map { (context, $0) }
        lock.unlock()
    }

    public func eventsSince(_ since: Int) -> (cursor: Int, events: [Event]) {
        lock.lock(); defer { lock.unlock() }
        return (cursor, events.filter { $0.cursor > since })
    }

    // MARK: - 入口

    public func handle(requestJSON: Data) -> Data {
        guard let request = JSONValue.parse(requestJSON), case .object = request else {
            return Self.error("请求不是一份 JSON 对象")
        }
        guard let method = request["method"]?.stringValue, !method.isEmpty else {
            return Self.error("请求缺少 method")
        }
        let params = request["params"] ?? .object([:])
        switch method {
        case "version":
            return Self.ok(.object([
                "core": .string(API.core),
                "contract": .int(API.contract),
                "features": .array(API.features.map { JSONValue.string($0) }),
                "platform": .string(Self.platformName),
                "dataDirectory": .string(hooks.dataDirectory().path),
            ]))
        case "echo":
            return Self.ok(params)
        case "error.demo":
            return Self.error("这是一条给界面看的错误说明")
        case "theme.list":
            return Self.ok(.object([
                "themes": .array(ThemeCatalog.palettes.map {
                    .object(["id": .string($0.id),
                             "displayName": .string($0.displayName),
                             "accent": .string($0.accent.hexARGB)])
                }),
                "styles": .array([.string("classic"), .string("liquidGlass")]),
            ]))
        case "theme.get":
            let color = params["color"]?.stringValue ?? "crystal"
            let style = params["style"]?.stringValue ?? "classic"
            return Self.ok(themeSpecJSON(ThemeCatalog.resolve(color: color, style: style)))
        case "home.sort":
            guard let items = Self.items(params["items"]) else { return Self.error("items 不是一组影视记录") }
            guard let key = HomeSortKey(rawOrDefault: params["key"]?.stringValue ?? "year") else {
                return Self.error("不支持的排序方式：\(params["key"]?.stringValue ?? "空")")
            }
            let sorted = sortHomeItems(items, key: key, ascending: params["ascending"]?.boolValue ?? false)
            return Self.ok(.object(["ids": .array(sorted.map { JSONValue.int64($0.id) })]))
        case "category.groups":
            guard let items = Self.items(params["items"]) else { return Self.error("items 不是一组影视记录") }
            let mode = CategoryMode(rawValue: params["mode"]?.stringValue ?? "none") ?? .none
            let groups = buildCategoryGroups(items: items, mode: mode,
                                             ascending: params["ascending"]?.boolValue ?? true)
            return Self.ok(.object([
                "groups": .array(groups.map {
                    .object([
                        "id": .string($0.id),
                        "kind": .string($0.kind),
                        "name": .string($0.name),
                        "count": .int($0.itemIDs.count),
                        "itemIds": .array($0.itemIDs.map { JSONValue.int64($0) }),
                    ])
                })
            ]))
        case "events.poll":
            let since = params["since"]?.intValue ?? 0
            let (head, list) = eventsSince(since)
            return Self.ok(.object([
                "cursor": .int(head),
                "events": .array(list.map {
                    .object(["cursor": .int($0.cursor), "topic": .string($0.topic), "payload": $0.payload])
                }),
            ]))
        case "events.push":
            // 宿主也能造一条事件：ABI 探针用它验「回调指针跨语言可用」
            push(topic: params["topic"]?.stringValue ?? "debug",
                 payload: params["payload"] ?? .object([:]))
            return Self.ok(.object(["cursor": .int(eventsSince(0).cursor)]))
        case "player.status":
            return Self.ok(Self.statusJSON(player.status))
        case "player.start":
            // wid 缺省就走 headless（vo=null / ao=null），CI 里没有窗口也能验整条链路
            let wid = params["wid"]?.int64Value ?? 0
            let started = wid == 0
                ? player.start(headless: true)
                : player.start(headless: false,
                               renderSurface: UnsafeMutableRawPointer(bitPattern: Int(wid)))
            if started, let path = params["path"]?.stringValue { player.load(path) }
            return Self.ok(.object([
                "started": .bool(started),
                "reason": player.unavailableReason.map { JSONValue.string($0) } ?? .null,
            ]))
        case "player.load":
            guard let path = params["path"]?.stringValue else { return Self.error("缺少要播放的路径") }
            let loaded = player.load(path)
            return Self.ok(.object(["loaded": .bool(loaded)]))
        case "player.pause":
            player.setPaused(params["paused"]?.boolValue ?? true)
            return Self.ok(.object(["paused": .bool(params["paused"]?.boolValue ?? true)]))
        case "player.stop":
            player.shutdown()
            return Self.ok(.object(["stopped": .bool(true)]))
        default:
            return Self.error("核心不认识的方法：\(method)")
        }
    }

    // MARK: - 小工具

    public static func ok(_ result: JSONValue) -> Data {
        JSONValue.object(["ok": .bool(true), "result": result]).serialized()
    }

    public static func error(_ message: String) -> Data {
        JSONValue.object(["ok": .bool(false), "error": .string(message)]).serialized()
    }

    public static func items(_ value: JSONValue?) -> [LibraryMovie]? {
        guard case .array(let raw)? = value else { return nil }
        var items: [LibraryMovie] = []
        for entry in raw {
            guard let id = entry["id"]?.int64Value else { return nil }
            items.append(LibraryMovie(
                id: id,
                title: entry["title"]?.stringValue ?? "",
                releaseDate: entry["releaseDate"]?.stringValue,
                voteAverage: entry["voteAverage"]?.doubleValue,
                directors: entry["directors"]?.arrayValue?.compactMap { $0.stringValue } ?? [],
                casts: entry["casts"]?.arrayValue?.compactMap { $0.stringValue } ?? []
            ))
        }
        return items
    }

    public static func statusJSON(_ status: PlayerStatus) -> JSONValue {
        .object([
            "available": .bool(status.available),
            "timePos": .number(status.timePos),
            "duration": .number(status.duration),
            "paused": .bool(status.paused),
            "idle": .bool(status.idle),
            "reason": status.reason.map { JSONValue.string($0) } ?? .null,
        ])
    }

    public static var platformName: String {
        #if os(Windows)
        return "windows"
        #elseif os(macOS)
        return "macos"
        #elseif os(Linux)
        return "linux"
        #else
        return "unknown"
        #endif
    }
}
