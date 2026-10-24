import Foundation
import GRDB
import NetFS

/// SMB 网络共享源的挂载元信息。
///
/// SMB 源以普通本地源入库（protocolType = "local"），目录扫描、刮削、离线缓存与
/// 播放全部沿用本地目录逻辑；authConfig 里带这份 JSON 表示它是 SMB 挂载体，
/// 供协议标签、连通性检测与卸载后的自动重挂使用。
nonisolated struct SMBShareInfo: Codable, Equatable {
    var host: String
    var share: String
    var username: String?
    var password: String?
    /// 上次成功的挂载点。系统可能把卷挂在 /Volumes 以外，存下来重挂时才认得回原路径。
    var mountPath: String?

    init(host: String, share: String, username: String? = nil, password: String? = nil, mountPath: String? = nil) {
        self.host = host
        self.share = share
        self.username = username
        self.password = password
        self.mountPath = mountPath
    }

    /// NetFS 用的标准 URL，凭据单独传，不塞进 URL
    var netFSURL: URL? {
        var components = URLComponents()
        components.scheme = "smb"
        components.host = host
        components.path = share.isEmpty ? "/" : "/" + share
        return components.url
    }

    /// mount_smbfs 用的 UNC 地址。访客要写在 URL 的用户名位置，
    /// `-o guest` 并不是 mount_smbfs 支持的选项。
    var mountURLString: String {
        let rawUser = (username?.isEmpty == false) ? username! : "guest"
        let user = Self.encoded(rawUser)
        guard let password, !password.isEmpty else { return "//\(user)@\(host)/\(share)" }
        let pass = Self.encoded(password)
        return "//\(user):\(pass)@\(host)/\(share)"
    }

    /// smbutil 查看共享列表用的地址（无凭据时按 guest 走）
    var listViewURLString: String {
        let user = (username?.isEmpty == false) ? username! : "guest"
        let pass = password ?? ""
        if pass.isEmpty { return "//\(user)@\(host)" }
        return "//\(Self.encoded(user)):\(Self.encoded(pass))@\(host)"
    }

    /// UNC 地址里只有分隔符必须转义；域名分隔的 `;` 与常见用户名字符保持原样，
    /// 免得把 user.name 变成 user%2Ename
    private static func encoded(_ value: String) -> String {
        let allowed = CharacterSet(charactersIn:
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789.-_~;")
        return value.addingPercentEncoding(withAllowedCharacters: allowed) ?? value
    }
}

/// 只允许一次回调的开关，用来在「挂载返回」和「超时」之间抢跑
nonisolated private final class OnceFlag: @unchecked Sendable {
    private let lock = NSLock()
    private var fired = false

    func take() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        if fired { return false }
        fired = true
        return true
    }
}

/// SMB 挂载工具链的错误，消息直接透给 UI 展示
nonisolated struct SMBMountError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

nonisolated enum SMBSourceSupport {
    /// 解析用户填写的地址：`smb://[user[:pass]@]host[/share]`、`//host/share`、`host[/share]` 都收。
    /// 返回的 share 可能为空，表示只连到服务器、还要去列共享。
    static func parseLocation(_ raw: String) -> SMBShareInfo? {
        var text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if text.hasPrefix("//") { text = "smb:" + text }
        if let scheme = text.range(of: "://") { text = String(text[scheme.upperBound...]) }
        text = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return nil }

        var user: String?
        var password: String?
        if let at = text.lastIndex(of: "@") {
            let credential = String(text[..<at])
            text = String(text[text.index(after: at)...])
            let pieces = credential.split(separator: ":", maxSplits: 1, omittingEmptySubsequences: false)
            user = pieces.first.flatMap { decode($0) }
            if pieces.count > 1 { password = decode(pieces[1]) }
        }
        let pieces = text.split(separator: "/", omittingEmptySubsequences: true).map(String.init)
        guard let host = pieces.first, !host.isEmpty else { return nil }
        let share = pieces.count > 1 ? (decode(pieces[1]) ?? pieces[1]) : ""
        return SMBShareInfo(
            host: host,
            share: share,
            username: (user?.isEmpty == false) ? user : nil,
            password: (password?.isEmpty == false) ? password : nil
        )
    }

    private static func decode(_ value: some StringProtocol) -> String? {
        String(value).removingPercentEncoding ?? String(value)
    }

    static func shareInfo(from source: MediaSource) -> SMBShareInfo? {
        guard let authConfig = source.authConfig,
              let data = authConfig.data(using: .utf8),
              let payload = try? JSONDecoder().decode(SMBShareInfo.self, from: data),
              !payload.host.isEmpty, !payload.share.isEmpty else { return nil }
        return payload
    }

    static func authConfigJSON(for info: SMBShareInfo) -> String? {
        guard let data = try? JSONEncoder().encode(info) else { return nil }
        return String(data: data, encoding: .utf8)
    }
}

