import Foundation
#if canImport(Darwin)
import Darwin
#elseif canImport(Glibc)
import Glibc
#elseif os(Windows)
import WinSDK
#endif

/// 运行时装载 libmpv。不在链接期依赖导入库：Windows 只要 `libmpv-2.dll` 放在程序目录，
/// mac/Linux 试系统里的 dylib/so，缺库时 `load()` 返回 nil，界面照样能起（显示不可用原因），
/// 不会一上来就崩。
public final class MpvLibrary {
    public typealias CreateFn = @convention(c) () -> UnsafeMutableRawPointer?
    public typealias InitializeFn = @convention(c) (UnsafeMutableRawPointer?) -> Int32
    public typealias DestroyFn = @convention(c) (UnsafeMutableRawPointer?) -> Void
    public typealias SetOptionStringFn = @convention(c) (UnsafeMutableRawPointer?, UnsafePointer<CChar>, UnsafePointer<CChar>) -> Int32
    public typealias SetPropertyStringFn = @convention(c) (UnsafeMutableRawPointer?, UnsafePointer<CChar>, UnsafePointer<CChar>) -> Int32
    public typealias CommandStringFn = @convention(c) (UnsafeMutableRawPointer?, UnsafePointer<CChar>) -> Int32
    public typealias GetStringPropertyFn = @convention(c) (UnsafeMutableRawPointer?, UnsafePointer<CChar>) -> UnsafeMutablePointer<CChar>?
    public typealias FreeFn = @convention(c) (UnsafeMutableRawPointer?) -> Void

    public let create: CreateFn
    public let initialize: InitializeFn
    public let destroy: DestroyFn
    public let setOptionString: SetOptionStringFn
    public let setPropertyString: SetPropertyStringFn
    public let commandString: CommandStringFn
    public let getStringProperty: GetStringPropertyFn
    public let free: FreeFn

    /// 试过的库名，失败时给界面当原因回显
    public static let candidateNames: [String] = [
        "libmpv-2.dll", "mpv-2.dll", "libmpv.2.dylib", "libmpv.so.2", "libmpv.so.1",
    ]

    /// 每个候选名的失败原因，按尝试顺序（Windows 带 Win32 错误码）。
    /// 「文件不在」和「在但装不起来」要分开说，后者通常是缺依赖 DLL；
    /// 只留最后一条会把真正那个 DLL 的错冲掉，所以整串都记下来。
    nonisolated(unsafe) public private(set) static var lastLoadFailures: [String] = []

    /// dlerror 在 mac 上会把每个搜索路径都念一遍，界面装不下，只留首行并截断
    private static func brief(_ text: String) -> String {
        let line = text.split(separator: "\n").first.map(String.init) ?? text
        return line.count > 160 ? String(line.prefix(157)) + "..." : line
    }

    private let handle: UnsafeMutableRawPointer

    public static func load(name: String? = nil) -> MpvLibrary? {
        lastLoadFailures = []
        for candidate in [name].compactMap({ $0 }) + candidateNames {
            if let library = open(candidate) { return library }
        }
        return nil
    }

    private static func open(_ name: String) -> MpvLibrary? {
        #if os(Windows)
        // W 系 API 只吃 UTF-16，直接塞 UTF-8 指针会让中文目录下的加载莫名失败
        var utf16 = Array(name.utf16)
        utf16.append(0)
        let raw = utf16.withUnsafeBufferPointer { LoadLibraryW($0.baseAddress) }
        guard let raw else {
            MpvLibrary.lastLoadFailures.append(MpvLibrary.brief("\(name)：LoadLibraryW 失败，Win32 错误 \(GetLastError())"))
            return nil
        }
        let handle = UnsafeMutableRawPointer(raw)
        #elseif canImport(Darwin) || canImport(Glibc)
        let raw = dlopen(name, RTLD_NOW | RTLD_LOCAL)
        guard let raw else {
            if let error = dlerror() { MpvLibrary.lastLoadFailures.append(MpvLibrary.brief("\(name)：\(String(cString: error))")) }
            return nil
        }
        let handle = UnsafeMutableRawPointer(raw)
        #else
        return nil
        #endif
        func symbol<T>(_ fn: String, _ type: T.Type) -> T? {
            #if os(Windows)
            guard let address = GetProcAddress(raw, fn) else {
                MpvLibrary.lastLoadFailures.append(MpvLibrary.brief("\(name)：缺符号 \(fn)"))
                return nil
            }
            #else
            guard let address = dlsym(raw, fn) else {
                MpvLibrary.lastLoadFailures.append(MpvLibrary.brief("\(name)：缺符号 \(fn)"))
                return nil
            }
            #endif
            return unsafeBitCast(address, to: T.self)
        }
        guard let create: CreateFn = symbol("mpv_create", CreateFn.self),
              let initialize: InitializeFn = symbol("mpv_initialize", InitializeFn.self),
              let destroy: DestroyFn = symbol("mpv_destroy", DestroyFn.self),
              let setOptionString: SetOptionStringFn = symbol("mpv_set_option_string", SetOptionStringFn.self),
              let setPropertyString: SetPropertyStringFn = symbol("mpv_set_property_string", SetPropertyStringFn.self),
              let commandString: CommandStringFn = symbol("mpv_command_string", CommandStringFn.self),
              let getStringProperty: GetStringPropertyFn = symbol("mpv_get_property_string", GetStringPropertyFn.self),
              let free: FreeFn = symbol("mpv_free", FreeFn.self)
        else {
            return nil
        }
        return MpvLibrary(handle: handle, create: create, initialize: initialize, destroy: destroy,
                          setOptionString: setOptionString, setPropertyString: setPropertyString,
                          commandString: commandString, getStringProperty: getStringProperty, free: free)
    }

