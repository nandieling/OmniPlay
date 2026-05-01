//
//  WebDAVMockIntegrationTests.swift
//  OmniPlayTests
//
//  Created by Codex on 2026/4/7.
//

import Foundation
import GRDB
import Testing
@testable import OmniPlay

@MainActor
@Suite("WebDAV Mock Integration", .serialized)
struct WebDAVMockIntegrationTests {

    @Test("WebDAV scanner ingests files via mock PROPFIND and applies BDMV size threshold")
    func webDAVScannerWithMockPROPFIND() async throws {
        let dbURL = makeTempDBURL()
        defer { cleanupDBFiles(at: dbURL) }

        try AppDatabase.shared.setup(databaseURL: dbURL)
        guard let dbQueue = AppDatabase.shared.dbQueue else {
            Issue.record("Database queue not initialized")
            return
        }

        try await dbQueue.write { db in
            try db.execute(
                sql: """
                INSERT INTO mediaSource (id, name, protocolType, baseUrl, authConfig)
                VALUES (?, ?, ?, ?, ?)
                """,
                arguments: [901, "WebDAV Mock", MediaSourceProtocol.webdav.rawValue, "http://tester:secret@webdav.mock/dav/Media/", nil]
            )
        }

        WebDAVMockURLProtocol.reset()
        WebDAVScannerRuntimeOverrides.protocolClasses = [WebDAVMockURLProtocol.self]
        defer {
            WebDAVScannerRuntimeOverrides.protocolClasses = nil
            WebDAVMockURLProtocol.reset()
        }

        WebDAVMockURLProtocol.stub(path: "/dav/Media", xmlBody: """
<?xml version="1.0" encoding="utf-8"?>
<d:multistatus xmlns:d="DAV:">
  <d:response><d:href>/dav/Media/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
  <d:response><d:href>/dav/Media/MovieA.2022.mkv</d:href><d:propstat><d:prop><d:resourcetype/><d:getcontentlength>111111</d:getcontentlength></d:prop></d:propstat></d:response>
  <d:response><d:href>/dav/Media/Show/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
  <d:response><d:href>/dav/Media/BDMovie/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
  <d:response><d:href>/dav/Media/.hidden/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
</d:multistatus>
""")

        WebDAVMockURLProtocol.stub(path: "/dav/Media/Show", xmlBody: """
<?xml version="1.0" encoding="utf-8"?>
<d:multistatus xmlns:d="DAV:">
  <d:response><d:href>/dav/Media/Show/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
  <d:response><d:href>/dav/Media/Show/S01E01.mp4</d:href><d:propstat><d:prop><d:resourcetype/><d:getcontentlength>88888</d:getcontentlength></d:prop></d:propstat></d:response>
</d:multistatus>
""")

        WebDAVMockURLProtocol.stub(path: "/dav/Media/BDMovie", xmlBody: """
<?xml version="1.0" encoding="utf-8"?>
<d:multistatus xmlns:d="DAV:">
  <d:response><d:href>/dav/Media/BDMovie/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
  <d:response><d:href>/dav/Media/BDMovie/BDMV/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
</d:multistatus>
""")

        WebDAVMockURLProtocol.stub(path: "/dav/Media/BDMovie/BDMV", xmlBody: """
<?xml version="1.0" encoding="utf-8"?>
<d:multistatus xmlns:d="DAV:">
  <d:response><d:href>/dav/Media/BDMovie/BDMV/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
  <d:response><d:href>/dav/Media/BDMovie/BDMV/STREAM/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
</d:multistatus>
""")

        WebDAVMockURLProtocol.stub(path: "/dav/Media/BDMovie/BDMV/STREAM", xmlBody: """
<?xml version="1.0" encoding="utf-8"?>
<d:multistatus xmlns:d="DAV:">
  <d:response><d:href>/dav/Media/BDMovie/BDMV/STREAM/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
  <d:response><d:href>/dav/Media/BDMovie/BDMV/STREAM/00001.m2ts</d:href><d:propstat><d:prop><d:resourcetype/><d:getcontentlength>100000</d:getcontentlength></d:prop></d:propstat></d:response>
  <d:response><d:href>/dav/Media/BDMovie/BDMV/STREAM/00002.m2ts</d:href><d:propstat><d:prop><d:resourcetype/><d:getcontentlength>20000</d:getcontentlength></d:prop></d:propstat></d:response>
</d:multistatus>
""")

        let source = try await dbQueue.read { db in
            try MediaSource.fetchOne(db, key: 901)
        }
        guard let source else {
            Issue.record("Failed to read inserted media source")
            return
        }

        let manager = MediaLibraryManager()
        let result = await manager.scanLocalSourceWithResult(source)

        let inserted = try await dbQueue.read { db in
            try VideoFile.filter(Column("sourceId") == 901).fetchAll(db)
        }
        let insertedPaths = Set(inserted.map(\.relativePath))

        #expect(insertedPaths.contains("MovieA.2022.mkv"))
        #expect(insertedPaths.contains("Show/S01E01.mp4"))
        #expect(insertedPaths.contains("BDMovie/BDMV/STREAM/00001.m2ts"))
        #expect(!insertedPaths.contains("BDMovie/BDMV/STREAM/00002.m2ts"))
        #expect(result.isSuccess)
        #expect(result.errorCategory == nil)
        #expect(result.insertedCount == inserted.count)

        let authHeader = WebDAVMockURLProtocol.lastAuthorizationHeader
        #expect(authHeader == "Basic \(Data("tester:secret".utf8).base64EncodedString())")
    }

