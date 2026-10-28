import Foundation

/// 自动刮削的失败账本。
///
/// 海报、分集剧照、未匹配条目的元数据抓取都会因为网络抖动、源站缺图、TMDB 认不出片而失败。
/// 以前每轮扫描都从零再试一遍，整库里缺图的条目能一直空转。这里按目标记失败次数：
/// 自动连续失败满 3 次就不再自动重试；用户手动点「扫描刮削」会整本清零，所以手动永远能再试。
nonisolated final class ScrapeFailureTracker {
    static let shared = ScrapeFailureTracker()

    /// 自动刮削的容忍上限：同一目标失败满 3 次后不再自动重试。
    static let maxAutoAttempts = 3

    enum Target {
        /// 海报缓存文件名（`PosterManager.cacheFileName` 的结果）
        case poster(String)
        /// videoFile.id
        case still(String)
        /// 代表文件的 videoFile.id
        case scrape(String)

        var key: String {
            switch self {
            case .poster(let id): return "poster:\(id)"
            case .still(let id): return "still:\(id)"
            case .scrape(let id): return "scrape:\(id)"
            }
        }
    }

    private static let storeKey = "scrapeAutoFailureCounts.v1"
    private let lock = NSLock()
    private var counts: [String: Int]

    private init() {
        counts = (UserDefaults.standard.dictionary(forKey: Self.storeKey) as? [String: Int]) ?? [:]
    }

    /// 自动刮削前问一句：这个目标还能不能试。手动动作不用问。
    func allowsAutoAttempt(_ target: Target) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return (counts[target.key] ?? 0) < Self.maxAutoAttempts
    }

    func failureCount(_ target: Target) -> Int {
        lock.lock()
        defer { lock.unlock() }
        return counts[target.key] ?? 0
    }

    /// 记一次失败，返回累计次数（调用方用它打日志）。
    @discardableResult
    func recordFailure(_ target: Target) -> Int {
        lock.lock()
        let key = target.key
        let previous = counts[key] ?? 0
        // 计数收敛在上限，手动清零后重新攒坏账也不会把字典越写越大
        let failures = min(previous + 1, Self.maxAutoAttempts)
        let becameGivingUp = failures == Self.maxAutoAttempts && previous < Self.maxAutoAttempts
        counts[key] = failures
        lock.unlock()
        persist()
        if becameGivingUp {
            print("[OmniPlay][刮削] \(key) 自动刮削连续失败 \(failures) 次，后续扫描不再自动重试（手动点「扫描刮削」会重新计数）。")
        }
        return failures
    }

    /// 抓成功就销账，别留着旧坏账影响下一轮判断。
    func clear(_ target: Target) {
        lock.lock()
        let removed = counts.removeValue(forKey: target.key) != nil
        lock.unlock()
        if removed { persist() }
    }

    /// 手动扫描刮削：整本清零，所有目标重新获得 3 次自动机会。
    func resetAll() {
        lock.lock()
        let hadEntries = !counts.isEmpty
        counts.removeAll()
        lock.unlock()
        if hadEntries { persist() }
    }

    /// 已经放弃自动重试的目标数，用于扫描日志。
    var gaveUpTargetCount: Int {
        lock.lock()
        defer { lock.unlock() }
        return counts.values.filter { $0 >= Self.maxAutoAttempts }.count
    }

    private func persist() {
        lock.lock()
        let snapshot = counts
        lock.unlock()
        UserDefaults.standard.set(snapshot, forKey: Self.storeKey)
    }
}
