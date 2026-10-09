import Foundation

struct LocalSidecarMetadata {
    var title: String?
    var date: String?
    var overview: String?
    var posterPath: String?
    var thumbnailPath: String?
    var voteAverage: Double?
    var tmdbId: Int?
    var seasonNumber: Int?
    var episodeNumber: Int?
    var directors: [String] = []
    var casts: [String] = []

    var hasAnyValue: Bool {
        title?.isEmpty == false ||
        date?.isEmpty == false ||
        overview?.isEmpty == false ||
        posterPath?.isEmpty == false ||
        thumbnailPath?.isEmpty == false ||
        voteAverage != nil ||
        tmdbId != nil ||
        seasonNumber != nil ||
        episodeNumber != nil ||
        !directors.isEmpty ||
        !casts.isEmpty
    }
}

final class LocalMetadataSidecarStore {
    static let shared = LocalMetadataSidecarStore()
    private let imageExtensions = ["jpg", "jpeg", "png", "webp"]

    private init() {}

    func readMovieMetadata(videoURL: URL) -> LocalSidecarMetadata? {
        var metadata = readFirstNFO(candidates: [
            videoURL.deletingPathExtension().appendingPathExtension("nfo"),
            videoURL.deletingLastPathComponent().appendingPathComponent("movie.nfo")
        ]) ?? LocalSidecarMetadata()
        metadata.posterPath = firstExistingImage(
            directory: videoURL.deletingLastPathComponent(),
            stems: ["\(videoURL.deletingPathExtension().lastPathComponent)-poster", videoURL.deletingPathExtension().lastPathComponent, "poster", "folder", "cover"]
        )?.path ?? metadata.posterPath
        return metadata.hasAnyValue ? metadata : nil
    }

    func readTVShowMetadata(videoURL: URL) -> LocalSidecarMetadata? {
        let showDirectory = tvShowDirectory(for: videoURL)
        var metadata = readFirstNFO(candidates: [showDirectory.appendingPathComponent("tvshow.nfo")]) ?? LocalSidecarMetadata()
        metadata.posterPath = firstExistingImage(directory: showDirectory, stems: ["poster", "folder", "cover"])?.path ?? metadata.posterPath
        return metadata.hasAnyValue ? metadata : nil
    }

    func readEpisodeMetadata(videoURL: URL) -> LocalSidecarMetadata? {
        var metadata = readFirstNFO(candidates: [videoURL.deletingPathExtension().appendingPathExtension("nfo")]) ?? LocalSidecarMetadata()
        metadata.thumbnailPath = firstExistingImage(
            directory: videoURL.deletingLastPathComponent(),
            stems: ["\(videoURL.deletingPathExtension().lastPathComponent)-thumb", "\(videoURL.deletingPathExtension().lastPathComponent)-thumbnail", videoURL.deletingPathExtension().lastPathComponent]
        )?.path ?? metadata.thumbnailPath
        return metadata.hasAnyValue ? metadata : nil
    }

    func exportMovie(metadata: LocalSidecarMetadata, videoURL: URL) async {
        writeNFO(metadata: metadata, rootName: "movie", to: videoURL.deletingPathExtension().appendingPathExtension("nfo"))
        await exportPoster(metadata.posterPath, to: videoURL.deletingLastPathComponent().appendingPathComponent("\(videoURL.deletingPathExtension().lastPathComponent)-poster.jpg"))
    }

    func exportTVShow(metadata: LocalSidecarMetadata, videoURL: URL) async {
        let directory = tvShowDirectory(for: videoURL)
        writeNFO(metadata: metadata, rootName: "tvshow", to: directory.appendingPathComponent("tvshow.nfo"))
        await exportPoster(metadata.posterPath, to: directory.appendingPathComponent("poster.jpg"))
    }

    func exportEpisodeThumbnail(sourceURL: URL, videoURL: URL) {
        let destination = videoURL.deletingLastPathComponent().appendingPathComponent("\(videoURL.deletingPathExtension().lastPathComponent)-thumb.jpg")
        copyFile(sourceURL, to: destination)
    }

    private func readFirstNFO(candidates: [URL]) -> LocalSidecarMetadata? {
        for candidate in candidates where FileManager.default.fileExists(atPath: candidate.path) {
            if let metadata = readNFO(candidate), metadata.hasAnyValue {
                return metadata
            }
        }
        return nil
    }

