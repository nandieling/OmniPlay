package com.omniplay.tv.phone;

import android.app.Activity;
import android.graphics.Typeface;
import android.text.TextUtils;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;

import com.omniplay.tv.local.LanDiscovery;
import com.omniplay.tv.local.LocalEngine;
import com.omniplay.tv.local.SavedConnections;
import com.omniplay.tv.local.SmbClient;
import com.omniplay.tv.local.UrlLabels;
import com.omniplay.tv.local.WebDavClient;
import com.omniplay.tv.ui.Theme;

import java.io.File;
import java.util.ArrayList;
import java.util.List;

/**
 * 媒体源抽屉：已挂载的源（停用 / 重命名 / 移除）、局域网自动发现的设备、上次登录过的服务器，
 * 以及三个添加入口。挂载流程与电视端一致——先登录，再挑要挂的目录，挂完后台扫描刮削，
 * 进度只写在首页顶部，抽屉里不再放第二个进度条。
 */
public final class PhoneSourceSheet {
    private PhoneSourceSheet() {
    }

    public static void show(final PhoneHost host) {
        final Activity activity = host.activity();
        final PhoneUi.Sheet sheet = PhoneUi.sheet(activity, "媒体源",
                "挂载后自动扫描刮削，进度写在首页顶部");
        final LinearLayout body = sheet.body;
        final List<LanDiscovery.LanDevice> devices = new ArrayList<>();
        final TextView lanStatus = PhoneUi.statusLine(activity, "正在扫描局域网…");
        final int[] token = {0};

        body.addView(PhoneUi.section(activity, "已挂载"));
        final LinearLayout mounted = new LinearLayout(activity);
        mounted.setOrientation(LinearLayout.VERTICAL);
        body.addView(mounted, PhoneUi.block(activity));

        body.addView(PhoneUi.section(activity, "局域网"));
        body.addView(lanStatus, PhoneUi.block(activity));
        final LinearLayout lan = new LinearLayout(activity);
        lan.setOrientation(LinearLayout.VERTICAL);
        body.addView(lan, PhoneUi.block(activity));

        body.addView(PhoneUi.section(activity, "上次登录过"));
        final LinearLayout saved = new LinearLayout(activity);
        saved.setOrientation(LinearLayout.VERTICAL);
        body.addView(saved, PhoneUi.block(activity));

        body.addView(PhoneUi.section(activity, "添加媒体源"));
        LinearLayout addRow = PhoneUi.buttonRow(activity);
        body.addView(addRow, PhoneUi.block(activity));
        addRow.addView(addButton(activity, "WebDAV", ignored -> {
            sheet.close();
            PhoneLoginSheet.show(host, "webdav", null, null, null);
        }), PhoneUi.width(0, PhoneUi.dp(activity, 50), 1f));
        addRow.addView(addButton(activity, "SMB", ignored -> {
            sheet.close();
            PhoneLoginSheet.show(host, "smb", null, null, null);
        }), PhoneUi.margin(PhoneUi.width(0, PhoneUi.dp(activity, 50), 1f), PhoneUi.dp(activity, 8), 0, 0, 0));
        addRow.addView(addButton(activity, "本地目录", ignored -> {
            sheet.close();
            pickLocalDirectory(host, LocalEngine.defaultLocalDirectory().getAbsolutePath(), null);
        }), PhoneUi.margin(PhoneUi.width(0, PhoneUi.dp(activity, 50), 1f), PhoneUi.dp(activity, 8), 0, 0, 0));

        renderMounted(host, mounted);
        renderSaved(host, saved);

        sheet.onDismiss(() -> LanDiscovery.cancel(token[0]));
        LocalEngine engine = host.engine();
        token[0] = LanDiscovery.start(activity, host.io(), new LanDiscovery.Listener() {
            @Override
            public void onDevice(LanDiscovery.LanDevice device) {
                host.main().post(() -> {
                    if (!contains(devices, device)) {
                        devices.add(device);
                    }
                    renderDevices(host, lan, devices);
                });
            }

            @Override
            public void onDone(List<LanDiscovery.LanDevice> found) {
                host.main().post(() -> {
                    devices.clear();
                    devices.addAll(found);
                    renderDevices(host, lan, devices);
                    lanStatus.setText(found.isEmpty()
                            ? "没找到设备。手动填地址也可以：用上面的「WebDAV」「SMB」按钮。"
                            : "找到 " + found.size() + " 台，点一台登录。");
                });
            }
        });
    }

