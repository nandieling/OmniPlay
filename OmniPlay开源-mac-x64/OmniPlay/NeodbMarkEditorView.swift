import SwiftUI
import GRDB

// 详情页 NeoDB 弹窗里的编辑区：标记、评分、评论一次写完。
//
// 没登录也能用——记录先留在本地 `mediaMark` 表里，登录后在设置页一键同步即可。
struct NeodbMarkEditorView: View {
    let localId: Int64

    @ObservedObject private var sync = NeodbSyncService.shared
    @AppStorage(AppThemeResolver.colorKey) var appTheme = ThemeType.appleLight.rawValue
    @AppStorage(AppThemeResolver.styleKey) var appThemeStyle = ThemeStyle.classic.rawValue
    var theme: AppTheme { AppThemeResolver.theme(colorRaw: appTheme, styleRaw: appThemeStyle) }

    @State private var shelfType: NeodbShelfType = .none
    @State private var stars: Double = 0
    @State private var comment: String = ""
    @State private var hasLocalRow = false
    @State private var hasPendingLocalChange = false
    @State private var isWorking = false
    @State private var notice: String = ""
    @State private var syncedAt: Double? = nil
    @State private var lastError: String = ""
    @State private var remotePage: URL? = nil
    @State private var openingRemotePage = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Image(systemName: "square.and.arrow.up.on.square")
                    .foregroundColor(theme.accent)
                Text("NeoDB 标记与评分")
                    .font(.subheadline.weight(.semibold))
                    .foregroundColor(theme.textPrimary)
                Spacer()
                if sync.isLoggedIn {
                    Text(sync.accountLabel.isEmpty ? "已登录" : sync.accountLabel)
                        .font(.caption)
                        .foregroundColor(theme.textSecondary)
                        .lineLimit(1)
                } else {
                    Text("未登录 NeoDB，记录先留在本地")
                        .font(.caption)
                        .foregroundColor(theme.textSecondary)
                }
            }

            HStack(spacing: 10) {
                Picker("标记", selection: $shelfType) {
                    ForEach(NeodbShelfType.allCases) { shelf in
                        Text(shelf.displayName).tag(shelf)
                    }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(maxWidth: 380)
                .onChange(of: shelfType) { _, _ in markDirty() }

                // 状态条右边：直接打开这条记录在 NeoDB 上的条目页
                Button(action: openRemotePage) {
                    if openingRemotePage {
                        ProgressView().controlSize(.small)
                            .frame(width: 76, height: 20)
                    } else {
                        HStack(spacing: 5) {
                            Image(systemName: "safari")
                                .font(.callout)
                            Text("NeoDB网页")
                                .font(.callout.weight(.medium))
                        }
                        .foregroundColor(theme.textPrimary)
                        .padding(.horizontal, 12)
                        .padding(.vertical, 6)
                        .background(Capsule().fill(theme.chipFill))
                        .themeGlassPlate(theme, cornerRadius: 999)
                        .overlay(Capsule().stroke(theme.textSecondary.opacity(0.2), lineWidth: 1))
                        .contentShape(Capsule())
                    }
                }
                .buttonStyle(.plain)
                .opacity(canOpenRemotePage ? 1 : 0.45)
                .disabled(!canOpenRemotePage || openingRemotePage)
                .help(canOpenRemotePage
                      ? "在浏览器里打开这条记录对应的 NeoDB 页面"
                      : "这条记录还没同步到 NeoDB，暂时没有网页可打开")
                Spacer(minLength: 0)
            }

            HStack(spacing: 6) {
                Text("评分")
                    .font(.subheadline)
                    .foregroundColor(theme.textSecondary)
                    .frame(width: 34, alignment: .leading)
                HStack(spacing: 3) {
                    ForEach(0..<5, id: \.self) { index in
                        starButton(index: index)
                    }
                }
                if stars > 0 {
                    Text(NeodbRating.displayGrade(NeodbRating.grade(fromStars: stars)) ?? "")
                        .font(.caption.monospacedDigit())
                        .foregroundColor(theme.textSecondary)
                    Button("清除") {
                        stars = 0
                        markDirty()
                    }
                    .buttonStyle(.plain)
                    .font(.caption)
                    .foregroundColor(theme.accent)
                }
                Spacer()
            }

            TextField("看完想说两句（会作为评论同步）", text: $comment, axis: .vertical)
                .textFieldStyle(.roundedBorder)
                .lineLimit(8...14)
                .font(.subheadline)
                .frame(maxWidth: 620, alignment: .leading)
                .onChange(of: comment) { _, _ in markDirty() }

            HStack(spacing: 10) {
                Button(action: save) {
                    if isWorking {
                        HStack(spacing: 6) {
                            ProgressView().controlSize(.small)
                            Text("保存中")
                        }
                    } else {
                        Text(hasLocalRow ? "更新记录" : "保存记录")
                    }
                }
                .buttonStyle(.bordered)
                .tint(theme.accent)
                .disabled(isWorking)

                Text(footerText)
                    .font(.caption)
                    .foregroundColor(lastError.isEmpty ? theme.textSecondary : .orange)
                    .lineLimit(2)
                Spacer(minLength: 0)
            }

            if !notice.isEmpty {
                Text(notice)
                    .font(.caption)
                    .foregroundColor(theme.textSecondary)
            }
        }
        .padding(14)
        .themeSurface(theme, cornerRadius: 10)
        .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).stroke(theme.textSecondary.opacity(0.18), lineWidth: 0.8))
        .onAppear(perform: load)
        .onReceive(NotificationCenter.default.publisher(for: .libraryUpdated)) { _ in
            load()
        }
    }

    private var footerText: String {
        if !lastError.isEmpty { return lastError }
        if hasPendingLocalChange { return "有改动还没同步" }
        if let syncedAt {
            let formatter = DateFormatter()
            formatter.dateFormat = "MM-dd HH:mm"
            return "上次同步 \(formatter.string(from: Date(timeIntervalSince1970: syncedAt)))"
        }
        return "这条记录还没同步过"
    }

    // 一颗星分左半 / 右半两个点击区：NeoDB 的评分是 1…10，半格刚好对上。
    private func starButton(index: Int) -> some View {
        let full = Double(index) + 1
        let symbol = stars >= full ? "star.fill" : (stars >= full - 0.5 ? "star.leadinghalf.filled" : "star")
        return ZStack {
            Image(systemName: symbol)
                .font(.system(size: 16))
                .foregroundColor(stars > 0 ? Color(hex: "FFAC00") : theme.textSecondary.opacity(0.55))
            HStack(spacing: 0) {
                Button(action: { setStars(full - 0.5) }) {
                    Rectangle().fill(.clear).frame(width: 9, height: 20).contentShape(Rectangle())
                }
                    .buttonStyle(.plain)
                    .help("评 \(String(format: "%.1f", full - 0.5)) 星")
                Button(action: { setStars(full) }) {
                    Rectangle().fill(.clear).frame(width: 9, height: 20).contentShape(Rectangle())
                }
                    .buttonStyle(.plain)
                    .help("评 \(String(format: "%.1f", full)) 星")
            }
        }
        .frame(width: 18, height: 20)
    }

    private func setStars(_ value: Double) {
        // 再点一次当前档位 = 取消评分。
        stars = (stars == value) ? max(0, value - 0.5) : value
        markDirty()
    }

    private func markDirty() {
        hasPendingLocalChange = true
        notice = ""
    }

    /// 有存过的链接、或者已经登录能现查，才给点。
    private var canOpenRemotePage: Bool { remotePage != nil || sync.isLoggedIn }

    /// 打开这条记录在 NeoDB 上的条目页。本地存过链接就直接开，否则现查一次并记下来。
    private func openRemotePage() {
        if let remotePage {
            NSWorkspace.shared.open(remotePage)
            return
        }
        guard canOpenRemotePage else { return }
        openingRemotePage = true
        Task {
            let url = await sync.remotePageURL(forLocalId: localId)
            await MainActor.run {
                openingRemotePage = false
                if let url {
                    remotePage = url
                    NSWorkspace.shared.open(url)
                } else {
                    notice = "还没找到这条记录在 NeoDB 上的页面，先在设置里同步一次试试。"
                }
            }
        }
    }

    private func load() {
        guard let dbQueue = AppDatabase.shared.dbQueue else { return }
        let record = try? dbQueue.read { db in
            try MediaMarkStore.editorState(db: db, localId: localId)
        }
        guard let record else { return }
        shelfType = record.shelfType
        stars = NeodbRating.stars(fromGrade: record.ratingGrade)
        comment = record.comment
        hasLocalRow = record.updatedAt > 0
        syncedAt = record.syncedAt
        lastError = record.lastError ?? ""
        hasPendingLocalChange = record.hasPendingLocalChange
        remotePage = record.remoteUrl.flatMap(URL.init(string:))
    }

    private func save() {
        guard let dbQueue = AppDatabase.shared.dbQueue else {
            notice = "媒体库还没准备好。"
            return
        }
        isWorking = true
        let chosenShelf = shelfType
        let grade = NeodbRating.grade(fromStars: stars)
        let text = comment
        Task {
            do {
                try await dbQueue.write { db in
                    try MediaMarkStore.save(db: db, localId: localId,
                                            shelfType: chosenShelf, ratingGrade: grade,
                                            comment: text, isManual: true)
                }
            } catch {
                notice = "本地保存失败：\(error.localizedDescription)"
            }
            let outcome = await sync.syncAfterEditIfNeeded()
            await MainActor.run {
                isWorking = false
                load()
                switch outcome {
                case .pushed(let summary):
                    notice = summary
                case .notPushed(let reason):
                    notice = reason ?? "已存在本地，登录 NeoDB 后在设置页点「立即同步」即可上传。"
                }
            }
        }
    }
}

