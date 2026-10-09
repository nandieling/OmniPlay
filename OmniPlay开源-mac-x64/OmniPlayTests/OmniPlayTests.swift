//
//  OmniPlayTests.swift
//  OmniPlayTests
//
//  Created by nan on 2026/3/16.
//

import Foundation
import Testing
@testable import OmniPlay

struct OmniPlayTests {

    @Test func basicSanity() async throws {
        #expect(!"OmniPlay".isEmpty)
    }

    @Test func playbackWatchStateUsesOneConsistentThreeStateRule() {
        #expect(PlaybackWatchState(progress: 0, duration: 1_000) == .unplayed)
        #expect(PlaybackWatchState(progress: 1, duration: 1_000) == .inProgress)
        #expect(PlaybackWatchState(progress: 500, duration: 1_000) == .inProgress)
        #expect(PlaybackWatchState(progress: 949, duration: 1_000) == .inProgress)
        #expect(PlaybackWatchState(progress: 950, duration: 1_000) == .watched)
        #expect(PlaybackWatchState(progress: 12, duration: 0) == .inProgress)
        #expect(PlaybackWatchState(progress: .nan, duration: 1_000) == .unplayed)
    }

    @Test func thumbnailManagerReplacesAndRemovesEpisodeStillFiles() throws {
        let manager = ThumbnailManager.shared
        let fileId = UUID().uuidString
        let tempDir = FileManager.default.temporaryDirectory.appendingPathComponent("OmniPlayThumbTest-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: tempDir) }
        let source = tempDir.appendingPathComponent("source.jpg")
        try Data([0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02, 0x03]).write(to: source)

        try manager.replaceThumbnail(fileId: fileId, with: source)
        #expect(FileManager.default.fileExists(atPath: manager.thumbnailURL(for: fileId).path))

        manager.removeAssets(for: [fileId])
        #expect(!FileManager.default.fileExists(atPath: manager.thumbnailURL(for: fileId).path))
    }

    @Test func tmdbProxyGatewayURLResolution() throws {
        let officialRequest = "https://api.themoviedb.org/3/search/multi?query=The%20Matrix&language=zh-CN"

        let hostOnly = try #require(TMDBService.resolvedAPIURL(
            for: officialRequest,
            customProxyURL: "proxy.example.com"
        ))
        #expect(hostOnly.absoluteString == "https://proxy.example.com/3/search/multi?query=The%20Matrix&language=zh-CN")

        let pathPrefix = try #require(TMDBService.resolvedAPIURL(
            for: officialRequest,
            customProxyURL: "https://gateway.example.com/tmdb/3/"
        ))
        #expect(pathPrefix.absoluteString == "https://gateway.example.com/tmdb/3/search/multi?query=The%20Matrix&language=zh-CN")

        #expect(TMDBService.proxyAPIBaseURL(customProxyURL: "ftp://proxy.example.com") == nil)
        #expect(TMDBService.proxyAPIBaseURL(customProxyURL: "https://user:password@proxy.example.com") == nil)
    }

    @Test func luckyStunSourceConfigurationRoundTripPreservesPath() throws {
        let original = LuckyStunSourceConfiguration(
            managementURL: "http://192.168.1.2:16601",
            username: "admin",
            password: "secret",
            ruleID: "webdav-rule",
            ruleName: "WebDAV",
            displayName: "WebDAV-stun",
            autoUpdate: true,
            updateIntervalMinutes: 15,
            lastUpdatedAt: 123,
            pathSuffix: "dav/TV"
        )
        let encoded = try JSONEncoder().encode(original)
        let decoded = try JSONDecoder().decode(LuckyStunSourceConfiguration.self, from: encoded)

        #expect(decoded == original)
    }

    @Test func luckyStunDisplayNameOverridesProtocolSubtitle() throws {
        let protocols: [MediaSourceProtocol] = [.webdav, .plex, .jellyfin, .emby]
        for protocolKind in protocols {
            var source = MediaSource(
                id: nil,
                name: "电影",
                protocolType: protocolKind.rawValue,
                baseUrl: "https://media.example.com/library",
                authConfig: nil
            )
            source.setAddressConfiguration(MediaSourceAddressConfiguration(
                luckyStun: LuckyStunSourceConfiguration(displayName: " Lucky-媒体源 ")
            ))
            #expect(source.luckyStunDisplayName == "Lucky-媒体源")

            source.setAddressConfiguration(MediaSourceAddressConfiguration())
            #expect(source.luckyStunDisplayName == nil)
        }
    }

}