    private static boolean contains(List<LanDiscovery.LanDevice> list, LanDiscovery.LanDevice device) {
        for (LanDiscovery.LanDevice existing : list) {
            if (existing.url.equals(device.url)) {
                return true;
            }
        }
        return false;
    }

    private static Button addButton(Activity activity, String label, android.view.View.OnClickListener onClick) {
        Button button = PhoneUi.primary(activity, label);
        button.setOnClickListener(onClick);
        return button;
    }

    // ---- 已挂载 ------------------------------------------------------------

    private static void renderMounted(PhoneHost host, LinearLayout container) {
        Activity activity = host.activity();
        container.removeAllViews();
        List<LocalEngine.Source> sources = host.engine().sources();
        if (sources.isEmpty()) {
            container.addView(PhoneUi.body(activity,
                    "还没有挂载任何目录。用下面的按钮添加，或从局域网列表里挑一台。"));
            return;
        }
        for (LocalEngine.Source source : sources) {
            LinearLayout card = PhoneUi.card(activity, PhoneUi.block(activity, 0, PhoneUi.dp(activity, 10)));

            LinearLayout head = PhoneUi.buttonRow(activity);
            card.addView(head, PhoneUi.block(activity));
            TextView name = PhoneUi.text(activity, source.name, 17, Typeface.BOLD,
                    source.enabled ? Theme.TEXT : Theme.SUBTLE);
            name.setSingleLine(true);
            name.setEllipsize(TextUtils.TruncateAt.END);
            head.addView(name, PhoneUi.weight(activity));
            TextView kind = PhoneUi.text(activity, kindLabel(source.kind), 12, Typeface.BOLD, Theme.ACCENT);
            head.addView(kind, PhoneUi.width(ViewGroup.LayoutParams.WRAP_CONTENT,
                    ViewGroup.LayoutParams.WRAP_CONTENT));

            card.addView(PhoneUi.body(activity, describe(source)),
                    PhoneUi.block(activity, PhoneUi.dp(activity, 4), 0));

            LinearLayout actions = PhoneUi.buttonRow(activity);
            card.addView(actions, PhoneUi.block(activity, PhoneUi.dp(activity, 6), 0));
            Button toggle = PhoneUi.ghost(activity, source.enabled ? "停用" : "启用");
            toggle.setOnClickListener(view -> {
                host.engine().setSourceEnabled(source.id, !source.enabled);
                renderMounted(host, container);
                host.refreshLibrary();
                host.toast(source.enabled ? "已停用：" + source.name : "已启用：" + source.name);
            });
            actions.addView(toggle, PhoneUi.button(activity, 96, 42));
            Button rename = PhoneUi.ghost(activity, "重命名");
            actions.addView(rename, PhoneUi.margin(PhoneUi.button(activity, 96, 42),
                    PhoneUi.dp(activity, 8), 0, 0, 0));
            rename.setOnClickListener(view -> rename(host, source, container));
            Button remove = PhoneUi.ghost(activity, "移除");
            actions.addView(remove, PhoneUi.margin(PhoneUi.button(activity, 96, 42),
                    PhoneUi.dp(activity, 8), 0, 0, 0));
            remove.setOnClickListener(view -> {
                host.engine().removeSource(source.id);
                renderMounted(host, container);
                host.refreshLibrary();
                host.toast("已移除：" + source.name);
            });
        }
    }

    private static String kindLabel(String kind) {
        if ("local".equals(kind)) {
            return "本地";
        }
        return "smb".equals(kind) ? "SMB" : "WebDAV";
    }