// 详情页片名右边的按钮：显示当前标记，点一下打开 NeoDB 弹窗。
struct NeodbMarkStatusButton: View {
    let localId: Int64
    let theme: AppTheme
    let action: () -> Void

    @ObservedObject private var sync = NeodbSyncService.shared
    @State private var shelfType: NeodbShelfType = .none
    @State private var hasPendingLocalChange = false

    var body: some View {
        Button(action: action) {
            // 按钮名固定叫「记录」，标记到哪一层只在提示里说，播完不改按钮名
            Text("记录")
                .font(.title3.bold())
                .lineLimit(1)
                .minimumScaleFactor(0.86)
                .foregroundColor(theme.textPrimary)
                .padding(.horizontal, 20)
                .padding(.vertical, 12)
                .background(Capsule().fill(theme.chipFill))
                .themeGlassPlate(theme, cornerRadius: 999)
                .overlay(Capsule().stroke(theme.textSecondary.opacity(0.2), lineWidth: 1))
                .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .help(shelfHintText)
        .onAppear(perform: refresh)
        .onReceive(NotificationCenter.default.publisher(for: .libraryUpdated)) { _ in refresh() }
        .onChange(of: sync.pendingCount) { _, _ in refresh() }
    }

    private var shelfHintText: String {
        var text = "NeoDB 标记、评分与评论"
        if shelfType != .none { text += "（当前：\(shelfType.displayName)）" }
        if hasPendingLocalChange { text += "，有改动还没同步" }
        return text
    }

    private func refresh() {
        guard let dbQueue = AppDatabase.shared.dbQueue else { return }
        guard let record = try? dbQueue.read({ db in
            try MediaMarkStore.editorState(db: db, localId: localId)
        }) else { return }
        shelfType = record.shelfType
        hasPendingLocalChange = record.hasPendingLocalChange
    }
}

// 点片名右边按钮后的弹窗。
struct NeodbMarkSheet: View {
    let localId: Int64
    let title: String

