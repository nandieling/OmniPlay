package com.omniplay.tv.phone;

import android.app.Activity;
import android.graphics.Typeface;
import android.text.TextUtils;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;

import com.omniplay.tv.local.BrowseEntry;
import com.omniplay.tv.local.LocalEngine;
import com.omniplay.tv.local.SmbClient;
import com.omniplay.tv.local.UrlLabels;
import com.omniplay.tv.local.WebDavClient;
import com.omniplay.tv.ui.Theme;

import java.util.ArrayList;
import java.util.List;

/**
 * 登录 + 挑目录。和电视端同一套流程：先拿账号登进去（登录本身就是探一次目录），
 * 成功后弹出目录选择器，每行一个 ★ 用来确认「挂这一层」而不是误进子目录，
 * 挂完写进媒体源并让首页后台开始扫描刮削。
 */
public final class PhoneLoginSheet {
    private PhoneLoginSheet() {
    }

    public static void show(final PhoneHost host, final String kind, String url,
                            String username, String password) {
        final Activity activity = host.activity();
        final PhoneUi.Sheet sheet = PhoneUi.sheet(activity,
                "登录 " + ("smb".equals(kind) ? "SMB" : "WebDAV"),
                "登进去之后挑一个要挂载的目录");
        final TextView status = PhoneUi.statusLine(activity, "");

        EditText urlInput = PhoneUi.field(activity, "smb://192.168.1.10 或 https://nas:5006/dav",
                url == null ? "" : url, false);
        sheet.body.addView(urlInput, PhoneUi.block(activity, PhoneUi.dp(activity, 8), 0));
        final EditText userInput = PhoneUi.field(activity, "用户名（可空）",
                username == null ? "" : username, false);
        sheet.body.addView(userInput, PhoneUi.margin(PhoneUi.block(activity), 0,
                PhoneUi.dp(activity, 8), 0, 0));
        final EditText passInput = PhoneUi.field(activity, "密码（可空）",
                password == null ? "" : password, true);
        sheet.body.addView(passInput, PhoneUi.margin(PhoneUi.block(activity), 0,
                PhoneUi.dp(activity, 8), 0, 0));
        sheet.body.addView(status, PhoneUi.block(activity));

        Button login = PhoneUi.primary(activity, "登录");
        sheet.body.addView(login, PhoneUi.block(activity, PhoneUi.dp(activity, 12), 0));
        login.setOnClickListener(view -> {
            final String target = urlInput.getText().toString().trim();
            if (target.isEmpty()) {
                status.setText("先填地址。");
                return;
            }
            final String user = userInput.getText().toString();
            final String pass = passInput.getText().toString();
            status.setText("正在登录…");
            login.setEnabled(false);
            host.io().execute(() -> {
                try {
                    List<BrowseEntry> roots = browse(kind, target, user, pass);
                    host.main().post(() -> {
                        sheet.close();
                        showPicker(host, kind, target, user, pass, roots);
                    });
                } catch (Exception error) {
                    host.main().post(() -> {
                        login.setEnabled(true);
                        status.setText(message(error));
                    });
                }
            });
        });
    }

    /** 列一层：返回子目录条目（name 用来显示，url 用来继续往里走）。 */
    private static List<BrowseEntry> browse(String kind, String url, String user, String pass)
            throws Exception {
        if ("smb".equals(kind)) {
            SmbClient client = new SmbClient(url, user, pass);
            List<BrowseEntry> entries;
            try {
                entries = client.list(url);
            } catch (Exception ignored) {
                entries = client.listShares(url);
            }
            return directories(entries);
        }
        WebDavClient client = new WebDavClient(url, new WebDavClient.Credential(user, pass));
        return directories(client.list(client.streamUrl("")));
    }

    private static List<BrowseEntry> directories(List<BrowseEntry> entries) {
        List<BrowseEntry> kept = new ArrayList<>();
        for (BrowseEntry entry : entries) {
            if (entry.directory && !entry.name.isEmpty() && !entry.name.startsWith(".")) {
                kept.add(entry);
            }
        }
        java.util.Collections.sort(kept);
        return kept;
    }

