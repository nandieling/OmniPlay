import Foundation
import OmniPlayCore

// C ABI 边界。刻意做小：一个 handle、一个 invoke、一个 free、一个事件回调。
// 内存规则：响应缓冲由核心分配，宿主用完必须调 omniplay_free_bytes，别拿自己的 free。

@_cdecl("omniplay_create")
public func omniplay_create() -> UnsafeMutableRawPointer {
    let service = OmniPlayService(hooks: Platform.hooks)
    return Unmanaged.passRetained(service).toOpaque()
}

@_cdecl("omniplay_invoke")
public func omniplay_invoke(
    _ handle: UnsafeMutableRawPointer?,
    _ request: UnsafePointer<UInt8>?,
    _ requestLength: Int32,
    _ responseLength: UnsafeMutablePointer<Int32>?
) -> UnsafeMutableRawPointer? {
    guard let handle else {
        return bridgeError("界面没把核心句柄传回来", responseLength)
    }
    let service = Unmanaged<OmniPlayService>.fromOpaque(handle).takeUnretainedValue()
    guard let request, requestLength > 0 else {
        return bridgeError("请求是空的", responseLength)
    }
    let data = Data(bytes: request, count: Int(requestLength))
    return bridgeBytes(service.handle(requestJSON: data), responseLength)
}

@_cdecl("omniplay_free_bytes")
public func omniplay_free_bytes(_ handle: UnsafeMutableRawPointer?, _ bytes: UnsafeMutableRawPointer?) {
    bytes?.deallocate()
}

@_cdecl("omniplay_set_event_callback")
public func omniplay_set_event_callback(
    _ handle: UnsafeMutableRawPointer?,
    _ context: UnsafeMutableRawPointer?,
    _ callback: (@convention(c) (UnsafeMutableRawPointer?, UnsafePointer<UInt8>?, Int32) -> Void)?
) {
    guard let handle else { return }
    Unmanaged<OmniPlayService>.fromOpaque(handle).takeUnretainedValue()
        .setEventCallback(context: context, function: callback)
}

/// 版本号要跨 ABI 边界长期有效，所以放进程级常量缓冲区里，宿主不需要 free。
private let versionCString: UnsafePointer<CChar> = {
    var bytes = Array(OmniPlayService.API.core.utf8).map { CChar(bitPattern: $0) }
    bytes.append(0)
    let buffer = UnsafeMutablePointer<CChar>.allocate(capacity: bytes.count)
    buffer.initialize(from: bytes, count: bytes.count)
    return UnsafePointer(buffer)
}()

@_cdecl("omniplay_version")
public func omniplay_version() -> UnsafePointer<CChar> {
    versionCString
}

@_cdecl("omniplay_destroy")
public func omniplay_destroy(_ handle: UnsafeMutableRawPointer?) {
    guard let handle else { return }
    Unmanaged<OmniPlayService>.fromOpaque(handle).release()
}

private func bridgeBytes(_ data: Data, _ responseLength: UnsafeMutablePointer<Int32>?) -> UnsafeMutableRawPointer {
    let buffer = UnsafeMutableRawPointer.allocate(byteCount: max(data.count, 1), alignment: 16)
    data.copyBytes(to: buffer.assumingMemoryBound(to: UInt8.self), count: data.count)
    responseLength?.pointee = Int32(data.count)
    return buffer
}

private func bridgeError(_ message: String, _ responseLength: UnsafeMutablePointer<Int32>?) -> UnsafeMutableRawPointer {
    bridgeBytes(OmniPlayService.error(message), responseLength)
}
