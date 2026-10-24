import Foundation
import SwiftUI
import Combine
import AppKit
import GRDB

// NeoDB 同步的编排层：设置项、登录态、待同步队列、推送与导入都在这一处。
//
// 界面只读它的 @Published 状态。这里是 MainActor：数据库读写走 dbQueue，
// 发请求交给 NeodbService 并 await，界面不会被网络或磁盘钉住。

@MainActor
final class NeodbSyncService: ObservableObject {
    static let shared = NeodbSyncService()

    static let minimumRequestInterval: TimeInterval = 1.2

    @Published private(set) var isLoggedIn = false
    @Published private(set) var accountLabel = ""
    @Published private(set) var pendingCount = 0
    @Published private(set) var isSyncing = false
    @Published private(set) var isImporting = false
    @Published private(set) var statusLines: [String] = []
    @Published private(set) var lastSyncAt: Date? = nil

    private let defaults: UserDefaults
    private var oauthApp: NeodbOAuthApp?

    private init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        // 早期版本能填自建实例，现在站点固定，清掉遗留地址，免得旧值还在设置里生效。
        defaults.removeObject(forKey: Keys.instance)
        if let raw = defaults.string(forKey: Keys.oauthApp) {
            oauthApp = try? JSONDecoder().decode(NeodbOAuthApp.self, from: Data(raw.utf8))
        }
        refresh()
    }

    private enum Keys {
        static let instance = "neodbInstance"
        static let account = "neodbAccountLabel"
        static let visibility = "neodbVisibility"
        static let postToFediverse = "neodbPostToFediverse"
        static let autoSyncOnLaunch = "neodbAutoSyncOnLaunch"
        static let syncAfterEdit = "neodbSyncAfterEdit"
        static let oauthApp = "neodbOAuthApp"
    }

    // MARK: 设置项

    var visibility: NeodbVisibility {
        get { NeodbVisibility(rawValue: defaults.integer(forKey: Keys.visibility)) ?? .onlyMe }
        set { defaults.set(newValue.rawValue, forKey: Keys.visibility) }
    }

    var postsToFediverse: Bool {
        get { defaults.bool(forKey: Keys.postToFediverse) }
        set { defaults.set(newValue, forKey: Keys.postToFediverse) }
    }

    var autoSyncOnLaunch: Bool {
        get { defaults.object(forKey: Keys.autoSyncOnLaunch) as? Bool ?? false }
        set { defaults.set(newValue, forKey: Keys.autoSyncOnLaunch) }
    }

    var syncAfterEdit: Bool {
        get { defaults.object(forKey: Keys.syncAfterEdit) as? Bool ?? true }
        set { defaults.set(newValue, forKey: Keys.syncAfterEdit) }
    }

    /// 站点固定为官方实例（暂不支持自建实例），钥匙串仍按主机分键。
    var instance: NeodbInstance? { NeodbInstance(raw: NeodbInstance.defaultRaw) }

    var storedToken: String? {
        guard let instance else { return nil }
        return NeodbTokenStore.token(for: instance)
    }

    // MARK: 状态

    func refresh() {
        let token = storedToken
        let loggedIn = instance != nil && (token?.isEmpty == false)
        isLoggedIn = loggedIn
        accountLabel = loggedIn ? (defaults.string(forKey: Keys.account) ?? "") : ""
        pendingCount = dbRead { db in (try? MediaMarkStore.pendingCount(db: db)) ?? 0 } ?? 0
    }

    private var database: (any DatabaseWriter)? { AppDatabase.shared.dbQueue }

    private func dbRead<T>(_ block: (Database) throws -> T) rethrows -> T? {
        guard let dbQueue = database else { return nil }
        return try? dbQueue.read(block)
    }

    private func appendStatus(_ line: String) {
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm:ss"
        statusLines.append("[\(formatter.string(from: Date()))] \(line)")
        if statusLines.count > 8 { statusLines.removeFirst(statusLines.count - 8) }
    }

    // MARK: 登录

    func loginWithPassword(identifier: String, password: String) async throws {
        guard let instance else { throw NeodbError.invalidInstance(NeodbInstance.defaultRaw) }
        let token = try await NeodbService.shared.loginWithPassword(instance: instance, identifier: identifier, password: password)
        try await adopt(token: token, instance: instance)
    }

    func loginWithToken(_ token: String) async throws {
        guard let instance else { throw NeodbError.invalidInstance(NeodbInstance.defaultRaw) }
        let trimmed = token.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { throw NeodbError.passwordLoginFailed(reason: "令牌为空") }
        try await adopt(token: trimmed, instance: instance)
    }

    /// OAuth 第一步：注册应用并给出授权链接，用户在浏览器里点同意。
    func beginOAuth() async throws -> URL {
        guard let instance else { throw NeodbError.invalidInstance(NeodbInstance.defaultRaw) }
        let app = try await NeodbService.shared.registerApp(instance: instance, clientName: "觅影 OmniPlay")
        oauthApp = app
        defaults.set(try JSONEncoder().encodedString(app), forKey: Keys.oauthApp)
        guard let url = NeodbService.shared.authorizeURL(instance: instance, app: app) else {
            throw NeodbError.invalidResponse
        }
        NSWorkspace.shared.open(url)
        return url
    }

    /// OAuth 第二步：把页面上的授权码换成长期令牌。
    func finishOAuth(code: String) async throws {
        guard let instance, let oauthApp else { throw NeodbError.notConfigured }
        let token = try await NeodbService.shared.exchangeCode(instance: instance, app: oauthApp, code: code)
        try await adopt(token: token, instance: instance)
    }

    func logout() {
        if let instance { NeodbTokenStore.remove(for: instance) }
        defaults.set("", forKey: Keys.account)
        refresh()
        appendStatus("已退出 NeoDB 登录。")
    }

    /// 令牌先拿去 `/api/me` 验一遍，验得过才落钥匙串：免得实例填错却存了个废令牌。
    private func adopt(token: String, instance: NeodbInstance) async throws {
        let account = try await NeodbService.shared.fetchAccount(instance: instance, token: token)
        try NeodbTokenStore.save(token: token, for: instance)
        defaults.set(account.label, forKey: Keys.account)
        refresh()
        appendStatus("已登录 \(account.label)。")
    }

    private var didAttemptLaunchSync = false

    /// 启动后跑一次：开了开关且已登录才推，别在没配好之前就发请求。
    func runLaunchSyncIfNeeded() async {
        guard !didAttemptLaunchSync else { return }
        didAttemptLaunchSync = true
        guard autoSyncOnLaunch, isLoggedIn else { return }
        try? await Task.sleep(nanoseconds: 4_000_000_000)
        _ = await syncNow()
    }

    // MARK: 推送

    /// 把本地待同步记录推给 NeoDB，返回给用户看的一行结果。
    @discardableResult
    func syncNow(harvestWatchHistory: Bool = true) async -> String {
        guard isLoggedIn, let instance, let token = storedToken else {
            appendStatus("还没登录 NeoDB，同步没开始。")
            return "请先登录 NeoDB。"
        }
        guard !isSyncing else { return "同步已经在跑了。" }
        guard let dbQueue = database else { return "媒体库还没准备好。" }

        isSyncing = true
        defer { isSyncing = false }

        if harvestWatchHistory {
            let inserted = (try? await dbQueue.write { try MediaMarkStore.harvestWatchHistory(db: $0) }) ?? 0
            if inserted > 0 { appendStatus("从播放记录里补出 \(inserted) 条待同步标记。") }
        }

        var queue = (try? await dbQueue.read { try MediaMarkStore.pendingMarks(db: $0) }) ?? []
        if queue.isEmpty {
            refresh()
            appendStatus("没有需要同步的标记。")
            return "本地标记都已经是最新的。"
        }
        if queue.count >= 200 { queue = Array(queue.prefix(200)) }

        var pushed = 0
        var skipped = 0
        var failures: [String] = []
        for record in queue {
            if MediaMarkStore.isEmpty(record) {
                try? await dbQueue.write { try MediaMarkStore.didSync(db: $0, key: record.key) }
                skipped += 1
                continue
            }
            do {
                let outcome = try await push(record: record, instance: instance, token: token, dbQueue: dbQueue)
                try await dbQueue.write { try MediaMarkStore.didSync(db: $0, key: record.key) }
                pushed += 1
                if outcome == .deleted { skipped += 1 }
            } catch let error as NeodbError {
                if case .unauthorized = error {
                    try? await dbQueue.write { try MediaMarkStore.didFail(db: $0, key: record.key, error: error.localizedDescription) }
                    appendStatus("登录已失效，同步中断。")
                    refresh()
                    return "NeoDB 登录已失效，请重新登录。"
                }
                try? await dbQueue.write { try MediaMarkStore.didFail(db: $0, key: record.key, error: error.localizedDescription) }
                failures.append(error.localizedDescription)
            } catch {
                try? await dbQueue.write { try MediaMarkStore.didFail(db: $0, key: record.key, error: error.localizedDescription) }
                failures.append(error.localizedDescription)
            }
            try? await Task.sleep(nanoseconds: UInt64(NeodbSyncService.minimumRequestInterval * 1_000_000_000))
        }

        refresh()
        lastSyncAt = Date()
        var line = "推送完成：写入 \(pushed) 条"
        if skipped > 0 { line += "，跳过 \(skipped) 条" }
        if !failures.isEmpty { line += "，\(failures.count) 条失败（\(failures.first ?? "")）" }
        appendStatus(line)
        return line
    }

    private enum PushOutcome {
        case wrote
        case deleted
        case unchanged
    }

    private func push(record: MediaMark, instance: NeodbInstance, token: String, dbQueue: any DatabaseWriter) async throws -> PushOutcome {
        let item = try await resolveCatalogItem(localId: record.localId, kind: record.kind,
                                               instance: instance, token: token)
        await rememberRemotePageURL(key: record.key, item: item, fallbackKind: record.kind,
                                    instance: instance, dbQueue: dbQueue)

        let remote = try await NeodbService.shared.remoteMark(instance: instance, token: token, itemUUID: item.uuid)

        // 本地是「未标记」：远端有标记就删掉，没有就什么都不做。
        if record.shelfType == .none {
            guard remote != nil else { return .unchanged }
            try await NeodbService.shared.deleteMark(instance: instance, token: token, itemUUID: item.uuid)
            return .deleted
        }

        let payload = NeodbMarkPayload(mark: record, visibility: visibility, postToFediverse: postsToFediverse)
        let remoteShelf = remote?.shelf ?? .none
        let remoteRating = remote?.ratingGrade ?? 0
        let remoteComment = remote?.commentTrimmed ?? ""
        let needsWrite = remote == nil
            || remoteShelf != record.shelfType
            || remoteRating != record.ratingGrade
            || remoteComment != record.commentTrimmed
        if needsWrite {
            try await NeodbService.shared.setMark(instance: instance, token: token, itemUUID: item.uuid, payload: payload)
            if record.shelfType == .progress, let percent = record.progressPercent {
                try? await NeodbService.shared.setProgress(instance: instance, token: token, itemUUID: item.uuid, percent: percent)
            }
            return .wrote
        }
        return .unchanged
    }

    /// 用豆瓣 / TMDB 外链对上 NeoDB 的条目：手动绑过豆瓣的直接信，走 TMDB 编号时校验一遍标题，
    /// 免得 movie 123 和 tv 123 这种同号不同片把评分写歪；标题因语言不同对不上时不硬拦，
    /// 先记下来，等另一条链接也试完再退回首选那条。
    private func resolveCatalogItem(localId: Int64, kind: MediaMarkKind,
                                    instance: NeodbInstance, token: String) async throws -> NeodbCatalogItem {
        let localTitle = dbRead { db in MediaMarkStore.title(db: db, localId: localId) } ?? nil
        let doubanID = dbRead { db in MediaMarkStore.boundDoubanSubjectID(db: db, movieId: localId) } ?? nil
        let ref = NeodbExternalRef(doubanSubjectID: doubanID, tmdbID: Int(localId), kind: kind)
        guard ref.isResolvable else { throw NeodbError.itemNotFound(url: MediaMark.key(localId: localId)) }

        var item: NeodbCatalogItem? = nil
        var fallback: NeodbCatalogItem? = nil
        var lastError: Error? = nil
        for url in ref.lookupURLs {
            do {
                let candidate = try await NeodbService.shared.catalogItem(instance: instance, token: token, url: url)
                if url == ref.doubanURL || NeodbTitleMatch.likelyMatch(local: localTitle, remote: candidate.title) {
                    item = candidate
                    break
                }
                if fallback == nil { fallback = candidate }
                lastError = NeodbError.wrongItem(localTitle: localTitle ?? MediaMark.key(localId: localId))
            } catch {
                lastError = error
            }
        }
        guard let item = item ?? fallback else {
            throw lastError ?? NeodbError.itemNotFound(url: ref.lookupURLs.first ?? MediaMark.key(localId: localId))
        }
        return item
    }

    private func rememberRemotePageURL(key: String, item: NeodbCatalogItem, fallbackKind: MediaMarkKind,
                                       instance: NeodbInstance, dbQueue: any DatabaseWriter) async {
        guard let url = instance.itemPageURL(category: item.category, fallbackKind: fallbackKind, uuid: item.uuid) else { return }
        try? await dbQueue.write { try MediaMarkStore.setRemoteURL(db: $0, key: key, url: url.absoluteString) }
    }

    /// 弹窗里「打开 NeoDB 页面」用：先读本地记下的地址，没记过就现查一次并记下来。
    func remotePageURL(forLocalId localId: Int64) async -> URL? {
        guard let dbQueue = database else { return nil }
        let cached: MediaMark? = (try? await dbQueue.read { db in
            try MediaMarkStore.mark(db: db, localId: localId)
        }) ?? nil
        if let stored = cached?.remoteUrl, let url = URL(string: stored), url.scheme != nil { return url }
        guard isLoggedIn, let instance, let token = storedToken else { return nil }
        let kind: MediaMarkKind = (try? await dbQueue.read { db in
            MediaMarkStore.derivedKind(db: db, localId: localId)
        }) ?? .movie
        guard let item = try? await resolveCatalogItem(localId: localId, kind: kind, instance: instance, token: token),
              let url = instance.itemPageURL(category: item.category, fallbackKind: kind, uuid: item.uuid) else {
            return nil
        }
        try? await dbQueue.write { try MediaMarkStore.setRemoteURL(db: $0, key: MediaMark.key(localId: localId), url: url.absoluteString) }
        return url
    }

    enum AfterEditSyncOutcome {
        case pushed(String)
        case notPushed(reason: String?)
    }

    /// 详情页保存标记后调用：开了「保存后立即同步」就马上推一次。
    /// 没推的话要说清为什么，不能拿上一条历史记录当本次结果报给用户。
    func syncAfterEditIfNeeded() async -> AfterEditSyncOutcome {
        guard isLoggedIn else { return .notPushed(reason: nil) }
        guard syncAfterEdit else {
            return .notPushed(reason: "改动已存在本地：设置里没开「保存后立即同步」，等下一次同步再推。")
        }
        // 有另一轮同步在跑要等它跑完再补推：那一轮的待推队列在改动落库前就定好了，
        // 不补这一次的话这笔改动会一直留在本地，界面上就是「推送完成但还是脏」。
        let waitedSince = Date()
        while isSyncing && Date().timeIntervalSince(waitedSince) < 20 {
            try? await Task.sleep(nanoseconds: 200_000_000)
        }
        if isSyncing {
            return .notPushed(reason: "改动已存在本地：这一轮同步还没结束，稍后会自动带上。")
        }
        return .pushed(await syncNow(harvestWatchHistory: false))
    }

    /// 播放器播完一部影视后调用：先按播放进度给这些条目补一条自动「已看」记录，
    /// 已登录就马上推给 NeoDB；没登录则留在本地等下一次同步。
    func syncFinishedPlaybackMarks(movieIds: Set<Int64>) async {
        guard !movieIds.isEmpty, let dbQueue = database else { return }
        let wrote = (try? await dbQueue.write { db in
            movieIds.reduce(0) { count, movieId in
                ((try? MediaMarkStore.markFinishedByPlayback(db: db, localId: movieId)) == true) ? count + 1 : count
            }
        }) ?? 0
        refresh()
        guard wrote > 0, isLoggedIn, !isSyncing else { return }
        _ = await syncNow(harvestWatchHistory: false)
    }

    // MARK: 导入

    /// 从 NeoDB 拉回已看 / 在看标记，按豆瓣或 TMDB id 对上本地影片。
    @discardableResult
    func importFromNeodb(maxPages: Int = 10) async -> String {
        guard isLoggedIn, let instance, let token = storedToken else {
            appendStatus("还没登录 NeoDB，导入没开始。")
            return "请先登录 NeoDB。"
        }
        guard !isImporting else { return "导入已经在跑了。" }
        guard let dbQueue = database else { return "媒体库还没准备好。" }

        isImporting = true
        defer { isImporting = false }

        var applied = 0
        var failures: [String] = []
        for kind in MediaMarkKind.allCases {
            for shelf in [NeodbShelfType.complete, .progress] {
                var page = 1
                while page <= maxPages {
                    let marks: [NeodbRemoteMark]
                    do {
                        marks = try await NeodbService.shared.shelfMarks(instance: instance, token: token, shelf: shelf, category: kind, page: page)
                    } catch {
                        failures.append(error.localizedDescription)
                        break
                    }
                    if marks.isEmpty { break }
                    applied += apply(marks: marks, kind: kind, dbQueue: dbQueue)
                    page += 1
                    try? await Task.sleep(nanoseconds: UInt64(NeodbSyncService.minimumRequestInterval * 1_000_000_000))
                }
            }
        }

        refresh()
        NotificationCenter.default.post(name: .libraryUpdated, object: nil)
        var line = "导入完成：更新 \(applied) 条本地标记"
        if !failures.isEmpty { line += "，\(failures.count) 次请求失败（\(failures.first ?? "")）" }
        appendStatus(line)
        return line
    }

    private func apply(marks: [NeodbRemoteMark], kind: MediaMarkKind, dbQueue: any DatabaseWriter) -> Int {
        var applied = 0
        for mark in marks {
            // 季 / 集条目的标记不往整部作品上套，只认条目本身就是电影或剧集的那一层。
            guard mark.itemParentUUID == nil else { continue }
            guard let localId = resolveLocalID(ref: mark.externalRef) else { continue }
            let existing = try? dbQueue.read { try MediaMarkStore.mark(db: $0, localId: localId) }
            let remoteTime = mark.createdAt
            let isNewer = existing == nil
                || remoteTime == 0
                || remoteTime > (existing?.updatedAt ?? 0)
                || MediaMarkStore.isEmpty(existing!)
            guard isNewer else { continue }
            let shouldMarkFilesWatched = existing?.shelfType != .complete
            let ok = (try? dbQueue.write { db in
                try MediaMarkStore.applyRemote(db: db, kind: kind, localId: localId, remote: mark,
                                              markLocalWatched: shouldMarkFilesWatched)
            }) != nil
            if ok {
                applied += 1
                if let uuid = mark.itemUUID, let instance,
                   let url = instance.itemPageURL(category: mark.itemCategory, fallbackKind: kind, uuid: uuid) {
                    try? dbQueue.write { try MediaMarkStore.setRemoteURL(db: $0, key: MediaMark.key(localId: localId), url: url.absoluteString) }
                }
            }
        }
        return applied
    }

    private func resolveLocalID(ref: NeodbExternalRef) -> Int64? {
        dbRead { db in
            if (try? db.tableExists("doubanMetadata")) ?? false,
               let douban = ref.doubanSubjectID,
               let id = (try? Int64.fetchOne(db, sql: "SELECT movieId FROM doubanMetadata WHERE subjectId = ?", arguments: [douban])) ?? nil,
               id > 0, movieExists(id, db) {
                return id
            }
            if let tmdb = ref.tmdbID, tmdb > 0, movieExists(Int64(tmdb), db) {
                return Int64(tmdb)
            }
            return nil
        } ?? nil
    }

    /// 电影和剧集都躺在 `movie` 表里，导入时按 id 查这一张表就够。
    private func movieExists(_ id: Int64, _ db: Database) -> Bool {
        ((try? Int64.fetchOne(db, sql: "SELECT id FROM movie WHERE id = ?", arguments: [id])) ?? 0) == id
    }
}

private extension JSONEncoder {
    func encodedString(_ value: some Encodable) throws -> String {
        String(data: try encode(value), encoding: .utf8) ?? ""
    }
}