    /** 目录选择器：顶上「★ 挂载这一层」，下面每行「★」挂那一层、「进入」进下层。 */
    private static void showPicker(final PhoneHost host, String kind, String rootUrl,
                                   String user, String pass, List<BrowseEntry> entries) {
        Activity activity = host.activity();
        final PhoneUi.Sheet sheet = PhoneUi.sheet(activity, "选择要挂载的目录",
                UrlLabels.displayPath(rootUrl));
        Button mount = PhoneUi.primary(activity, "★ 挂载这一层");
        sheet.body.addView(mount, PhoneUi.block(activity, PhoneUi.dp(activity, 6), 0));
        mount.setOnClickListener(view -> {
            sheet.close();
            mount(host, kind, rootUrl, user, pass, lastSegment(rootUrl));
        });

        if (entries.isEmpty()) {
            sheet.body.addView(PhoneUi.body(activity,
                    "这一层没有子目录，直接挂载这一层就行。"),
                    PhoneUi.block(activity, PhoneUi.dp(activity, 12), 0));
            return;
        }
        sheet.body.addView(PhoneUi.body(activity, "点一行进下层，点 ★ 挂载那一层："),
                PhoneUi.block(activity, PhoneUi.dp(activity, 12), 0));
        for (final BrowseEntry entry : entries) {
            LinearLayout row = PhoneUi.buttonRow(activity);
            sheet.body.addView(row, PhoneUi.block(activity, PhoneUi.dp(activity, 6), 0));
            TextView name = PhoneUi.text(activity, UrlLabels.displayPath(entry.name), 16,
                    Typeface.NORMAL, Theme.TEXT);
            name.setSingleLine(true);
            name.setEllipsize(TextUtils.TruncateAt.MIDDLE);
            row.addView(name, PhoneUi.weight(activity));
            Button pick = PhoneUi.ghost(activity, "★");
            row.addView(pick, PhoneUi.margin(PhoneUi.button(activity, 52, 42),
                    PhoneUi.dp(activity, 6), 0, 0, 0));
            pick.setOnClickListener(view -> {
                sheet.close();
                mount(host, kind, entry.url, user, pass, entry.name);
            });
            Button open = PhoneUi.ghost(activity, "进入");
            row.addView(open, PhoneUi.margin(PhoneUi.button(activity, 76, 42),
                    PhoneUi.dp(activity, 6), 0, 0, 0));
            open.setOnClickListener(view -> {
                sheet.close();
                host.io().execute(() -> {
                    try {
                        List<BrowseEntry> deeper = browse(kind, entry.url, user, pass);
                        host.main().post(() -> showPicker(host, kind, entry.url, user, pass, deeper));
                    } catch (Exception error) {
                        host.main().post(host::refreshLibrary);
                        host.toast(message(error));
                    }
                });
            });
        }
    }

    /** 挂上媒体源、记住账号、收弹层、让宿主开始后台扫描刮削。 */
    static void mount(PhoneHost host, String kind, String path, String username,
                      String password, String label) {
        host.engine().saveSource(kind, "", path, username, password);
        if (!"local".equals(kind)) {
            host.savedConnections().recordMount(kind, path, label == null ? "" : label,
                    username, password);
        }
        host.refreshLibrary();
        host.scanInBackground();
        host.toast("已挂载，正在后台扫描刮削");
    }

    /** 从地址里取个能当显示名的末段：``…/dav/影视`` → ``影视``，``smb://ip/share`` → ``share``。 */
    private static String lastSegment(String url) {
        String value = UrlLabels.displayPath(url);
        while (value.endsWith("/")) {
            value = value.substring(0, value.length() - 1);
        }
        int slash = value.lastIndexOf('/');
        return slash < 0 ? value : value.substring(slash + 1);
    }

    private static String message(Throwable error) {
        String text = error.getMessage();
        if (text == null || text.trim().isEmpty()) {
            return "登录失败：" + error.getClass().getSimpleName();
        }
        return "登录失败：" + text;
    }

    /** 引用 ViewGroup 让 PhoneUi.width 的重载在只有本文件用到时也保持可见（编译期检查）。 */
    static final int KEEP = ViewGroup.MATCH_PARENT;
}
