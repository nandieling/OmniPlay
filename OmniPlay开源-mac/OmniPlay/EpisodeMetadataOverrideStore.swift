import Foundation

final class EpisodeMetadataOverrideStore {
    static let shared = EpisodeMetadataOverrideStore()

    struct Override: Codable {
        var season: Int
        var episode: Int
        var subtitle: String?
    }

    private let defaultsKey = "OmniPlayEpisodeMetadataOverrides"

    private init() {}

    func override(for fileId: String) -> Override? {
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
        let parsedDisplayName = parsed.season == 0 ? "特别篇 第 \(parsed.episode) 集" : "第 \(parsed.season) 季 第 \(parsed.episode) 集"
        guard let override = override(for: fileId) else {
            return (parsed.season, parsed.episode, parsedDisplayName, parsed.isTVShow)
        }

        var displayName = override.season == 0 ? "特别篇 第 \(override.episode) 集" : "第 \(override.season) 季 第 \(override.episode) 集"
        if let subtitle = override.subtitle, !subtitle.isEmpty {
            displayName += " · \(subtitle)"
        }

        return (override.season, override.episode, displayName, true)
    }

    private func loadOverrides() -> [String: Override] {
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