    @Test("WebDAV scanner retries on 5xx and succeeds on subsequent response")
    func webDAVScannerRetriesOnServerError() async throws {
        let dbURL = makeTempDBURL()
        defer { cleanupDBFiles(at: dbURL) }

        try AppDatabase.shared.setup(databaseURL: dbURL)
        guard let dbQueue = AppDatabase.shared.dbQueue else {
            Issue.record("Database queue not initialized")
            return
        }

        try await dbQueue.write { db in
            try db.execute(
                sql: """
                INSERT INTO mediaSource (id, name, protocolType, baseUrl, authConfig)
                VALUES (?, ?, ?, ?, ?)
                """,
                arguments: [902, "WebDAV Retry", MediaSourceProtocol.webdav.rawValue, "http://webdav.mock/dav/Retry/", nil]
            )
        }

        WebDAVMockURLProtocol.reset()
        WebDAVScannerRuntimeOverrides.protocolClasses = [WebDAVMockURLProtocol.self]
        defer {
            WebDAVScannerRuntimeOverrides.protocolClasses = nil
            WebDAVMockURLProtocol.reset()
        }

        WebDAVMockURLProtocol.setSequence(path: "/dav/Retry", responses: [
            .status(500),
            .multistatus("""
<?xml version="1.0" encoding="utf-8"?>
<d:multistatus xmlns:d="DAV:">
  <d:response><d:href>/dav/Retry/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
  <d:response><d:href>/dav/Retry/Test.mp4</d:href><d:propstat><d:prop><d:resourcetype/><d:getcontentlength>1234</d:getcontentlength></d:prop></d:propstat></d:response>
</d:multistatus>
""")
        ])

        let source = try await dbQueue.read { db in
            try MediaSource.fetchOne(db, key: 902)
        }
        guard let source else {
            Issue.record("Failed to read inserted media source")
            return
        }

        let manager = MediaLibraryManager()
        let result = await manager.scanLocalSourceWithResult(source)

        let inserted = try await dbQueue.read { db in
            try VideoFile.filter(Column("sourceId") == 902).fetchAll(db)
        }
        #expect(inserted.count == 1)
        #expect(inserted.first?.relativePath == "Test.mp4")
        #expect(WebDAVMockURLProtocol.callCount(path: "/dav/Retry") == 2)
        #expect(result.isSuccess)
        #expect(result.errorCategory == nil)
    }