    /** 目录那一行：WebDAV 存的是编码过的 URL，显示前先解回中文目录名。 */
    private static String describe(LocalEngine.Source source) {
        String path = "local".equals(source.kind) ? source.path : UrlLabels.displayPath(source.path);
        String account = source.username.isEmpty() ? "" : " · " + source.username;
        return path + account + (source.enabled ? "" : " · 已停用，海报已收起");
    }

    private static void rename(PhoneHost host, LocalEngine.Source source, LinearLayout container) {
        Activity activity = host.activity();
        final PhoneUi.Sheet sheet = PhoneUi.sheet(activity, "重命名", source.name);
        final EditText input = PhoneUi.field(activity, "显示名称", source.name, false);
        sheet.body.addView(input, PhoneUi.block(activity, PhoneUi.dp(activity, 8), 0));
        Button save = PhoneUi.primary(activity, "保存");
        sheet.body.addView(save, PhoneUi.block(activity, PhoneUi.dp(activity, 14), 0));
        save.setOnClickListener(view -> {
            String value = input.getText().toString().trim();
            if (!value.isEmpty()) {
                host.engine().renameSource(source.id, value);
            }
            sheet.close();
            renderMounted(host, container);
            host.refreshLibrary();
        });
    }

    // ---- 局域网 / 已保存 -----------------------------------------------------

    private static void renderDevices(PhoneHost host, LinearLayout container,
                                      List<LanDiscovery.LanDevice> devices) {
        Activity activity = host.activity();
        container.removeAllViews();
        if (devices.isEmpty()) {
            return;
        }
        for (LanDiscovery.LanDevice device : devices) {
            LinearLayout card = PhoneUi.card(activity, PhoneUi.block(activity, 0, PhoneUi.dp(activity, 10)));
            TextView label = PhoneUi.text(activity, device.label(), 16, Typeface.BOLD, Theme.TEXT);
            label.setSingleLine(true);
            label.setEllipsize(TextUtils.TruncateAt.END);
            card.addView(label, PhoneUi.block(activity));
            TextView url = PhoneUi.text(activity, UrlLabels.displayPath(device.url), 12,
                    Typeface.NORMAL, Theme.SUBTLE);
            url.setSingleLine(true);
            url.setEllipsize(TextUtils.TruncateAt.MIDDLE);
            card.addView(url, PhoneUi.block(activity, PhoneUi.dp(activity, 2), 0));
            Button login = PhoneUi.primary(activity, "登录");
            card.addView(login, PhoneUi.margin(PhoneUi.width(PhoneUi.dp(activity, 120),
                    PhoneUi.dp(activity, 44)), 0, PhoneUi.dp(activity, 8), 0, 0));
            login.setOnClickListener(view -> {
                SavedConnections.Entry remembered = host.savedConnections()
                        .find(device.kind, device.url);
                PhoneLoginSheet.show(host, device.kind, device.url,
                        remembered == null ? "" : remembered.username,
                        remembered == null ? "" : remembered.password);
            });
            container.addView(card);
        }
    }