/// 挂载走系统 NetFS（访达「连接服务器」用的就是它），挂载点由系统在 /Volumes 下创建；
/// 系统挂载不成时再用 mount_smbfs 挂到 App 自己的目录，觅影不需要自己实现 SMB 协议。
nonisolated enum SMBSourceManager {
    /// /Volumes 归 root 所有，普通用户建不了目录，mount_smbfs 兜底时挂到这里
    static let appMountRoot: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support", isDirectory: true)
        return base.appendingPathComponent("OmniPlay/SMB", isDirectory: true)
    }()

    /// 挂载共享，返回本地挂载路径
    static func mount(_ info: SMBShareInfo) async -> Result<String, SMBMountError> {
        let shareName = sanitizedVolumeName(info.share)
        guard !shareName.isEmpty else {
            return .failure(SMBMountError(message: "还没有选中共享。"))
        }
        // 访达或上一轮已经挂好这份共享，直接复用
        let finderPath = "/Volumes/" + shareName
        if isMounted(finderPath) { return .success(finderPath) }
        if let stored = info.mountPath.map(canonicalMountPath), isMounted(stored) { return .success(stored) }

        let existingVolumes = Set((try? FileManager.default.contentsOfDirectory(atPath: "/Volumes")) ?? [])
        let outcome = await netFSMount(info, shareName: shareName, existingVolumes: existingVolumes, timeout: 25)

        switch outcome {
        case .success(let path):
            return .success(canonicalMountPath(path))
        case .failure(let failure):
            // 挂载点、权限这类问题值得换个路子重试；账号、共享名错了重试也只是再报一遍
            guard shouldFallbackToTool(status: failure.status) else { return .failure(failure.error) }
            return await mountViaTool(info, shareName: shareName)
        }
    }

    /// 用 smbutil 列出服务器上的磁盘共享
    static func listShares(_ info: SMBShareInfo) async -> Result<[String], SMBMountError> {
        switch await runTool("/usr/bin/smbutil", arguments: ["view", info.listViewURLString], timeout: 15) {
        case .failure(let error):
            return .failure(SMBMountError(message: friendlyMessage(error.message)))
        case .success(let output):
            return .success(parseShareListing(output))
        }
    }

    /// smbutil view 的输出解析（Share/Disk/Comment 三列）
    static func parseShareListing(_ output: String) -> [String] {
        var shares: [String] = []
        for line in output.split(whereSeparator: \.isNewline) {
            let columns = line.split(whereSeparator: \.isWhitespace).map(String.init)
            guard columns.count >= 2, columns[1].caseInsensitiveCompare("Disk") == .orderedSame else { continue }
            shares.append(columns[0])
        }
        return shares
    }

    /// 扫描开始前把掉线/重启后失去挂载的 SMB 源静默重挂回去
    @MainActor
    static func remountPersistedSources() async {
        guard let dbQueue = AppDatabase.shared.dbQueue else { return }
        let sources = (try? await dbQueue.read { try MediaSource.fetchAll($0) }) ?? []
        // stat 一个已经断掉的 SMB 挂载点能卡好几秒，整轮检查放后台跑，别拖住界面；
        // 里面有库写入，优先级跟媒体库其余访问保持一致
        await Task.detached(priority: .userInitiated) {
            for source in sources {
                guard let info = SMBSourceSupport.shareInfo(from: source) else { continue }
                // 目录还在就用不着折腾；只剩挂载失败留下的空壳时重新挂载
                if isUsableSourceDirectory(source.baseUrl) { continue }
                await remount(info, for: source, dbQueue: dbQueue)
            }
        }.value
    }

    /// 重挂一个 SMB 源并把新位置写回库；返回媒体源现在应该指向的路径，挂不上返回 nil
    @discardableResult
    static func remount(_ info: SMBShareInfo, for source: MediaSource, dbQueue: any DatabaseWriter) async -> String? {
        guard case .success(let mountPath) = await mount(info) else { return nil }
        // 媒体源可能指向共享里的子目录，重挂后要把相对路径接回新的挂载点
        let updatedPath = rebasedPath(info: info, mountPath: mountPath, previous: source.baseUrl)
        guard updatedPath != source.baseUrl, let sourceID = source.id else { return updatedPath }
        try? await dbQueue.write { db in
            guard var latest = try MediaSource.fetchOne(db, key: sourceID) else { return }
            latest.baseUrl = updatedPath
            var configuration = latest.addressConfiguration()
            configuration.localAddress = updatedPath
            configuration.activeAddress = updatedPath
            latest.setAddressConfiguration(configuration)
            try? latest.update(db)
        }
        return updatedPath
    }

    /// 列出挂载目录下的可见子目录，跳过隐藏目录和 NAS 常见的垃圾目录
    static func listSubdirectories(at path: String) -> [String] {
        let contentURL = URL(fileURLWithPath: path)
        let keys: Set<URLResourceKey> = [.isDirectoryKey]
        let urls = (try? FileManager.default.contentsOfDirectory(
            at: contentURL,
            includingPropertiesForKeys: Array(keys),
            options: [.skipsHiddenFiles]
        )) ?? []
        let junk: Set<String> = [
            "@eaDir", "#recycle", "#snapshot", "$RECYCLE.BIN", "System Volume Information",
            "lost+found", ".AppleDump", "@share", ".thumbnails",
        ]
        return urls
            .filter { url in
                guard !junk.contains(url.lastPathComponent) else { return false }
                return (try? url.resourceValues(forKeys: keys))?.isDirectory == true
            }
            .map(\.lastPathComponent)
            .sorted { $0.localizedStandardCompare($1) == .orderedAscending }
    }

    /// 卸载浏览时临时挂上的共享；卷正被占用就算了，不报错
    @discardableResult
    static func unmount(_ path: String) async -> Bool {
        guard isMounted(path) else { return true }
        if case .success = await runTool("/usr/sbin/diskutil", arguments: ["unmount", path], timeout: 15) { return true }
        if case .success = await runTool("/sbin/umount", arguments: ["-f", path], timeout: 15) { return true }
        return false
    }

    /// 路径当前是不是真实挂载点（挂载失败残留的空目录不算）
    static func isMounted(_ path: String) -> Bool {
        var own = stat()
        var parent = stat()
        guard stat(path, &own) == 0,
              stat((path as NSString).deletingLastPathComponent, &parent) == 0 else { return false }
        return own.st_dev != parent.st_dev
    }

    /// 媒体源目录能不能直接用：真实挂载点、或不是 /Volumes 下的空壳
    static func isUsableSourceDirectory(_ path: String) -> Bool {
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: path, isDirectory: &isDirectory), isDirectory.boolValue else { return false }
        guard path.hasPrefix("/Volumes/"), !isMounted(path) else { return true }
        guard let contents = try? FileManager.default.contentsOfDirectory(atPath: path) else { return true }
        return !contents.isEmpty
    }

    // MARK: - 挂载实现

    private struct NetFSFailure: Error {
        let status: Int32
        let error: SMBMountError
    }

    /// 系统挂载可能卡在鉴权或网络等待上，给它 25 秒；超时后线程继续在后台跑，
    /// 稍后真的挂上了也会被下一次 isMounted 检查直接复用。
    private static func netFSMount(
        _ info: SMBShareInfo,
        shareName: String,
        existingVolumes: Set<String>,
        timeout: TimeInterval
    ) async -> Result<String, NetFSFailure> {
        let once = OnceFlag()
        return await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                let outcome = netFSMountBlocking(info, shareName: shareName, existingVolumes: existingVolumes)
                if once.take() { continuation.resume(returning: outcome) }
            }
            DispatchQueue.global(qos: .userInitiated).asyncAfter(deadline: .now() + timeout) {
                guard once.take() else { return }
                continuation.resume(returning: .failure(NetFSFailure(
                    status: 60,
                    error: SMBMountError(message: "挂载等待超时，请检查服务器状态与共享权限；也可以先在访达里连接该共享。")
                )))
            }
        }
    }

    /// 系统挂载：挂载点交给系统在 /Volumes 下创建，凭据直接传给它、不进 URL
    private static func netFSMountBlocking(
        _ info: SMBShareInfo,
        shareName: String,
        existingVolumes: Set<String>
    ) -> Result<String, NetFSFailure> {
        guard let url = info.netFSURL else {
            return .failure(NetFSFailure(status: -1, error: SMBMountError(message: "地址无法解析：\(info.host)/\(info.share)")))
        }
        var mountpoints: Unmanaged<CFArray>?
        let openOptions = NSMutableDictionary()
        // 账号留空就按访客走；总之把身份给全，别留给 NetAuth 弹窗提问的余地
        let user = (info.username?.isEmpty == false) ? info.username! : "guest"
        if info.username?.isEmpty != false {
            openOptions[kNetFSUseGuestKey as String] = true
        }
        let status = Int32(NetFSMountURLSync(
            url as CFURL,
            nil,
            user as CFString,
            info.password as CFString?,
            openOptions,
            nil,
            &mountpoints
        ))
        guard status != 0 else {
            // 系统返回的挂载点可能是 URL 转义过的（中文共享名最容易踩到），
            // 逐个还原并在盘上验真，用第一个真实存在的那个
            var candidates: [String] = []
            if let reported = (mountpoints.map { $0.takeUnretainedValue() as CFArray } as? [String])?.first {
                candidates.append(canonicalMountPath(reported))
            }
            candidates.append("/Volumes/" + shareName)
            if let fresh = freshVolume(after: existingVolumes, shareName: shareName) {
                candidates.append(fresh)
            }
            for candidate in candidates where isMounted(candidate) || isUsableSourceDirectory(candidate) {
                return .success(candidate)
            }
            return .failure(NetFSFailure(
                status: 2,
                error: SMBMountError(message: "挂载完成，但在 /Volumes 下找不到「\(info.share)」卷。")
            ))
        }
        return .failure(NetFSFailure(status: status, error: netFSError(status, share: info.share)))
    }

    /// 兜底：mount_smbfs 挂到 App 自己的目录。两个位置参数缺一不可，少一个只打印 usage
    private static func mountViaTool(_ info: SMBShareInfo, shareName: String) async -> Result<String, SMBMountError> {
        let mountPoint = appMountRoot.appendingPathComponent(shareName).path
        let alreadyThere = FileManager.default.fileExists(atPath: mountPoint)
        if !alreadyThere {
            do {
                try FileManager.default.createDirectory(atPath: mountPoint, withIntermediateDirectories: true)
            } catch {
                return .failure(SMBMountError(message: "创建挂载点失败：\(error.localizedDescription)"))
            }
        }
        let arguments = ["-N", info.mountURLString, mountPoint]
        switch await runTool("/sbin/mount_smbfs", arguments: arguments, timeout: 25) {
        case .success:
            if isMounted(mountPoint) { return .success(mountPoint) }
            return .failure(SMBMountError(message: "挂载完成，但 \(mountPoint) 不是挂载点。"))
        case .failure(let error):
            if isMounted(mountPoint) { return .success(mountPoint) }
            if !alreadyThere { cleanupEmptyMountPoint(mountPoint) }
            return .failure(SMBMountError(message: "挂载失败：\(friendlyMessage(error.message))"))
        }
    }

    /// 只有挂载点/权限类错误才值得再用 mount_smbfs 试一次
    private static func shouldFallbackToTool(status: Int32) -> Bool {
        switch Int(status) {
        case 1, 2, 13, 20, 21, 30, -6602: return true
        default: return false
        }
    }

    /// NetFS 返回正数是 errno、负数是 OSStatus，都翻成看得懂的中文
    private static func netFSError(_ status: Int32, share: String) -> SMBMountError {
        let code = Int(status)
        let reason: String
        if status > 0 {
            reason = String(cString: strerror(Int32(code)))
        } else {
            switch code {
            case -128: reason = "已取消挂载"
            case -5998, -6003: reason = "服务器上没有可挂载的共享"
            case -6004: reason = "服务器不允许访客访问"
            default: reason = "服务器拒绝连接或挂载失败"
            }
        }
        var hint = ""
        switch code {
        case 2, -5998, -6003: hint = "，请检查共享名「\(share)」是否正确"
        case 13, 22, -6004: hint = "，请填写正确的用户名与密码"
        case 1: hint = "，当前系统不允许本用户直接挂载，可先在访达里连接该共享"
        case 50, 51, 60, 61: hint = "，请检查服务器地址与网络"
        default: break
        }
        return SMBMountError(message: "\(reason)\(hint)（错误码 \(code)）")
    }

    // MARK: - 工具方法

    /// 重新挂载后算出媒体源该指向哪里（共享根或共享里的子目录）
    private static func rebasedPath(info: SMBShareInfo, mountPath: String, previous: String) -> String {
        let roots = [
            info.mountPath,
            info.mountPath.flatMap { $0.removingPercentEncoding },
            "/Volumes/" + sanitizedVolumeName(info.share),
        ].compactMap { $0 }.filter { !$0.isEmpty }
        // 老库里可能存的是转义过的路径，先还原再比前缀，子目录才不会掉
        for candidate in [previous, previous.removingPercentEncoding ?? previous] {
            for root in roots where candidate.hasPrefix(root) {
                return mountPath + String(candidate.dropFirst(root.count))
            }
        }
        return mountPath
    }

    /// 挂载成功后新出现的 /Volumes 卷；没有就返回 nil
    private static func freshVolume(after before: Set<String>, shareName: String) -> String? {
        let after = Set((try? FileManager.default.contentsOfDirectory(atPath: "/Volumes")) ?? [])
        guard let fresh = after.subtracting(before).first(where: { $0.hasPrefix(shareName) }),
              isMounted("/Volumes/" + fresh) else { return nil }
        return "/Volumes/" + fresh
    }

    /// 挂载失败时把这次自己建的空挂载点收掉，别留垃圾
    private static func cleanupEmptyMountPoint(_ path: String) {
        guard !isMounted(path) else { return }
        guard let contents = try? FileManager.default.contentsOfDirectory(atPath: path), contents.isEmpty else { return }
        try? FileManager.default.removeItem(atPath: path)
    }

    /// mount_smbfs / smbutil 的英文报错转成看得懂的中文提示
    private static func friendlyMessage(_ raw: String) -> String {
        let cleaned = raw.replacingOccurrences(of: "mount_smbfs: ", with: "")
            .replacingOccurrences(of: "smbutil: ", with: "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        let lowered = cleaned.lowercased()
        if lowered.contains("login failure") || lowered.contains("bad user name")
            || lowered.contains("unknown user") || lowered.contains("authentication") {
            return "登录失败，请填写正确的用户名与密码（留空会按访客访问）。"
        }
        if lowered.contains("permission denied") || lowered.contains("access denied") {
            return "服务器拒绝了访问，请检查用户名与密码。"
        }
        if lowered.contains("no route to host") || lowered.contains("connection refused")
            || lowered.contains("network is unreachable") || lowered.contains("timed out")
            || lowered.contains("timeout") || lowered.contains("no such domain") {
            return "连不上服务器，请检查地址、网络与 SMB 服务是否开启。"
        }
        if lowered.contains("object name") || lowered.contains("share name") {
            return "找不到这个共享，请检查共享名是否正确。"
        }
        if lowered.contains("smb library") || lowered.contains("device not configured") {
            return "系统 SMB 组件没能启动，请重试；也可以先在访达里连接该共享。"
        }
        if lowered.contains("resource busy") || lowered.contains("file exists") {
            return "这个共享似乎已经挂载了，请在访达侧边栏确认后再试。"
        }
        return cleaned.isEmpty ? "挂载失败。" : cleaned
    }

    private static func sanitizedVolumeName(_ share: String) -> String {
        share.replacingOccurrences(of: "/", with: "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// NetFS 会把非 ASCII 的挂载点按 URL 转义返回（/Volumes/%E7%94%B5%E5%BD%B1），
    /// 直接入库会让本地扫描找一个不存在目录；统一还原成真正的 POSIX 路径
    static func canonicalMountPath(_ raw: String) -> String {
        var path = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if path.hasPrefix("file://"), let url = URL(string: path) { path = url.path }
        while path.count > 1 && path.hasSuffix("/") { path.removeLast() }
        guard path.contains("%"), let decoded = path.removingPercentEncoding, decoded != path else {
            return path
        }
        // 只有解出来的路径真在盘上才替换，免得把名字里带 % 的共享改坏
        return FileManager.default.fileExists(atPath: decoded) ? decoded : path
    }

    private static func runTool(
        _ launchPath: String,
        arguments: [String],
        timeout: TimeInterval
    ) async -> Result<String, SMBMountError> {
        await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                continuation.resume(returning: runToolBlocking(
                    launchPath,
                    arguments: arguments,
                    timeout: timeout
                ))
            }
        }
    }

    /// 同步跑外部命令：轮询等待得留在同步上下文里，异步上下文里 sleep 会踩 Swift 6 的坑
    private static func runToolBlocking(
        _ launchPath: String,
        arguments: [String],
        timeout: TimeInterval
    ) -> Result<String, SMBMountError> {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: launchPath)
        process.arguments = arguments
        let errorPipe = Pipe()
        process.standardError = errorPipe
        let outputPipe = Pipe()
        process.standardOutput = outputPipe
        // 挡住标准输入：mount_smbfs/smbutil 不带凭据时不该卡在交互提问上
        process.standardInput = FileHandle.nullDevice
        do {
            try process.run()
        } catch {
            return .failure(SMBMountError(message: "无法执行 \(URL(fileURLWithPath: launchPath).lastPathComponent)：\(error.localizedDescription)"))
        }
        let deadline = Date().addingTimeInterval(timeout)
        while process.isRunning {
            if Date() > deadline {
                process.terminate()
                return .failure(SMBMountError(message: "操作超时，请检查服务器地址与网络。"))
            }
            Thread.sleep(forTimeInterval: 0.2)
        }
        let standardOutput = String(
            data: outputPipe.fileHandleForReading.readDataToEndOfFile(),
            encoding: .utf8
        ) ?? ""
        if process.terminationStatus == 0 { return .success(standardOutput) }
        let data = errorPipe.fileHandleForReading.readDataToEndOfFile()
        let message = String(data: data, encoding: .utf8)?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return .failure(SMBMountError(message: message.isEmpty ? "失败（退出码 \(process.terminationStatus)）" : message))
    }
}
