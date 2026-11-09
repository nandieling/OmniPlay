import Foundation

/// 状态全部落在 `UserDefaults` 里，本尊没有可变态字段。
/// 本工程开了 `SWIFT_DEFAULT_ACTOR_ISOLATION = MainActor`，播放表要在后台任务里按集序排序，
/// 所以读取这条链（`shared`、`override(for:)`、`loadOverrides`）显式留在非隔离一侧。
final class EpisodeMetadataOverrideStore: @unchecked Sendable {
    nonisolated static let shared = EpisodeMetadataOverrideStore()

    struct Override: Codable {
        var season: Int
        var episode: Int
        var subtitle: String?
    }

    private nonisolated let defaultsKey = "OmniPlayEpisodeMetadataOverrides"

    private init() {}

    nonisolated func override(for fileId: String) -> Override? {
        loadOverrides()[fileId]
    }

    func saveOverride(fileId: String, season: Int, episode: Int, subtitle: String?) {
        var overrides = loadOverrides()
        let normalizedSubtitle = subtitle?.trimmingCharacters(in: .whitespacesAndNewlines)
        overrides[fileId] = Override(
            season: max(0, season),
            episode: max(1, episode),
            subtitle: normalizedSubtitle?.isEmpty == true ? nil : normalizedSubtitle
        )
        persist(overrides)
    }

    func resolvedEpisodeInfo(fileId: String, fileName: String, fallbackIndex: Int) -> (season: Int, episode: Int, displayName: String, isTVShow: Bool) {
        let parsed = MediaNameParser.parseEpisodeDescriptor(from: fileName, fallbackIndex: fallbackIndex)
        let parsedDisplayName = parsed.displayName
        guard let override = override(for: fileId) else {
            return (parsed.season, parsed.episode, parsedDisplayName, parsed.isTVShow)
        }

        var displayName = override.season == 0 ? "特别篇 第 \(override.episode) 集" : "第 \(override.season) 季 第 \(override.episode) 集"
        if let subtitle = override.subtitle, !subtitle.isEmpty {
            displayName += " · \(subtitle)"
        } else if let detailRange = parsedDisplayName.range(of: " · ") {
            displayName += String(parsedDisplayName[detailRange.lowerBound...])
        }

        return (override.season, override.episode, displayName, true)
    }

    /// 播放表、分集墙、「下一集」「继续播放」共用的排序输入。
    nonisolated func playbackSortOverride(for fileId: String) -> MediaNameParser.EpisodeOrderOverride? {
        guard let override = override(for: fileId) else { return nil }
        return MediaNameParser.EpisodeOrderOverride(season: override.season, episode: override.episode)
    }

    nonisolated private func loadOverrides() -> [String: Override] {
        guard let data = UserDefaults.standard.data(forKey: defaultsKey),
              let decoded = try? JSONDecoder().decode([String: Override].self, from: data) else {
            return [:]
        }
        return decoded
    }

    private func persist(_ overrides: [String: Override]) {
        guard let data = try? JSONEncoder().encode(overrides) else { return }
        UserDefaults.standard.set(data, forKey: defaultsKey)
    }
}
