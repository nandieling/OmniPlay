import Foundation
import GRDB

// MARK: - 短剧（竖屏/微短剧）识别与元数据
//
// 短剧的特征是「一个目录几十到上百个文件、单集一两分钟、片名只有中文」，
// 现有的剧集识别只认 SxxExx / 第N集，纯数字分集会被当成一百部独立电影，
// 每张卡各刮一次、配额按集数烧。这里补三件事：目录级识别、集号解析、
// 以及一条与 doubanMetadata 同形态的旁表存短剧专属字段。

enum ShortDrama {
    /// `videoFile.mediaKind` 取值。缺列或旧库读不到时按 nil 处理，走 mediaType 老逻辑。
    nonisolated static let kindValue = "shortDrama"

    static let sensitivityDefaultsKey = "shortDramaSensitivity"
    static let enabledDefaultsKey = "enableShortDramaScrape"
    static let doubanBudgetDefaultsKey = "shortDramaDoubanBudget"
    static let hongguoEnabledDefaultsKey = "enableHongguoShortDramaSource"

    /// 默认开：识别不出来的目录仍走原来的电影/剧集分支，不会被短剧逻辑抢走。
    static func isEnabled(_ defaults: UserDefaults = .standard) -> Bool {
        defaults.object(forKey: enabledDefaultsKey) == nil ? true : defaults.bool(forKey: enabledDefaultsKey)
    }

    static func doubanBudgetPerScan(_ defaults: UserDefaults = .standard) -> Int {
        let stored = defaults.integer(forKey: doubanBudgetDefaultsKey)
        return stored > 0 ? stored : 20
    }

    /// 红果补充源默认开：它给的是权威集数、简介、竖版封面与分类标签，
    /// 只往短剧旁表里补字段，不决定条目身份。
    static func hongguoEnabled(_ defaults: UserDefaults = .standard) -> Bool {
        defaults.object(forKey: hongguoEnabledDefaultsKey) == nil ? true : defaults.bool(forKey: hongguoEnabledDefaultsKey)
    }
}

