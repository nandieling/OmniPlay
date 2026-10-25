//
//  JSONValue.swift
//  OmniPlayCore —— 桥上的 JSON 值与手写的 JSON 读写
//
//  为什么不用 JSONSerialization / Codable：
//  1. Windows 上的 swift-corelibs-foundation 把 NSNumber 的布尔判断做得不一致
//     （CFGetTypeID 那套在 Windows 上不可用），「2019 到底是整数还是布尔」这种
//     事实在两个平台上可能被解析成不同结果。
//  2. JSONSerialization 输出浮点时会写 2019.0，跨平台逐字节比对的契约测试会假报警。
//  自己写一层约 200 行的读写，换来的是 mac / Windows 完全一致的字节级行为，
//  这一层是整座桥的地基，值得自己攥着。
//

import Foundation

public enum JSONValue {
    case null
    case bool(Bool)
    case number(Double)
    case string(String)
    case array([JSONValue])
    case object([String: JSONValue])
}

extension JSONValue: Equatable {}

// MARK: - 读

public extension JSONValue {
    static func parse(_ data: Data) -> JSONValue? {
        parse([UInt8](data))
    }

    static func parse(_ text: String) -> JSONValue? {
        parse([UInt8](text.utf8))
    }

    static func parse(_ bytes: [UInt8]) -> JSONValue? {
        var reader = JSONReader(bytes: bytes)
        guard let value = reader.readValue(depth: 0), reader.skipWhitespaceAndCheckEnd() else { return nil }
        return value
    }
}

private struct JSONReader {
    private let bytes: [UInt8]
    private var index: Int
    private static let maxDepth = 64

    init(bytes: [UInt8]) {
        self.bytes = bytes
        self.index = 0
    }

    mutating func skipWhitespaceAndCheckEnd() -> Bool {
        skipWhitespace()
        return index >= bytes.count
    }

    private mutating func skipWhitespace() {
        while index < bytes.count {
            switch bytes[index] {
            case UInt8(ascii: " "), UInt8(ascii: "\t"), UInt8(ascii: "\n"), UInt8(ascii: "\r"):
                index += 1
            default:
                return
            }
        }
    }

    private func peek() -> UInt8? {
        index < bytes.count ? bytes[index] : nil
    }

    fileprivate mutating func readValue(depth: Int) -> JSONValue? {
        guard depth < Self.maxDepth else { return nil }
        skipWhitespace()
        guard let head = peek() else { return nil }
        switch head {
        case UInt8(ascii: "{"): return readObject(depth: depth)
        case UInt8(ascii: "["): return readArray(depth: depth)
        case UInt8(ascii: "\""): return readString().map { JSONValue.string($0) }
        case UInt8(ascii: "t"): return readLiteral("true") ? .bool(true) : nil
        case UInt8(ascii: "f"): return readLiteral("false") ? .bool(false) : nil
        case UInt8(ascii: "n"): return readLiteral("null") ? .null : nil
        default: return readNumber()
        }
    }

    private mutating func readObject(depth: Int) -> JSONValue? {
        index += 1  // {
        var result: [String: JSONValue] = [:]
        skipWhitespace()
        if peek() == UInt8(ascii: "}") {
            index += 1
            return .object(result)
        }
        while true {
            skipWhitespace()
            guard peek() == UInt8(ascii: "\""), let key = readString() else { return nil }
            skipWhitespace()
            guard peek() == UInt8(ascii: ":") else { return nil }
            index += 1
            guard let value = readValue(depth: depth + 1) else { return nil }
            result[key] = value
            skipWhitespace()
            switch peek() {
            case UInt8(ascii: ","):
                index += 1
            case UInt8(ascii: "}"):
                index += 1
                return .object(result)
            default:
                return nil
            }
        }
    }

    private mutating func readArray(depth: Int) -> JSONValue? {
        index += 1  // [
        var result: [JSONValue] = []
        skipWhitespace()
        if peek() == UInt8(ascii: "]") {
            index += 1
            return .array(result)
        }
        while true {
            guard let value = readValue(depth: depth + 1) else { return nil }
            result.append(value)
            skipWhitespace()
            switch peek() {
            case UInt8(ascii: ","):
                index += 1
            case UInt8(ascii: "]"):
                index += 1
                return .array(result)
            default:
                return nil
            }
        }
    }

