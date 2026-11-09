package com.omniplay.tv.phone;

import android.app.Activity;
import android.content.pm.PackageInfo;
import android.graphics.Typeface;
import android.widget.LinearLayout;
import android.widget.TextView;

import com.omniplay.tv.ui.GuideContent;
import com.omniplay.tv.ui.Theme;

/**
 * 使用指南与「关于 / 开源」。内容与 mac 版的 UsageGuideView、OpenSourceInfoView 对得上，
 * 但按安卓这边实际有的东西写：遥控器操作、播放模式、字幕转档、手机输入这几件事。
 */
public final class PhoneGuideSheet {
    private PhoneGuideSheet() {
    }

    /** 使用指南：正文取自 {@link GuideContent}，与电视端那一层弹层是同一份文字。 */
    public static void showGuide(PhoneHost host) {
        Activity activity = host.activity();
        PhoneUi.Sheet sheet = PhoneUi.sheet(activity, "使用指南", "添加源、刮削、播放、字幕");
        for (GuideContent.Section section : GuideContent.guide()) {
            render(sheet.body, activity, section);
        }
    }

    /** 关于 / 开源：版本、数据来源、内置组件与许可证，同样共用一份正文。 */
    public static void showAbout(PhoneHost host) {
        Activity activity = host.activity();
        PhoneUi.Sheet sheet = PhoneUi.sheet(activity, "关于", "觅影 OmniPlay");
        LinearLayout body = sheet.body;
        body.addView(PhoneUi.text(activity, "觅影 OmniPlay", 20, Typeface.BOLD, Theme.TEXT),
                PhoneUi.block(activity));
        body.addView(PhoneUi.body(activity, "版本 " + version(activity)
                + "（" + versionCode(activity) + "）· 本机独立运行，不上传任何数据"),
                PhoneUi.block(activity));
        for (GuideContent.Section section
                : GuideContent.about("配套项目：NAS 上的「OmniPlay 字幕转档」与 mac 版觅影同源，"
                        + "抽内封字幕、蓝光盘整盘转档都由它做，电视端只负责在播放时调用转好的文件。")) {
            render(body, activity, section);
        }
    }

    private static void render(LinearLayout body, Activity activity, GuideContent.Section section) {
        body.addView(PhoneUi.text(activity, section.title, 15, Typeface.BOLD, Theme.ACCENT),
                PhoneUi.block(activity, PhoneUi.dp(activity, 14), PhoneUi.dp(activity, 4)));
        TextView paragraph = PhoneUi.text(activity, section.body, 14, Typeface.NORMAL, Theme.MUTED);
        paragraph.setLineSpacing(PhoneUi.dp(activity, 3), 1f);
        body.addView(paragraph, PhoneUi.block(activity));
    }

    private static String version(Activity activity) {
        PackageInfo info = packageInfo(activity);
        return info == null || info.versionName == null ? "1.0" : info.versionName;
    }

    private static String versionCode(Activity activity) {
        PackageInfo info = packageInfo(activity);
        if (info == null) {
            return "-";
        }
        long code = info.getLongVersionCode();
        return String.valueOf(code);
    }

    private static PackageInfo packageInfo(Activity activity) {
        try {
            return activity.getPackageManager()
                    .getPackageInfo(activity.getPackageName(), 0);
        } catch (Exception ignored) {
            return null;
        }
    }
}