/// 短剧标题比对。目录名与来源标题都可能带季号（`S07`、「持械入宋：第七季」），
/// 季号必须参与比对——S07 的目录绑到「第一季」比认不出来更糟。
enum ShortDramaTitleMatch {
    nonisolated static func normalize(_ value: String) -> String {
        value
            .lowercased()
            .replacingOccurrences(of: #"[【】（）《》「」『』\[\]()·\.\-_\s:：,，、/／]"#, with: "", options: .regularExpression)
    }

    /// 剥掉结尾的季号（`：第七季`、` 第3季`、` S07`），返回核心片名与季号。
    nonisolated static func splitSeason(from title: String) -> (core: String, season: Int?) {
        let trimmed = title.trimmingCharacters(in: .whitespacesAndNewlines)
        let patterns = [
            #"[：:·/\s._-]*第\s*([0-9]{1,3}|[零一二两三四五六七八九十]{1,3})\s*季$"#,
            #"[·\s._-]*[Ss](?:eason)?\s*([0-9]{1,2})$"#
        ]
        for pattern in patterns {
            guard let regex = try? NSRegularExpression(pattern: pattern) else { continue }
            let range = NSRange(trimmed.startIndex..<trimmed.endIndex, in: trimmed)
            guard let match = regex.firstMatch(in: trimmed, options: [], range: range), match.numberOfRanges >= 2,
                  let whole = Range(match.range, in: trimmed),
                  let token = Range(match.range(at: 1), in: trimmed) else { continue }
            let season = Int(String(trimmed[token])) ?? chineseNumber(String(trimmed[token]))
            let core = String(trimmed[..<whole.lowerBound]).trimmingCharacters(in: .whitespacesAndNewlines)
            guard !core.isEmpty else { continue }
            return (core, season)
        }
        return (trimmed, nil)
    }

    /// 季号校正：对得上加分，对不上大幅扣分，来源没季号小幅扣分。
    nonisolated static func seasonAdjusted(base: Int, candidateSeason: Int?, wantedSeason: Int?) -> Int {
        guard base > 0 else { return 0 }
        var score = base
        if let wanted = wantedSeason {
            if let candidate = candidateSeason {
                // 认错季会塞进来一段看着像真的但说错内容的简介，比不补更坏
                score += candidate == wanted ? 10 : -70
            } else {
                score -= 15
            }
        } else if candidateSeason != nil {
            score -= 10
        }
        return min(100, max(0, score))
    }

    private nonisolated static let chineseDigits: [Character: Int] = [
        "零": 0, "一": 1, "二": 2, "两": 2, "三": 3, "四": 4,
        "五": 5, "六": 6, "七": 7, "八": 8, "九": 9
    ]

    /// 支持「七」「十」「二十一」这类写法，够覆盖季号常见范围。
    private nonisolated static func chineseNumber(_ text: String) -> Int? {
        let characters = Array(text)
        guard !characters.isEmpty else { return nil }
        if characters.count == 1, let value = chineseDigits[characters[0]] { return value }
        guard let tenIndex = characters.firstIndex(of: "十") else { return nil }
        let head = characters[..<tenIndex]
        let tail = characters[characters.index(after: tenIndex)...]
        let tens: Int
        if head.isEmpty {
            tens = 1
        } else if head.count == 1, let value = chineseDigits[head[0]] {
            tens = value
        } else {
            return nil
        }
        let ones: Int
        if tail.isEmpty {
            ones = 0
        } else if tail.count == 1, let value = chineseDigits[tail[tail.startIndex]] {
            ones = value
        } else {
            return nil
        }
        guard tens > 0 else { return nil }
        return tens * 10 + ones
    }
}

/// 识别灵敏度，对应设置页三档。分数算法见 `ShortDramaClassifier`。
enum ShortDramaSensitivity: String, CaseIterable, Identifiable {
    case conservative
    case standard
    case aggressive

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .conservative: return "保守（只在特征极强时识别为短剧）"
        case .standard: return "标准（推荐）"
        case .aggressive: return "激进（目录文件多即认短剧）"
        }
    }

    nonisolated var threshold: Int {
        switch self {
        case .conservative: return 3
        case .standard: return 2
        case .aggressive: return 1
        }
    }

    /// 激进档允许只靠「同目录文件多」这一条信号成立，其余档位要求至少两条信号。
    nonisolated var allowsSingleSignal: Bool { self == .aggressive }

    static func current(_ defaults: UserDefaults = .standard) -> ShortDramaSensitivity {
        guard let raw = defaults.string(forKey: ShortDrama.sensitivityDefaultsKey) else { return .standard }
        return ShortDramaSensitivity(rawValue: raw) ?? .standard
    }
}

/// 一个目录里的短剧判定结果。
struct ShortDramaVerdict: Equatable {
    var isShortDrama: Bool
    var score: Int
    var reasons: [String]
    /// 解析出的集号，升序去重。
    var episodeNumbers: [Int]
    /// 合集文件（`全70集.mp4` 这类），它们不参与集号统计。
    var compilationCount: Int
    /// 单集文件或时长有没有给出「一集很短」的体量证据。
    /// 只有文件数和集号是不够的——一部 76 集的电视剧也是同样的形状。
    var contentConfirmed: Bool = false
    /// 形状像短剧、但体量证据不足被闸门拦下的。这类目录不贴电影/剧集标签，
    /// 留着手调灵敏度还能救回来。
    var blockedByContentGate: Bool = false

    nonisolated static let notShortDrama = ShortDramaVerdict(
        isShortDrama: false, score: 0, reasons: [], episodeNumbers: [], compilationCount: 0
    )
}