    private mutating func readString() -> String? {
        guard peek() == UInt8(ascii: "\"") else { return nil }
        index += 1
        // 转义序列一律就地还原成 UTF-8 字节，避免「原始字节 / 码点」两条通道拼接错位
        var utf8: [UInt8] = []
        while index < bytes.count {
            let byte = bytes[index]
            if byte == UInt8(ascii: "\"") {
                index += 1
                return String(decoding: utf8, as: UTF8.self)
            }
            if byte != UInt8(ascii: "\\") {
                utf8.append(byte)
                index += 1
                continue
            }
            index += 1
            guard index < bytes.count else { return nil }
            let escape = bytes[index]
            index += 1
            switch escape {
            case UInt8(ascii: "\""): utf8.append(UInt8(ascii: "\""))
            case UInt8(ascii: "\\"): utf8.append(UInt8(ascii: "\\"))
            case UInt8(ascii: "/"): utf8.append(UInt8(ascii: "/"))
            case UInt8(ascii: "b"): utf8.append(0x08)
            case UInt8(ascii: "f"): utf8.append(0x0C)
            case UInt8(ascii: "n"): utf8.append(0x0A)
            case UInt8(ascii: "r"): utf8.append(0x0D)
            case UInt8(ascii: "t"): utf8.append(0x09)
            case UInt8(ascii: "u"):
                guard let unit = readHex4() else { return nil }
                var scalarValue = UInt32(unit)
                // 遇 surrogate 对时把高低位拼回真正的码点
                if unit >= 0xD800, unit <= 0xDBFF, peek() == UInt8(ascii: "\\"),
                   index + 1 < bytes.count, bytes[index + 1] == UInt8(ascii: "u") {
                    index += 2
                    guard let low = readHex4(), low >= 0xDC00, low <= 0xDFFF else { return nil }
                    scalarValue = 0x10000 + ((UInt32(unit) - 0xD800) << 10) + (UInt32(low) - 0xDC00)
                }
                guard let scalar = Unicode.Scalar(scalarValue) else { return nil }
                utf8.append(contentsOf: Array(String(scalar).utf8))
            default:
                return nil
            }
        }
        return nil  // 未闭合
    }

    private mutating func readHex4() -> UInt16? {
        guard index + 4 <= bytes.count else { return nil }
        var value: UInt16 = 0
        for offset in 0..<4 {
            guard let digit = hexDigit(bytes[index + offset]) else { return nil }
            value = value << 4 | digit
        }
        index += 4
        return value
    }

    private func hexDigit(_ byte: UInt8) -> UInt16? {
        switch byte {
        case UInt8(ascii: "0")...UInt8(ascii: "9"): return UInt16(byte - UInt8(ascii: "0"))
        case UInt8(ascii: "a")...UInt8(ascii: "f"): return UInt16(byte - UInt8(ascii: "a") + 10)
        case UInt8(ascii: "A")...UInt8(ascii: "F"): return UInt16(byte - UInt8(ascii: "A") + 10)
        default: return nil
        }
    }

    private mutating func readNumber() -> JSONValue? {
        let start = index
        if peek() == UInt8(ascii: "-") { index += 1 }
        let firstDigit = index
        var digits = 0
        while let byte = peek(), byte >= UInt8(ascii: "0"), byte <= UInt8(ascii: "9") {
            index += 1
            digits += 1
        }
        if digits == 0 { return nil }
        if peek() == UInt8(ascii: ".") {
            index += 1
            var fraction = 0
            while let byte = peek(), byte >= UInt8(ascii: "0"), byte <= UInt8(ascii: "9") {
                index += 1
                fraction += 1
            }
            if fraction == 0 { return nil }
        }
        // 「01」不是合法 JSON 数字
        if digits > 1, bytes[firstDigit] == UInt8(ascii: "0") { return nil }
        if peek() == UInt8(ascii: "e") || peek() == UInt8(ascii: "E") {
            index += 1
            if peek() == UInt8(ascii: "+") || peek() == UInt8(ascii: "-") { index += 1 }
            var exp = 0
            while let byte = peek(), byte >= UInt8(ascii: "0"), byte <= UInt8(ascii: "9") {
                index += 1
                exp += 1
            }
            if exp == 0 { return nil }
        }
        return Double(String(decoding: bytes[start..<index], as: UTF8.self)).map { JSONValue.number($0) }
    }

