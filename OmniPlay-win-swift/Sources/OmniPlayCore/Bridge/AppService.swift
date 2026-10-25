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
            "library.stats", "library.sections", "library.section", "library.categories",
            "library.mark", "library.progress", "library.seed",
            "events.poll", "events.push",
            "player.status", "player.start", "player.load", "player.seek", "player.pause", "player.stop",
        ]
    }

    /// 闭包里想直接给用户看的一句话错误时用这个（区别于系统抛出的底层异常）
    public struct BridgeFailure: Error, CustomStringConvertible {
        public let message: String
        public init(_ message: String) { self.message = message }
        public var description: String { message }
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

    /// 片库。第一次有 library.* 请求时才开库：version、player.* 这些调用不该
    /// 顺手在磁盘上建文件，测试和 ABI 探针也因此能完全不碰数据库。
    private var store: LibraryStore?

    public init(hooks: PlatformHooks = PlatformHooks(), player: PlayerCore = PlayerCore()) {
        self.hooks = hooks
        self.player = player
    }

    /// 库文件位置。宿主想换目录就注入自己的 dataDirectory 钩子。
    public var libraryFileURL: URL { hooks.dataDirectory().appendingPathComponent("library.db") }

    private func library() throws -> LibraryStore {
        if let store { return store }
        var failures: [String] = []
        for candidate in libraryCandidates {
            do {
                let database = try SQLiteDatabase(fileURL: candidate)
                let opened = try LibraryStore(database: database)
                store = opened
                if !failures.isEmpty || !database.openNotes.isEmpty {
                    var notes = failures.map { "打不开 \($0)" }
                    notes += database.openNotes
                    push(topic: "library.location", payload: .object([
                        "file": .string(candidate.path),
                        "notes": .array(notes.map { JSONValue.string($0) }),
                    ]))
                }
                return opened
            } catch {
                failures.append("\(candidate.path)：\(error)")
            }
        }
        throw SQLiteDatabase.Failure.open(reason: failures.joined(separator: "\n"))
    }

    /// 首选数据目录；那个位置打不开（同步盘接管、目录只读、漫游配置没落地）就退回
    /// 临时目录。界面至少有东西可画，原因作为事件推给宿主显示。
    private var libraryCandidates: [URL] {
        [libraryFileURL,
         URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("OmniPlay/library.db")]
    }

    /// library.* 的统一出口：开库、空库铺演示数据、异常翻成给人看的一句话。
    private func libraryCall(seeded: Bool = true, _ body: (LibraryStore) throws -> JSONValue) -> Data {
        do {
            let store = try library()
            if seeded, try store.stats().movies == 0 {
                let inserted = try DemoSeed.run(in: store)
                push(topic: "library.changed", payload: .object(["seeded": .int(inserted)]))
            }
            return Self.ok(try body(store))
        } catch let failure as BridgeFailure {
            return Self.error(failure.message)
        } catch {
            return Self.error("片库操作失败：\(error)")
        }
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
            return Self.ok(Self.groupsJSON(buildCategoryGroups(items: items, mode: mode,
                                                              ascending: params["ascending"]?.boolValue ?? true)))
        case "library.stats":
            return libraryCall { try Self.statsJSON($0.stats()) }
        case "library.sections":
            return libraryCall { store in
                .object(["sections": .array(try store.sections().map(Self.sectionJSON)),
                         "stats": Self.statsJSON(try store.stats())])
            }
        case "library.section":
            guard let id = params["id"]?.stringValue else { return Self.error("缺少分组 id") }
            return libraryCall { store in
                guard let section = try store.section(id: id) else {
                    throw BridgeFailure("没有这个分组：\(id)")
                }
                return .object(["section": Self.sectionJSON(section)])
            }
        case "library.categories":
            let mode = CategoryMode(rawValue: params["mode"]?.stringValue ?? "director") ?? .director
            return libraryCall { store in
                Self.groupsJSON(try categoryGroups(store: store, mode: mode,
                                                  ascending: params["ascending"]?.boolValue ?? true))
            }
        case "library.mark":
            guard let fileID = params["fileId"]?.int64Value else { return Self.error("缺少影视文件 id") }
            let watched = params["watched"]?.boolValue ?? true
            return libraryCall { store in
                try store.setWatched(videoFileID: fileID, watched)
                push(topic: "library.changed",
                     payload: .object(["fileId": .int64(fileID), "watched": .bool(watched)]))
                return .object(["fileId": .int64(fileID), "watched": .bool(watched)])
            }
        case "library.progress":
            guard let fileID = params["fileId"]?.int64Value else { return Self.error("缺少影视文件 id") }
            let position = params["position"]?.doubleValue ?? 0
            let duration = params["duration"]?.doubleValue ?? 0
            return libraryCall { store in
                try store.recordProgress(videoFileID: fileID, position: position, duration: duration)
                let percent = duration > 0 ? min(100, max(0, Int((position / duration * 100).rounded()))) : 0
                return .object(["fileId": .int64(fileID), "percent": .int(percent)])
            }
        case "library.seed":
            return libraryCall(seeded: false) { store in
                let before = try store.stats().movies
                if before == 0 { try DemoSeed.run(in: store) }
                return .object(["movies": .int(try store.stats().movies), "seeded": .bool(before == 0)])
            }
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
        case "player.seek":
            let seconds = params["seconds"]?.doubleValue ?? 0
            let seeked = player.seek(seconds)
            return Self.ok(.object(["seeked": .bool(seeked), "seconds": .number(seconds)]))
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

    /// 分类页：库里的影视直接建桶，排序沿用 HomeSort
    private func categoryGroups(store: LibraryStore, mode: CategoryMode,
                                ascending: Bool) throws -> [CategoryGroup] {
        let items = try store.movies().map {
            LibraryMovie(id: $0.id, title: $0.title,
                         releaseDate: $0.year.map { String(format: "%04d-01-01", $0) },
                         voteAverage: nil, directors: $0.directors, casts: $0.casts)
        }
        return buildCategoryGroups(items: items, mode: mode, ascending: ascending)
    }

    static func groupsJSON(_ groups: [CategoryGroup]) -> JSONValue {
        .object([
            "groups": .array(groups.map {
                .object([
                    "id": .string($0.id),
                    "kind": .string($0.kind),
                    "name": .string($0.name),
                    "count": .int($0.itemIDs.count),
                    "itemIds": .array($0.itemIDs.map { JSONValue.int64($0) }),
                ])
            })
        ])
    }

    static func statsJSON(_ stats: LibraryStore.Stats) -> JSONValue {
        .object([
            "movies": .int(stats.movies), "files": .int(stats.files),
            "watched": .int(stats.watched), "resumable": .int(stats.resumable),
        ])
    }

    static func sectionJSON(_ section: LibraryStore.Section) -> JSONValue {
        .object([
            "id": .string(section.id),
            "title": .string(section.title),
            "count": .int(section.entries.count),
            "entries": .array(section.entries.map(Self.entryJSON)),
        ])
    }

    /// 海报墙的一条。hue 是占位色相，海报接上之后界面自己忽略它。
    static func entryJSON(_ entry: LibraryStore.Entry) -> JSONValue {
        .object([
            "fileId": .int64(entry.videoFileID),
            "movieId": .int64(entry.movie.id),
            "title": .string(entry.movie.title),
            "year": entry.movie.year.map { JSONValue.int($0) } ?? .null,
            "path": .string(entry.path),
            "duration": .number(entry.duration),
            "position": .number(entry.position),
            "percent": .int(entry.progressPercent),
            "watched": .bool(entry.watched),
            "hue": .number((entry.tintHue * 1000).rounded() / 1000),
            "genres": .array(entry.movie.genres.map { JSONValue.string($0) }),
            "directors": .array(entry.movie.directors.map { JSONValue.string($0) }),
            "casts": .array(entry.movie.casts.map { JSONValue.string($0) }),
        ])
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
