import Foundation
import AppKit
import GRDB

/// iCloud Drive 多端同步：媒体库数据库 + 本地海报。
///
/// 策略（避免两台 mac 并发写同一个云端 SQLite）：
/// - 数据库始终以「本地文件」作为读写源（性能与稳定性不受 iCloud 文件协调影响）。
/// - 启动时：若云端数据库更新（按修改时间），全量拷贝覆盖本地库后重新打开数据库。
/// - 运行中：每 60 秒将本地库全量拷贝到云端（iCloud 负责在设备间传播）。
/// - 退出前再推送一次。
/// - 本地未登录 iCloud / ad-hoc 签名导致容器不可用时，自动回退纯本地存储。
/// 海报目录（独立小文件）直接存放在容器内，由 iCloud 原生文件同步处理。
enum OmniPlayICloudSync {
    static let dbFileName = "omniplay.sqlite"
    static let posterDirectoryName = "OmniPlay/Posters"

    /// iCloud 容器根目录（Application Support 内），不可用返回 nil
    static func containerURL() -> URL? {
        guard let url = FileManager.default
            .url(forUbiquityContainerIdentifier: "icloud.nan.OmniPlay") else { return nil }
        let fm = FileManager.default
        let container = url.appendingPathComponent("Documents", isDirectory: true)
        if !fm.fileExists(atPath: container.path),
           (try? fm.createDirectory(at: container, withIntermediateDirectories: true)) == nil {
            // 创建失败（如未登录 iCloud），视为不可用
            return nil
        }
        return container
    }

    /// 海报目录：容器可用时直接放 iCloud（跨设备原生同步），否则放本地 Application Support
    static func posterDirectory() -> URL {
        if let container = containerURL() {
            let dir = container.appendingPathComponent(posterDirectoryName, isDirectory: true)
            try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            return dir
        }
        let paths = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)
        let dir = paths[0].appendingPathComponent(posterDirectoryName, isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    // MARK: - 数据库启动初始化

    /// 打开数据库，并在云端有更新版本时先拉取覆盖。
    /// 返回实际使用的本地库文件 URL 与容器 URL（nil 表示纯本地模式）。
    @discardableResult
    static func openDatabase() throws -> (databaseURL: URL, containerURL: URL?) {
        let appSupportURL = try FileManager.default.url(for: .applicationSupportDirectory,
                                                        in: .userDomainMask, appropriateFor: nil, create: true)
        let appDirectory = appSupportURL.appendingPathComponent("OmniPlay", isDirectory: true)
        if !FileManager.default.fileExists(atPath: appDirectory.path) {
            try FileManager.default.createDirectory(at: appDirectory, withIntermediateDirectories: true, attributes: nil)
        }
        let localDB = appDirectory.appendingPathComponent(dbFileName)

        if let container = containerURL() {
            let cloudDB = container.appendingPathComponent(dbFileName)
            let localExists = FileManager.default.fileExists(atPath: localDB.path)
            let shouldPull: Bool
            if !localExists {
                shouldPull = FileManager.default.fileExists(atPath: cloudDB.path)
            } else {
                let cloudModified = try? modifiedDate(of: cloudDB)
                let localModified = try? modifiedDate(of: localDB)
                shouldPull = cloudModified != nil && localModified != nil && cloudModified! > localModified!
            }
            if shouldPull, (try? copyCloudDatabaseTo(container: container, localDB: localDB)) == nil {
                print("☁️ 已从 iCloud 拉取更新的媒体库")
            }
            return (localDB, container)
        }
        return (localDB, nil)
    }

    // MARK: - 数据库推送 / 拉取（全量拷贝，NSFileCoordinator 保护）

    /// 将本地库推送到 iCloud 容器
    static func pushLocalDatabase() {
        guard let db = AppDatabase.shared.databaseURL,
              let container = AppDatabase.shared.icloudContainerURL else { return }
        push(database: db, container: container)
    }

    /// 拉取云端库覆盖本地（启动时的拉取同样复用此实现，避免重复文件操作）
    static func copyCloudDatabaseTo(container: URL, localDB: URL) throws {
        let cloudDB = container.appendingPathComponent(dbFileName)
        let fm = FileManager.default
        var isFile: ObjCBool = false
        guard fm.fileExists(atPath: cloudDB.path, isDirectory: &isFile), isFile.boolValue else {
            // 云端还没有库文件，无需拉取
            throw NoCloudDatabaseError()
        }
        let coordinator = NSFileCoordinator()
        var didCopy = false
        coordinator.coordinate(readingItemAt: cloudDB, options: .withoutChanges, error: nil) { cloudURL in
            do {
                try fm.replaceItem(at: localDB, withItemAt: cloudURL,
                                   backupItemName: nil, options: [],
                                   resultingItemURL: nil)
                didCopy = true
            } catch {
                do {
                    try fm.copyItem(at: cloudURL, to: localDB)
                    didCopy = true
                } catch {
                    print("⚠️ iCloud 媒体库拉取失败：\(error.localizedDescription)")
                }
            }
        }
        if didCopy {
            // 云端只有一份 .sqlite；本地残留的 WAL 会被 SQLite 当成未提交事务回放，
            // 把刚拉下来的库又盖回去，所以换文件时一起清掉
            AppDatabase.removeWALFiles(beside: localDB)
            AppDatabase.shared.databaseURL = localDB
        }
    }

    static func push(database: URL, container: URL) {
        // WAL 模式下最近的改动可能还在 -wal 里，先落盘再拷文件，
        // 免得推到云端的副本比本地少一段事务
        AppDatabase.shared.checkpointForCopy()
        let cloudDB = container.appendingPathComponent(dbFileName)
        let fm = FileManager.default
        let coordinator = NSFileCoordinator()
        var didWrite = false
        coordinator.coordinate(writingItemAt: cloudDB, options: .init(), error: nil) { cloudURL in
            do {
                if fm.fileExists(atPath: cloudURL.path) {
                    try fm.removeItem(at: cloudURL)
                }
                try fm.copyItem(at: database, to: cloudURL)
                didWrite = true
            } catch {
                print("⚠️ iCloud 媒体库推送失败：\(error.localizedDescription)")
            }
        }
        if didWrite {
            // 以云端写入时间为准对齐本地修改时间，避免下次启动误判“云端更新”
            try? fm.setAttributes([.modificationDate: Date()], ofItemAtPath: database.path)
        }
    }

    // MARK: - 运行中定时同步

    /// 启动定时推送（每 60 秒），并在应用终止前再推一次
    static func startBackgroundSync() {
        Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { _ in
            pushLocalDatabase()
        }
        NotificationCenter.default.addObserver(forName: NSApplication.willTerminateNotification,
                                               object: nil, queue: .main) { _ in
            pushLocalDatabase()
        }
    }

    private struct NoCloudDatabaseError: Error {}

    private static func modifiedDate(of url: URL) throws -> Date? {
        try url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate
    }
}