    private mutating func readLiteral(_ literal: String) -> Bool {
        let length = literal.utf8.count
        guard index + length <= bytes.count else { return false }
        for (offset, byte) in literal.utf8.enumerated() where bytes[index + offset] != byte { return false }
        index += length
        return true
    }
}

// MARK: - 写

public extension JSONValue {
    /// 键排序输出：测试可以逐字节比对，日志 diff 也干净。
    func serialized() -> Data {
        Data(utf8Bytes())
    }

    func serializedString() -> String {
        String(decoding: utf8Bytes(), as: UTF8.self)
    }

    func utf8Bytes() -> [UInt8] {
        var out: [UInt8] = []
        write(into: &out)
        return out
    }

    func write(into out: inout [UInt8]) {
        switch self {
        case .null:
            out.append(contentsOf: Array("null".utf8))
        case .bool(let value):
            out.append(contentsOf: Array((value ? "true" : "false").utf8))
        case .number(let value):
            out.append(contentsOf: Array(JSONValue.numberText(value).utf8))
        case .string(let value):
            JSONValue.writeEscaped(value, into: &out)
        case .array(let items):
            out.append(UInt8(ascii: "["))
            for (offset, item) in items.enumerated() {
                if offset > 0 { out.append(UInt8(ascii: ",")) }
                item.write(into: &out)
            }
            out.append(UInt8(ascii: "]"))
        case .object(let dict):
            out.append(UInt8(ascii: "{"))
            for (offset, key) in dict.keys.sorted().enumerated() {
                if offset > 0 { out.append(UInt8(ascii: ",")) }
                JSONValue.writeEscaped(key, into: &out)
                out.append(UInt8(ascii: ":"))
                dict[key]!.write(into: &out)
            }
            out.append(UInt8(ascii: "}"))
        }
    }

    /// 整数值不带小数点，避免 WPF 侧把 2019 读成 2019.0。
    private static func numberText(_ value: Double) -> String {
        guard value == value.rounded() else { return String(value) }
        if abs(value) < 9007199254740992 { return String(Int64(value)) }   // 2^53 内能精确还原
        return String(format: "%.0f", value)
    }

    private static func writeEscaped(_ text: String, into out: inout [UInt8]) {
        out.append(UInt8(ascii: "\""))
        for scalar in text.unicodeScalars {
            let uni = scalar.value
            if uni == 0x22 { out.append(contentsOf: [0x5C, 0x22]) }
            else if uni == 0x5C { out.append(contentsOf: [0x5C, 0x5C]) }
            else if uni == 0x08 { out.append(contentsOf: [0x5C, 0x62]) }
            else if uni == 0x0C { out.append(contentsOf: [0x5C, 0x66]) }
            else if uni == 0x0A { out.append(contentsOf: [0x5C, 0x6E]) }
            else if uni == 0x0D { out.append(contentsOf: [0x5C, 0x72]) }
            else if uni == 0x09 { out.append(contentsOf: [0x5C, 0x74]) }
            else if uni < 0x20 { out.append(contentsOf: Array(String(format: "\\u%04X", uni).utf8)) }
            else { out.append(contentsOf: Array(String(scalar).utf8)) }
        }
        out.append(UInt8(ascii: "\""))
    }
}

// MARK: - 取值

public extension JSONValue {
    subscript(key: String) -> JSONValue? {
        if case .object(let dict) = self { return dict[key] }
        return nil
    }

    subscript(index: Int) -> JSONValue? {
        if case .array(let items) = self, items.indices.contains(index) { return items[index] }
        return nil
    }

    var boolValue: Bool? {
        switch self {
        case .bool(let value): return value
        case .number(let value): return value != 0
        default: return nil
        }
    }

    var doubleValue: Double? {
        if case .number(let value) = self { return value }
        return nil
    }

    var intValue: Int? { doubleValue.map { Int($0) } }

    var int64Value: Int64? { doubleValue.map { Int64($0) } }

    var stringValue: String? {
        if case .string(let value) = self { return value }
        return nil
    }

    var arrayValue: [JSONValue]? {
        if case .array(let items) = self { return items }
        return nil
    }

    var objectValue: [String: JSONValue]? {
        if case .object(let dict) = self { return dict }
        return nil
    }

    var isNull: Bool {
        if case .null = self { return true }
        return false
    }

    static func int(_ value: Int) -> JSONValue { .number(Double(value)) }

    static func int64(_ value: Int64) -> JSONValue { .number(Double(value)) }
}