    init(handle: UnsafeMutableRawPointer, create: @escaping CreateFn, initialize: @escaping InitializeFn,
         destroy: @escaping DestroyFn, setOptionString: @escaping SetOptionStringFn,
         setPropertyString: @escaping SetPropertyStringFn, commandString: @escaping CommandStringFn,
         getStringProperty: @escaping GetStringPropertyFn, free: @escaping FreeFn) {
        self.handle = handle
        self.create = create
        self.initialize = initialize
        self.destroy = destroy
        self.setOptionString = setOptionString
        self.setPropertyString = setPropertyString
        self.commandString = commandString
        self.getStringProperty = getStringProperty
        self.free = free
    }
}

public struct PlayerStatus: Equatable {
    public var available = false
    public var timePos: Double = 0
    public var duration: Double = 0
    public var paused = true
    public var idle = true
    public var reason: String?
}

/// 播放器状态机。渲染表面由宿主给（mac: CAMetalLayer 的指针；Windows: 子窗口的 HWND），
/// 核心只负责选项、命令与状态读取——这部分逻辑 mac 版已经踩过一遍坑，直接共用。
public final class PlayerCore {
    public static let shared = PlayerCore()

    private let library: MpvLibrary?
    private var handle: UnsafeMutableRawPointer?
    public let unavailableReason: String?

    public init(library: MpvLibrary? = MpvLibrary.load()) {
        self.library = library
        if library == nil {
            let tried = MpvLibrary.candidateNames.joined(separator: ", ")
            if MpvLibrary.lastLoadFailures.isEmpty {
                self.unavailableReason = "没找到 libmpv（试过：\(tried)）"
            } else {
                self.unavailableReason = "libmpv 装载失败："
                    + MpvLibrary.lastLoadFailures.joined(separator: "；")
                    + "（试过：\(tried)）"
            }
        } else {
            self.unavailableReason = nil
        }
    }

    public var isAvailable: Bool { library != nil }

    /// 音频输出优先级：mac 必须优先 AVFoundation。libmpv 的 coreaudio 驱动在 CoreAudio
    /// 设备变动时会用可能已释放的上下文回调 hotplug_cb，直接 SIGSEGV（mac 版实测踩过）；
    /// Windows 用 WASAPI 独占外的普通共享通道。
    public static var audioOutputs: String {
        #if os(macOS)
        return "avfoundation,coreaudio"
        #elseif os(Windows)
        return "wasapi,sndio"
        #else
        return "pulse,alsa"
        #endif
    }

    /// headless 模式下不给窗口句柄，CI 也能验「库能加载、文件能打开、时间能推进」。
    @discardableResult
    public func start(headless: Bool = false, renderSurface: UnsafeMutableRawPointer? = nil) -> Bool {
        guard let library, handle == nil else { return handle != nil }
        guard let handle = library.create() else { return false }
        _ = library.setOptionString(handle, "vo", headless ? "null" : "gpu")
        _ = library.setOptionString(handle, "ao", headless ? "null" : Self.audioOutputs)
        _ = library.setOptionString(handle, "idle", "no")
        _ = library.setOptionString(handle, "keep-open", "yes")
        if let renderSurface {
            // wid 是整数：mac 传 NSView 指针，Windows 传 HWND
            _ = library.setOptionString(handle, "wid", "\(UInt(bitPattern: renderSurface))")
        }
        guard library.initialize(handle) == 0 else {
            library.destroy(handle)
            return false
        }
        self.handle = handle
        return true
    }

    @discardableResult
    public func load(_ path: String) -> Bool {
        guard let library, let handle else { return false }
        return "loadfile \"\(path)\"".withCString { library.commandString(handle, $0) == 0 }
    }

    public func setPaused(_ paused: Bool) {
        guard let library, let handle else { return }
        _ = library.setPropertyString(handle, "pause", paused ? "yes" : "no")
    }

    public var status: PlayerStatus {
        guard let library, let handle else {
            return PlayerStatus(available: library != nil, reason: unavailableReason)
        }
        func number(_ property: String) -> Double? {
            guard let raw = property.withCString({ library.getStringProperty(handle, $0) }) else { return nil }
            defer { library.free(UnsafeMutableRawPointer(mutating: raw)) }
            return Double(String(cString: raw))
        }
        func flag(_ property: String) -> Bool {
            guard let raw = property.withCString({ library.getStringProperty(handle, $0) }) else { return false }
            defer { library.free(UnsafeMutableRawPointer(mutating: raw)) }
            return String(cString: raw) == "yes"
        }
        var status = PlayerStatus(available: true)
        status.timePos = number("time-pos") ?? 0
        status.duration = number("duration") ?? 0
        status.paused = flag("pause")
        status.idle = flag("idle-active")
        return status
    }

    public func shutdown() {
        guard let library, let handle else { return }
        library.destroy(handle)
        self.handle = nil
    }
}