enum ShortDramaClassifier {
    /// 文件名以纯数字开头（`001.mp4`、`12-超清.mp4`）时的集号。上限 999 防误吃年份。
    nonisolated static func bareEpisodeNumber(from fileName: String) -> Int? {
        let stem = ((fileName as NSString).lastPathComponent as NSString).deletingPathExtension
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard let match = stem.firstMatch(pattern: #"^0*(\d{1,3})(?:[^\d]|$)"#), match.count > 1,
              let value = Int(match[1]), value > 0 else {
            return nil
        }
        // 四位数或「1080」这类分辨率前缀不当集号
        let digits = stem.prefix(while: { $0.isNumber })
        guard digits.count <= 3 else { return nil }
        return value
    }

    /// 短剧分集号：纯数字、`第N集/话`、`EP N`/`E N` 都认。
    nonisolated static func episodeNumber(from fileName: String) -> Int? {
        let stem = ((fileName as NSString).lastPathComponent as NSString).deletingPathExtension
        if let match = stem.firstMatch(pattern: #"第\s*(\d{1,3})\s*[集话]"#), match.count > 1,
           let value = Int(match[1]), value > 0 {
            return value
        }
        if let match = stem.firstMatch(pattern: #"(?i)\b(?:ep|e|part)\s*[-_ ]?(\d{1,3})\b"#), match.count > 1,
           let value = Int(match[1]), value > 0 {
            return value
        }
        return bareEpisodeNumber(from: fileName)
    }

    /// 合集/合并文件：整部塞在一个文件里，不该拆集。
    nonisolated static func isCompilationFile(_ fileName: String) -> Bool {
        let stem = ((fileName as NSString).lastPathComponent as NSString).deletingPathExtension
        if stem.containsPattern(#"全\s*\d{1,3}\s*集"#) { return true }
        if stem.containsPattern(#"第?\s*\d{1,3}\s*[-–~]\s*\d{1,3}\s*集"#) { return true }
        if stem.containsPattern(#"(全集|合集|完整版|一键连播|一口气)"#) { return true }
        return false
    }

    nonisolated static func episodeDisplayName(episode: Int) -> String {
        "第\(episode)集"
    }

    /// 目录级打分。判据与分值见方案文档 §3，这里只补一条实现期的修正：
    /// 文件数分不开短剧和长篇国产剧（一部 76 集的电视剧也是几十上百个文件），
    /// 而扫描期时长还没入库，所以改用「文件大小」当量——短剧单集几十 MB，
    /// 长剧单集几百 MB 起，这个量级差在入库时就已经拿得到。
    ///
    /// - 同目录文件数：≥30 记 2 分，20–29 记 1 分
    /// - 集号连到 ≥30，或连到 20–29 且多数文件名以纯数字开头：记 2 分
    /// - 中位文件大小 <150MB 记 2 分，>400MB 扣 3 分
    /// - 已知平均时长 45–420s 记 2 分，421–600s 记 1 分，>900s 扣 3 分
    /// - 目录名带「短剧 / 竖屏 / 合集 / N集」记 1 分
    /// - 目录里出现 SxxExx 命名扣 1 分
    ///
    /// 分数之外还有一道闸门：文件或时长至少要给出一条体量证据，否则不认短剧。
    /// 150–400MB 是模糊区间（不加分也不否决），落在这里的目录走原来的电影/剧集分支；
    /// 激进档本来就是「文件多即认」，由它越过闸门。
    nonisolated static func evaluate(
        directoryPath: String,
        fileNames: [String],
        fileSizes: [Int64] = [],
        durations: [Double] = [],
        sensitivity: ShortDramaSensitivity = .standard
    ) -> ShortDramaVerdict {
        // 合集目录里往往只有一个文件，不参与判定
        let usable = fileNames.filter { !isCompilationFile($0) }
        guard usable.count >= 10 else { return .notShortDrama }

        var score = 0
        var signals = 0
        var reasons: [String] = []
        var contentConfirmed = false

        func report(_ delta: Int, _ reason: String, countsAsSignal: Bool = true) {
            score += delta
            if delta > 0 {
                if countsAsSignal { signals += 1 }
                reasons.append(reason)
            } else if delta < 0 {
                reasons.append(reason)
            }
        }

        if usable.count >= 30 {
            report(2, "同目录 \(usable.count) 个文件")
        } else if usable.count >= 20 {
            report(1, "同目录 \(usable.count) 个文件")
        }

        let episodes = Set(usable.compactMap { episodeNumber(from: $0) }).sorted()
        let bareNamed = usable.filter { bareEpisodeNumber(from: $0) != nil }.count
        let bareMajority = bareNamed * 2 >= usable.count
        if episodes.count >= 30 {
            report(2, "集号连到 \(episodes.last ?? 0)")
        } else if episodes.count >= 20 && bareMajority {
            report(2, "集号连到 \(episodes.last ?? 0)，纯数字命名")
        }

        // 短剧单集 1–3 分钟，转码后普遍在几十 MB；长剧一集几百 MB 起。
        let knownSizes = fileSizes.filter { $0 > 0 }.sorted()
        if knownSizes.count >= max(3, usable.count / 4) {
            let median = knownSizes[knownSizes.count / 2]
            let megabytes = Double(median) / 1_048_576
            if median < 150 * 1_048_576 {
                report(2, "中位文件 \(Int(megabytes)) MB")
                contentConfirmed = true
            } else if median > 400 * 1_048_576 {
                report(-3, "中位文件 \(Int(megabytes)) MB，更像长剧", countsAsSignal: false)
            }
        }

        let knownDurations = durations.filter { $0 > 0 }
        if knownDurations.count >= max(3, usable.count / 4) {
            let average = knownDurations.reduce(0, +) / Double(knownDurations.count)
            if average >= 45 && average <= 420 {
                report(2, "平均时长 \(Int(average)) 秒")
                contentConfirmed = true
            } else if average > 420 && average <= 600 {
                report(1, "平均时长 \(Int(average)) 秒偏长")
            } else if average > 900 {
                report(-3, "平均时长 \(Int(average)) 秒，更像长剧", countsAsSignal: false)
            }
        }

        if directoryPath.containsPattern(#"(短剧|竖屏)"#) || directoryPath.containsPattern(#"\d{2,3}\s*集"#) {
            report(1, "目录名带短剧特征")
        } else if directoryPath.containsPattern(#"(合集|全集)"#) {
            report(1, "目录名带合集字样")
        }

        if usable.contains(where: { $0.containsPattern(#"(?i)\bs\d{1,2}\s*[eE]\d{1,3}\b"#) }) {
            report(-1, "出现 SxxExx 命名", countsAsSignal: false)
        }

        // 体量闸门：长剧和短剧在「文件多 + 集号连续」上完全同形，只有单集体量分得开。
        if !contentConfirmed && sensitivity != .aggressive {
            return ShortDramaVerdict(
                isShortDrama: false,
                score: score,
                reasons: reasons + ["缺少短剧体量证据（单集文件或时长）"],
                episodeNumbers: episodes,
                compilationCount: fileNames.count - usable.count,
                // 只有形状确实像短剧（文件多、集号连得上）才值得留给灵敏度去救，
                // 普通二十集以下的目录直接按电影/剧集处理。
                blockedByContentGate: usable.count >= 30 || episodes.count >= 30
            )
        }

        let enoughSignals = sensitivity.allowsSingleSignal ? signals >= 1 : signals >= 2
        return ShortDramaVerdict(
            isShortDrama: score >= sensitivity.threshold && enoughSignals,
            score: score,
            reasons: reasons,
            episodeNumbers: episodes,
            compilationCount: fileNames.count - usable.count,
            contentConfirmed: contentConfirmed
        )
    }

    /// 老库重分类里「只贴类型标签」的判据：目录本身像短剧就行，不管条目身上有没有 metadata。
    /// 已经整部归成一条记录的目录（比如走剧集路径分好组的 `S07` 目录）只需要补 mediaKind，
    /// 就能进首页的「只看短剧」，不用碰用户已经刮好的标题、海报和简介。
    nonisolated static func shouldTagExistingDirectory(_ verdict: ShortDramaVerdict) -> Bool {
        verdict.isShortDrama
    }

    /// 老库重分类里「合并成一条」的判据：目录像短剧，且这批记录身上没有值得保留的东西。
    nonisolated static func shouldMergeExistingDirectory(
        _ verdict: ShortDramaVerdict,
        hasValuableMetadata: Bool
    ) -> Bool {
        shouldTagExistingDirectory(verdict) && !hasValuableMetadata
    }

    /// 上面两条的便捷写法。重分类那边已经算过判定，走 verdict 版本不重复算分。
    nonisolated static func shouldTagExistingDirectory(
        directoryPath: String,
        fileNames: [String],
        fileSizes: [Int64] = [],
        durations: [Double] = [],
        sensitivity: ShortDramaSensitivity = .standard
    ) -> Bool {
        evaluate(
            directoryPath: directoryPath,
            fileNames: fileNames,
            fileSizes: fileSizes,
            durations: durations,
            sensitivity: sensitivity
        ).isShortDrama
    }

    /// 老库重分类里「合并成一条」的判据：目录像短剧，且这些条目身上没有值得保留的东西。
    /// 「值得保留」= 有海报 / 有评分 / 绑过豆瓣 / 有 NeoDB 标记 / 用户手改过（isLocked），
    /// 这些情况一律不合并，免得把用户手工整理好的库冲掉。
    nonisolated static func shouldMergeExistingDirectory(
        directoryPath: String,
        fileNames: [String],
        fileSizes: [Int64] = [],
        durations: [Double] = [],
        hasValuableMetadata: Bool,
        sensitivity: ShortDramaSensitivity = .standard
    ) -> Bool {
        shouldMergeExistingDirectory(
            evaluate(
                directoryPath: directoryPath,
                fileNames: fileNames,
                fileSizes: fileSizes,
                durations: durations,
                sensitivity: sensitivity
            ),
            hasValuableMetadata: hasValuableMetadata
        )
    }

    /// 目录级判定的输入：把同一父目录的文件聚在一起。
    nonisolated static func directoryKey(relativePath: String) -> String {
        let normalized = relativePath.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let slash = normalized.lastIndex(of: "/") else { return "" }
        return String(normalized[normalized.startIndex..<slash])
    }

    /// 路径里的季号（`S07`、`Season 7`、`第7季`）。多季短剧要靠它保留季号。
    nonisolated static func seasonNumber(inPath path: String) -> Int? {
        for pattern in [
            #"(?i)(?:^|[\s._(\[-])s(?:eason)?[\s._-]*(\d{1,2})(?![\d])"#,
            #"第\s*(\d{1,2})\s*季"#
        ] {
            if let match = path.firstMatch(pattern: pattern), match.count > 1, let value = Int(match[1]) {
                return value
            }
        }
        return nil
    }
}

/// 短剧专属元数据旁表，形态照 doubanMetadata：一个条目一行，来源与刷新时间一起存。
struct ShortDramaMetadata: Codable, FetchableRecord, PersistableRecord {
    static let databaseTableName = "shortDramaMetadata"

    static let createTableSQL = """
        CREATE TABLE IF NOT EXISTS "shortDramaMetadata"(\
        "movieId" INTEGER PRIMARY KEY REFERENCES "movie"("id") ON DELETE CASCADE, \
        "source" TEXT NOT NULL, \
        "externalId" TEXT, \
        "platform" TEXT, \
        "episodeCount" INTEGER, \
        "leads" TEXT, \
        "tags" TEXT, \
        "posterOrientation" TEXT, \
        "fetchedAt" DOUBLE NOT NULL, \
        "nextRefreshAt" DOUBLE NOT NULL, \
        "lastError" TEXT)
        """

    static func createTable(_ db: Database) throws {
        try db.execute(sql: createTableSQL)
    }

    var movieId: Int64
    /// tmdb / douban / local
    var source: String
    var externalId: String?
    /// 红果 / 河马 / 抖音 之类，来源给了才填
    var platform: String?
    var episodeCount: Int?
    /// JSON 字符串数组，短剧主角常常不在 TMDB credits 里
    var leads: String?
    /// JSON 字符串数组：逆袭 / 重生 / 穿越 / 甜宠
    var tags: String?
    var posterOrientation: String?
    var fetchedAt: Double
    var nextRefreshAt: Double
    var lastError: String?

    static func save(
        db: Database,
        movieId: Int64,
        source: String,
        externalId: String? = nil,
        platform: String? = nil,
        episodeCount: Int? = nil,
        tags: [String] = [],
        posterOrientation: String? = nil,
        lastError: String? = nil,
        refreshInterval: TimeInterval = 30 * 24 * 3600,
        now: Date = Date()
    ) throws {
        let record = ShortDramaMetadata(
            movieId: movieId,
            source: source,
            externalId: externalId,
            platform: platform,
            episodeCount: episodeCount,
            leads: nil,
            tags: tags.isEmpty ? nil : Movie.encodePersonList(tags),
            posterOrientation: posterOrientation,
            fetchedAt: now.timeIntervalSince1970,
            nextRefreshAt: now.timeIntervalSince1970 + refreshInterval,
            lastError: lastError
        )
        try record.save(db)
    }

    var tagList: [String] { Movie.decodePersonList(tags) }
    var leadList: [String] { Movie.decodePersonList(leads) }
}

extension ShortDramaMetadata {
    /// 换主键。必须用一条 UPDATE：占位影片行随后就被删掉，而这张表的外键是
    /// `ON DELETE CASCADE`，「先删再插」会在两步之间被级联带走（真机上就丢过一次）。
    nonisolated static func rekey(_ db: Database, from oldID: Int64, to newID: Int64) throws {
        try db.execute(
            sql: "UPDATE shortDramaMetadata SET movieId = ? WHERE movieId = ?",
            arguments: [newID, oldID]
        )
    }

    /// 缺行时补一条，来源按现有证据认：绑过豆瓣算 douban，已经刮到在线条目算 tmdb，
    /// 其余算 local。已经有行的不动，免得盖掉真实来源。
    nonisolated static func backfillMissing(
        db: Database,
        movieId: Int64,
        episodeCount: Int,
        hasDouban: Bool = false,
        scrapedOnline: Bool = false
    ) throws {
        guard try ShortDramaMetadata.fetchOne(db, key: movieId) == nil else { return }
        let source: String
        if hasDouban || ShortDramaClassifier.isDoubanBacked(movieID: movieId) {
            source = "douban"
        } else {
            source = scrapedOnline ? "tmdb" : "local"
        }
        try ShortDramaMetadata.save(db: db, movieId: movieId, source: source, episodeCount: episodeCount)
    }

    /// 补充源（红果）来的字段并进去：已有值一律不覆盖，缺什么补什么。
    /// 来源字段只在整行还是本地占位时才升级，豆瓣 / TMDB 先写好的出处不被抢走。
    nonisolated static func enrich(
        db: Database,
        movieId: Int64,
        source: String,
        externalId: String?,
        platform: String?,
        episodeCount: Int?,
        tags: [String],
        posterOrientation: String?
    ) throws {
        let now = Date().timeIntervalSince1970
        var record = try ShortDramaMetadata.fetchOne(db, key: movieId)
        let isNew = record == nil
        if isNew {
            record = ShortDramaMetadata(
                movieId: movieId,
                source: "local",
                externalId: nil,
                platform: nil,
                episodeCount: nil,
                leads: nil,
                tags: nil,
                posterOrientation: nil,
                fetchedAt: now,
                nextRefreshAt: now + 30 * 24 * 3600,
                lastError: nil
            )
        }
        guard var filled = record else { return }
        if filled.externalId == nil { filled.externalId = externalId }
        if filled.platform == nil { filled.platform = platform }
        if filled.episodeCount == nil { filled.episodeCount = episodeCount }
        if filled.tags == nil, !tags.isEmpty { filled.tags = Movie.encodePersonList(tags) }
        if filled.posterOrientation == nil { filled.posterOrientation = posterOrientation }
        if isNew || filled.source == "local" { filled.source = source }
        try filled.save(db)
    }

    /// 文件已经标成短剧、旁表却没有记录的条目（旧库，或占位行换真实 id 时被级联删过），
    /// 一次扫出来补齐，别让旁表长期空着。
    nonisolated static func backfillMissingRows(db: Database, limit: Int = 500) throws -> Int {
        let rows = try Row.fetchAll(
            db,
            sql: """
                SELECT vf.movieId AS movieId, COUNT(*) AS episodeCount,
                       MAX(CASE WHEN dm.movieId IS NOT NULL THEN 1 ELSE 0 END) AS hasDouban,
                       MAX(CASE WHEN vf.mediaType = 'unmatched' THEN 1 ELSE 0 END) AS hasUnmatched
                FROM videoFile vf
                LEFT JOIN doubanMetadata dm ON dm.movieId = vf.movieId
                WHERE vf.mediaKind = ? AND vf.movieId IS NOT NULL
                  AND vf.movieId NOT IN (SELECT movieId FROM shortDramaMetadata)
                GROUP BY vf.movieId
                LIMIT ?
                """,
            arguments: [ShortDrama.kindValue, limit]
        )
        var filled = 0
        for row in rows {
            let movieId: Int64 = row["movieId"]
            let episodeCount: Int = row["episodeCount"]
            let hasDouban: Int = row["hasDouban"]
            let hasUnmatched: Int = row["hasUnmatched"]
            try ShortDramaMetadata.backfillMissing(
                db: db,
                movieId: movieId,
                episodeCount: episodeCount,
                hasDouban: hasDouban != 0,
                scrapedOnline: hasUnmatched == 0
            )
            filled += 1
        }
        return filled
    }
}

private extension String {
    nonisolated func firstMatch(pattern: String, options: NSRegularExpression.Options = []) -> [String]? {
        matches(pattern: pattern, options: options).first
    }

    nonisolated func containsPattern(_ pattern: String, options: NSRegularExpression.Options = []) -> Bool {
        firstMatch(pattern: pattern, options: options) != nil
    }

    nonisolated func matches(pattern: String, options: NSRegularExpression.Options = []) -> [[String]] {
        guard let regex = try? NSRegularExpression(pattern: pattern, options: options) else { return [] }
        let nsRange = NSRange(startIndex..<endIndex, in: self)
        return regex.matches(in: self, options: [], range: nsRange).map { result in
            (0..<result.numberOfRanges).map { index in
                guard let range = Range(result.range(at: index), in: self) else { return "" }
                return String(self[range])
            }
        }
    }
}

extension ShortDramaClassifier {
    /// 用目录名拼搜索词：去掉集数、清晰度、年份这类装饰，只留片名。
    nonisolated static func searchKeyword(directoryPath: String) -> String {
        let name = directoryPath
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .split(separator: "/")
            .last
            .map(String.init) ?? directoryPath
        var cleaned = name
        for pattern in [
            #"(?i)(?:^|[\s._(\[-])s(?:eason)?[\s._-]*\d{1,2}(?![\d])"#,
            #"(?i)\b(1080p|720p|2160p|4k|x264|x265|hevc|web-?dl|webrip|hdrip)\b"#,
            #"\d{4}"#,
            #"\d{2,3}\s*[-–~]?\s*\d{0,3}\s*集"#,
            #"(短剧|竖屏|合集|全集|完整版|中英文字幕|双语字幕)"#,
            #"[（(]\s*\d{4}\s*[)）]"#
        ] {
            cleaned = cleaned.replacingOccurrences(of: pattern, with: " ", options: .regularExpression)
        }
        // 装饰剥掉后剩下的连接符（「-100集」拆解后剩的破折号、年份留下的空括号）一并清掉，
        // 否则搜索词会带尾缀，豆瓣那条精确同名判据就吃不到。
        cleaned = cleaned
            .replacingOccurrences(
                of: #"[【】（）()《》「」『』\[\]_\.·,，、+]"#,
                with: " ",
                options: .regularExpression
            )
            .replacingOccurrences(of: #"[-–—]+"#, with: " ", options: .regularExpression)
            .replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return cleaned.isEmpty ? name.trimmingCharacters(in: .whitespacesAndNewlines) : cleaned
    }

    /// 豆瓣条目在 movie 表里占用的主键。豆瓣 subject id 与 TMDB id 同为 7–8 位整数，
    /// 直接混用会撞号串片，所以整体挪到一个独立号段，减回偏移就是 subject id。
    nonisolated static let doubanIDOffset: Int64 = 10_000_000_000

    nonisolated static func doubanLocalID(subjectID: Int) -> Int64 {
        doubanIDOffset + Int64(subjectID)
    }

    nonisolated static func isDoubanBacked(movieID: Int64) -> Bool {
        movieID >= doubanIDOffset
    }
}

/// 首页海报墙的类型筛选。目前库里只有短剧能从「影视」里分出来，
/// 等 mediaKind 也给剧集写上值之后，这里再扩「电影 / 剧集」两档。
enum LibraryKindFilter: String, CaseIterable, Identifiable {
    case all
    case movie
    case tv
    case shortDrama

    var id: String { rawValue }

    var label: String {
        switch self {
        case .all: return "全部影视"
        case .movie: return "只看电影"
        case .tv: return "只看剧集"
        case .shortDrama: return "只看短剧"
        }
    }

    /// 按钮上占一格位置的小标签，选中非默认档时显示
    var shortLabel: String { self == .all ? "" : label.replacingOccurrences(of: "只看", with: "") }

    var symbolName: String {
        switch self {
        case .all: return "square.grid.2x2"
        case .movie: return "film"
        case .tv: return "play.tv"
        case .shortDrama: return "play.rectangle.on.rectangle"
        }
    }

    /// 对应的 `videoFile.mediaKind` 取值，`.all` 表示不过滤。
    var kindValue: String? { self == .all ? nil : rawValue }
}

/// 电影与剧集的形态判定，写进 `videoFile.mediaKind`。
/// 只认文件名与片名里的集号：多碟电影（CD1/CD2、Part 1）没有集号，仍然是电影，
/// 不能因为「一个条目两个文件」就判成剧集。
enum MediaKindResolver {
    nonisolated static let movie = "movie"
    nonisolated static let tv = "tv"

    /// 一个条目（可能含多个文件）算电影还是剧集。
    nonisolated static func resolve(fileNames: [String], title: String = "") -> String {
        for name in fileNames {
            let stem = ((name as NSString).lastPathComponent as NSString).deletingPathExtension
            if stem.containsPattern(#"(?i)\bs\d{1,2}[eE]\d{1,3}\b"#) { return tv }
            if stem.containsPattern(#"第\s*\d{1,4}\s*[集话]"#) { return tv }
            if stem.containsPattern(#"(?i)\bep[-_ ]?\d{1,4}\b"#) { return tv }
        }
        if title.containsPattern(#"第\s*[0-9零〇一二三四五六七八九十百]{1,4}\s*季"#) { return tv }
        return movie
    }
}