    @Environment(\.dismiss) private var dismiss
    @AppStorage(AppThemeResolver.colorKey) var appTheme = ThemeType.appleLight.rawValue
    @AppStorage(AppThemeResolver.styleKey) var appThemeStyle = ThemeStyle.classic.rawValue
    var theme: AppTheme { AppThemeResolver.theme(colorRaw: appTheme, styleRaw: appThemeStyle) }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 10) {
                Text(title)
                    .font(.headline)
                    .foregroundColor(theme.textPrimary)
                    .lineLimit(1)
                Spacer(minLength: 12)
                // 「完成」做成与详情页已播/未播同款的胶囊按钮
                Button(action: { dismiss() }) {
                    Text("完成")
                        .font(.title3.bold())
                        .lineLimit(1)
                        .foregroundColor(theme.textPrimary)
                        .padding(.horizontal, 20)
                        .padding(.vertical, 10)
                        .background(Capsule().fill(theme.chipFill))
                        .themeGlassPlate(theme, cornerRadius: 999)
                        .overlay(Capsule().stroke(theme.textSecondary.opacity(0.2), lineWidth: 1))
                        .contentShape(Capsule())
                }
                .buttonStyle(.plain)
                .keyboardShortcut(.defaultAction)
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 12)

            Divider()

            NeodbMarkEditorView(localId: localId)
                .padding(16)

        }
        .frame(width: 620)
        .background(theme.backdropLayer)
    }
}