    @Test("WebDAV scanner should stop on 401 and not ingest files")
    func webDAVScannerStopsOnUnauthorized() async throws {
        let dbURL = makeTempDBURL()
        defer { cleanupDBFiles(at: dbURL) }

        try AppDatabase.shared.setup(databaseURL: dbURL)
        guard let dbQueue = AppDatabase.shared.dbQueue else {
            Issue.record("Database queue not initialized")
            return
        }

        try await dbQueue.write { db in
            try db.execute(
                sql: """
                INSERT INTO mediaSource (id, name, protocolType, baseUrl, authConfig)
                VALUES (?, ?, ?, ?, ?)
                """,
                arguments: [903, "WebDAV Unauthorized", MediaSourceProtocol.webdav.rawValue, "http://webdav.mock/dav/AuthFail/", nil]
            )
        }

        WebDAVMockURLProtocol.reset()
        WebDAVScannerRuntimeOverrides.protocolClasses = [WebDAVMockURLProtocol.self]
        defer {
            WebDAVScannerRuntimeOverrides.protocolClasses = nil
            WebDAVMockURLProtocol.reset()
        }

        WebDAVMockURLProtocol.setSequence(path: "/dav/AuthFail", responses: [.status(401)])

        let source = try await dbQueue.read { db in
            try MediaSource.fetchOne(db, key: 903)
        }
        guard let source else {
            Issue.record("Failed to read inserted media source")
            return
        }

        let manager = MediaLibraryManager()
        let result = await manager.scanLocalSourceWithResult(source)

        let inserted = try await dbQueue.read { db in
            try VideoFile.filter(Column("sourceId") == 903).fetchAll(db)
        }
        #expect(inserted.isEmpty)
        #expect(WebDAVMockURLProtocol.callCount(path: "/dav/AuthFail") == 1)
        #expect(result.isSuccess == false)
        #expect(result.errorCategory == .auth)
        #expect(result.diagnostic?.statusCode == 401)
        #expect(result.diagnostic?.retryAttempts == 1)
    }

    @Test("WebDAV scanner should classify repeated 5xx as server error and expose diagnostics")
    func webDAVScannerClassifiesServerErrorAndDiagnostics() async throws {
        let dbURL = makeTempDBURL()
        defer { cleanupDBFiles(at: dbURL) }

        try AppDatabase.shared.setup(databaseURL: dbURL)
        guard let dbQueue = AppDatabase.shared.dbQueue else {
            Issue.record("Database queue not initialized")
            return
        }

        try await dbQueue.write { db in
            try db.execute(
                sql: """
                INSERT INTO mediaSource (id, name, protocolType, baseUrl, authConfig)
                VALUES (?, ?, ?, ?, ?)
                """,
                arguments: [904, "WebDAV 5xx", MediaSourceProtocol.webdav.rawValue, "http://webdav.mock/dav/Fail/", nil]
            )
        }

        WebDAVMockURLProtocol.reset()
        WebDAVScannerRuntimeOverrides.protocolClasses = [WebDAVMockURLProtocol.self]
        defer {
            WebDAVScannerRuntimeOverrides.protocolClasses = nil
            WebDAVMockURLProtocol.reset()
        }

        WebDAVMockURLProtocol.setSequence(path: "/dav/Fail", responses: [.status(500), .status(500), .status(500)])

        let source = try await dbQueue.read { db in
            try MediaSource.fetchOne(db, key: 904)
        }
        guard let source else {
            Issue.record("Failed to read inserted media source")
            return
        }

        let manager = MediaLibraryManager()
        let result = await manager.scanLocalSourceWithResult(source)

        #expect(result.isSuccess == false)
        #expect(result.errorCategory == .server)
        #expect(result.diagnostic?.statusCode == 500)
        #expect(result.diagnostic?.retryAttempts == 3)
        #expect(WebDAVMockURLProtocol.callCount(path: "/dav/Fail") >= 3)
    }

