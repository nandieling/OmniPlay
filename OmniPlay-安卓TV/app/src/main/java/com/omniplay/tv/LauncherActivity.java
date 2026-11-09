package com.omniplay.tv;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.res.Configuration;
import android.os.Bundle;

/**
 * 桌面只有一个入口，电视与手机两套界面由这里现场挑一个。
 *
 * <p>判定原本只看设备身份（uiMode 是不是电视、有没有 leanback），可盒子与虚拟机的身份经常不准：
 * Mac Android Subsystem 这类跑法既报触屏又能吃遥控器输入，判错一次就只能重装才能进另一套界面。
 * 于是再加一层手动指定——两端设置里都有「打开时进的界面」，记在 {@code omniplay_tv_ui} 的
 * {@code ui_shell_mode} 里，手动指定优先于自动判定，改完下次打开生效。</p>
 */
public final class LauncherActivity extends Activity {
    public static final String UI_PREFS = "omniplay_tv_ui";
    public static final String KEY_SHELL_MODE = "ui_shell_mode";
    public static final String SHELL_AUTO = "auto";
    public static final String SHELL_TV = "tv";
    public static final String SHELL_PHONE = "phone";
    public static final String[] SHELL_MODES = {SHELL_AUTO, SHELL_TV, SHELL_PHONE};

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        Class<?> target = preferTelevision() ? MainActivity.class : PhoneMainActivity.class;
        Intent intent = new Intent(this, target);
        startActivity(intent);
        finish();
    }

    /** 手动指定优先；没指定过才按设备身份判。 */
    private boolean preferTelevision() {
        String shell = shellMode(this);
        if (SHELL_TV.equals(shell)) {
            return true;
        }
        if (SHELL_PHONE.equals(shell)) {
            return false;
        }
        return looksLikeTelevision();
    }

    private boolean looksLikeTelevision() {
        int uiModeType = getResources().getConfiguration().uiMode & Configuration.UI_MODE_TYPE_MASK;
        if (uiModeType == Configuration.UI_MODE_TYPE_TELEVISION) {
            return true;
        }
        PackageManager manager = getPackageManager();
        // 只报 leanback 却带着触屏的，是盒子与虚拟机一类（遥控器和手指都有），别强推 D-pad 界面；
        // 真电视的 uiMode 会写成 television，上面那条已经接住了。
        return manager.hasSystemFeature(PackageManager.FEATURE_LEANBACK)
                && !manager.hasSystemFeature(PackageManager.FEATURE_TOUCHSCREEN);
    }

    /** 当前指定的界面：auto / tv / phone。 */
    public static String shellMode(Context context) {
        String value = context.getApplicationContext()
                .getSharedPreferences(UI_PREFS, MODE_PRIVATE)
                .getString(KEY_SHELL_MODE, SHELL_AUTO);
        return value == null || value.isEmpty() ? SHELL_AUTO : value;
    }

    /** 记下要进哪套界面；同步落盘，别刚选完就被杀进程丢掉。 */
    public static void setShellMode(Context context, String mode) {
        context.getApplicationContext()
                .getSharedPreferences(UI_PREFS, MODE_PRIVATE)
                .edit().putString(KEY_SHELL_MODE, mode).commit();
    }

    public static String shellLabel(String mode) {
        if (SHELL_TV.equals(mode)) {
            return "电视版";
        }
        if (SHELL_PHONE.equals(mode)) {
            return "手机版";
        }
        return "自动";
    }
}