    private func readNFO(_ url: URL) -> LocalSidecarMetadata? {
        guard let document = try? XMLDocument(contentsOf: url, options: [.nodeLoadExternalEntitiesNever]) else {
            return nil
        }

        func value(_ names: [String]) -> String? {
            for name in names {
                if let node = try? document.nodes(forXPath: "//*[local-name()='\(name)']").first,
                   let text = node.stringValue?.trimmingCharacters(in: .whitespacesAndNewlines),
                   !text.isEmpty {
                    return text
                }
            }
            return nil
        }

        let tmdbId = (try? document.nodes(forXPath: "//*[local-name()='uniqueid' and @type='tmdb']").first?.stringValue)
            ?? value(["tmdbid", "tmdb_id", "id"])

        func nameList(_ names: [String]) -> [String] {
            var result: [String] = []
            for name in names {
                guard let nodes = try? document.nodes(forXPath: "//*[local-name()='\(name)']") else { continue }
                for node in nodes {
                    let text = node.stringValue?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
                    if !text.isEmpty { result.append(text) }
                }
            }
            return result
        }

        func actorNames() -> [String] {
            guard let nodes = try? document.nodes(forXPath: "//*[local-name()='actor']") else { return [] }
            var result: [String] = []
            for node in nodes {
                guard let element = node as? XMLElement else { continue }
                var attributeValue: String?
                for attribute in (element.attributes ?? []) {
                    if attribute.name == "name" {
                        attributeValue = attribute.stringValue
                        break
                    }
                }
                let text = (element.stringValue ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                let candidate = (attributeValue ?? text).trimmingCharacters(in: .whitespacesAndNewlines)
                if !candidate.isEmpty { result.append(candidate) }
            }
            return result
        }

        let directors = nameList(["director", "showrunner", "creator"])
        let casts = actorNames()

        return LocalSidecarMetadata(
            title: value(["title", "showtitle", "originaltitle"]),
            date: value(["premiered", "releasedate", "aired", "year"]),
            overview: value(["plot", "overview", "outline"]),
            posterPath: nil,
            thumbnailPath: nil,
            voteAverage: Double(value(["rating", "userrating"]) ?? ""),
            tmdbId: Int(tmdbId ?? ""),
            seasonNumber: Int(value(["season"]) ?? ""),
            episodeNumber: Int(value(["episode"]) ?? ""),
            directors: directors,
            casts: casts
        )
    }

    private func writeNFO(metadata: LocalSidecarMetadata, rootName: String, to url: URL) {
        let root = XMLElement(name: rootName)
        addElement("title", metadata.title, to: root)
        addElement(rootName == "tvshow" ? "premiered" : "releasedate", metadata.date, to: root)
        addElement("plot", metadata.overview, to: root)
        if let voteAverage = metadata.voteAverage {
            addElement("rating", String(format: "%.1f", voteAverage), to: root)
        }
        if let tmdbId = metadata.tmdbId {
            let unique = XMLElement(name: "uniqueid", stringValue: String(tmdbId))
            unique.addAttribute(XMLNode.attribute(withName: "type", stringValue: "tmdb") as! XMLNode)
            unique.addAttribute(XMLNode.attribute(withName: "default", stringValue: "true") as! XMLNode)
            root.addChild(unique)
        }
        if let season = metadata.seasonNumber { addElement("season", String(season), to: root) }
        if let episode = metadata.episodeNumber { addElement("episode", String(episode), to: root) }
        for director in metadata.directors { addElement("director", director, to: root) }
        for actor in metadata.casts {
            let element = XMLElement(name: "actor")
            element.addAttribute(XMLNode.attribute(withName: "name", stringValue: actor) as! XMLNode)
            root.addChild(element)
        }

        let document = XMLDocument(rootElement: root)
        document.version = "1.0"
        document.characterEncoding = "utf-8"
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? document.xmlData(options: [.nodePrettyPrint]).write(to: url)
    }

    private func addElement(_ name: String, _ value: String?, to root: XMLElement) {
        guard let trimmed = value?.trimmingCharacters(in: .whitespacesAndNewlines), !trimmed.isEmpty else { return }
        root.addChild(XMLElement(name: name, stringValue: trimmed))
    }

    private func firstExistingImage(directory: URL, stems: [String]) -> URL? {
        for stem in stems {
            for ext in imageExtensions {
                let candidate = directory.appendingPathComponent(stem).appendingPathExtension(ext)
                if FileManager.default.fileExists(atPath: candidate.path) {
                    return candidate
                }
            }
        }
        return nil
    }

    private func exportPoster(_ posterPath: String?, to destination: URL) async {
        guard let posterPath, !posterPath.isEmpty else { return }
        if posterPath.hasPrefix("/") {
            copyFile(URL(fileURLWithPath: posterPath), to: destination)
            return
        }

        let url: URL?
        if posterPath.hasPrefix("http://") || posterPath.hasPrefix("https://") {
            url = URL(string: posterPath)
        } else {
            url = URL(string: "https://image.tmdb.org/t/p/w500\(posterPath)")
        }
        guard let url else { return }
        do {
            let (data, _) = try await URLSession.shared.data(from: url)
            try FileManager.default.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
            try data.write(to: destination, options: .atomic)
        } catch {}
    }

    private func copyFile(_ source: URL, to destination: URL) {
        guard FileManager.default.fileExists(atPath: source.path) else { return }
        do {
            try FileManager.default.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
            if FileManager.default.fileExists(atPath: destination.path) {
                try FileManager.default.removeItem(at: destination)
            }
            try FileManager.default.copyItem(at: source, to: destination)
        } catch {}
    }

    private func tvShowDirectory(for videoURL: URL) -> URL {
        let parent = videoURL.deletingLastPathComponent()
        let name = parent.lastPathComponent.lowercased()
        if name.range(of: #"^(season\s*\d+|s\d{1,2}|第\s*\d+\s*季)$"#, options: .regularExpression) != nil {
            return parent.deletingLastPathComponent()
        }
        return parent
    }
}