    @Test("WebDAV scanner should classify URLError as network error")
    func webDAVScannerClassifiesNetworkError() async throws {
        let dbURL = makeTempDBURL()
        defer { cleanupDBFiles(at: dbURL) }

        try AppDatabase.shared.setup(databaseURL: dbURL)
        guard let dbQueue = AppDatabase.shared.dbQueue else {
            Issue.record("Database queue not initialized")
            return
        }

        try await dbQueue.write { db in
            try db.execute(
                sql: """
                INSERT INTO mediaSource (id, name, protocolType, baseUrl, authConfig)
                VALUES (?, ?, ?, ?, ?)
                """,
                arguments: [905, "WebDAV Network", MediaSourceProtocol.webdav.rawValue, "http://webdav.mock/dav/NetFail/", nil]
            )
        }

        WebDAVMockURLProtocol.reset()
        WebDAVScannerRuntimeOverrides.protocolClasses = [WebDAVMockURLProtocol.self]
        defer {
            WebDAVScannerRuntimeOverrides.protocolClasses = nil
            WebDAVMockURLProtocol.reset()
        }

        WebDAVMockURLProtocol.setSequence(path: "/dav/NetFail", responses: [.urlError(.notConnectedToInternet)])

        let source = try await dbQueue.read { db in
            try MediaSource.fetchOne(db, key: 905)
        }
        guard let source else {
            Issue.record("Failed to read inserted media source")
            return
        }

        let manager = MediaLibraryManager()
        let result = await manager.scanLocalSourceWithResult(source)

        #expect(result.isSuccess == false)
        #expect(result.errorCategory == .network)
        #expect(result.diagnostic?.urlErrorCode == URLError.Code.notConnectedToInternet.rawValue)
        #expect(result.diagnostic?.retryAttempts == 3)
    }

    @Test("Offline cache policy blocks WebDAV source but allows local/direct")
    func offlineCachePolicyForWebDAV() {
        let manager = OfflineCacheManager.shared

        let webdav = MediaSource(id: 1, name: "webdav", protocolType: MediaSourceProtocol.webdav.rawValue, baseUrl: "https://mock/dav", authConfig: nil)
        let local = MediaSource(id: 2, name: "local", protocolType: MediaSourceProtocol.local.rawValue, baseUrl: "/tmp/demo", authConfig: nil)
        let direct = MediaSource(id: 3, name: "direct", protocolType: MediaSourceProtocol.direct.rawValue, baseUrl: "/", authConfig: nil)

        #expect(manager.supportsCaching(mediaSource: webdav) == false)
        #expect(manager.supportsCaching(mediaSource: local) == true)
        #expect(manager.supportsCaching(mediaSource: direct) == true)
    }

    @Test("Missing-source check should not block WebDAV playback")
    func missingSourceCheckForWebDAV() {
        let manager = OfflineCacheManager.shared
        let file = VideoFile(
            id: "vf-webdav-missing",
            sourceId: 777,
            relativePath: "Show/S01E01.mp4",
            fileName: "S01E01.mp4",
            mediaType: "unmatched",
            movieId: nil,
            episodeId: nil,
            playProgress: 0.0,
            duration: 0.0
        )
        let webdav = MediaSource(id: 777, name: "webdav", protocolType: MediaSourceProtocol.webdav.rawValue, baseUrl: "https://mock/dav", authConfig: nil)

        #expect(manager.hasMissingSource(for: file, mediaSource: webdav) == false)
    }

    @Test("Diagnostics formatter should sanitize credential in endpoint and include key fields")
    func diagnosticsFormatterSanitizesEndpointAndFormats() {
        let endpoint = MediaSourceScanDiagnosticsFormatter.sanitizedEndpoint(from: "https://user:pass@nas.local:5006/dav/Media")
        #expect(endpoint == "https://nas.local:5006/dav/Media")

        let result = MediaSourceScanResult(
            sourceId: 1,
            sourceName: "NAS",
            protocolType: MediaSourceProtocol.webdav.rawValue,
            scannedCount: 0,
            insertedCount: 0,
            removedCount: 0,
            errorCategory: .auth,
            userMessage: "认证失败",
            diagnostic: MediaSourceScanDiagnostic(
                sourceName: "NAS",
                protocolType: MediaSourceProtocol.webdav.rawValue,
                endpoint: endpoint,
                category: .auth,
                statusCode: 401,
                urlErrorCode: nil,
                retryAttempts: 1,
                timestamp: Date(timeIntervalSince1970: 0),
                message: "WebDAV 认证失败"
            )
        )
        let report = MediaSourceScanDiagnosticsFormatter.diagnosticsReport(results: [result])
        #expect(report.contains("源] NAS"))
        #expect(report.contains("分类=auth"))
        #expect(report.contains("端点=https://nas.local:5006/dav/Media"))
        #expect(!report.contains("user:pass"))
    }

