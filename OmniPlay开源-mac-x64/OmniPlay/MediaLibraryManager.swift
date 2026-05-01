import Foundation
import GRDB
import SwiftUI

extension Notification.Name { static let libraryUpdated = Notification.Name("OmniPlayLibraryUpdated") }

enum MediaNameParser {
    nonisolated static func cleanedTitleSource(from rawPath: String) -> String {
        var textToParse = rawPath
        if rawPath.contains("BDMV") || rawPath.hasSuffix(".m2ts") || rawPath.hasSuffix(".m2t") {
            let components = rawPath.components(separatedBy: "/")
            if let bdmvIndex = components.firstIndex(of: "BDMV"), bdmvIndex > 0 {
                var candidate = components[bdmvIndex - 1]
                if isGenericDiscOrVolumeFolder(candidate), bdmvIndex > 1 {
                    candidate = components[bdmvIndex - 2]
                    if bdmvIndex > 2 {
                        candidate = mergeSplitTitleSegments(previous: components[bdmvIndex - 3], current: candidate)
                    }
                }
                textToParse = candidate
            } else if let streamIndex = components.firstIndex(of: "STREAM"), streamIndex > 1 {
                var candidate = components[streamIndex - 2]
                if isGenericDiscOrVolumeFolder(candidate), streamIndex > 2 {
                    candidate = components[streamIndex - 3]
                    if streamIndex > 3 {
                        candidate = mergeSplitTitleSegments(previous: components[streamIndex - 4], current: candidate)
                    }
                }
                textToParse = candidate
            }
        } else {
            let nsPath = rawPath as NSString
            let fileStem = (nsPath.lastPathComponent as NSString).deletingPathExtension
            textToParse = fileStem

            let hasChineseInFileStem = fileStem.range(of: #"\p{Han}"#, options: .regularExpression) != nil
            if !hasChineseInFileStem {
                let parentPath = nsPath.deletingLastPathComponent as NSString
                let parentName = parentPath.lastPathComponent
                let hasChineseInParent = parentName.range(of: #"\p{Han}"#, options: .regularExpression) != nil
                if hasChineseInParent {
                    // 文件名是英文/编码名时，优先带上父目录中文名参与刮削。
                    textToParse = "\(parentName) \(fileStem)"
                }
            }
        }
        return textToParse
    }
    
    nonisolated static func extractSearchMetadata(from rawPath: String) -> (chineseTitle: String?, foreignTitle: String?, fullCleanTitle: String?, year: String?) {
        let originalText = cleanedTitleSource(from: rawPath)
        var textToParse = truncateBeforeReleaseMetadata(originalText)
        textToParse = textToParse.replacingOccurrences(
            of: #"[\\[\\]\\(\\)\\{\\}【】（）《》「」『』〔〕〖〗]"#,
            with: " ",
            options: .regularExpression
        )
        
        textToParse = truncateEpisodeDescriptor(from: textToParse)

        let extractedYear: String? = chooseReleaseYear(from: originalText) ?? chooseReleaseYear(from: textToParse)
        if let extractedYear {
            let removePattern = #"[ \.\-_\(\)\[\]\{\}【】（）《》「」『』〔〕〖〗]*\#(extractedYear)[ \.\-_\(\)\[\]\{\}【】（）《》「」『』〔〕〖〗]*"#
            if let cutRange = textToParse.range(of: removePattern, options: .regularExpression) {
                textToParse = String(textToParse[..<cutRange.lowerBound])
            }
        }
        
        let cleanPatterns = [
            #"(?i)\b(1080p|2160p|4k|720p|480p|blu[- ]?ray|bluray|bdrip|web[- ]?dl|webrip|remux|x264|x265|h\.?264|h\.?265|hevc|aac|dts|hdr|dv)\b"#,
            #"(?i)\b[sS]\d{1,2}[eE][pP]?\d{1,3}\b"#,
            #"(?i)\bpart\s*\d+\b"#,
            #"(?i)\b[sS]\d{1,2}\b"#,
            #"(?i)\b[eE][pP]?\d{1,3}\b"#,
            #"第\s*\d{1,3}\s*[集话]"#,
            #"(?i)\bseason\s*\d{1,2}\b"#,
            #"(?i)\b\d{1,2}bit\b"#,
            #"(?i)\b(aac|ac3|eac3|ddp|dts|truehd|flac|mp3)\d*(\.\d+)?\b"#,
            #"(?i)\b(bonus|extras?|featurette|behind[- ]?the[- ]?scenes|trailer|sample)\b"#,
            #"(?i)\b(disc|disk|cd|dvd)\b"#,
            #"(?i)\b(disc|disk|cd|dvd)\s*[-_ ]?\d{1,2}\b"#,
            #"(?i)\b(bdrom|bdmv)\b"#,
            #"(?i)\b(vol|volume)\s*[-_ ]?\d{1,2}([\-–]\d{1,2})?\b"#,
            #"\d{1,3}\s*周年\s*纪念版"#,
            #"(花絮|幕后花絮|幕后特辑|幕后|特典|附赠|预告片|样片)"#,
            #"(?i)\b(cctv\d*k?|cmctv)\b"#,
            #"(映画|剧场版|劇場版|電影版|电影版|完全版|总集篇|總集篇|特別篇|特别篇)"#,
            #"(?i)\b(special\s*features?|featurettes?)\b"#,
            #"(?i)\b\d{1,3}(st|nd|rd|th)\s+anniversary(\s+edition)?\b"#,
            #"(?i)\b(anniversary|edition)\b"#
        ]
        for pattern in cleanPatterns {
            textToParse = textToParse.replacingOccurrences(of: pattern, with: " ", options: .regularExpression)
        }
        
        textToParse = textToParse.replacingOccurrences(of: #"[._]+"#, with: " ", options: .regularExpression)
        textToParse = textToParse.replacingOccurrences(of: #"\[[^\]]*\]|\([^\)]*\)"#, with: " ", options: .regularExpression)
        textToParse = textToParse.replacingOccurrences(of: #"[【】（）《》「」『』〔〕〖〗]"#, with: " ", options: .regularExpression)
        textToParse = textToParse.replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        
        let tokens = textToParse.split(separator: " ").map(String.init)
        let originalTokens = originalText
            .replacingOccurrences(of: #"[._]+"#, with: " ", options: .regularExpression)
            .replacingOccurrences(of: #"\[[^\]]*\]|\([^\)]*\)"#, with: " ", options: .regularExpression)
            .replacingOccurrences(of: #"[【】（）《》「」『』〔〕〖〗]"#, with: " ", options: .regularExpression)
            .replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .split(separator: " ")
            .map(String.init)
        let chineseTitle = extractChineseTitle(from: tokens) ?? extractChineseTitle(from: originalTokens)
        let foreignTitle = extractForeignTitle(from: tokens)
        let fullTitle = textToParse.isEmpty ? nil : textToParse
        
        return (
            chineseTitle: chineseTitle,
            foreignTitle: foreignTitle,
            fullCleanTitle: fullTitle,
            year: extractedYear
        )
    }
    
    nonisolated static func extractParentFolderChineseTitle(from rawPath: String) -> String? {
        let parentPath = (rawPath as NSString).deletingLastPathComponent
        guard !parentPath.isEmpty, parentPath != rawPath else { return nil }
        let parentName = (parentPath as NSString).lastPathComponent.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !parentName.isEmpty else { return nil }
        guard parentName.range(of: #"\p{Han}"#, options: .regularExpression) != nil else { return nil }
        if let bracketed = extractBracketedChineseTitle(from: parentName) {
            return bracketed
        }
        let parsed = extractSearchMetadata(from: parentName)
        if let title = parsed.chineseTitle?.trimmingCharacters(in: .whitespacesAndNewlines), !title.isEmpty {
            return title
        }
        return nil
    }
    
    nonisolated static func parseEpisodeInfo(from fileName: String, fallbackIndex: Int) -> (season: Int, episode: Int, displayName: String, isTVShow: Bool, detectedSubtitle: String?) {
        var season = 1
        var episode = fallbackIndex + 1
        var displayName = fileName
        var isTVShow = false
        var detectedSubtitle: String? = nil
        
        if let match = fileName.range(of: #"[sS](\d{1,2})[eE][pP]?(\d{1,3})"#, options: .regularExpression) {
            let matched = String(fileName[match]).uppercased()
            let normalized = matched.replacingOccurrences(of: "EP", with: "E")
            let parts = normalized.components(separatedBy: "E")
            if parts.count == 2 {
                let seasonText = parts[0].replacingOccurrences(of: "S", with: "")
                let rawSeason = Int(seasonText) ?? 1
                season = (seasonText == "0") ? 1 : rawSeason
                episode = Int(parts[1]) ?? episode
            }
            detectedSubtitle = extractEpisodeDescriptorSuffix(from: fileName, after: match)
            displayName = episodeDisplayName(episode: episode, subtitle: detectedSubtitle)
            isTVShow = true
        } else if let match = fileName.range(of: #"[eE][pP]?(\d{1,3})"#, options: .regularExpression) {
            let matched = String(fileName[match]).uppercased()
            episode = Int(matched.replacingOccurrences(of: "EP", with: "").replacingOccurrences(of: "E", with: "")) ?? episode
            detectedSubtitle = extractEpisodeDescriptorSuffix(from: fileName, after: match)
            displayName = episodeDisplayName(episode: episode, subtitle: detectedSubtitle)
            isTVShow = true
        } else if let match = fileName.range(of: #"第(\d{1,3})[集话]"#, options: .regularExpression) {
            let matched = String(fileName[match])
            episode = Int(matched.replacingOccurrences(of: "第", with: "").replacingOccurrences(of: "集", with: "").replacingOccurrences(of: "话", with: "")) ?? episode
            detectedSubtitle = extractEpisodeDescriptorSuffix(from: fileName, after: match)
            displayName = episodeDisplayName(episode: episode, subtitle: detectedSubtitle)
            isTVShow = true
        }
        
        return (season, episode, displayName, isTVShow, detectedSubtitle)
    }

    nonisolated static func parsePreferredSeason(from rawPath: String) -> Int? {
        if let match = rawPath.range(of: #"[sS](\d{1,2})(?!\d)"#, options: .regularExpression) {
            let text = String(rawPath[match]).uppercased().replacingOccurrences(of: "S", with: "")
            if let value = Int(text), value > 0 { return value }
        }
        if let match = rawPath.range(of: #"(?i)season[\s._-]*(\d{1,2})"#, options: .regularExpression) {
            let text = String(rawPath[match]).replacingOccurrences(of: #"(?i)season[\s._-]*"#, with: "", options: .regularExpression)
            if let value = Int(text), value > 0 { return value }
        }
        if let match = rawPath.range(of: #"第\s*([一二三四五六七八九十零〇两\d]{1,3})\s*季"#, options: .regularExpression) {
            let text = String(rawPath[match]).replacingOccurrences(of: "第", with: "").replacingOccurrences(of: "季", with: "").trimmingCharacters(in: .whitespaces)
            if let value = Int(text), value > 0 { return value }
            if let value = chineseNumberToInt(text), value > 0 { return value }
        }
        return nil
    }

    nonisolated static func resolvePreferredSeason(from rawPath: String, fileName: String, fallbackIndex: Int = 0) -> Int? {
        let parsed = parseEpisodeInfo(from: fileName, fallbackIndex: fallbackIndex)
        if parsed.isTVShow, parsed.season > 0 {
            // 文件名中的 SxxEyy 优先级最高，避免目录里的 S01-11 干扰。
            return parsed.season
        }
        return parsePreferredSeason(from: rawPath)
    }
    
    nonisolated static func isLikelyTVEpisodePath(_ rawPath: String) -> Bool {
        let lower = rawPath.lowercased()
        if lower.range(of: #"[s]\d{1,2}[e][p]?\d{1,3}"#, options: .regularExpression) != nil { return true }
        if lower.range(of: #"\bep?\d{1,3}\b"#, options: .regularExpression) != nil { return true }
        if lower.range(of: #"\bs\d{1,2}\b"#, options: .regularExpression) != nil { return true }
        if lower.range(of: #"\bseason[\s._-]*\d{1,2}\b"#, options: .regularExpression) != nil { return true }
        if rawPath.range(of: #"第\s*\d{1,3}\s*[集话]"#, options: .regularExpression) != nil { return true }
        if rawPath.range(of: #"第\s*[一二三四五六七八九十零〇两\d]{1,3}\s*季"#, options: .regularExpression) != nil { return true }
        return false
    }

    nonisolated static func isLikelyMoviePath(_ rawPath: String) -> Bool {
        let lower = rawPath.lowercased()
        if lower.contains("/bdmv/") || lower.hasSuffix(".iso") || lower.hasSuffix(".m2ts") || lower.hasSuffix(".m2t") {
            return true
        }
        let fileName = (rawPath as NSString).lastPathComponent.lowercased()
        if fileName.range(of: #"(disc|disk|dvd|cd|vol|volume)[-_ ]?\d{0,2}"#, options: .regularExpression) != nil {
            return true
        }
        return false
    }
    
    nonisolated static func episodeSortKey(for fileName: String, fallbackIndex: Int) -> (Int, Int, String) {
        let parsed = parseEpisodeInfo(from: fileName, fallbackIndex: fallbackIndex)
        let seasonOrder = parsed.season == 0 ? Int.max : parsed.season
        return (seasonOrder, parsed.episode, fileName)
    }

    nonisolated private static func truncateEpisodeDescriptor(from input: String) -> String {
        let patterns = [
            #"(?i)(^|[\s._-])[s]\d{1,2}[e][p]?\d{1,3}.*$"#,
            #"(?i)(^|[\s._-])[e][p]?\d{1,3}.*$"#,
            #"第\s*\d{1,3}\s*[集话].*$"#
        ]
        for pattern in patterns {
            if let range = input.range(of: pattern, options: .regularExpression) {
                let prefix = String(input[..<range.lowerBound])
                    .replacingOccurrences(of: #"[._-]+"#, with: " ", options: .regularExpression)
                    .trimmingCharacters(in: .whitespacesAndNewlines)
                if !prefix.isEmpty {
                    return prefix
                }
            }
        }
        return input
    }

    nonisolated private static func extractEpisodeDescriptorSuffix(from fileName: String, after range: Range<String.Index>) -> String? {
        let stem = ((fileName as NSString).lastPathComponent as NSString).deletingPathExtension
        guard let matchRange = stem.range(of: String(fileName[range])) ?? stem.range(of: String(fileName[range]), options: [.caseInsensitive]) else {
            return nil
        }
        var suffix = String(stem[matchRange.upperBound...])
        suffix = suffix.replacingOccurrences(of: #"^[\s._\-–—:]+"#, with: "", options: .regularExpression)
        suffix = suffix.replacingOccurrences(of: #"[\s._\-–—:]+$"#, with: "", options: .regularExpression)
        suffix = suffix.replacingOccurrences(of: #"[._]+"#, with: " ", options: .regularExpression)
        suffix = suffix.replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression)
        suffix = suffix.trimmingCharacters(in: .whitespacesAndNewlines)
        suffix = trimEpisodeSuffixReleaseMetadata(suffix)
        guard !suffix.isEmpty else { return nil }
        if let partRange = suffix.range(of: #"(?i)^part\s*0*(\d+)$"#, options: .regularExpression) {
            let partText = String(suffix[partRange])
                .replacingOccurrences(of: #"(?i)part"#, with: "", options: .regularExpression)
                .trimmingCharacters(in: .whitespacesAndNewlines)
            return "Part \(Int(partText) ?? 1)"
        }
        return suffix
    }

    nonisolated private static func trimEpisodeSuffixReleaseMetadata(_ suffix: String) -> String {
        let normalized = suffix
            .replacingOccurrences(of: #"[\[\]\(\)\{\}【】（）《》「」『』〔〕〖〗]"#, with: " ", options: .regularExpression)
            .replacingOccurrences(of: #"[._\-–—:]+"#, with: " ", options: .regularExpression)
            .replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !normalized.isEmpty else { return "" }

        let tokens = normalized.split(separator: " ").map(String.init)
        var kept: [String] = []
        for token in tokens {
            if isEpisodeSuffixReleaseMetadataToken(token) {
                break
            }
            kept.append(token)
        }
        return kept.joined(separator: " ").trimmingCharacters(in: .whitespacesAndNewlines)
    }

    nonisolated private static func isEpisodeSuffixReleaseMetadataToken(_ token: String) -> Bool {
        let lower = token.lowercased()
        if lower.range(of: #"^(19\d{2}|20\d{2})$"#, options: .regularExpression) != nil { return true }
        if lower.range(of: #"^(\d{3,4}p|[48]k|uhd|fhd|hd|sd)$"#, options: .regularExpression) != nil { return true }
        if lower.range(of: #"^(8|10|12)bit$"#, options: .regularExpression) != nil { return true }
        if lower.range(of: #"^h\.?(264|265)$"#, options: .regularExpression) != nil { return true }
        if lower.range(of: #"^x(264|265)$"#, options: .regularExpression) != nil { return true }

        let metadataTokens: Set<String> = [
            "bluray", "blu", "ray", "bdrip", "web", "dl", "webdl", "webrip", "hdtv", "uhdtv",
            "remux", "avc", "hevc", "hdr", "dv", "dovi", "sdr", "hdr10", "hdr10plus",
            "aac", "ac3", "eac3", "ddp", "dts", "truehd", "atmos", "flac", "mp3", "opus",
            "nf", "netflix", "amzn", "amazon", "dsnp", "disney", "hulu", "atvp", "max",
            "cmctv", "hhweb", "hdsky", "baha", "friday", "bglobal", "b-global", "complete",
            "proper", "repack", "internal"
        ]
        return metadataTokens.contains(lower)
    }

    nonisolated private static func episodeDisplayName(episode: Int, subtitle: String?) -> String {
        let base = "第 \(episode) 集"
        guard let subtitle, !subtitle.isEmpty else { return base }
        return "\(base) · \(subtitle)"
    }
    
    nonisolated private static func extractChineseTitle(from tokens: [String]) -> String? {
        let genericChineseTokens: Set<String> = [
            "电影", "電影", "电影版", "電影版", "剧场版", "劇場版", "映画", "完全版", "总集篇", "總集篇",
            "特别篇", "特別篇", "花絮", "幕后", "幕後", "特典", "附赠", "附贈", "预告片", "預告片", "样片", "樣片"
        ]
        var groups: [[String]] = []
        var current: [String] = []
        
        for token in tokens {
            let hasChinese = token.range(of: #"\p{Han}"#, options: .regularExpression) != nil
            let normalizedToken = token.trimmingCharacters(in: .whitespacesAndNewlines)
            let isGenericChineseToken = genericChineseTokens.contains(normalizedToken)
            let isNumericSuffix = !current.isEmpty && token.range(of: #"^\d+$"#, options: .regularExpression) != nil
            if hasChinese && !isGenericChineseToken {
                current.append(token)
            } else if isNumericSuffix {
                current.append(token)
            } else if !current.isEmpty {
                groups.append(current)
                current = []
            }
        }
        if !current.isEmpty { groups.append(current) }
        
        let merged = groups
            .max(by: { $0.joined().count < $1.joined().count })?
            .joined()
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard let merged, !merged.isEmpty else { return nil }
        return merged
    }

    nonisolated private static func isGenericDiscOrVolumeFolder(_ input: String) -> Bool {
        let token = input
            .replacingOccurrences(of: #"[._\-\s]+"#, with: "", options: .regularExpression)
            .lowercased()
        return token.range(of: #"^(vol(ume)?\d{0,2}|disc\d{0,2}|disk\d{0,2}|dvd\d{0,2}|cd\d{0,2}|bdrom|bdmv)$"#, options: .regularExpression) != nil
    }
    
    nonisolated private static func extractForeignTitle(from tokens: [String]) -> String? {
        let noiseTokens: Set<String> = [
            "tx", "mweb", "adweb", "web", "dl", "bluray", "bdrip", "webrip",
            "proper", "repack", "remastered", "extended", "unrated",
            "vol", "volume", "disc", "disk", "cd", "part", "bonus", "extra", "extras",
            "featurette", "trailer", "sample", "cmctv", "bdrom", "bdmv", "special", "features", "anniversary", "edition"
        ]
        let filtered = tokens.filter { token in
            let lower = token.lowercased()
            if noiseTokens.contains(lower) { return false }
            if lower.hasPrefix("-") || lower.hasSuffix("-") { return false }
            if lower.range(of: #"^\d+$"#, options: .regularExpression) != nil { return false }
            if lower.range(of: #"^(disc|disk|cd|dvd)$"#, options: .regularExpression) != nil { return false }
            if lower.range(of: #"^(disc|disk|cd|dvd)[-_ ]?\d{1,2}$"#, options: .regularExpression) != nil { return false }
            if lower.range(of: #"^(vol|volume)[-_ ]?\d{1,2}([\-–]\d{1,2})?$"#, options: .regularExpression) != nil { return false }
            if lower.range(of: #"^[se]\d{1,3}$"#, options: .regularExpression) != nil { return false }
            if lower.range(of: #"^(x|h)?26[45]$"#, options: .regularExpression) != nil { return false }
            if lower.range(of: #"^(aac|ac3|eac3|ddp|dts|truehd|flac|mp3)\d*(\.\d+)?$"#, options: .regularExpression) != nil { return false }
            if lower.range(of: #"^\d{1,2}bit$"#, options: .regularExpression) != nil { return false }
            if lower.range(of: #"^cctv\d*k?$"#, options: .regularExpression) != nil { return false }
            return token.range(of: #"\p{Han}"#, options: .regularExpression) == nil &&
                token.range(of: #"^[\p{L}0-9'&:+-]+$"#, options: .regularExpression) != nil
        }
        let merged = filtered.joined(separator: " ").trimmingCharacters(in: .whitespacesAndNewlines)
        guard !merged.isEmpty else { return nil }
        let lowered = merged.lowercased().trimmingCharacters(in: .whitespacesAndNewlines)
        if lowered.range(of: #"^(vol|volume|disc|disk|cd|part)\s*\d*$"#, options: .regularExpression) != nil {
            return nil
        }
        return merged
    }

    nonisolated private static func truncateBeforeReleaseMetadata(_ input: String) -> String {
        let normalized = input
            .replacingOccurrences(of: #"[._]+"#, with: " ", options: .regularExpression)
            .replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        let tokens = normalized.split(separator: " ").map(String.init)
        guard !tokens.isEmpty else { return input }

        var endIndex = tokens.count
        for (idx, rawToken) in tokens.enumerated() {
            let token = rawToken.lowercased()
            if isReleaseMetadataToken(token) {
                endIndex = idx
                break
            }
        }
        guard endIndex > 0 else { return normalized }
        return tokens.prefix(endIndex).joined(separator: " ")
    }

    nonisolated private static func isReleaseMetadataToken(_ token: String) -> Bool {
        if token.range(of: #"^[se]\d{1,3}$"#, options: .regularExpression) != nil { return true }
        if token.range(of: #"^s\d{1,2}e[p]?\d{1,3}$"#, options: .regularExpression) != nil { return true }
        if token.range(of: #"^ep?\d{1,3}$"#, options: .regularExpression) != nil { return true }
        if token.range(of: #"^\d{3,4}p$"#, options: .regularExpression) != nil { return true }
        if token.range(of: #"^(4k|uhd|hdr|dv|atmos|ddp\d*(\.\d+)?|aac\d*(\.\d+)?|ac3|eac3|dts|truehd|flac|mp3)$"#, options: .regularExpression) != nil { return true }
        if token.range(of: #"^(x|h)?26[45]$"#, options: .regularExpression) != nil { return true }
        if token.range(of: #"^(web|webdl|webrip|bluray|bdrip|remux)$"#, options: .regularExpression) != nil { return true }
        if token.range(of: #"^(amzn|nf|netflix|dsnp|disney|hmax|max|atvp|appletv|hulu|cr)$"#, options: .regularExpression) != nil { return true }
        if token.range(of: #"^(flux|ntb|cakes|tgx|successfulcrab)$"#, options: .regularExpression) != nil { return true }
        if token.range(of: #"^(bonus|extra|extras|featurette|trailer|sample)$"#, options: .regularExpression) != nil { return true }
        if token.range(of: #"^(disc|disk|cd|dvd)$"#, options: .regularExpression) != nil { return true }
        if token.range(of: #"^(disc|disk|cd|dvd)[-_ ]?\d{1,2}$"#, options: .regularExpression) != nil { return true }
        if token.range(of: #"^(bdrom|bdmv)$"#, options: .regularExpression) != nil { return true }
        if token.range(of: #"^(vol|volume)[-_ ]?\d{1,2}([\-–]\d{1,2})?$"#, options: .regularExpression) != nil { return true }
        return false
    }

    nonisolated private static func extractYear(from text: String) -> String? {
        let ns = text as NSString
        let regex = try? NSRegularExpression(pattern: #"(19\d{2}|20\d{2})"#, options: [])
        let matches = regex?.matches(in: text, options: [], range: NSRange(location: 0, length: ns.length)) ?? []
        for match in matches {
            guard match.range.length == 4 else { continue }
            let yearText = ns.substring(with: match.range)
            guard let year = Int(yearText), year >= 1900, year <= 2099 else { continue }
            // 前后只要不是数字即可，允许后面直接跟 S01EP01 这类标记。
            let prevIsDigit: Bool = {
                guard match.range.location > 0 else { return false }
                let prev = ns.substring(with: NSRange(location: match.range.location - 1, length: 1))
                return prev.range(of: #"^\d$"#, options: .regularExpression) != nil
            }()
            let nextIsDigit: Bool = {
                let nextLocation = match.range.location + match.range.length
                guard nextLocation < ns.length else { return false }
                let next = ns.substring(with: NSRange(location: nextLocation, length: 1))
                return next.range(of: #"^\d$"#, options: .regularExpression) != nil
            }()
            if !prevIsDigit && !nextIsDigit {
                return yearText
            }
        }
        return nil
    }

    nonisolated private static func chooseReleaseYear(from text: String) -> String? {
        let candidates = extractYearCandidates(from: text)
        guard !candidates.isEmpty else { return nil }
        if candidates.count >= 2 {
            let first = candidates[0]
            let second = candidates[1]
            let ns = text as NSString
            let compactPrefix = ns.substring(with: NSRange(location: 0, length: min(ns.length, 12)))
                .replacingOccurrences(of: #"[.\-_/\s]+"#, with: "", options: .regularExpression)
            if compactPrefix.hasPrefix(first) {
                return second
            }
        }
        return candidates[0]
    }

    nonisolated private static func extractYearCandidates(from text: String) -> [String] {
        let ns = text as NSString
        let regex = try? NSRegularExpression(pattern: #"(19\d{2}|20\d{2})"#, options: [])
        let matches = regex?.matches(in: text, options: [], range: NSRange(location: 0, length: ns.length)) ?? []
        var years: [String] = []
        for match in matches {
            guard match.range.length == 4 else { continue }
            let yearText = ns.substring(with: match.range)
            guard let year = Int(yearText), year >= 1900, year <= 2099 else { continue }
            let prevIsDigit: Bool = {
                guard match.range.location > 0 else { return false }
                let prev = ns.substring(with: NSRange(location: match.range.location - 1, length: 1))
                return prev.range(of: #"^\d$"#, options: .regularExpression) != nil
            }()
            let nextIsDigit: Bool = {
                let nextLocation = match.range.location + match.range.length
                guard nextLocation < ns.length else { return false }
                let next = ns.substring(with: NSRange(location: nextLocation, length: 1))
                return next.range(of: #"^\d$"#, options: .regularExpression) != nil
            }()
            if !prevIsDigit && !nextIsDigit {
                years.append(yearText)
            }
        }
        return years
    }

    nonisolated private static func mergeSplitTitleSegments(previous: String, current: String) -> String {
        let prev = previous.trimmingCharacters(in: .whitespacesAndNewlines)
        let curr = current.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !prev.isEmpty else { return curr }
        guard !curr.isEmpty else { return prev }
        guard !isGenericDiscOrVolumeFolder(prev), !isReleaseMetadataToken(prev.lowercased()) else { return curr }
        let prevHasTitle = prev.range(of: #"[A-Za-z\p{Han}]"#, options: .regularExpression) != nil
        let currHasTitle = curr.range(of: #"[A-Za-z\p{Han}]"#, options: .regularExpression) != nil
        guard prevHasTitle && currHasTitle else { return curr }
        return "\(prev) \(curr)"
    }

    nonisolated private static func extractBracketedChineseTitle(from text: String) -> String? {
        let pattern = #"(?:\[|\(|【|（)\s*([\p{Han}\d]{2,})\s*(?:\]|\)|】|）)"#
        guard let regex = try? NSRegularExpression(pattern: pattern, options: []) else { return nil }
        let ns = text as NSString
        let fullRange = NSRange(location: 0, length: ns.length)
        guard let match = regex.firstMatch(in: text, options: [], range: fullRange), match.numberOfRanges > 1 else {
            return nil
        }
        let value = ns.substring(with: match.range(at: 1))
            .replacingOccurrences(of: #"\d{1,3}\s*周年\s*纪念版"#, with: "", options: .regularExpression)
            .replacingOccurrences(of: #"(花絮|幕后花絮|幕后特辑|幕后|特典|附赠|预告片|样片)"#, with: "", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return value.isEmpty ? nil : value
    }

    nonisolated private static func chineseNumberToInt(_ input: String) -> Int? {
        let text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return nil }
        let map: [Character: Int] = [
            "零": 0, "〇": 0, "一": 1, "二": 2, "两": 2, "三": 3, "四": 4,
            "五": 5, "六": 6, "七": 7, "八": 8, "九": 9
        ]
        if text == "十" { return 10 }
        if text.hasPrefix("十") {
            let ones = text.dropFirst().first.flatMap { map[$0] } ?? 0
            return 10 + ones
        }
        if let idx = text.firstIndex(of: "十") {
            let tensChar = text[text.startIndex]
            let tens = map[tensChar] ?? 0
            let ones = text[text.index(after: idx)...].first.flatMap { map[$0] } ?? 0
            return tens * 10 + ones
        }
        if text.count == 1, let first = text.first { return map[first] }
        return nil
    }
}

class MediaLibraryManager {
    private let dbQueue: DatabaseQueue

    init(dbQueue: DatabaseQueue? = nil) {
        if let dbQueue {
            self.dbQueue = dbQueue
            return
        }
        guard let sharedQueue = AppDatabase.shared.dbQueue else {
            fatalError("AppDatabase has not been initialized. Call AppDatabase.shared.setup(databaseURL:) before creating MediaLibraryManager.")
        }
        self.dbQueue = sharedQueue
    }

    fileprivate func sourceExists(_ sourceID: Int64) async throws -> Bool {
        try await dbQueue.read { db in
            let count = try Int.fetchOne(
                db,
                sql: "SELECT COUNT(*) FROM mediaSource WHERE id = ?",
                arguments: [sourceID]
            ) ?? 0
            return count > 0
        }
    }
    
    func fetchAllMovies() throws -> [Movie] {
        return try dbQueue.read { db in
            let sql = """
            SELECT DISTINCT movie.*
            FROM movie
            JOIN videoFile ON videoFile.movieId = movie.id
            JOIN mediaSource ON mediaSource.id = videoFile.sourceId
            WHERE videoFile.mediaType != 'direct'
            """
            return try Movie.fetchAll(db, sql: sql)
        }
    }
    
    func batchLockAllMovies() async throws {
        try await dbQueue.write { db in try db.execute(sql: "UPDATE movie SET isLocked = 1") }
        await MainActor.run { NotificationCenter.default.post(name: .libraryUpdated, object: nil) }
    }
    
    func updateVideoFileMatch(fileId: String, newTMDBMovieId: Int64) throws {
        try dbQueue.write { db in
            guard var file = try VideoFile.fetchOne(db, key: fileId) else { return }
            let oldFakeMovieId = file.movieId; file.mediaType = "movie"; file.movieId = newTMDBMovieId; file.episodeId = nil
            try file.update(db); if let fakeId = oldFakeMovieId, fakeId < 0 { _ = try Movie.deleteOne(db, key: fakeId) }
        }
        DispatchQueue.main.async { NotificationCenter.default.post(name: .libraryUpdated, object: nil) }
    }
    
    // 🌟 核心升级：3轮递进式自动刮削引擎
    func processUnmatchedFiles(sourceID: Int64? = nil) async throws {
        let unmatchedFiles = try await dbQueue.read { db in
            if let sourceID {
                return try VideoFile.fetchAll(
                    db,
                    sql: """
                    SELECT videoFile.*
                    FROM videoFile
                    JOIN mediaSource ON mediaSource.id = videoFile.sourceId
                    WHERE videoFile.mediaType = 'unmatched'
                      AND videoFile.sourceId = ?
                    """,
                    arguments: [sourceID]
                )
            }
            return try VideoFile.fetchAll(
                db,
                sql: """
                SELECT videoFile.*
                FROM videoFile
                JOIN mediaSource ON mediaSource.id = videoFile.sourceId
                WHERE videoFile.mediaType = 'unmatched'
                """
            )
        }
        if unmatchedFiles.isEmpty {
            print("🤖 递进刮削器：没有找到需要刮削的新文件。")
            return
        }
        
        print("🤖 递进刮削器：开始刮削 \(unmatchedFiles.count) 个新文件...")
        
        var tvAutoReuseCache: [String: TMDBResult] = [:]

        for file in unmatchedFiles {
            try Task.checkCancellation()
            guard try await sourceExists(file.sourceId) else { continue }
            if let movieId = file.movieId { let isLocked = try await dbQueue.read { db in try Movie.fetchOne(db, key: movieId)?.isLocked ?? false }; if isLocked { continue } }
            
            if let reuseKey = tvAutoReuseKey(for: file),
               let cached = tvAutoReuseCache[reuseKey],
               shouldAutoReuseTVResult(cached, for: file) {
                print("   🔁 自动复用上一集刮削结果：\(cached.displayTitle)")
                try await handleSuccessfulScrape(cached, for: file, tvCache: &tvAutoReuseCache)
                continue
            }

            let extraction = extractTitlesAndYear(from: file)
            let targetYear = extraction.year
            let preferredMediaType: String? = {
                if MediaNameParser.isLikelyTVEpisodePath(file.relativePath) { return "tv" }
                if MediaNameParser.isLikelyMoviePath(file.relativePath) { return "movie" }
                return nil
            }()
            let preferredSeason = MediaNameParser.resolvePreferredSeason(
                from: file.relativePath,
                fileName: file.fileName,
                fallbackIndex: 0
            )
            let shouldApplyYearFilter = !((preferredMediaType?.lowercased() == "tv") || preferredSeason != nil)
            let yearForSearch = shouldApplyYearFilter ? targetYear : nil
            
            print("\n🎬 正在处理文件: \(file.fileName)")
            
            // --- 尝试 1: 中文名 + 年份 (精准模式) ---
            if let cnTitle = extraction.chineseTitle, !cnTitle.isEmpty {
                print("   👉 尝试 [1/3] 精准模式 (中文+年份): [\(cnTitle)] 年份权重: [\(targetYear ?? "无")]")
                if let tmdbResult = try await TMDBService.shared.multiSearch(
                    query: cnTitle,
                    year: yearForSearch,
                    preferredMediaType: preferredMediaType,
                    preferredSeason: preferredSeason,
                    secondaryQuery: extraction.foreignTitle
                ) {
                    if !isYearPlausibleMatch(
                        result: tmdbResult,
                        targetYear: targetYear,
                        preferredMediaType: preferredMediaType,
                        preferredSeason: preferredSeason,
                        tolerance: 1
                    ) {
                        print("   ⚠️ [尝试 1] 命中但年份偏差过大，继续尝试下一轮：\(tmdbResult.displayTitle)")
                    } else {
                        print("   ✅ [尝试 1] 刮削成功: \(tmdbResult.displayTitle)")
                        try await handleSuccessfulScrape(tmdbResult, for: file, tvCache: &tvAutoReuseCache)
                        continue
                    }
                }
            }
            
            // --- 尝试 2: 父文件夹中文名 + 年份（文件名中文不可靠时回退） ---
            if let folderChinese = extraction.parentChineseTitle,
               !folderChinese.isEmpty,
               folderChinese != extraction.chineseTitle {
                print("   👉 尝试 [2/4] 回退模式 (父目录中文+年份): [\(folderChinese)] 年份权重: [\(targetYear ?? "无")]")
                if let tmdbResult = try await TMDBService.shared.multiSearch(
                    query: folderChinese,
                    year: yearForSearch,
                    preferredMediaType: preferredMediaType,
                    preferredSeason: preferredSeason,
                    secondaryQuery: extraction.foreignTitle
                ) {
                    if !isYearPlausibleMatch(
                        result: tmdbResult,
                        targetYear: targetYear,
                        preferredMediaType: preferredMediaType,
                        preferredSeason: preferredSeason,
                        tolerance: 1
                    ) {
                        print("   ⚠️ [尝试 2] 命中但年份偏差过大，继续尝试下一轮：\(tmdbResult.displayTitle)")
                    } else {
                        print("   ✅ [尝试 2] 回退刮削成功: \(tmdbResult.displayTitle)")
                        try await handleSuccessfulScrape(tmdbResult, for: file, tvCache: &tvAutoReuseCache)
                        continue
                    }
                }
            }
            
            // --- 尝试 3: 外文名 + 年份（中文名缺失或尝试失败后降级） ---
            if let foreignTitle = extraction.foreignTitle, !foreignTitle.isEmpty {
                let foreignCandidates = buildForeignQueryCandidates(from: foreignTitle)
                var matchedInForeignTry = false
                for (queryIndex, queryTitle) in foreignCandidates.enumerated() {
                    let suffix = foreignCandidates.count > 1 ? " #\(queryIndex + 1)" : ""
                    print("   👉 尝试 [3/4] 降级模式\(suffix) (外文+年份): [\(queryTitle)] 年份权重: [\(targetYear ?? "无")]")
                    if let tmdbResult = try await TMDBService.shared.multiSearch(
                        query: queryTitle,
                        year: yearForSearch,
                        preferredMediaType: preferredMediaType,
                        preferredSeason: preferredSeason,
                        secondaryQuery: extraction.chineseTitle
                    ) {
                        if !isYearPlausibleMatch(
                            result: tmdbResult,
                            targetYear: targetYear,
                            preferredMediaType: preferredMediaType,
                            preferredSeason: preferredSeason,
                            tolerance: 1
                        ) {
                            print("   ⚠️ [尝试 3] 命中但年份偏差过大，继续尝试下一轮：\(tmdbResult.displayTitle)")
                            continue
                        }
                        print("   ✅ [尝试 3] 刮削成功: \(tmdbResult.displayTitle)")
                        try await handleSuccessfulScrape(tmdbResult, for: file, tvCache: &tvAutoReuseCache)
                        matchedInForeignTry = true
                        break
                    }
                }
                if matchedInForeignTry { continue }
            }
            
            // --- 尝试 4: 完整清洗名兜底 ---
            if let fullTitle = extraction.fullCleanTitle, !fullTitle.isEmpty {
                print("   👉 尝试 [4/5] 兜底模式 (完整清洗名+年份): [\(fullTitle)] 年份权重: [\(targetYear ?? "无")]")
                if let tmdbResult = try await TMDBService.shared.multiSearch(
                    query: fullTitle,
                    year: yearForSearch,
                    preferredMediaType: preferredMediaType,
                    preferredSeason: preferredSeason,
                    secondaryQuery: extraction.foreignTitle ?? extraction.chineseTitle
                ) {
                    if !isYearPlausibleMatch(
                        result: tmdbResult,
                        targetYear: targetYear,
                        preferredMediaType: preferredMediaType,
                        preferredSeason: preferredSeason,
                        tolerance: 1
                    ) {
                        print("   ⚠️ [尝试 4] 命中但年份偏差过大，继续尝试最终降级：\(tmdbResult.displayTitle)")
                    } else {
                        print("   ✅ [尝试 4] 兜底刮削成功: \(tmdbResult.displayTitle)")
                        try await handleSuccessfulScrape(tmdbResult, for: file, tvCache: &tvAutoReuseCache)
                        continue
                    }
                }
            }
            
            // --- 尝试 5: 去空格去数字的纯名字强降级 ---
            if let fullTitle = extraction.fullCleanTitle {
                let fallbackTitle = fullTitle.replacingOccurrences(of: #"[ \d]"#, with: "", options: .regularExpression).trimmingCharacters(in: .whitespaces)
                if !fallbackTitle.isEmpty && fallbackTitle != fullTitle {
                    print("   👉 尝试 [5/5] 强降级模式 (纯名字): [\(fallbackTitle)]")
                    if let tmdbResult = try await TMDBService.shared.multiSearch(
                        query: fallbackTitle,
                        preferredMediaType: preferredMediaType,
                        preferredSeason: preferredSeason,
                        secondaryQuery: extraction.foreignTitle ?? extraction.chineseTitle
                    ) {
                        print("   ✅ [尝试 5] 降级刮削成功: \(tmdbResult.displayTitle)")
                        try await handleSuccessfulScrape(tmdbResult, for: file, tvCache: &tvAutoReuseCache)
                    } else {
                        print("   ❌ [尝试 5] 彻底未找到: [\(fullTitle)]")
                    }
                } else {
                    print("   ❌ 彻底未找到: [\(fullTitle)]")
                }
            }
            
        }

        await MainActor.run { NotificationCenter.default.post(name: .libraryUpdated, object: nil) }
    }
    
    private func saveScrapingResult(_ tmdbResult: TMDBResult, for file: VideoFile) async throws {
        try Task.checkCancellation()
        guard try await sourceExists(file.sourceId) else { return }
        let extracted = extractTitlesAndYear(from: file)
        let chineseFallback = extracted.chineseTitle ?? extracted.parentChineseTitle
        let resultId = Int64(tmdbResult.id); let oldFakeMovieId = file.movieId; let rTitle = tmdbResult.preferredTitle(chineseFallback: chineseFallback); let rDate = tmdbResult.releaseDate ?? tmdbResult.firstAirDate; let rOverview = tmdbResult.overview; let rPoster = tmdbResult.posterPath; let rVoteAverage = tmdbResult.voteAverage; let rFileId = file.id
        
        let didSave = try await dbQueue.write { db -> Bool in
            let sourceCount = try Int.fetchOne(
                db,
                sql: "SELECT COUNT(*) FROM mediaSource WHERE id = ?",
                arguments: [file.sourceId]
            ) ?? 0
            guard sourceCount > 0 else { return false }

            let movie: Movie
            if var existingMovie = try Movie.fetchOne(db, key: resultId) {
                if !existingMovie.isLocked {
                    existingMovie.title = rTitle
                    existingMovie.releaseDate = rDate
                    existingMovie.overview = rOverview
                    existingMovie.posterPath = rPoster
                    existingMovie.voteAverage = rVoteAverage
                    try existingMovie.update(db)
                }
                movie = existingMovie
            } else {
                movie = Movie(id: resultId, title: rTitle, releaseDate: rDate, overview: rOverview, posterPath: rPoster, voteAverage: rVoteAverage, isLocked: false)
                try movie.insert(db)
            }
            if var fileToUpdate = try VideoFile.fetchOne(db, key: rFileId) { fileToUpdate.mediaType = "movie"; fileToUpdate.movieId = movie.id; try fileToUpdate.update(db) }
            if let fakeId = oldFakeMovieId, fakeId < 0 { _ = try Movie.deleteOne(db, key: fakeId) }
            return true
        }
        guard didSave else { return }
        
        if let path = tmdbResult.posterPath { PosterManager.shared.downloadPoster(posterPath: path) }
        if isTVResult(tmdbResult) {
            ThumbnailManager.shared.enqueueEpisodeThumbnails(for: resultId)
        }
        await MainActor.run { NotificationCenter.default.post(name: .libraryUpdated, object: nil) }
    }
    
    private func extractTitlesAndYear(from file: VideoFile) -> (chineseTitle: String?, parentChineseTitle: String?, foreignTitle: String?, fullCleanTitle: String?, year: String?) {
        let extracted = MediaNameParser.extractSearchMetadata(from: file.relativePath)
        let parentChinese = MediaNameParser.extractParentFolderChineseTitle(from: file.relativePath)
        return (
            chineseTitle: extracted.chineseTitle,
            parentChineseTitle: parentChinese,
            foreignTitle: extracted.foreignTitle,
            fullCleanTitle: extracted.fullCleanTitle,
            year: extracted.year
        )
    }

    private func isYearPlausibleMatch(
        result: TMDBResult,
        targetYear: String?,
        preferredMediaType: String?,
        preferredSeason: Int?,
        tolerance: Int
    ) -> Bool {
        if preferredMediaType?.lowercased() == "tv" || preferredSeason != nil {
            // 剧集文件名里的年份常是资源年份，不应强约束拦截。
            return true
        }
        guard let targetYear, let target = Int(targetYear) else { return true }
        let candidateYearString = String((result.releaseDate ?? result.firstAirDate ?? "").prefix(4))
        guard let candidate = Int(candidateYearString) else { return true }
        return abs(candidate - target) <= tolerance
    }

    private func buildForeignQueryCandidates(from title: String) -> [String] {
        let trimmed = title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return [] }
        var candidates: [String] = [trimmed]
        let normalized = trimmed
            .replacingOccurrences(of: "ＡＫＡ", with: "AKA")
            .replacingOccurrences(of: #"\bA\.?K\.?A\.?\b"#, with: "AKA", options: .regularExpression)
            .replacingOccurrences(of: "aka", with: "AKA")
        if normalized.contains("AKA") {
            let parts = normalized.components(separatedBy: "AKA").map {
                $0.trimmingCharacters(in: .whitespacesAndNewlines)
            }.filter { !$0.isEmpty }
            candidates.append(contentsOf: parts)
        }
        var seen = Set<String>()
        return candidates.filter {
            let key = $0.lowercased()
            guard !seen.contains(key) else { return false }
            seen.insert(key)
            return true
        }
    }

    private func handleSuccessfulScrape(
        _ tmdbResult: TMDBResult,
        for file: VideoFile,
        tvCache: inout [String: TMDBResult]
    ) async throws {
        try Task.checkCancellation()
        guard try await sourceExists(file.sourceId) else { return }
        try await saveScrapingResult(tmdbResult, for: file)
        guard shouldAutoReuseTVResult(tmdbResult, for: file),
              let key = tvAutoReuseKey(for: file) else { return }
        tvCache[key] = tmdbResult
    }

    private func tvAutoReuseKey(for file: VideoFile) -> String? {
        guard MediaNameParser.isLikelyTVEpisodePath(file.relativePath) else { return nil }
        let parent = (file.relativePath as NSString).deletingLastPathComponent
        guard !parent.isEmpty else { return nil }
        return "\(file.sourceId)#\(parent.lowercased())"
    }

    private func shouldAutoReuseTVResult(_ result: TMDBResult, for file: VideoFile) -> Bool {
        guard MediaNameParser.isLikelyTVEpisodePath(file.relativePath) else { return false }
        if let mediaType = result.mediaType?.lowercased() {
            if mediaType == "tv" { return true }
            if mediaType == "movie" { return false }
        }
        if let firstAirDate = result.firstAirDate, !firstAirDate.isEmpty {
            return true
        }
        let release = result.releaseDate ?? ""
        return release.isEmpty
    }
    
    private func isTVResult(_ result: TMDBResult) -> Bool {
        if let mediaType = result.mediaType?.lowercased(), mediaType == "tv" { return true }
        if let firstAir = result.firstAirDate, !firstAir.isEmpty { return true }
        return false
    }
}

enum MediaSourceScanErrorCategory: String, Codable {
    case auth
    case network
    case server
    case config
    case unknown

    var displayName: String {
        switch self {
        case .auth: return "认证错误"
        case .network: return "网络错误"
        case .server: return "服务器错误"
        case .config: return "配置错误"
        case .unknown: return "未知错误"
        }
    }
}

struct MediaSourceScanDiagnostic: Codable {
    let sourceName: String
    let protocolType: String
    let endpoint: String
    let category: MediaSourceScanErrorCategory
    let statusCode: Int?
    let urlErrorCode: Int?
    let retryAttempts: Int
    let timestamp: Date
    let message: String
}

struct MediaSourceScanResult {
    let sourceId: Int64?
    let sourceName: String
    let protocolType: String
    let scannedCount: Int
    let insertedCount: Int
    let removedCount: Int
    let errorCategory: MediaSourceScanErrorCategory?
    let userMessage: String
    let diagnostic: MediaSourceScanDiagnostic?

    var isSuccess: Bool { errorCategory == nil }
}

enum MediaSourceScanDiagnosticsFormatter {
    static func diagnosticsReport(results: [MediaSourceScanResult]) -> String {
        let failures = results.filter { !$0.isSuccess && $0.diagnostic != nil }
        guard !failures.isEmpty else { return "" }

        let header = "OmniPlay 扫描诊断 (\(ISO8601DateFormatter().string(from: Date())))"
        let blocks = failures.compactMap { result -> String? in
            guard let diagnostic = result.diagnostic else { return nil }
            let status = diagnostic.statusCode.map(String.init) ?? "-"
            let urlError = diagnostic.urlErrorCode.map(String.init) ?? "-"
            return """
            [源] \(diagnostic.sourceName)
            协议=\(diagnostic.protocolType)
            分类=\(diagnostic.category.rawValue)
            端点=\(diagnostic.endpoint)
            HTTP状态=\(status)
            URLError=\(urlError)
            重试次数=\(diagnostic.retryAttempts)
            时间=\(ISO8601DateFormatter().string(from: diagnostic.timestamp))
            消息=\(diagnostic.message)
            """
        }
        return ([header] + blocks).joined(separator: "\n\n")
    }

    static func sanitizedEndpoint(from raw: String) -> String {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return trimmed }
        guard var components = URLComponents(string: trimmed) else { return trimmed }
        components.user = nil
        components.password = nil
        return components.string ?? trimmed
    }

    static func sanitizedEndpoint(from source: MediaSource) -> String {
        sanitizedEndpoint(from: source.normalizedBaseURL())
    }
}

private struct ScannedMediaFile {
    let relativePath: String
    let fileName: String
}

enum WebDAVScannerRuntimeOverrides {
    static var protocolClasses: [AnyClass]? = nil
}

enum WebDAVPreflightErrorCategory: String {
    case auth
    case network
    case server
    case config
    case unknown
}

struct WebDAVPreflightResult {
    let isReachable: Bool
    let category: WebDAVPreflightErrorCategory?
    let message: String
    let httpStatusCode: Int?
    let urlErrorCode: Int?
    let sanitizedEndpoint: String
}

enum WebDAVPreflightDiagnosticsFormatter {
    static func diagnosticsReport(result: WebDAVPreflightResult, sourceName: String) -> String {
        guard result.isReachable == false else { return "" }
        let header = "OmniPlay WebDAV 预检诊断 (\(ISO8601DateFormatter().string(from: Date())))"
        let category = result.category?.rawValue ?? "unknown"
        let status = result.httpStatusCode.map(String.init) ?? "-"
        let urlError = result.urlErrorCode.map(String.init) ?? "-"
        return """
\(header)

[源] \(sourceName)
协议=webdav
分类=\(category)
端点=\(result.sanitizedEndpoint)
HTTP状态=\(status)
URLError=\(urlError)
消息=\(result.message)
"""
    }
}

struct WebDAVPreflightChecker {
    func check(baseURL rawBaseURL: String, username: String, password: String) async -> WebDAVPreflightResult {
        let normalized = MediaSourceProtocol.webdav.normalizedBaseURL(rawBaseURL)
        guard MediaSourceProtocol.webdav.isValidBaseURL(normalized), let url = URL(string: normalized) else {
            return WebDAVPreflightResult(
                isReachable: false,
                category: .config,
                message: "WebDAV 地址无效，请输入 http(s):// 开头且包含主机名的地址。",
                httpStatusCode: nil,
                urlErrorCode: nil,
                sanitizedEndpoint: MediaSourceScanDiagnosticsFormatter.sanitizedEndpoint(from: normalized)
            )
        }
        let trimmedUser = username.trimmingCharacters(in: .whitespacesAndNewlines)
        let authHeader: String? = {
            guard !trimmedUser.isEmpty else { return nil }
            let raw = "\(trimmedUser):\(password)"
            let encoded = Data(raw.utf8).base64EncodedString()
            return "Basic \(encoded)"
        }()

        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 12
        config.timeoutIntervalForResource = 20
        config.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        config.urlCache = nil
        config.httpCookieStorage = nil
        config.httpShouldSetCookies = false
        config.connectionProxyDictionary = [:]
        if let protocolClasses = WebDAVScannerRuntimeOverrides.protocolClasses {
            config.protocolClasses = protocolClasses
        }
        let session = URLSession(configuration: config)

        func makeRequest(url: URL) -> URLRequest {
            var request = URLRequest(url: url)
            request.httpMethod = "PROPFIND"
            request.timeoutInterval = 12
            request.setValue("0", forHTTPHeaderField: "Depth")
            request.setValue("text/xml; charset=\"utf-8\"", forHTTPHeaderField: "Content-Type")
            request.httpBody = """
<?xml version="1.0" encoding="utf-8" ?>
<d:propfind xmlns:d="DAV:">
  <d:prop>
    <d:resourcetype/>
  </d:prop>
</d:propfind>
""".data(using: .utf8)
            if let authHeader {
                request.setValue(authHeader, forHTTPHeaderField: "Authorization")
            }
            return request
        }

        func resolveRedirectURL(from response: HTTPURLResponse, base: URL) -> URL? {
            guard let location = response.value(forHTTPHeaderField: "Location")?.trimmingCharacters(in: .whitespacesAndNewlines),
                  !location.isEmpty else { return nil }
            if let absolute = URL(string: location) {
                return absolute
            }
            return URL(string: location, relativeTo: base)?.absoluteURL
        }

        func runPreflight(currentURL: URL, redirectsLeft: Int) async throws -> (HTTPURLResponse, URL) {
            let request = makeRequest(url: currentURL)
            let (_, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                throw NSError(domain: "WebDAVPreflightChecker", code: -1, userInfo: [NSLocalizedDescriptionKey: "invalid_response"])
            }
            let code = http.statusCode
            if (code == 301 || code == 302 || code == 307 || code == 308),
               redirectsLeft > 0,
               let nextURL = resolveRedirectURL(from: http, base: currentURL) {
                return try await runPreflight(currentURL: nextURL, redirectsLeft: redirectsLeft - 1)
            }
            return (http, currentURL)
        }

        do {
            let (http, finalURL) = try await runPreflight(currentURL: url, redirectsLeft: 3)
            let sanitized = MediaSourceScanDiagnosticsFormatter.sanitizedEndpoint(from: finalURL.absoluteString)
            let code = http.statusCode
            if code == 401 || code == 403 {
                return WebDAVPreflightResult(
                    isReachable: false,
                    category: .auth,
                    message: "连接测试失败：认证失败（HTTP \(code)），请检查账号密码。",
                    httpStatusCode: code,
                    urlErrorCode: nil,
                    sanitizedEndpoint: sanitized
                )
            }
            if (500...599).contains(code) {
                return WebDAVPreflightResult(
                    isReachable: false,
                    category: .server,
                    message: "连接测试失败：服务端异常（HTTP \(code)）。",
                    httpStatusCode: code,
                    urlErrorCode: nil,
                    sanitizedEndpoint: sanitized
                )
            }
            if (200...299).contains(code) || code == 207 {
                return WebDAVPreflightResult(
                    isReachable: true,
                    category: nil,
                    message: "连接测试成功。",
                    httpStatusCode: code,
                    urlErrorCode: nil,
                    sanitizedEndpoint: sanitized
                )
            }
            if code == 301 || code == 302 || code == 307 || code == 308 {
                return WebDAVPreflightResult(
                    isReachable: false,
                    category: .config,
                    message: "连接测试失败：重定向次数过多，请填写最终 WebDAV 目录地址。",
                    httpStatusCode: code,
                    urlErrorCode: nil,
                    sanitizedEndpoint: sanitized
                )
            }
            return WebDAVPreflightResult(
                isReachable: false,
                category: .config,
                message: "连接测试失败：返回 HTTP \(code)，请检查地址路径与 WebDAV 服务状态。",
                httpStatusCode: code,
                urlErrorCode: nil,
                sanitizedEndpoint: sanitized
            )
        } catch let error as URLError {
            let nsError = error as NSError
            let userInfoText = nsError.userInfo.description.lowercased()
            let isLocalNetworkDenied = error.code == .notConnectedToInternet && userInfoText.contains("local network prohibited")
            let message: String
            if isLocalNetworkDenied {
                message = "连接测试失败：系统未授权本地网络访问。请到“系统设置 -> 隐私与安全性 -> 本地网络”中允许觅影，然后重试。"
            } else {
                message = "连接测试失败：网络错误（\(error.code.rawValue)）\(error.localizedDescription)"
            }
            return WebDAVPreflightResult(
                isReachable: false,
                category: .network,
                message: message,
                httpStatusCode: nil,
                urlErrorCode: error.code.rawValue,
                sanitizedEndpoint: MediaSourceScanDiagnosticsFormatter.sanitizedEndpoint(from: normalized)
            )
        } catch {
            let nsError = error as NSError
            if nsError.domain == "WebDAVPreflightChecker", nsError.code == -1 {
                return WebDAVPreflightResult(
                    isReachable: false,
                    category: .unknown,
                    message: "连接测试失败：未收到有效响应。",
                    httpStatusCode: nil,
                    urlErrorCode: nil,
                    sanitizedEndpoint: MediaSourceScanDiagnosticsFormatter.sanitizedEndpoint(from: normalized)
                )
            }
            return WebDAVPreflightResult(
                isReachable: false,
                category: .unknown,
                message: "连接测试失败：\(error.localizedDescription)",
                httpStatusCode: nil,
                urlErrorCode: nil,
                sanitizedEndpoint: MediaSourceScanDiagnosticsFormatter.sanitizedEndpoint(from: normalized)
            )
        }
    }
}

struct WebDAVDirectoryItem: Identifiable, Hashable {
    let id: String
    let url: URL
    let name: String

    var displayURL: String {
        let normalized = MediaSourceProtocol.webdav.normalizedBaseURL(url.absoluteString)
        return normalized.removingPercentEncoding ?? normalized
    }
}

struct WebDAVDirectoryBrowser {
    private struct DAVEntry {
        var href: String = ""
        var displayName: String = ""
        var isDirectory = false
    }

    private final class PROPFINDParser: NSObject, XMLParserDelegate {
        private(set) var entries: [DAVEntry] = []
        private var currentEntry: DAVEntry?
        private var currentElement: String = ""
        private var textBuffer: String = ""

        private func localName(_ raw: String) -> String {
            if let idx = raw.lastIndex(of: ":") {
                return String(raw[raw.index(after: idx)...]).lowercased()
            }
            return raw.lowercased()
        }

        func parse(data: Data) throws -> [DAVEntry] {
            let parser = XMLParser(data: data)
            parser.delegate = self
            parser.shouldProcessNamespaces = true
            parser.shouldReportNamespacePrefixes = true
            parser.shouldResolveExternalEntities = false
            guard parser.parse() else {
                let reason = parser.parserError?.localizedDescription ?? "未知 XML 解析错误"
                throw NSError(domain: "WebDAVDirectoryBrowser", code: -1, userInfo: [NSLocalizedDescriptionKey: "WebDAV 文件夹列表解析失败：\(reason)"])
            }
            return entries
        }

        func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName qName: String?, attributes attributeDict: [String : String] = [:]) {
            let name = localName(qName ?? elementName)
            currentElement = name
            textBuffer = ""
            if name == "response" {
                currentEntry = DAVEntry()
            } else if name == "collection" {
                currentEntry?.isDirectory = true
            }
        }

        func parser(_ parser: XMLParser, foundCharacters string: String) {
            textBuffer += string
        }

        func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName qName: String?) {
            let name = localName(qName ?? elementName)
            let text = textBuffer.trimmingCharacters(in: .whitespacesAndNewlines)

            if name == "href", !text.isEmpty {
                currentEntry?.href = text
            } else if name == "displayname", !text.isEmpty {
                currentEntry?.displayName = text
            } else if name == "response" {
                if let entry = currentEntry, !entry.href.isEmpty {
                    entries.append(entry)
                }
                currentEntry = nil
            }

            currentElement = ""
            textBuffer = ""
        }
    }

    func listSharedFolders(baseURL rawBaseURL: String, username: String, password: String) async throws -> [WebDAVDirectoryItem] {
        let normalized = MediaSourceProtocol.webdav.normalizedBaseURL(rawBaseURL)
        guard MediaSourceProtocol.webdav.isValidBaseURL(normalized),
              let baseURL = URL(string: normalized) else {
            throw NSError(domain: "WebDAVDirectoryBrowser", code: 1001, userInfo: [NSLocalizedDescriptionKey: "WebDAV 地址无效，请输入 http(s):// 开头且包含主机名的地址。"])
        }

        var cleanComponents = URLComponents(url: baseURL, resolvingAgainstBaseURL: false)
        cleanComponents?.user = nil
        cleanComponents?.password = nil
        if cleanComponents?.path.isEmpty == true {
            cleanComponents?.path = "/"
        }
        guard let requestURL = cleanComponents?.url else {
            throw NSError(domain: "WebDAVDirectoryBrowser", code: 1002, userInfo: [NSLocalizedDescriptionKey: "WebDAV 地址无效。"])
        }

        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 12
        config.timeoutIntervalForResource = 25
        config.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        config.urlCache = nil
        config.httpCookieStorage = nil
        config.httpShouldSetCookies = false
        config.connectionProxyDictionary = [:]
        if let protocolClasses = WebDAVScannerRuntimeOverrides.protocolClasses {
            config.protocolClasses = protocolClasses
        }
        let session = URLSession(configuration: config)

        var request = URLRequest(url: requestURL)
        request.httpMethod = "PROPFIND"
        request.timeoutInterval = 12
        request.setValue("1", forHTTPHeaderField: "Depth")
        request.setValue("text/xml; charset=\"utf-8\"", forHTTPHeaderField: "Content-Type")
        request.httpBody = """
<?xml version="1.0" encoding="utf-8" ?>
<d:propfind xmlns:d="DAV:">
  <d:prop>
    <d:displayname/>
    <d:resourcetype/>
  </d:prop>
</d:propfind>
""".data(using: .utf8)

        let trimmedUser = username.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmedUser.isEmpty {
            let raw = "\(trimmedUser):\(password)"
            let encoded = Data(raw.utf8).base64EncodedString()
            request.setValue("Basic \(encoded)", forHTTPHeaderField: "Authorization")
        }

        do {
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                throw NSError(domain: "WebDAVDirectoryBrowser", code: 1003, userInfo: [NSLocalizedDescriptionKey: "WebDAV 未返回有效响应。"])
            }

            if http.statusCode == 401 || http.statusCode == 403 {
                throw NSError(domain: "WebDAVDirectoryBrowser", code: http.statusCode, userInfo: [NSLocalizedDescriptionKey: "WebDAV 认证失败，请检查用户名和密码。"])
            }
            guard (200...299).contains(http.statusCode) || http.statusCode == 207 else {
                throw NSError(domain: "WebDAVDirectoryBrowser", code: http.statusCode, userInfo: [NSLocalizedDescriptionKey: "WebDAV 文件夹列表请求失败：HTTP \(http.statusCode)。"])
            }

            let entries = try PROPFINDParser().parse(data: data)
            let currentPath = normalizePath(requestURL.path)
            var folders: [WebDAVDirectoryItem] = []
            var seen: Set<String> = []

            for entry in entries where entry.isDirectory {
                guard let resolved = resolveHref(entry.href, baseURL: requestURL) else { continue }
                let cleanURL = sanitizedURL(resolved)
                let path = normalizePath(cleanURL.path)
                if path == currentPath { continue }
                let key = MediaSourceProtocol.webdav.normalizedBaseURL(cleanURL.absoluteString)
                guard !key.isEmpty, !seen.contains(key) else { continue }
                seen.insert(key)

                let name = folderName(for: cleanURL, displayName: entry.displayName)
                folders.append(WebDAVDirectoryItem(id: key, url: cleanURL, name: name))
            }

            return folders.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
        } catch let error as URLError {
            throw NSError(domain: "WebDAVDirectoryBrowser", code: error.code.rawValue, userInfo: [NSLocalizedDescriptionKey: "WebDAV 文件夹列表请求失败：\(error.localizedDescription)"])
        }
    }

    private func resolveHref(_ href: String, baseURL: URL) -> URL? {
        if let absolute = URL(string: href), absolute.scheme != nil {
            return absolute
        }
        if let relative = URL(string: href, relativeTo: baseURL)?.absoluteURL {
            return relative
        }
        if let decoded = href.removingPercentEncoding,
           let relative = URL(string: decoded, relativeTo: baseURL)?.absoluteURL {
            return relative
        }
        return nil
    }

    private func sanitizedURL(_ url: URL) -> URL {
        var components = URLComponents(url: url, resolvingAgainstBaseURL: false)
        components?.user = nil
        components?.password = nil
        return components?.url ?? url
    }

    private func folderName(for url: URL, displayName: String) -> String {
        let decodedDisplayName = displayName.removingPercentEncoding ?? displayName
        if !decodedDisplayName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return decodedDisplayName
        }

        let decodedPath = url.path.removingPercentEncoding ?? url.path
        if let last = decodedPath.split(separator: "/").last {
            return String(last)
        }
        return url.host ?? "WebDAV 文件夹"
    }

    private func normalizePath(_ value: String) -> String {
        if value.isEmpty { return "/" }
        var path = value
        while path.count > 1 && path.hasSuffix("/") {
            path.removeLast()
        }
        return path
    }
}

private protocol MediaSourceScanner {
    func scanFiles(in source: MediaSource) async throws -> [ScannedMediaFile]
    var sourceDescription: String { get }
}

private enum MediaSourceScanError: LocalizedError {
    case unsupportedProtocol(String)
    case invalidBaseURL(String)
    case accessDenied(String)
    case requestFailed(String)

    var errorDescription: String? {
        switch self {
        case .unsupportedProtocol(let value):
            return "不支持的媒体源协议：\(value)"
        case .invalidBaseURL(let value):
            return "媒体源地址无效：\(value)"
        case .accessDenied(let value):
            return "系统拒绝访问该路径：\(value)"
        case .requestFailed(let value):
            return value
        }
    }
}

private struct LocalFilesystemScanner: MediaSourceScanner {
    let sourceDescription: String

    init(source: MediaSource) {
        sourceDescription = source.baseUrl
    }

    func scanFiles(in source: MediaSource) async throws -> [ScannedMediaFile] {
        let normalized = MediaSourceProtocol.local.normalizedBaseURL(source.baseUrl)
        let rootURL = URL(fileURLWithPath: normalized)
        let rootPathPrefix = rootURL.path.hasSuffix("/") ? rootURL.path : rootURL.path + "/"

        guard let enumerator = FileManager.default.enumerator(
            at: rootURL,
            includingPropertiesForKeys: [.isDirectoryKey, .fileSizeKey],
            options: [.skipsHiddenFiles]
        ) else {
            throw MediaSourceScanError.accessDenied(rootURL.path)
        }

        let exts = ["mp4", "mkv", "mov", "avi", "rmvb", "flv", "webm", "m2ts", "ts", "iso", "m4v", "wmv"]
        var bdmvGroups: [String: [(url: URL, size: Int)]] = [:]
        var normalFiles: [URL] = []

        while let fileURL = enumerator.nextObject() as? URL {
            if Task.isCancelled { return [] }
            if (try? fileURL.resourceValues(forKeys: [.isDirectoryKey]))?.isDirectory == true { continue }

            if exts.contains(fileURL.pathExtension.lowercased()) {
                if fileURL.path.contains("BDMV/STREAM") {
                    let size = (try? fileURL.resourceValues(forKeys: [.fileSizeKey]))?.fileSize ?? 0
                    let comps = fileURL.pathComponents
                    if let idx = comps.firstIndex(of: "BDMV") {
                        let parent = comps[0..<idx].joined(separator: "/")
                        bdmvGroups[parent, default: []].append((url: fileURL, size: size))
                    }
                } else {
                    normalFiles.append(fileURL)
                }
            }
        }

        var resolved: [URL] = normalFiles
        for (_, files) in bdmvGroups {
            let maxSize = files.map { $0.size }.max() ?? 0
            if maxSize > 0 {
                let threshold = Double(maxSize) * 0.3
                resolved.append(contentsOf: files.filter { Double($0.size) >= threshold }.map { $0.url })
            }
        }

        return resolved.map { url in
            let relativePath = url.path.replacingOccurrences(of: rootPathPrefix, with: "")
            return ScannedMediaFile(relativePath: relativePath, fileName: url.lastPathComponent)
        }
    }
}

private struct WebDAVScanner: MediaSourceScanner {
    let sourceDescription: String
    private let session: URLSession

    init(source: MediaSource) {
        sourceDescription = source.baseUrl
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 20
        configuration.timeoutIntervalForResource = 40
        configuration.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        configuration.urlCache = nil
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        // 禁用系统代理/PAC 解析，避免局域网 WebDAV 扫描被代理噪声干扰。
        configuration.connectionProxyDictionary = [:]
        if let protocolClasses = WebDAVScannerRuntimeOverrides.protocolClasses {
            configuration.protocolClasses = protocolClasses
        }
        session = URLSession(configuration: configuration)
    }

    private struct AuthCredential {
        let username: String
        let password: String
    }

    private struct DAVItem {
        let url: URL
        let isDirectory: Bool
        let contentLength: Int64
    }

    private final class PROPFINDParser: NSObject, XMLParserDelegate {
        struct Entry {
            var href: String = ""
            var isDirectory = false
            var contentLength: Int64 = 0
        }

        private(set) var entries: [Entry] = []
        private var currentEntry: Entry?
        private var currentElement: String = ""
        private var textBuffer: String = ""

        private func localName(_ raw: String) -> String {
            if let idx = raw.lastIndex(of: ":") {
                return String(raw[raw.index(after: idx)...]).lowercased()
            }
            return raw.lowercased()
        }

        func parse(data: Data) throws -> [Entry] {
            let parser = XMLParser(data: data)
            parser.delegate = self
            parser.shouldProcessNamespaces = true
            parser.shouldReportNamespacePrefixes = true
            parser.shouldResolveExternalEntities = false
            guard parser.parse() else {
                let reason = parser.parserError?.localizedDescription ?? "未知 XML 解析错误"
                throw MediaSourceScanError.requestFailed("WebDAV 响应解析失败：\(reason)")
            }
            return entries
        }

        func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName qName: String?, attributes attributeDict: [String : String] = [:]) {
            let name = localName(qName ?? elementName)
            currentElement = name
            textBuffer = ""

            if name == "response" {
                currentEntry = Entry()
            } else if name == "collection" {
                currentEntry?.isDirectory = true
            }
        }

        func parser(_ parser: XMLParser, foundCharacters string: String) {
            textBuffer += string
        }

        func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName qName: String?) {
            let name = localName(qName ?? elementName)
            let text = textBuffer.trimmingCharacters(in: .whitespacesAndNewlines)

            if name == "href", !text.isEmpty {
                currentEntry?.href = text
            } else if name == "getcontentlength", let size = Int64(text) {
                currentEntry?.contentLength = size
            } else if name == "response" {
                if let entry = currentEntry, !entry.href.isEmpty {
                    entries.append(entry)
                }
                currentEntry = nil
            }

            currentElement = ""
            textBuffer = ""
        }
    }

    func scanFiles(in source: MediaSource) async throws -> [ScannedMediaFile] {
        let normalized = MediaSourceProtocol.webdav.normalizedBaseURL(source.baseUrl)
        guard let rawBaseURL = URL(string: normalized) else {
            throw MediaSourceScanError.invalidBaseURL(source.baseUrl)
        }

        var baseComponents = URLComponents(url: rawBaseURL, resolvingAgainstBaseURL: false)
        baseComponents?.user = nil
        baseComponents?.password = nil
        guard let baseURL = baseComponents?.url else {
            throw MediaSourceScanError.invalidBaseURL(source.baseUrl)
        }

        let credential = resolveCredential(from: source.authConfig, fallbackURL: rawBaseURL)
        let extSet: Set<String> = ["mp4", "mkv", "mov", "avi", "rmvb", "flv", "webm", "m2ts", "ts", "iso", "m4v", "wmv"]

        var pendingDirectories: [URL] = [baseURL]
        var visitedDirectories: Set<String> = []
        var normalFiles: [ScannedMediaFile] = []
        var bdmvGroups: [String: [(file: ScannedMediaFile, size: Int64)]] = [:]

        while let directoryURL = pendingDirectories.first {
            if Task.isCancelled { return [] }
            pendingDirectories.removeFirst()

            let normalizedDirKey = normalizePath(directoryURL.path)
            if visitedDirectories.contains(normalizedDirKey) { continue }
            visitedDirectories.insert(normalizedDirKey)

            let entries = try await listDirectory(at: directoryURL, credential: credential, baseURL: baseURL)
            for entry in entries {
                if Task.isCancelled { return [] }
                guard let relativePath = relativePath(for: entry.url, baseURL: baseURL), !relativePath.isEmpty else { continue }
                if containsHiddenPathComponent(relativePath) { continue }

                if entry.isDirectory {
                    pendingDirectories.append(entry.url)
                    continue
                }

                let fileName = (relativePath as NSString).lastPathComponent
                guard !fileName.isEmpty else { continue }
                let ext = (fileName as NSString).pathExtension.lowercased()
                guard extSet.contains(ext) else { continue }

                let file = ScannedMediaFile(relativePath: relativePath, fileName: fileName)
                if relativePath.contains("BDMV/STREAM") {
                    let components = relativePath.components(separatedBy: "/")
                    if let bdmvIndex = components.firstIndex(of: "BDMV"), bdmvIndex > 0 {
                        let parent = components[0..<bdmvIndex].joined(separator: "/")
                        bdmvGroups[parent, default: []].append((file: file, size: max(0, entry.contentLength)))
                    } else {
                        normalFiles.append(file)
                    }
                } else {
                    normalFiles.append(file)
                }
            }
        }

        var resolved = normalFiles
        for (_, files) in bdmvGroups {
            let maxSize = files.map(\.size).max() ?? 0
            if maxSize > 0 {
                let threshold = Double(maxSize) * 0.3
                resolved.append(contentsOf: files.filter { Double($0.size) >= threshold }.map(\.file))
            } else {
                resolved.append(contentsOf: files.map(\.file))
            }
        }

        return resolved
    }

    private func resolveCredential(from authConfig: String?, fallbackURL: URL) -> AuthCredential? {
        if let id = WebDAVCredentialStore.shared.credentialID(from: authConfig),
           let stored = WebDAVCredentialStore.shared.loadCredential(id: id) {
            return AuthCredential(username: stored.username, password: stored.password)
        }

        if let legacy = WebDAVCredentialStore.shared.decodeLegacyCredential(from: authConfig) {
            return AuthCredential(username: legacy.username, password: legacy.password)
        }

        if let user = fallbackURL.user, !user.isEmpty {
            return AuthCredential(username: user, password: fallbackURL.password ?? "")
        }
        return nil
    }

    private func listDirectory(at directoryURL: URL, credential: AuthCredential?, baseURL: URL) async throws -> [DAVItem] {
        var request = URLRequest(url: directoryURL)
        request.httpMethod = "PROPFIND"
        request.setValue("1", forHTTPHeaderField: "Depth")
        request.setValue("text/xml; charset=\"utf-8\"", forHTTPHeaderField: "Content-Type")
        request.timeoutInterval = 20
        request.httpBody = """
<?xml version="1.0" encoding="utf-8" ?>
<d:propfind xmlns:d="DAV:">
  <d:prop>
    <d:resourcetype/>
    <d:getcontentlength/>
  </d:prop>
</d:propfind>
""".data(using: .utf8)

        if let credential {
            let raw = "\(credential.username):\(credential.password)"
            let encoded = Data(raw.utf8).base64EncodedString()
            request.setValue("Basic \(encoded)", forHTTPHeaderField: "Authorization")
        }

        let maxAttempts = 3
        var lastError: Error?
        for attempt in 1...maxAttempts {
            let start = Date()
            do {
                log("PROPFIND start attempt=\(attempt)/\(maxAttempts) url=\(directoryURL.absoluteString)")
                let (data, response) = try await session.data(for: request)
                let elapsedMS = Int(Date().timeIntervalSince(start) * 1000)
                guard let http = response as? HTTPURLResponse else {
                    throw MediaSourceScanError.requestFailed("WebDAV 请求失败：未收到有效响应。")
                }

                if http.statusCode == 401 || http.statusCode == 403 {
                    throw MediaSourceScanError.requestFailed("WebDAV 认证失败：HTTP \(http.statusCode) (\(directoryURL.absoluteString))")
                }
                if (500...599).contains(http.statusCode), attempt < maxAttempts {
                    log("PROPFIND retry status=\(http.statusCode) elapsed=\(elapsedMS)ms url=\(directoryURL.absoluteString)")
                    try? await Task.sleep(nanoseconds: UInt64(attempt) * 300_000_000)
                    continue
                }
                guard (200...299).contains(http.statusCode) || http.statusCode == 207 else {
                    throw MediaSourceScanError.requestFailed("WebDAV 请求失败：HTTP \(http.statusCode) (\(directoryURL.absoluteString))")
                }

                let parser = PROPFINDParser()
                let entries = try parser.parse(data: data)
                let currentPath = normalizePath(directoryURL.path)

                var items: [DAVItem] = []
                for entry in entries {
                    guard let resolved = resolveHref(entry.href, baseURL: baseURL) else { continue }
                    let path = normalizePath(resolved.path)
                    if path == currentPath { continue }
                    items.append(DAVItem(url: resolved, isDirectory: entry.isDirectory, contentLength: entry.contentLength))
                }
                log("PROPFIND success status=\(http.statusCode) elapsed=\(elapsedMS)ms entries=\(entries.count) items=\(items.count) url=\(directoryURL.absoluteString)")
                return items
            } catch let error as URLError {
                lastError = error
                let retryableCodes: Set<URLError.Code> = [
                    .timedOut, .networkConnectionLost, .cannotConnectToHost, .cannotFindHost, .dnsLookupFailed, .notConnectedToInternet
                ]
                let isRetryable = retryableCodes.contains(error.code)
                log("PROPFIND networkError code=\(error.code.rawValue) retryable=\(isRetryable) attempt=\(attempt) url=\(directoryURL.absoluteString)")
                if isRetryable, attempt < maxAttempts {
                    try? await Task.sleep(nanoseconds: UInt64(attempt) * 300_000_000)
                    continue
                }
                throw MediaSourceScanError.requestFailed("WebDAV 网络错误(\(error.code.rawValue))：\(error.localizedDescription) (\(directoryURL.absoluteString))")
            } catch let scanError as MediaSourceScanError {
                lastError = scanError
                if attempt < maxAttempts,
                   case .requestFailed(let message) = scanError,
                   message.contains("HTTP 5") {
                    try? await Task.sleep(nanoseconds: UInt64(attempt) * 300_000_000)
                    continue
                }
                throw scanError
            } catch {
                lastError = error
                log("PROPFIND unexpectedError attempt=\(attempt) url=\(directoryURL.absoluteString) error=\(error.localizedDescription)")
                if attempt < maxAttempts {
                    try? await Task.sleep(nanoseconds: UInt64(attempt) * 300_000_000)
                    continue
                }
                throw MediaSourceScanError.requestFailed("WebDAV 请求异常：\(error.localizedDescription) (\(directoryURL.absoluteString))")
            }
        }

        throw MediaSourceScanError.requestFailed("WebDAV 请求失败：重试后仍未成功 (\(directoryURL.absoluteString))。最后错误：\(lastError?.localizedDescription ?? "未知错误")")
    }

    private func log(_ message: String) {
        print("[WebDAVScanner] \(message)")
    }

    private func resolveHref(_ href: String, baseURL: URL) -> URL? {
        if let absolute = URL(string: href), absolute.scheme != nil {
            return absolute
        }
        if let relative = URL(string: href, relativeTo: baseURL)?.absoluteURL {
            return relative
        }
        if let decoded = href.removingPercentEncoding,
           let relative = URL(string: decoded, relativeTo: baseURL)?.absoluteURL {
            return relative
        }
        return nil
    }

    private func normalizePath(_ value: String) -> String {
        if value.isEmpty { return "/" }
        var path = value
        while path.count > 1 && path.hasSuffix("/") {
            path.removeLast()
        }
        return path
    }

    private func relativePath(for url: URL, baseURL: URL) -> String? {
        let fullPath = normalizePath(url.path)
        let basePath = normalizePath(baseURL.path)

        if fullPath == basePath { return nil }
        guard fullPath.hasPrefix(basePath) else { return nil }

        var relative = String(fullPath.dropFirst(basePath.count))
        if relative.hasPrefix("/") { relative.removeFirst() }
        return relative.removingPercentEncoding ?? relative
    }

    private func containsHiddenPathComponent(_ relativePath: String) -> Bool {
        let parts = relativePath.split(separator: "/")
        return parts.contains { $0.hasPrefix(".") }
    }
}

private enum MediaSourceScannerFactory {
    static func make(source: MediaSource) throws -> any MediaSourceScanner {
        guard let protocolKind = source.protocolKind else {
            throw MediaSourceScanError.unsupportedProtocol(source.protocolType)
        }
        switch protocolKind {
        case .local:
            return LocalFilesystemScanner(source: source)
        case .webdav:
            guard MediaSourceProtocol.webdav.isValidBaseURL(source.baseUrl) else {
                throw MediaSourceScanError.invalidBaseURL(source.baseUrl)
            }
            return WebDAVScanner(source: source)
        case .direct:
            throw MediaSourceScanError.unsupportedProtocol(MediaSourceProtocol.direct.rawValue)
        }
    }
}

extension MediaLibraryManager {
    // 🌟 这里是完完整整的本地文件雷达扫描入库逻辑！保证它存在！
    func scanLocalSource(_ source: MediaSource) async {
        _ = await scanLocalSourceWithResult(source)
    }

    func scanLocalSourceWithResult(_ source: MediaSource) async -> MediaSourceScanResult {
        guard let sourceId = source.id else {
            let message = "媒体源缺少ID，无法扫描。"
            return MediaSourceScanResult(
                sourceId: nil,
                sourceName: source.name,
                protocolType: source.protocolType,
                scannedCount: 0,
                insertedCount: 0,
                removedCount: 0,
                errorCategory: .config,
                userMessage: message,
                diagnostic: buildDiagnostic(
                    source: source,
                    category: .config,
                    message: message,
                    error: nil,
                    retryAttempts: 0
                )
            )
        }
        var scanningSource = source
        if source.protocolKind == .webdav {
            scanningSource = await migrateWebDAVCredentialIfNeeded(source)
        }

        print("\n==================================")
        print("📂 雷达启动：开始扫描文件夹 -> \(scanningSource.baseUrl)")

        let scannedFiles: [ScannedMediaFile]
        do {
            let scanner = try MediaSourceScannerFactory.make(source: scanningSource)
            scannedFiles = try await scanner.scanFiles(in: scanningSource)
        } catch {
            print("🚨【致命错误】扫描失败：\(error.localizedDescription)")
            let category = classifyScanError(error, source: scanningSource)
            let message = userFacingMessage(for: category, sourceName: scanningSource.name, fallback: error.localizedDescription)
            return MediaSourceScanResult(
                sourceId: sourceId,
                sourceName: scanningSource.name,
                protocolType: scanningSource.protocolType,
                scannedCount: 0,
                insertedCount: 0,
                removedCount: 0,
                errorCategory: category,
                userMessage: message,
                diagnostic: buildDiagnostic(
                    source: scanningSource,
                    category: category,
                    message: error.localizedDescription,
                    error: error,
                    retryAttempts: defaultRetryAttempts(for: category, source: scanningSource)
                )
            )
        }

        let sourceStillExists = (try? await sourceExists(sourceId)) == true
        if Task.isCancelled || !sourceStillExists {
            return MediaSourceScanResult(
                sourceId: sourceId,
                sourceName: scanningSource.name,
                protocolType: scanningSource.protocolType,
                scannedCount: 0,
                insertedCount: 0,
                removedCount: 0,
                errorCategory: nil,
                userMessage: "扫描已停止：媒体源已移除或同步任务已取消。",
                diagnostic: nil
            )
        }

        print("✅ 雷达扫描完毕：共找到 \(scannedFiles.count) 个可用视频文件。准备入库...")
        print("==================================\n")

        let scannedPathSet = Set(scannedFiles.map(\.relativePath))
        var insertedCount = 0
        var removedCount = 0

        do {
            let counts = try await dbQueue.write { db -> (inserted: Int, removed: Int) in
                let sourceCount = try Int.fetchOne(
                    db,
                    sql: "SELECT COUNT(*) FROM mediaSource WHERE id = ?",
                    arguments: [sourceId]
                ) ?? 0
                guard sourceCount > 0 else { return (inserted: 0, removed: 0) }

                let existingFiles = try VideoFile
                    .filter(Column("sourceId") == sourceId)
                    .fetchAll(db)

                var localRemovedCount = 0
                for file in existingFiles where !scannedPathSet.contains(file.relativePath) {
                    _ = try VideoFile.deleteOne(db, key: file.id)
                    localRemovedCount += 1
                }
                if localRemovedCount > 0 {
                    print("🧹 已移除 \(localRemovedCount) 个源内不存在的旧文件记录。")
                }

                let existingPathSet = Set(existingFiles.map(\.relativePath))
                var localInsertedCount = 0
                for item in scannedFiles where !existingPathSet.contains(item.relativePath) {
                    let relativePath = item.relativePath

                    var fakeMovieId = Int64(relativePath.hashValue)
                    fakeMovieId = fakeMovieId > 0 ? -fakeMovieId : fakeMovieId
                    if fakeMovieId == 0 { fakeMovieId = -Int64.random(in: 1...999999) }

                    var displayTitle = (item.fileName as NSString).deletingPathExtension
                    let comps = relativePath.components(separatedBy: "/")
                    if let bdmvIndex = comps.firstIndex(of: "BDMV"), bdmvIndex > 0 {
                        displayTitle = comps[bdmvIndex - 1]
                    } else if (displayTitle.count <= 2 && displayTitle.allSatisfy { $0.isNumber }) || displayTitle == "00000" {
                        if comps.count >= 2 { displayTitle = comps[comps.count - 2] }
                    }

                    let dummyMovie = Movie(id: fakeMovieId, title: displayTitle, releaseDate: nil, overview: "正在排队等待刮削...", posterPath: nil, voteAverage: nil, isLocked: false)
                    try? dummyMovie.insert(db)

                    let newVideo = VideoFile(id: UUID().uuidString, sourceId: sourceId, relativePath: relativePath, fileName: item.fileName, mediaType: "unmatched", movieId: fakeMovieId, episodeId: nil, playProgress: 0.0, duration: 0.0)
                    try newVideo.insert(db)
                    localInsertedCount += 1
                }

                // 删除失去任何视频关联的影视卡片，避免首页残留“已不存在文件”的旧条目
                try db.execute(sql: "DELETE FROM movie WHERE id NOT IN (SELECT DISTINCT movieId FROM videoFile WHERE movieId IS NOT NULL)")
                return (inserted: localInsertedCount, removed: localRemovedCount)
            }
            insertedCount = counts.inserted
            removedCount = counts.removed
        } catch {
            print("❌ 扫描入库失败：\(error)")
            let category: MediaSourceScanErrorCategory = .unknown
            let message = userFacingMessage(for: category, sourceName: scanningSource.name, fallback: error.localizedDescription)
            return MediaSourceScanResult(
                sourceId: sourceId,
                sourceName: scanningSource.name,
                protocolType: scanningSource.protocolType,
                scannedCount: scannedFiles.count,
                insertedCount: insertedCount,
                removedCount: removedCount,
                errorCategory: category,
                userMessage: message,
                diagnostic: buildDiagnostic(
                    source: scanningSource,
                    category: category,
                    message: error.localizedDescription,
                    error: error,
                    retryAttempts: 0
                )
            )
        }

        DispatchQueue.main.async { NotificationCenter.default.post(name: .libraryUpdated, object: nil) }
        return MediaSourceScanResult(
            sourceId: sourceId,
            sourceName: scanningSource.name,
            protocolType: scanningSource.protocolType,
            scannedCount: scannedFiles.count,
            insertedCount: insertedCount,
            removedCount: removedCount,
            errorCategory: nil,
            userMessage: "扫描完成：新增 \(insertedCount)，移除 \(removedCount)，共发现 \(scannedFiles.count) 个文件。",
            diagnostic: nil
        )
    }

    private func migrateWebDAVCredentialIfNeeded(_ source: MediaSource) async -> MediaSource {
        guard source.protocolKind == .webdav else { return source }
        guard let authConfig = source.authConfig, !authConfig.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return source
        }
        if WebDAVCredentialStore.shared.credentialID(from: authConfig) != nil {
            return source
        }

        guard let legacy = WebDAVCredentialStore.shared.decodeLegacyCredential(from: authConfig) else {
            return source
        }

        var migratedSource = source
        do {
            let credentialID = try WebDAVCredentialStore.shared.saveCredential(
                username: legacy.username,
                password: legacy.password
            )
            let reference = WebDAVCredentialStore.shared.authReference(for: credentialID)
            migratedSource.authConfig = reference
            if let sourceID = source.id {
                try await AppDatabase.shared.dbQueue.write { db in
                    try db.execute(
                        sql: "UPDATE mediaSource SET authConfig = ? WHERE id = ?",
                        arguments: [reference, sourceID]
                    )
                }
            }
            print("🔐 已将 WebDAV 明文凭据迁移到 Keychain：\(source.name)")
        } catch {
            print("⚠️ WebDAV 凭据迁移 Keychain 失败，继续使用旧配置：\(error.localizedDescription)")
        }
        return migratedSource
    }

    private func classifyScanError(_ error: Error, source: MediaSource) -> MediaSourceScanErrorCategory {
        if let scanError = error as? MediaSourceScanError {
            switch scanError {
            case .invalidBaseURL, .unsupportedProtocol, .accessDenied:
                return .config
            case .requestFailed(let message):
                return classifyRequestMessage(message, source: source)
            }
        }

        if let urlError = error as? URLError {
            let retryableCodes: Set<URLError.Code> = [
                .timedOut, .networkConnectionLost, .cannotConnectToHost, .cannotFindHost, .dnsLookupFailed, .notConnectedToInternet
            ]
            if retryableCodes.contains(urlError.code) { return .network }
        }

        return .unknown
    }

    private func classifyRequestMessage(_ message: String, source: MediaSource) -> MediaSourceScanErrorCategory {
        if message.contains("认证失败") || message.contains("HTTP 401") || message.contains("HTTP 403") {
            return .auth
        }

        if let code = extractHTTPStatusCode(from: message) {
            if (500...599).contains(code) { return .server }
            if code == 401 || code == 403 { return .auth }
            if (400...499).contains(code) { return .config }
        }

        if message.contains("网络错误") { return .network }
        if source.protocolKind == .webdav && message.contains("重试后仍未成功") { return .server }
        if message.contains("地址无效") || message.contains("不支持") { return .config }
        return .unknown
    }

    private func userFacingMessage(for category: MediaSourceScanErrorCategory, sourceName: String, fallback: String) -> String {
        switch category {
        case .auth:
            return "“\(sourceName)”连接失败：认证失败，请检查 WebDAV 用户名/密码。"
        case .network:
            return "“\(sourceName)”连接失败：网络不可达或超时。"
        case .server:
            return "“\(sourceName)”连接失败：服务端返回错误，请稍后重试。"
        case .config:
            return "“\(sourceName)”配置无效或不可访问，请检查源地址。"
        case .unknown:
            return "“\(sourceName)”扫描失败：\(fallback)"
        }
    }

    private func buildDiagnostic(
        source: MediaSource,
        category: MediaSourceScanErrorCategory,
        message: String,
        error: Error?,
        retryAttempts: Int
    ) -> MediaSourceScanDiagnostic {
        MediaSourceScanDiagnostic(
            sourceName: source.name,
            protocolType: source.protocolType,
            endpoint: MediaSourceScanDiagnosticsFormatter.sanitizedEndpoint(from: source),
            category: category,
            statusCode: extractHTTPStatusCode(from: message),
            urlErrorCode: extractURLErrorCode(from: message, fallback: error),
            retryAttempts: retryAttempts,
            timestamp: Date(),
            message: message
        )
    }

    private func defaultRetryAttempts(for category: MediaSourceScanErrorCategory, source: MediaSource) -> Int {
        guard source.protocolKind == .webdav else { return 0 }
        switch category {
        case .network, .server:
            return 3
        default:
            return 1
        }
    }

    private func extractHTTPStatusCode(from message: String) -> Int? {
        let pattern = #"HTTP\s+(\d{3})"#
        guard let regex = try? NSRegularExpression(pattern: pattern) else { return nil }
        let ns = message as NSString
        let range = NSRange(location: 0, length: ns.length)
        guard let match = regex.firstMatch(in: message, range: range), match.numberOfRanges > 1 else { return nil }
        let codeText = ns.substring(with: match.range(at: 1))
        return Int(codeText)
    }

    private func extractURLErrorCode(from message: String, fallback: Error?) -> Int? {
        let pattern = #"网络错误\((-?\d+)\)"#
        if let regex = try? NSRegularExpression(pattern: pattern) {
            let ns = message as NSString
            let range = NSRange(location: 0, length: ns.length)
            if let match = regex.firstMatch(in: message, range: range), match.numberOfRanges > 1 {
                let codeText = ns.substring(with: match.range(at: 1))
                if let code = Int(codeText) { return code }
            }
        }

        if let urlError = fallback as? URLError {
            return urlError.code.rawValue
        }
        return nil
    }
}
