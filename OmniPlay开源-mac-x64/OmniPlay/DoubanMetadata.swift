import Foundation
import GRDB

struct DoubanMetadata: Codable, FetchableRecord, PersistableRecord {
    var movieId: Int64
    var subjectId: String
    var subjectURL: String
    var title: String
    var originalTitle: String?
    var year: String?
    var rating: Double?
    var ratingCount: Int?
    var summary: String?
    var genres: String?
    var countries: String?
    var directors: String?
    var casts: String?
    var posterURL: String?
    var fetchedAt: Double
    var nextRefreshAt: Double
    var lastError: String?

    static let databaseTableName = "doubanMetadata"

    var genreList: [String] { Self.decodeList(genres) }
    var countryList: [String] { Self.decodeList(countries) }
    var directorList: [String] { Self.decodeList(directors) }
    var castList: [String] { Self.decodeList(casts) }

    var isInvalidPlaceholder: Bool {
        rating == nil && ["豆瓣", "豆瓣电影"].contains(title.trimmingCharacters(in: .whitespacesAndNewlines))
    }

    func applyPosterToMovie(_ db: Database) throws {
        guard let posterURL = posterURL?.trimmingCharacters(in: .whitespacesAndNewlines),
              !posterURL.isEmpty,
              Self.isUsablePosterURL(posterURL),
              var movie = try Movie.fetchOne(db, key: movieId) else {
            return
        }
        if let currentPoster = movie.posterPath?.trimmingCharacters(in: .whitespacesAndNewlines),
           !currentPoster.isEmpty,
           !Self.isUnusableDoubanPosterURL(currentPoster) {
            return
        }
        movie.posterPath = posterURL
        try movie.update(db)
    }

    static func clearUnusableDoubanPosterIfNeeded(movieId: Int64, db: Database) throws {
        guard var movie = try Movie.fetchOne(db, key: movieId),
              let posterPath = movie.posterPath?.trimmingCharacters(in: .whitespacesAndNewlines),
              isUnusableDoubanPosterURL(posterPath) else {
            return
        }
        movie.posterPath = nil
        try movie.update(db)
    }

    static func isUsablePosterURL(_ value: String) -> Bool {
        let lowercased = value.lowercased()
        guard lowercased.hasPrefix("http://") || lowercased.hasPrefix("https://") else {
            return false
        }
        if lowercased.contains("qnmob") { return false }
        if lowercased.contains("/q/60/") || lowercased.contains("/w/300/") || lowercased.contains("/h/300/") {
            return false
        }
        return lowercased.contains("doubanio.com/view/photo/s_ratio_poster/public/")
            || lowercased.contains("doubanio.com/view/photo/raw/public/")
            || lowercased.contains("doubanio.com/view/photo/l/public/")
    }

    static func isUnusableDoubanPosterURL(_ value: String) -> Bool {
        let lowercased = value.lowercased()
        guard lowercased.contains("doubanio.com") else { return false }
        if lowercased.contains("qnmob") { return true }
        return lowercased.contains("/q/60/")
            || lowercased.contains("/w/300/")
            || lowercased.contains("/h/300/")
            || lowercased.contains("imageview2/1")
    }

    static func encodeList(_ values: [String]) -> String? {
        let normalized = values
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
        guard !normalized.isEmpty else { return nil }
        guard let data = try? JSONEncoder().encode(normalized) else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func decodeList(_ value: String?) -> [String] {
        guard let value,
              let data = value.data(using: .utf8),
              let decoded = try? JSONDecoder().decode([String].self, from: data) else {
            return []
        }
        return decoded
    }
}

struct DoubanSubjectPayload {
    let subjectId: String
    let subjectURL: String
    let title: String
    let originalTitle: String?
    let year: String?
    let rating: Double?
    let ratingCount: Int?
    let summary: String?
    let genres: [String]
    let countries: [String]
    let directors: [String]
    let casts: [String]
    let posterURL: String?

    func metadata(movieId: Int64, fetchedAt: Date, cacheDays: Int) -> DoubanMetadata {
        DoubanMetadata(
            movieId: movieId,
            subjectId: subjectId,
            subjectURL: subjectURL,
            title: title,
            originalTitle: originalTitle,
            year: year,
            rating: rating,
            ratingCount: ratingCount,
            summary: summary,
            genres: DoubanMetadata.encodeList(genres),
            countries: DoubanMetadata.encodeList(countries),
            directors: DoubanMetadata.encodeList(directors),
            casts: DoubanMetadata.encodeList(casts),
            posterURL: posterURL,
            fetchedAt: fetchedAt.timeIntervalSince1970,
            nextRefreshAt: fetchedAt.addingTimeInterval(Double(cacheDays) * 24 * 60 * 60).timeIntervalSince1970,
            lastError: nil
        )
    }
}