    @Test("WebDAV preflight checker should succeed on 207 and sanitize endpoint")
    func webDAVPreflightCheckerSucceeds() async {
        WebDAVMockURLProtocol.reset()
        WebDAVScannerRuntimeOverrides.protocolClasses = [WebDAVMockURLProtocol.self]
        defer {
            WebDAVScannerRuntimeOverrides.protocolClasses = nil
            WebDAVMockURLProtocol.reset()
        }

        WebDAVMockURLProtocol.stub(path: "/dav/Media", xmlBody: """
<?xml version="1.0" encoding="utf-8"?>
<d:multistatus xmlns:d="DAV:">
  <d:response><d:href>/dav/Media/</d:href></d:response>
</d:multistatus>
""")

        let checker = WebDAVPreflightChecker()
        let result = await checker.check(
            baseURL: "http://user:secret@webdav.mock/dav/Media",
            username: "tester",
            password: "123456"
        )

        #expect(result.isReachable)
        #expect(result.category == nil)
        #expect(result.httpStatusCode == 207)
        #expect(result.sanitizedEndpoint == "http://webdav.mock/dav/Media")
        #expect(WebDAVMockURLProtocol.callCount(path: "/dav/Media") == 1)
        let authHeader = WebDAVMockURLProtocol.lastAuthorizationHeader
        #expect(authHeader == "Basic \(Data("tester:123456".utf8).base64EncodedString())")
    }

    @Test("WebDAV preflight checker should classify auth and network errors")
    func webDAVPreflightCheckerClassifiesFailures() async {
        WebDAVMockURLProtocol.reset()
        WebDAVScannerRuntimeOverrides.protocolClasses = [WebDAVMockURLProtocol.self]
        defer {
            WebDAVScannerRuntimeOverrides.protocolClasses = nil
            WebDAVMockURLProtocol.reset()
        }

        WebDAVMockURLProtocol.setSequence(path: "/dav/AuthFail", responses: [.status(401)])
        WebDAVMockURLProtocol.setSequence(path: "/dav/NetFail", responses: [.urlError(.cannotConnectToHost)])

        let checker = WebDAVPreflightChecker()
        let authResult = await checker.check(
            baseURL: "http://webdav.mock/dav/AuthFail",
            username: "bad",
            password: "pwd"
        )
        #expect(authResult.isReachable == false)
        #expect(authResult.category == .auth)
        #expect(authResult.httpStatusCode == 401)

        let netResult = await checker.check(
            baseURL: "http://webdav.mock/dav/NetFail",
            username: "",
            password: ""
        )
        #expect(netResult.isReachable == false)
        #expect(netResult.category == .network)
        #expect(netResult.urlErrorCode == URLError.Code.cannotConnectToHost.rawValue)
    }

    @Test("WebDAV preflight diagnostics formatter should format failure and ignore success")
    func webDAVPreflightDiagnosticsFormatter() {
        let success = WebDAVPreflightResult(
            isReachable: true,
            category: nil,
            message: "ok",
            httpStatusCode: 207,
            urlErrorCode: nil,
            sanitizedEndpoint: "https://nas.local/dav"
        )
        #expect(WebDAVPreflightDiagnosticsFormatter.diagnosticsReport(result: success, sourceName: "NAS").isEmpty)

        let failure = WebDAVPreflightResult(
            isReachable: false,
            category: .auth,
            message: "认证失败",
            httpStatusCode: 401,
            urlErrorCode: nil,
            sanitizedEndpoint: "https://nas.local/dav"
        )
        let report = WebDAVPreflightDiagnosticsFormatter.diagnosticsReport(result: failure, sourceName: "NAS")
        #expect(report.contains("WebDAV 预检诊断"))
        #expect(report.contains("[源] NAS"))
        #expect(report.contains("分类=auth"))
        #expect(report.contains("HTTP状态=401"))
        #expect(report.contains("端点=https://nas.local/dav"))
    }

