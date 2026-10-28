import SwiftUI
import AppKit

/// 偏好设置顶部「使用教程」按钮打开的帮助文档，以 sheet 盖在偏好设置之上。
///
/// 顺序按真实使用路径排：先让媒体库跑起来，再说播放，最后讲 TMDB 反代网关。
/// 网关一节最细，因为「海报墙一片灰、刮削一直转」九成是 TMDB 直连不通，
/// 而这一步用户要自己动手建反代，光看设置里那一行输入框猜不出来。
struct UsageGuideSheet: View {
    @Environment(\.dismiss) private var dismiss
    let theme: AppTheme

    var body: some View {
        VStack(spacing: 0) {
            HStack(alignment: .top) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("使用教程")
                        .font(.title2.bold())
                        .foregroundColor(theme.textPrimary)
                    Text("添加媒体源、播放，以及 TMDB 反代网关怎么搭")
                        .font(.subheadline)
                        .foregroundColor(theme.textSecondary)
                }
                Spacer()
                Button(action: { dismiss() }) {
                    Image(systemName: "xmark.circle.fill")
                        .font(.title2)
                        .foregroundColor(theme.textSecondary)
                }
                .buttonStyle(.plain)
                .accessibilityIdentifier("usageGuide.close")
            }
            .padding()

