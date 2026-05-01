import SwiftUI
import GRDB

struct SettingsView: View {
    @Environment(\.dismiss) var dismiss
    
    // 绑定全局持久化设置
    @AppStorage("keepLocalPosters") var keepLocalPosters = true
    @AppStorage("autoScanOnStartup") var autoScanOnStartup = true
    @AppStorage("enableFastTooltip") var enableFastTooltip = true
    @AppStorage("showMediaSourceRealPath") var showMediaSourceRealPath = true
    @AppStorage("removeWebDAVCredentialWhenRemovingSource") var removeWebDAVCredentialWhenRemovingSource = false
    @AppStorage("usePublicTMDBApi") var usePublicTMDBApi = true
    @AppStorage("tmdbApiKey") var tmdbApiKey = ""
    
    @AppStorage("appLanguage") var appLanguage = "zh-Hans"
    @AppStorage("defaultAudio") var defaultAudio = "auto"
    @AppStorage("defaultSub") var defaultSub = "chi"
    @AppStorage("playbackQualityMode") var playbackQualityMode = "balanced"
    
    @AppStorage("appTheme") var appTheme = ThemeType.appleLight.rawValue
    var theme: AppTheme { ThemeType(rawValue: appTheme)?.colors ?? ThemeType.crystal.colors }
    
    @ObservedObject var cacheManager = OfflineCacheManager.shared
    