    private func makeTempDBURL() -> URL {
        URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("omniplay-webdav-test-\(UUID().uuidString).sqlite")
    }

    private func cleanupDBFiles(at dbURL: URL) {
        let fm = FileManager.default
        let path = dbURL.path
        try? fm.removeItem(atPath: path)
        try? fm.removeItem(atPath: "\(path)-shm")
        try? fm.removeItem(atPath: "\(path)-wal")
    }
}

private final class WebDAVMockURLProtocol: URLProtocol {
    private struct StubResponse {
        let statusCode: Int
        let body: Data
        let errorCode: URLError.Code?
    }

    private static let lock = NSLock()
    private static var responsesByPath: [String: [StubResponse]] = [:]
    private static var callsByPath: [String: Int] = [:]
    static var lastAuthorizationHeader: String?

    static func reset() {
        lock.lock()
        defer { lock.unlock() }
        responsesByPath = [:]
        callsByPath = [:]
        lastAuthorizationHeader = nil
    }

    static func stub(path: String, xmlBody: String) {
        setSequence(path: path, responses: [.multistatus(xmlBody)])
    }

    static func setSequence(path: String, responses: [Response]) {
        let normalized = normalize(path)
        lock.lock()
        defer { lock.unlock() }
        responsesByPath[normalized] = responses.map { response in
            switch response {
            case .status(let code):
                return StubResponse(statusCode: code, body: Data(), errorCode: nil)
            case .multistatus(let xml):
                return StubResponse(statusCode: 207, body: Data(xml.utf8), errorCode: nil)
            case .urlError(let code):
                return StubResponse(statusCode: -1, body: Data(), errorCode: code)
            }
        }
    }

    static func callCount(path: String) -> Int {
        let normalized = normalize(path)
        lock.lock()
        defer { lock.unlock() }
        return callsByPath[normalized] ?? 0
    }

    override class func canInit(with request: URLRequest) -> Bool {
        guard let scheme = request.url?.scheme?.lowercased() else { return false }
        return scheme == "http" || scheme == "https"
    }

    override class func canonicalRequest(for request: URLRequest) -> URLRequest {
        request
    }

    override func startLoading() {
        guard let url = request.url else {
            client?.urlProtocol(self, didFailWithError: URLError(.badURL))
            return
        }
        guard request.httpMethod?.uppercased() == "PROPFIND" else {
            client?.urlProtocol(self, didFailWithError: URLError(.unsupportedURL))
            return
        }

        let path = Self.normalize(url.path)
        Self.lock.lock()
        Self.callsByPath[path, default: 0] += 1
        var selected: StubResponse?
        if var queue = Self.responsesByPath[path], !queue.isEmpty {
            selected = queue.removeFirst()
            Self.responsesByPath[path] = queue.isEmpty ? [selected!] : queue
        }
        Self.lastAuthorizationHeader = request.value(forHTTPHeaderField: "Authorization")
        Self.lock.unlock()

        guard let selected else {
            let response = HTTPURLResponse(url: url, statusCode: 404, httpVersion: nil, headerFields: nil)!
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocolDidFinishLoading(self)
            return
        }

        if selected.statusCode == -1 {
            client?.urlProtocol(self, didFailWithError: URLError(selected.errorCode ?? .unknown))
            return
        }

        let headers = ["Content-Type": "application/xml; charset=utf-8"]
        let response = HTTPURLResponse(url: url, statusCode: selected.statusCode, httpVersion: nil, headerFields: headers)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        if !selected.body.isEmpty {
            client?.urlProtocol(self, didLoad: selected.body)
        }
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}

    private static func normalize(_ path: String) -> String {
        if path.isEmpty { return "/" }
        var value = path
        while value.count > 1 && value.hasSuffix("/") {
            value.removeLast()
        }
        return value
    }

    enum Response {
        case status(Int)
        case multistatus(String)
        case urlError(URLError.Code)
    }
}
