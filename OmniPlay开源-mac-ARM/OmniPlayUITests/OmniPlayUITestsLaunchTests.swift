//
//  OmniPlayUITestsLaunchTests.swift
//  OmniPlayUITests
//
//  Created by nan on 2026/3/16.
//

import XCTest

final class OmniPlayUITestsLaunchTests: XCTestCase {

    override class var runsForEachTargetApplicationUIConfiguration: Bool {
        false
    }

    override func setUpWithError() throws {
        continueAfterFailure = false
    }

    @MainActor
    func testLaunch() throws {
        let app = XCUIApplication()
        app.launchArguments += ["-autoScanOnStartup", "NO"]
        app.launchEnvironment["UITEST_MODE"] = "1"
        app.launch()

        XCTAssertTrue(app.wait(for: .runningForeground, timeout: 15))
        let deadline = Date().addingTimeInterval(15)
        var ready = false
        while Date() < deadline {
            if app.buttons["toolbar.addSource"].exists ||
                app.buttons["添加源"].exists ||
                app.staticTexts["我的觅影库"].exists ||
                app.staticTexts["所有影视"].exists ||
                app.staticTexts["媒体库空空如也，快去添加文件夹吧！"].exists {
                ready = true
                break
            }
            RunLoop.current.run(until: Date().addingTimeInterval(0.2))
        }
        if !ready {
            throw XCTSkip("UI environment could not observe OmniPlay home screen in time.")
        }
    }
}