    @State private var isValidatingAPI = false
    @State private var apiValidationMessage = ""
    @State private var apiValidationColor: Color = .primary
    
    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text("偏好设置").font(.title2.bold())
                Spacer()
                Button(action: { dismiss() }) {
                    Image(systemName: "xmark.circle.fill").font(.title2).foregroundColor(.secondary)
                }.buttonStyle(.plain)
            }.padding()
            Divider()
            
            Form {
                Section(header: Text("基础设置").font(.headline)) {
                    Toggle("启动时自动扫描并同步库", isOn: $autoScanOnStartup)
                    Toggle("删除文件夹时保留本地缓存海报", isOn: $keepLocalPosters)
                    Toggle("启用快速悬停提示 (Tooltip)", isOn: $enableFastTooltip)
                    Toggle("媒体源显示真实路径", isOn: $showMediaSourceRealPath)
                    Toggle("移除 WebDAV 源时同时删除保存的登录凭据", isOn: $removeWebDAVCredentialWhenRemovingSource)
                }
                
                Section(header: Text("离线缓存").font(.headline).padding(.top, 10)) {
                    HStack {
                        Text("保存位置")
                        Spacer()
                        
                        if let dir = cacheManager.cacheDirectory {
                            Text(dir.path)
                                .foregroundColor(.secondary)
                                .lineLimit(1)
                                .truncationMode(.middle)
                                .frame(maxWidth: 200, alignment: .trailing)
                        } else {
                            Text("未设置").foregroundColor(.red)
                        }
                        
                        Button("更改目录") {
                            cacheManager.selectCacheDirectory()
                        }
                        .buttonStyle(.bordered)
                        .tint(theme.accent)
                    }
                    Text("设置后，您可以在影视详情页将 NAS 视频缓存至此目录，方便离线观看。")
                        .font(.caption).foregroundColor(theme.textSecondary)
                }
                
                Section(header: Text("外观与主题").font(.headline).padding(.top, 10)) {
                    Picker("应用主题配色", selection: $appTheme) {
                        ForEach(ThemeType.allCases) { themeItem in
                            HStack {
                                Circle()
                                    .fill(themeItem.colors.accent)
                                    .frame(width: 12, height: 12)
                                Text(themeItem.displayName)
                            }.tag(themeItem.rawValue)
                        }
                    }
                }
                
                Section(header: Text("语言与播放偏好").font(.headline).padding(.top, 10)) {
                    Picker("软件与刮削语言", selection: $appLanguage) {
                        Text("简体中文").tag("zh-Hans")
                        Text("English").tag("en")
                    }.help("更改此项将改变 TMDB 刮削获取的海报和简介语言。")
                    
                    Picker("默认首选音轨", selection: $defaultAudio) {
                        Text("智能匹配 (制片国家语言)").tag("auto")
                        Text("中文").tag("chi")
                        Text("英语").tag("eng")
                        Text("日语").tag("jpn")
                    }
                    
                    Picker("默认首选字幕", selection: $defaultSub) {
                        Text("中文优先 (简/繁)").tag("chi")
                        Text("英语优先").tag("eng")
                        Text("关闭字幕").tag("no")
                    }

                    Picker("播放画质模式", selection: $playbackQualityMode) {
                        Text("流畅优先").tag("smooth")
                        Text("平衡 (推荐)").tag("balanced")
                        Text("画质优先").tag("quality")
                    }
                    .help("切换后新打开的播放窗口会生效。")
                }
                
                Section(header: Text("刮削服务 (TMDB)").font(.headline).padding(.top, 10)) {
                    Toggle("启用公共 TMDB API（未填写自定义 API 时使用）", isOn: $usePublicTMDBApi)
                        .help("自定义 API Key / v4 令牌不为空时，总是优先使用自定义 API。")
                    
                    HStack {
                        TextField("API Key / v4 令牌", text: $tmdbApiKey)
                            .textFieldStyle(.roundedBorder)
                        
                        Button(action: validateTMDBApi) {
                            if isValidatingAPI {
                                ProgressView().controlSize(.small).frame(width: 40)
                            } else {
                                Text("验证")
                            }
                        }
                        .disabled(currentTMDBApiKeyForValidation().isEmpty || isValidatingAPI)
                    }
                    Text(tmdbApiKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                         ? (usePublicTMDBApi ? "当前使用公共 TMDB API。" : "当前未启用 TMDB API，请填写自定义 API 或开启公共 API。")
                         : "当前优先使用自定义 TMDB API。")
                        .font(.caption)
                        .foregroundColor(theme.textSecondary)
                    
                    if !apiValidationMessage.isEmpty {
                        Text(apiValidationMessage)
                            .font(.caption)
                            .foregroundColor(apiValidationColor)
                            .padding(.top, 2)
                            .transition(.opacity)
                    }
                }
            }
            .padding().formStyle(.grouped)
        }
        .frame(width: 550, height: 600)
        .environment(\.locale, .init(identifier: appLanguage))
        .onChange(of: appLanguage) { oldValue, newValue in
            UserDefaults.standard.set([newValue], forKey: "AppleLanguages")
            UserDefaults.standard.synchronize()
        }
    }
    
    private func currentTMDBApiKeyForValidation() -> String {
        let trimmed = tmdbApiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmed.isEmpty { return trimmed }
        return usePublicTMDBApi ? TMDBAPIConfig.publicApiKey : ""
    }
    
    private func validateTMDBApi() {
        let key = currentTMDBApiKeyForValidation()
        guard !key.isEmpty else {
            apiValidationMessage = "请填写自定义 API 或开启公共 TMDB API。"
            apiValidationColor = .red
            return
        }
        isValidatingAPI = true
        apiValidationMessage = "正在连接 TMDB 服务器..."
        apiValidationColor = .secondary
        
        Task {
            let url = URL(string: "https://api.themoviedb.org/3/configuration")!
            var request = URLRequest(url: url)
            
            if key.count > 50 {
                request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            } else {
                request.url = URL(string: "https://api.themoviedb.org/3/configuration?api_key=\(key)")!
            }
            
            do {
                let (_, response) = try await URLSession.shared.data(for: request)
                await MainActor.run {
                    if let httpResponse = response as? HTTPURLResponse, httpResponse.statusCode == 200 {
                        apiValidationMessage = "✅ 验证成功！API 状态正常。"
                        apiValidationColor = .green
                    } else {
                        apiValidationMessage = "❌ 验证失败：密钥无效或受限。"
                        apiValidationColor = .red
                    }
                    isValidatingAPI = false
                }
            } catch {
                await MainActor.run {
                    apiValidationMessage = "❌ 网络错误：无法连接到 TMDB (\(error.localizedDescription))"
                    apiValidationColor = .red
                    isValidatingAPI = false
                }
            }
        }
    }
}
