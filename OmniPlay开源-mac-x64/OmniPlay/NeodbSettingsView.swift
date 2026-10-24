import SwiftUI

// 设置页里的「NeoDB 记录」区块：登录、偏好、同步与导入都在这一处。
struct NeodbSettingsSection: View {
    private enum LoginMode: String, CaseIterable, Identifiable {
        case password
        case token
        case oauth

        var id: String { rawValue }

        var displayName: String {
            switch self {
            case .password: return "账户密码"
            case .token: return "访问令牌"
            case .oauth: return "浏览器授权"
            }
        }
    }

    let theme: AppTheme

    @ObservedObject private var sync = NeodbSyncService.shared
    @State private var loginMode: LoginMode = .password
    @State private var identifier = ""
    @State private var password = ""
    @State private var tokenInput = ""
    @State private var oauthCode = ""
    @State private var isWorking = false
    @State private var message = ""
    @State private var messageIsError = false

    var body: some View {
        Section(header: Text("NeoDB 记录").font(.headline).padding(.top, 10)) {
            if sync.isLoggedIn {
                HStack(spacing: 8) {
                    Label("已登录 \(sync.accountLabel.isEmpty ? "NeoDB" : sync.accountLabel)", systemImage: "checkmark.circle.fill")
                        .foregroundColor(theme.accent)
                        .font(.callout)
                    Spacer(minLength: 0)
                    Button("退出登录") {
                        sync.logout()
                        message = "已退出登录。"
                        messageIsError = false
                    }
                    .buttonStyle(.bordered)
                }
            } else {
                Picker("登录方式", selection: $loginMode) {
                    ForEach(LoginMode.allCases) { mode in
                        Text(mode.displayName).tag(mode)
                    }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(width: 300)

                loginModeContent

                // 只在有内容时出现，不占额外行
                if !message.isEmpty {
                    Text(message)
                        .font(.caption)
                        .foregroundColor(messageIsError ? theme.dangerColor : theme.textSecondary)
                }
            }

            HStack(spacing: 10) {
                Text("标记可见性")
                Picker("", selection: Binding(
                    get: { sync.visibility },
                    set: { sync.visibility = $0 })) {
                    ForEach(NeodbVisibility.allCases) { visibility in
                        Text(visibility.displayName).tag(visibility)
                    }
                }
                .labelsHidden()
                .fixedSize()
                Spacer(minLength: 0)
            }

            Toggle("同步时把这条标记发到联邦宇宙", isOn: Binding(
                get: { sync.postsToFediverse },
                set: { sync.postsToFediverse = $0 }))

            Toggle("在详情页改完标记就马上同步", isOn: Binding(
                get: { sync.syncAfterEdit },
                set: { sync.syncAfterEdit = $0 }))

            Toggle("启动时自动同步一次", isOn: Binding(
                get: { sync.autoSyncOnLaunch },
                set: { sync.autoSyncOnLaunch = $0 }))

            HStack(spacing: 10) {
                Button(action: { run { await sync.syncNow() } }) {
                    HStack(spacing: 6) {
                        if sync.isSyncing { ProgressView().controlSize(.small) }
                        Text("立即同步")
                    }
                }
                .buttonStyle(.bordered)
                .tint(theme.accent)
                .disabled(sync.isSyncing || !sync.isLoggedIn)

                Button(action: { run { await sync.importFromNeodb() } }) {
                    HStack(spacing: 6) {
                        if sync.isImporting { ProgressView().controlSize(.small) }
                        Text("从 NeoDB 导入")
                    }
                }
                .buttonStyle(.bordered)
                .disabled(sync.isImporting || !sync.isLoggedIn)

                Text(sync.pendingCount > 0 ? "待同步 \(sync.pendingCount) 条" : "本地标记都是最新的")
                    .font(.caption)
                    .foregroundColor(sync.pendingCount > 0 ? theme.accent : theme.textSecondary)
                Spacer(minLength: 0)
            }

            ForEach(sync.statusLines.indices.reversed(), id: \.self) { index in
                Text(sync.statusLines[index])
                    .font(.caption.monospacedDigit())
                    .foregroundColor(theme.textSecondary)
            }

            if !sync.isLoggedIn {
                Text("未登录：详情页里写的标记、评分、评论会先存在本机，登录后再一起推上去。")
                    .font(.caption)
                    .foregroundColor(theme.warningColor)
            }
        }
    }

    // 三种登录都按「输入框一行、按钮一行」排。分组表单会把裸输入框顶到行尾，
    // 输入框统一用 NSTextField 版的 PlainSingleLineField，包一层 HStack 用 Spacer
    // 钉住左缘，才能与下方按钮的左侧对齐。
    @ViewBuilder
    private var loginModeContent: some View {
        switch loginMode {
        case .password:
            VStack(alignment: .leading, spacing: 6) {
                loginFieldLabel("邮箱或用户名")
                loginRow(PlainSingleLineField(text: $identifier))
                loginFieldLabel("密码")
                loginRow(PlainSingleLineField(text: $password, isSecure: true, onSubmit: submitPasswordLogin))
                loginButton(title: "登录", enabled: !identifier.isEmpty && !password.isEmpty) {
                    try await sync.loginWithPassword(identifier: identifier, password: password)
                }
            }

        case .token:
            VStack(alignment: .leading, spacing: 6) {
                loginRow(PlainSingleLineField(text: $tokenInput))
                loginButton(title: "使用令牌", enabled: !tokenInput.isEmpty) {
                    try await sync.loginWithToken(tokenInput)
                }
            }

        case .oauth:
            VStack(alignment: .leading, spacing: 6) {
                Button("打开授权页面") {
                    run {
                        do {
                            _ = try await sync.beginOAuth()
                            message = "在浏览器里点同意，然后把页面上的授权码填到下面。"
                            messageIsError = false
                        } catch {
                            fail(error)
                        }
                    }
                }
                .buttonStyle(.bordered)
                loginRow(PlainSingleLineField(text: $oauthCode))
                loginButton(title: "保存授权码", enabled: !oauthCode.isEmpty) {
                    try await sync.finishOAuth(code: oauthCode)
                }
            }
        }
    }

    private func loginRow(_ field: PlainSingleLineField) -> some View {
        HStack(spacing: 0) {
            field
                .frame(width: 320)
            Spacer(minLength: 0)
        }
    }

    private func loginFieldLabel(_ text: String) -> some View {
        Text(text)
            .font(.subheadline)
            .foregroundColor(theme.textSecondary)
    }

    private func submitPasswordLogin() {
        guard !identifier.isEmpty, !password.isEmpty else { return }
        submitLogin { try await sync.loginWithPassword(identifier: identifier, password: password) }
    }

    private func loginButton(title: String, enabled: Bool, action: @escaping () async throws -> Void) -> some View {
        Button(title) {
            submitLogin(action: action)
        }
        .buttonStyle(.bordered)
        .tint(theme.accent)
        .disabled(!enabled || isWorking)
    }

    private func submitLogin(action: @escaping () async throws -> Void) {
        run {
            do {
                try await action()
                message = ""
            } catch {
                fail(error)
            }
        }
    }

    private func run(_ work: @escaping @MainActor () async -> Void) {
        guard !isWorking else { return }
        isWorking = true
        Task { @MainActor in
            await work()
            isWorking = false
            sync.refresh()
        }
    }

    private func fail(_ error: Error) {
        message = error.localizedDescription
        messageIsError = true
    }
}
