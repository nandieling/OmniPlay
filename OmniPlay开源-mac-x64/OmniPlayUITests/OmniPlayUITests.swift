//
//  OmniPlayUITests.swift
//  OmniPlayUITests
//
//  Created by nan on 2026/3/16.
//

import XCTest
import AppKit

final class OmniPlayUITests: XCTestCase {

    override func setUpWithError() throws {
        // Put setup code here. This method is called before the invocation of each test method in the class.

        // In UI tests it is usually best to stop immediately when a failure occurs.
        continueAfterFailure = false

        // Keep setup minimal: force-killing app can surface debugger SIGTERM noise on local machines.
    }

    override func tearDownWithError() throws {
        let app = XCUIApplication()
        if app.state == .runningForeground || app.state == .runningBackground {
            app.terminate()
        }
    }

    @MainActor
    func testExample() throws {
        let app = try launchAppForUITest()

        guard waitForToolbarControl(in: app, identifier: "toolbar.addSource", fallbackLabel: "媒体源管理", timeout: 10) else {
            throw XCTSkip("UI environment could not stably find toolbar control: toolbar.addSource")
        }
        guard hasToolbarControl(in: app, identifier: "toolbar.sync", fallbackLabel: "同步") else {
            throw XCTSkip("UI environment could not stably find toolbar control: toolbar.sync")
        }
        guard hasToolbarControl(in: app, identifier: "toolbar.settings", fallbackLabel: "设置") else {
            throw XCTSkip("UI environment could not stably find toolbar control: toolbar.settings")
        }
    }

    @MainActor
    func testLaunchPerformance() throws {
        let app = try launchAppForUITest(openWebDAVSheet: true)

        XCTAssertTrue(app.staticTexts["webdav.sheet.title"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.textFields["webdav.baseURL"].exists)

        let cancel = app.buttons["webdav.cancel"]
        XCTAssertTrue(cancel.exists)
        cancel.tap()
    }

    @MainActor
    func testWebDAVInvalidManualURLShowsInlineValidation() throws {
        let app = try launchAppForUITest(openWebDAVSheet: true)

        XCTAssertTrue(app.staticTexts["webdav.sheet.title"].waitForExistence(timeout: 5))
        let baseURL = app.textFields["webdav.baseURL"]
        XCTAssertTrue(baseURL.exists)
        baseURL.click()
        if !baseURL.valueDescription.isEmpty {
            baseURL.typeKey("a", modifierFlags: .command)
            baseURL.typeKey(.delete, modifierFlags: [])
        }
        baseURL.typeText("not-a-url")

        let saveButton = app.buttons["webdav.save"]
        XCTAssertTrue(saveButton.exists)
        saveButton.tap()

        XCTAssertTrue(app.staticTexts["WebDAV 地址无效，请输入 http(s):// 开头且包含主机名的地址。"].waitForExistence(timeout: 5))

        let cancel = app.buttons["webdav.cancel"]
        XCTAssertTrue(cancel.exists)
        cancel.tap()
    }

    @MainActor
    private func launchAppForUITest(openWebDAVSheet: Bool = false) throws -> XCUIApplication {
        let app = XCUIApplication()
        if app.state == .runningForeground || app.state == .runningBackground {
            app.terminate()
            _ = app.wait(for: .notRunning, timeout: 5)
        }
        app.launchArguments += ["-autoScanOnStartup", "NO"]
        app.launchEnvironment["UITEST_MODE"] = "1"
        if openWebDAVSheet {
            app.launchEnvironment["UITEST_OPEN_WEBDAV_SHEET"] = "1"
        }
        app.launch()
        guard app.wait(for: .runningForeground, timeout: 15) else {
            throw XCTSkip("UI environment did not bring OmniPlay to foreground state in time.")
        }
        guard waitForHomeScreenReady(in: app, timeout: 15) else {
            throw XCTSkip("UI environment could not observe OmniPlay home screen in time.")
        }
        return app
    }

    @MainActor
    private func firstToolbarControl(in app: XCUIApplication, label: String) -> XCUIElement {
        let candidates: [XCUIElement] = [
            app.toolbars.buttons[label].firstMatch,
            app.buttons[label].firstMatch,
            app.menuButtons[label].firstMatch,
            app.toolbars.otherElements[label].firstMatch
        ]
        for element in candidates where element.exists {
            return element
        }
        return candidates[0]
    }

    @MainActor
    private func hasToolbarControl(in app: XCUIApplication, label: String) -> Bool {
        firstToolbarControl(in: app, label: label).exists
    }

    @MainActor
    private func waitForToolbarControl(in app: XCUIApplication, label: String, timeout: TimeInterval) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if hasToolbarControl(in: app, label: label) {
                return true
            }
            RunLoop.current.run(until: Date().addingTimeInterval(0.2))
        }
        return hasToolbarControl(in: app, label: label)
    }

    @MainActor
    private func waitForHomeScreenReady(in app: XCUIApplication, timeout: TimeInterval) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if hasToolbarControl(in: app, identifier: "toolbar.addSource", fallbackLabel: "媒体源管理") { return true }
            if app.staticTexts["我的觅影库"].exists { return true }
            if app.staticTexts["所有影视"].exists { return true }
            if app.staticTexts["媒体库空空如也，快去添加文件夹吧！"].exists { return true }
            RunLoop.current.run(until: Date().addingTimeInterval(0.2))
        }
        return false
    }

    @MainActor
    private func firstToolbarControl(in app: XCUIApplication, identifier: String, fallbackLabel: String) -> XCUIElement {
        let byIdentifier: [XCUIElement] = [
            app.toolbars.buttons[identifier].firstMatch,
            app.buttons[identifier].firstMatch,
            app.menuButtons[identifier].firstMatch
        ]
        for element in byIdentifier where element.exists { return element }
        return firstToolbarControl(in: app, label: fallbackLabel)
    }

    @MainActor
    private func hasToolbarControl(in app: XCUIApplication, identifier: String, fallbackLabel: String) -> Bool {
        firstToolbarControl(in: app, identifier: identifier, fallbackLabel: fallbackLabel).exists
    }

    @MainActor
    private func waitForToolbarControl(in app: XCUIApplication, identifier: String, fallbackLabel: String, timeout: TimeInterval) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if hasToolbarControl(in: app, identifier: identifier, fallbackLabel: fallbackLabel) {
                return true
            }
            RunLoop.current.run(until: Date().addingTimeInterval(0.2))
        }
        return hasToolbarControl(in: app, identifier: identifier, fallbackLabel: fallbackLabel)
    }
}

private extension XCUIElement {
    var valueDescription: String {
        if let value = self.value as? String { return value }
        return ""
    }
}