            Divider()

            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    quickStartPanel
                    playbackPanel
                    gatewayPanel
                    workerPanel
                    verifyPanel
                    faqPanel
                    otherPanel
                }
                .padding(20)
            }
            .background(theme.backdropLayer)
        }
        .frame(width: 760, height: 720)
        .background(theme.backdropLayer)
        .preferredColorScheme(theme.preferredScheme)
    }

    // MARK: - 各段内容

    private var quickStartPanel: some View {
        GuidePanel(title: "三分钟跑起来", theme: theme) {
            GuideStep(index: 1, title: "添加媒体源", theme: theme) {
                Text("点工具栏的加号文件夹图标，选本地文件夹、SMB 共享或 WebDAV 媒体源。加完立刻扫描入库，海报墙就会出现条目。")
                GuideInlineLinkRow(label: "本地 / SMB / WebDAV 都能同时挂多个源，随时在同一个入口里停用或移除。", theme: theme)
            }
            GuideStep(index: 2, title: "自动刮削", theme: theme) {
                Text("入库后自动去 TMDB 取标题、海报、简介与分集剧照。海报都显示出来了就说明刮削通了；一片灰多半是网关没配，看下面第三节。")
            }
            GuideStep(index: 3, title: "开始播放", theme: theme) {
                Text("点海报进详情页，选具体文件播放。也可以把文件直接拖到海报墙上、用文件菜单「打开视频文件…」（⌘O），或在访达里右键「打开方式 → 觅影」。")
            }
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Image(systemName: "arrow.triangle.2.circlepath")
                    .foregroundColor(theme.accent)
                Text("工具栏的循环箭头随时重新扫描目录并刷新刮削结果；iCloud 图标切到离线缓存编辑模式，把常用条目提前拉到本地。")
                    .foregroundColor(theme.textSecondary)
            }
            .font(.callout)
            .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var playbackPanel: some View {
        GuidePanel(title: "播放与关闭", theme: theme) {
            GuideBulletRow(theme: theme, text: "播放走独立全屏窗口，退出后回到你离开时的位置；关掉播放页直接回到那条片子的详情页。")
            GuideBulletRow(theme: theme, text: "进度自动记录，播完按「已看」写入，可被豆瓣或 NeoDB 当作观看历史收割。")
            GuideBulletRow(theme: theme, text: "支持 mp4、mkv、mov、avi、rmvb、flv、webm、m2ts、ts、m4v、wmv 与蓝光 ISO 镜像。")
            GuideBulletRow(theme: theme, text: "同一时间再次打开文件会在当前播放窗口换片，不会开出一堆窗口。")
            GuideBulletRow(theme: theme, text: "设置里「视频文件默认打开方式 → 设为默认」一键把常用视频格式关联到觅影，之后双击即播。")
        }
    }

    private var gatewayPanel: some View {
        GuidePanel(title: "TMDB 反代网关（重点）", theme: theme) {
            Text("api.themoviedb.org 与 image.tmdb.org 在国内直连经常超时甚至不通，表现就是刮削卡住、海报墙一片灰。觅影支持把 TMDB 的全部请求改道到你自己的一张网关上，网关用免费的 Cloudflare Worker 就能撑起，不需要服务器。")
                .fixedSize(horizontal: false, vertical: true)

            GuideKeyValueRow(
                label: "在哪填",
                value: "偏好设置 → 刮削服务 (TMDB) → TMDB 代理网关地址",
                theme: theme)
            GuideKeyValueRow(
                label: "填什么",
                value: "域名即可，例如 https://tmdb.myname.de5.net；也支持带路径前缀，例如 https://example.com/tmdb。末尾的 /3 觅影自己补，留空表示直连官方。",
                theme: theme)
            GuideKeyValueRow(
                label: "网关要提供两条路径",
                value: "/3/…  转发到 api.themoviedb.org（刮削、详情、演职员）；/t/p/…  转发到 image.tmdb.org（海报与剧照）。两者用同一个域名，路径结构原样保留。",
                theme: theme)
            GuideKeyValueRow(
                label: "API Key 仍是你自己的",
                value: "网关只做转发，不代替凭据。TMDB API 一栏照常填你在 themoviedb.org 申请的 Key，请求头原样透传即可。",
                theme: theme)

            Text("推荐方案（全程免费）")
                .font(.subheadline.weight(.semibold))
                .foregroundColor(theme.textPrimary)
                .padding(.top, 4)

            GuideStep(index: 1, title: "领一个免费域名", theme: theme) {
                Text("在 my.dnshe.com 注册账号，免费领取一个 de5.net 域名，例如 myname.de5.net。")
            }
            GuideStep(index: 2, title: "接入 Cloudflare 做 DNS 解析", theme: theme) {
                Text("在 Cloudflare 添加这个站点，拿到 Cloudflare 分配的两个 NS 地址，回 DNSHE 把域名的 DNS 服务器改成这两个。若这个免费域不支持改 NS，就保留 DNSHE 的解析，直接加一条 CNAME 指向你的 Worker 域名。")
            }
            GuideStep(index: 3, title: "建 Worker 反代 API 与图片服务器", theme: theme) {
                Text("Cloudflare 控制台 → Workers & Pages → Create Worker，粘贴下面这段脚本，再在「自定义域名」里绑定 tmdb.myname.de5.net。")
            }
            GuideStep(index: 4, title: "回到觅影填地址", theme: theme) {
                Text("把 https://tmdb.myname.de5.net 填进「TMDB 代理网关地址」，点「验证」，提示 TMDB 连接正常即成。之后重新扫描刮削，海报会一张张补上来。")
            }
        }
    }

    private var workerPanel: some View {
        GuidePanel(title: "Cloudflare Worker 脚本", subtitle: "按路径转发，API 与图片共用一个域名；海报、剧照能被 CDN 缓存", theme: theme) {
            GuideCodeBlock(code: UsageGuideSheet.workerScript, theme: theme)
            Text("绑定自定义域名后，Cloudflare 会自动为该域名签发证书，觅影里用 https 直接填即可。")
                .font(.caption)
                .foregroundColor(theme.textSecondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var verifyPanel: some View {
        GuidePanel(title: "配好了怎么自查", theme: theme) {
            GuideStep(index: 1, title: "API 通不通", theme: theme) {
                Text("浏览器打开 https://你的域名/3/configuration?api_key=你的Key，能返回一段 JSON 就通了。")
            }
            GuideStep(index: 2, title: "图片通不通", theme: theme) {
                Text("浏览器打开 https://你的域名/t/p/w500/某张海报路径，能显示出图片就通了。海报路径在觅影缓存目录里能看到，形如 /abc123.jpg。")
            }
            GuideStep(index: 3, title: "觅影里验证", theme: theme) {
                Text("偏好设置的 TMDB 一栏点「验证」，提示连接正常即可。回到首页点一次循环箭头重新刮削。")
            }
        }
    }

    private var faqPanel: some View {
        GuidePanel(title: "常见问题", theme: theme) {
            GuideFAQRow(question: "验证提示 401 / 403", answer: "API Key 不对，或者网关把 Authorization 请求头吞掉了。觅影用的是 v3 Key（api_key 参数）与 v4 Bearer 两种形式之一，反代脚本不要改请求头。", theme: theme)
            GuideFAQRow(question: "简介刮到了，只有海报不显示", answer: "Worker 没转发 /t/p/ 这条路径，或 Cloudflare 缓存了一次错误响应。补上图片转发、在 Cloudflare 清一次缓存即可。觅影在这种情况下会自动回落到官方图片地址重试。", theme: theme)
            GuideFAQRow(question: "提示域名解析失败", answer: "NS 记录还没生效（通常几分钟，偶尔几小时），或者域名在 Cloudflare 那边没开启代理（橙色云朵）。", theme: theme)
            GuideFAQRow(question: "免费额度够吗", answer: "Workers 免费档每天 10 万次请求。首次刮削一千条库大约几千次 API 调用加一万次图片请求，海报之后由本地缓存与 CDN 承担，日常几乎不再产生请求。", theme: theme)
            GuideFAQRow(question: "不想用 Cloudflare", answer: "任何反代都行：nginx、Caddy、群晖、Nas 上的容器都可以，只要保持上面那两条路径结构不变。", theme: theme)
            GuideFAQRow(question: "完全不配网关能用吗", answer: "能。网络直连 TMDB 没问题就留空，觅影直接走官方地址；豆瓣绑定、红果短剧、本地 NFO 刮削都不依赖 TMDB。", theme: theme)
        }
    }

    private var otherPanel: some View {
        GuidePanel(title: "还有这些", theme: theme) {
            GuideBulletRow(theme: theme, text: "详情页可绑定豆瓣条目，同步观看状态与标记；NeoDB 同理，在偏好设置里单独登录。")
            GuideBulletRow(theme: theme, text: "「本地刮削文件」打开后，读取目录里已有的 NFO、海报与剧照优先于在线刮削，也能把觅影整理的结果导出回去。")
            GuideBulletRow(theme: theme, text: "「离线缓存」把选中的条目连同海报、剧照拉到本地或 iCloud，断网也能翻库看简介。")
            GuideBulletRow(theme: theme, text: "主题与语言在偏好设置里换，液态玻璃主题的底色会跟着海报走。")
        }
    }

    /// 路径转发版：API 与图片共用一个 Worker，海报交给 CDN 缓存一天。
    static let workerScript = """
    export default {
      async fetch(request) {
        const url = new URL(request.url)
        // /t/p/ 开头是海报与剧照，其余都当作 TMDB API
        const upstream = url.pathname.startsWith('/t/p/')
          ? 'image.tmdb.org'
          : 'api.themoviedb.org'

        const target = new URL(url.pathname + url.search, 'https://' + upstream)
        const headers = new Headers(request.headers)
        headers.set('host', upstream)
        headers.delete('accept-encoding')

        // 海报是公开且不变的内容，让 CDN 缓存一天，重复打开首页不再回源
        const init = { method: request.method, headers, body: request.body, redirect: 'follow' }
        const response = await fetch(new Request(target, init))
        if (upstream === 'image.tmdb.org') {
          const cached = new Response(response.body, response)
          cached.headers.set('cache-control', 'public, max-age=86400')
          return cached
        }
        return response
      }
    }
    """
}

// MARK: - 排版件

private struct GuidePanel<Content: View>: View {
    let title: String
    var subtitle: String?
    let theme: AppTheme
    @ViewBuilder var content: Content

    init(title: String, subtitle: String? = nil, theme: AppTheme, @ViewBuilder content: () -> Content) {
        self.title = title
        self.subtitle = subtitle
        self.theme = theme
        self.content = content()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title)
                .font(.headline)
                .foregroundColor(theme.textPrimary)
            if let subtitle {
                Text(subtitle)
                    .font(.caption)
                    .foregroundColor(theme.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            content
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: 12, style: .continuous).fill(theme.surface)
        )
        .overlay(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .stroke(theme.accent.opacity(0.12), lineWidth: 1)
        )
    }
}

private struct GuideStep<Content: View>: View {
    let index: Int
    let title: String
    let theme: AppTheme
    @ViewBuilder var content: Content

    init(index: Int, title: String, theme: AppTheme, @ViewBuilder content: () -> Content) {
        self.index = index
        self.title = title
        self.theme = theme
        self.content = content()
    }

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Text("\(index)")
                .font(.caption.bold())
                .foregroundColor(theme.accent)
                .frame(width: 20, height: 20)
                .background(Circle().fill(theme.accent.opacity(0.14)))
            VStack(alignment: .leading, spacing: 4) {
                Text(title)
                    .font(.subheadline.weight(.semibold))
                    .foregroundColor(theme.textPrimary)
                content
                    .font(.callout)
                    .foregroundColor(theme.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

private struct GuideBulletRow: View {
    let theme: AppTheme
    let text: String

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Image(systemName: "circle.fill")
                .font(.system(size: 5))
                .foregroundColor(theme.textSecondary.opacity(0.6))
            Text(text)
                .font(.callout)
                .foregroundColor(theme.textSecondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }
}

private struct GuideKeyValueRow: View {
    let label: String
    let value: String
    let theme: AppTheme

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(label)
                .font(.callout.weight(.semibold))
                .foregroundColor(theme.textPrimary)
            Text(value)
                .font(.callout)
                .foregroundColor(theme.textSecondary)
                .textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(.leading, 10)
        .overlay(alignment: .leading) {
            Rectangle()
                .fill(theme.accent.opacity(0.35))
                .frame(width: 3)
                .padding(.vertical, 2)
        }
    }
}

private struct GuideInlineLinkRow: View {
    let label: String
    let theme: AppTheme

    var body: some View {
        Text(label)
            .font(.caption)
            .foregroundColor(theme.textSecondary)
            .fixedSize(horizontal: false, vertical: true)
    }
}

private struct GuideFAQRow: View {
    let question: String
    let answer: String
    let theme: AppTheme

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(question)
                .font(.callout.weight(.semibold))
                .foregroundColor(theme.textPrimary)
            Text(answer)
                .font(.callout)
                .foregroundColor(theme.textSecondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(.bottom, 2)
    }
}

private struct GuideCodeBlock: View {
    let code: String
    let theme: AppTheme
    @State private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(code)
                .font(.system(size: 11, design: .monospaced))
                .foregroundColor(theme.textPrimary)
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)

            HStack(spacing: 10) {
                Button(action: copy) {
                    Label(copied ? "已复制" : "复制脚本", systemImage: copied ? "checkmark" : "doc.on.doc")
                        .font(.caption)
                }
                .buttonStyle(.bordered)
                .tint(theme.accent)

                Link(destination: URL(string: "https://developers.cloudflare.com/workers/")!) {
                    Label("Workers 文档", systemImage: "arrow.up.right")
                        .font(.caption)
                }
                .foregroundColor(theme.accent)
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: 10, style: .continuous).fill(theme.subtleFill)
        )
    }

    private func copy() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(code, forType: .string)
        copied = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.6) { copied = false }
    }
}
