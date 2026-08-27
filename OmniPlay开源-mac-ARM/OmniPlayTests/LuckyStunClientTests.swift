import Foundation
import Testing
@testable import OmniPlay

@Suite(.serialized)
struct LuckyStunClientTests {
    @MainActor
    @Test func loginLoadsRulesThroughCloudflareSubpath() async throws {
        LuckyStunMockURLProtocol.reset()
        LuckyStunMockURLProtocol.stub(
            path: "/secure/api/login",
            body: #"{"ret":0,"token":"test-token"}"#
        )
        LuckyStunMockURLProtocol.stub(
            path: "/secure/api/stunrulelist",
            body: #"{"ret":0,"list":[{"Key":"media-stun","Name":"Media STUN","PublicAddr":"203.0.113.8:18443","StunType":"tcp","Enable":true}]}"#
        )
        let session = makeSession()
        defer {
            session.invalidateAndCancel()
            LuckyStunMockURLProtocol.reset()
        }

        let client = LuckyStunClient(session: session)
        let rules = try await client.fetch(
            managementURL: "https://lucky.example.com/secure/#/stun#list",
            username: " lucky-admin ",
            password: "secret",
            selectedRuleID: "",
            selectedRuleName: ""
        )

        let requests = LuckyStunMockURLProtocol.capturedRequests()
        #expect(requests.count == 2)
        #expect(requests[0].method == "POST")
        #expect(requests[0].url.path == "/secure/api/login")
        #expect(requests[0].queryValue(named: "_") != nil)
        #expect(requests[0].header(named: "Origin") == "https://lucky.example.com")
        let loginBody = try #require(
            try JSONSerialization.jsonObject(with: requests[0].body) as? [String: String]
        )
        #expect(loginBody == ["Account": "lucky-admin", "Password": "secret", "TwoFA": ""])

        #expect(requests[1].method == "GET")
        #expect(requests[1].url.path == "/secure/api/stunrulelist")
        #expect(requests[1].header(named: "Lucky-Admin-Token") == "test-token")
        #expect(rules == [
            LuckyStunRule(id: "media-stun", name: "Media STUN", address: "http://203.0.113.8:18443")
        ])
    }

    @MainActor
    @Test func loginRejectsLuckyApplicationError() async throws {
        LuckyStunMockURLProtocol.reset()
        LuckyStunMockURLProtocol.stub(
            path: "/api/login",
            body: #"{"ret":1,"msg":"IncorrectAccountOrPassword"}"#
        )
        let session = makeSession()
        defer {
            session.invalidateAndCancel()
            LuckyStunMockURLProtocol.reset()
        }

        let client = LuckyStunClient(session: session)
        do {
            _ = try await client.fetch(
                managementURL: "https://lucky.example.com",
                username: "admin",
                password: "wrong",
                selectedRuleID: "",
                selectedRuleName: ""
            )
            Issue.record("Expected Lucky login to fail")
        } catch {
            #expect(error.localizedDescription == "Lucky STUN 登录失败：账号或密码错误")
        }
        #expect(LuckyStunMockURLProtocol.capturedRequests().count == 1)
    }

    private func makeSession() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [LuckyStunMockURLProtocol.self]
        return URLSession(configuration: configuration)
    }
}

private struct LuckyStunCapturedRequest {
    let method: String
    let url: URL
    let headers: [String: String]
    let body: Data

    func header(named name: String) -> String? {
        headers.first { $0.key.caseInsensitiveCompare(name) == .orderedSame }?.value
    }

    func queryValue(named name: String) -> String? {
        URLComponents(url: url, resolvingAgainstBaseURL: false)?
            .queryItems?
            .first(where: { $0.name == name })?
            .value
    }
}

private final class LuckyStunMockURLProtocol: URLProtocol {
    private struct Stub {
        let statusCode: Int
        let body: Data
    }

    private static let lock = NSLock()
    private static var stubs: [String: Stub] = [:]
    private static var requests: [LuckyStunCapturedRequest] = []

    static func reset() {
        lock.lock()
        defer { lock.unlock() }
        stubs = [:]
        requests = []
    }

    static func stub(path: String, statusCode: Int = 200, body: String) {
        lock.lock()
        defer { lock.unlock() }
        stubs[path] = Stub(statusCode: statusCode, body: Data(body.utf8))
    }

    static func capturedRequests() -> [LuckyStunCapturedRequest] {
        lock.lock()
        defer { lock.unlock() }
        return requests
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
        let captured = LuckyStunCapturedRequest(
            method: request.httpMethod ?? "",
            url: url,
            headers: request.allHTTPHeaderFields ?? [:],
            body: Self.bodyData(from: request)
        )
        Self.lock.lock()
        Self.requests.append(captured)
        let stub = Self.stubs[url.path]
        Self.lock.unlock()

        guard let stub else {
            let response = HTTPURLResponse(url: url, statusCode: 404, httpVersion: nil, headerFields: nil)!
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocolDidFinishLoading(self)
            return
        }
        let response = HTTPURLResponse(
            url: url,
            statusCode: stub.statusCode,
            httpVersion: nil,
            headerFields: ["Content-Type": "application/json"]
        )!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: stub.body)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}

    private static func bodyData(from request: URLRequest) -> Data {
        if let body = request.httpBody { return body }
        guard let stream = request.httpBodyStream else { return Data() }
        stream.open()
        defer { stream.close() }
        var result = Data()
        var buffer = [UInt8](repeating: 0, count: 4_096)
        while stream.hasBytesAvailable {
            let count = stream.read(&buffer, maxLength: buffer.count)
            guard count > 0 else { break }
            result.append(buffer, count: count)
        }
        return result
    }
}