    private static void renderSaved(PhoneHost host, LinearLayout container) {
        Activity activity = host.activity();
        container.removeAllViews();
        List<SavedConnections.Entry> entries = host.savedConnections().all();
        if (entries.isEmpty()) {
            container.addView(PhoneUi.body(activity, "还没有记住的服务器。登录成功一次就会出现在这里。"));
            return;
        }
        for (SavedConnections.Entry entry : entries) {
            LinearLayout card = PhoneUi.card(activity, PhoneUi.block(activity, 0, PhoneUi.dp(activity, 10)));
            TextView title = PhoneUi.text(activity, entry.title(), 16, Typeface.BOLD, Theme.TEXT);
            title.setSingleLine(true);
            title.setEllipsize(TextUtils.TruncateAt.END);
            card.addView(title, PhoneUi.block(activity));
            card.addView(PhoneUi.body(activity, entry.detail()),
                    PhoneUi.block(activity, PhoneUi.dp(activity, 2), 0));
            LinearLayout actions = PhoneUi.buttonRow(activity);
            card.addView(actions, PhoneUi.block(activity, PhoneUi.dp(activity, 6), 0));
            Button again = PhoneUi.primary(activity, "登录");
            actions.addView(again, PhoneUi.width(PhoneUi.dp(activity, 120), PhoneUi.dp(activity, 44)));
            again.setOnClickListener(view -> PhoneLoginSheet.show(host, entry.kind, entry.hostKey,
                    entry.username, entry.password));
            Button forget = PhoneUi.ghost(activity, "忘记");
            actions.addView(forget, PhoneUi.margin(PhoneUi.button(activity, 88, 44),
                    PhoneUi.dp(activity, 8), 0, 0, 0));
            forget.setOnClickListener(view -> {
                host.savedConnections().remove(entry.kind, entry.hostKey);
                renderSaved(host, container);
            });
            container.addView(card);
        }
    }

    // ---- 本地目录 -----------------------------------------------------------

    /** 本地源不需要登录，直接挑目录；每行一个 ★ 用来确认这一层，免得误进子目录。 */
    static void pickLocalDirectory(final PhoneHost host, String absolutePath, String label) {
        Activity activity = host.activity();
        File directory = new File(absolutePath);
        final PhoneUi.Sheet sheet = PhoneUi.sheet(activity, "选择本地目录",
                UrlLabels.displayPath(directory.getAbsolutePath()));
        Button mount = PhoneUi.primary(activity, "★ 挂载这个目录");
        sheet.body.addView(mount, PhoneUi.block(activity, PhoneUi.dp(activity, 6), 0));
        mount.setOnClickListener(view -> {
            sheet.close();
            PhoneLoginSheet.mount(host, "local", directory.getAbsolutePath(), "", "", label);
        });
        File[] children = directory.listFiles();
        if (children == null || children.length == 0) {
            sheet.body.addView(PhoneUi.body(activity, "这个目录里没有子目录。"),
                    PhoneUi.block(activity, PhoneUi.dp(activity, 12), 0));
            return;
        }
        List<File> subdirectories = new ArrayList<>();
        for (File child : children) {
            if (child.isDirectory() && !child.getName().startsWith(".")) {
                subdirectories.add(child);
            }
        }
        sheet.body.addView(PhoneUi.body(activity, subdirectories.isEmpty()
                ? "没有子目录，直接挂载这一层。" : "点一行进下层，点 ★ 挂载这一层："),
                PhoneUi.block(activity, PhoneUi.dp(activity, 12), 0));
        for (File child : subdirectories) {
            LinearLayout row = PhoneUi.buttonRow(activity);
            sheet.body.addView(row, PhoneUi.block(activity, PhoneUi.dp(activity, 6), 0));
            TextView name = PhoneUi.text(activity, child.getName(), 16, Typeface.NORMAL, Theme.TEXT);
            name.setSingleLine(true);
            name.setEllipsize(TextUtils.TruncateAt.MIDDLE);
            row.addView(name, PhoneUi.weight(activity));
            Button pick = PhoneUi.ghost(activity, "★");
            row.addView(pick, PhoneUi.margin(PhoneUi.button(activity, 52, 42),
                    PhoneUi.dp(activity, 6), 0, 0, 0));
            pick.setOnClickListener(view -> {
                sheet.close();
                PhoneLoginSheet.mount(host, "local", child.getAbsolutePath(), "", "", child.getName());
            });
            Button open = PhoneUi.ghost(activity, "进入");
            row.addView(open, PhoneUi.margin(PhoneUi.button(activity, 76, 42),
                    PhoneUi.dp(activity, 6), 0, 0, 0));
            open.setOnClickListener(view -> {
                sheet.close();
                pickLocalDirectory(host, child.getAbsolutePath(), child.getName());
            });
        }
    }
}
