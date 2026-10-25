import CSQLite
import Foundation

/// SQLite 的薄封装，只做核心要的那四件事：执行、查询、写入、事务。
///
/// 自带的合并源而不是 GRDB：GRDB 在 Windows 上编不过（docs/SPIKE.md §1），而 mac 与
/// Windows 必须跑同一份库、同一个 SQLite 版本，自己包一层反而最省心。
/// 值只覆盖 NULL / INTEGER / REAL / TEXT——库里不存二进制，多一种就多一处跨平台的坑。
public final class SQLiteDatabase {
    public enum Failure: Error, CustomStringConvertible {
        case open(reason: String)
        case prepare(sql: String, reason: String)
        case step(reason: String)

        public var description: String {
            switch self {
            case .open(let reason): return "打不开数据库：\(reason)"
            case .prepare(let sql, let reason): return "语句准备失败：\(reason)\n\(sql)"
            case .step(let reason): return "语句执行失败：\(reason)"
            }
        }
        public var message: String { description }
    }

    public enum Value {
        case null
        case int(Int64)
        case real(Double)
        case text(String)
    }

    /// 一行结果。按列名取，取不到就是默认值——上层要区分 NULL 的字段自己先判 hasValue。
    public struct Row {
        fileprivate let names: [String]
        fileprivate let values: [Value]

        public func value(_ column: String) -> Value? {
            guard let at = names.firstIndex(of: column) else { return nil }
            return values[at]
        }
        public func hasValue(_ column: String) -> Bool {
            guard let found = value(column) else { return false }
            if case .null = found { return false }
            return true
        }
        public func int64(_ column: String, default fallback: Int64 = 0) -> Int64 {
            switch value(column) {
            case .int(let raw)?: return raw
            case .real(let raw)?: return Int64(raw)
            case .text(let raw)?: return Int64(raw) ?? fallback
            default: return fallback
            }
        }
        public func double(_ column: String, default fallback: Double = 0) -> Double {
            switch value(column) {
            case .real(let raw)?: return raw
            case .int(let raw)?: return Double(raw)
            case .text(let raw)?: return Double(raw) ?? fallback
            default: return fallback
            }
        }
        public func text(_ column: String) -> String? {
            if case .text(let raw)? = value(column) { return raw }
            return nil
        }
        public func flag(_ column: String) -> Bool { int64(column) != 0 }
    }

    public struct WriteResult {
        public let lastRowID: Int64
        public let changes: Int
    }

    public let fileURL: URL
    private var handle: OpaquePointer?
    private let lock = NSLock()

    public init(fileURL: URL) throws {
        self.fileURL = fileURL
        try FileManager.default.createDirectory(
            at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        var db: OpaquePointer?
        let flags = SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX
        let code = fileURL.path.withCString { sqlite3_open_v2($0, &db, flags, nil) }
        guard code == SQLITE_OK, let db else {
            let reason = db.map { String(cString: sqlite3_errmsg($0)) } ?? "SQLite 返回码 \(code)"
            if let db { sqlite3_close_v2(db) }
            throw Failure.open(reason: reason)
        }
        handle = db
        // WAL：界面读的时候不等着写入方撒手；外键：删片时 videoFile 跟着走
        for pragma in ["PRAGMA journal_mode=WAL", "PRAGMA foreign_keys=ON", "PRAGMA synchronous=NORMAL"] {
            try exec(pragma)
        }
    }

    deinit { close() }

    public func close() {
        lock.lock(); defer { lock.unlock() }
        if let handle { sqlite3_close_v2(handle) }
        handle = nil
    }

    public func exec(_ sql: String) throws {
        lock.lock(); defer { lock.unlock() }
        guard let handle else { throw Failure.step(reason: "数据库已关闭") }
        var message: UnsafeMutablePointer<CChar>?
        let code = sql.withCString { sqlite3_exec(handle, $0, nil, nil, &message) }
        guard code == SQLITE_OK else {
            let reason = message.map { String(cString: $0) } ?? "SQLite 返回码 \(code)"
            if let message { sqlite3_free(message) }
            throw Failure.step(reason: reason)
        }
    }

    @discardableResult
    public func run(_ sql: String, _ params: [Value] = []) throws -> WriteResult {
        lock.lock(); defer { lock.unlock() }
        let statement = try prepare(sql, params)
        defer { sqlite3_finalize(statement) }
        let code = sqlite3_step(statement)
        guard code == SQLITE_DONE || code == SQLITE_ROW else {
            throw Failure.step(reason: lastReason)
        }
        return WriteResult(lastRowID: sqlite3_last_insert_rowid(handle),
                           changes: Int(sqlite3_changes(handle)))
    }

    public func query(_ sql: String, _ params: [Value] = []) throws -> [Row] {
        lock.lock(); defer { lock.unlock() }
        let statement = try prepare(sql, params)
        defer { sqlite3_finalize(statement) }
        var rows: [Row] = []
        while sqlite3_step(statement) == SQLITE_ROW {
            let count = Int(sqlite3_column_count(statement))
            var names: [String] = []
            var values: [Value] = []
            for index in 0..<count {
                names.append(String(cString: sqlite3_column_name(statement, Int32(index))))
                switch sqlite3_column_type(statement, Int32(index)) {
                case SQLITE_INTEGER: values.append(.int(sqlite3_column_int64(statement, Int32(index))))
                case SQLITE_FLOAT: values.append(.real(sqlite3_column_double(statement, Int32(index))))
                case SQLITE_NULL: values.append(.null)
                default:
                    let text = sqlite3_column_text(statement, Int32(index))
                    values.append(text.map { .text(String(cString: UnsafeRawPointer($0).assumingMemoryBound(to: CChar.self))) } ?? .null)
                }
            }
            rows.append(Row(names: names, values: values))
        }
        return rows
    }

    public func first(_ sql: String, _ params: [Value] = []) throws -> Row? {
        try query(sql, params).first
    }

    public func transaction(_ body: () throws -> Void) throws {
        try exec("BEGIN IMMEDIATE")
        do {
            try body()
        } catch {
            try? exec("ROLLBACK")
            throw error
        }
        try exec("COMMIT")
    }

    // MARK: - 内部

    private var lastReason: String {
        handle.map { String(cString: sqlite3_errmsg($0)) } ?? "数据库已关闭"
    }

    private func prepare(_ sql: String, _ params: [Value]) throws -> OpaquePointer {
        guard let handle else { throw Failure.step(reason: "数据库已关闭") }
        var statement: OpaquePointer?
        let code = sql.withCString { sqlite3_prepare_v2(handle, $0, -1, &statement, nil) }
        guard code == SQLITE_OK, let statement else {
            throw Failure.prepare(sql: sql, reason: lastReason)
        }
        let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)
        for (offset, param) in params.enumerated() {
            let index = Int32(offset + 1)
            let result: Int32
            switch param {
            case .null:
                result = sqlite3_bind_null(statement, index)
            case .int(let raw):
                result = sqlite3_bind_int64(statement, index, raw)
            case .real(let raw):
                result = sqlite3_bind_double(statement, index, raw)
            case .text(let raw):
                result = raw.withCString { sqlite3_bind_text(statement, index, $0, -1, transient) }
            }
            guard result == SQLITE_OK else {
                sqlite3_finalize(statement)
                throw Failure.prepare(sql: sql, reason: "第 \(index) 个参数绑不上（\(result)）")
            }
        }
        return statement
    }
}
