package com.omniplay.tv.phone;

import android.app.Activity;
import android.app.Dialog;
import android.content.Intent;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.text.InputType;
import android.view.Gravity;
import android.view.ViewGroup;
import android.view.Window;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import com.omniplay.tv.ui.Theme;

/**
 * 手机端弹层的公共外壳：一个从底部推上来的面板 + 几个控件工厂。
 * 颜色一律取自 {@link Theme}，于是手机与电视共用同一套主题（含深色）。
 */
public final class PhoneUi {
    private PhoneUi() {
    }

    public static int dp(Activity activity, float value) {
        return Math.round(value * activity.getResources().getDisplayMetrics().density);
    }

    /**
     * 底部面板。标题行带一个「关闭」，内容放进可滚动的容器，最高占屏幕八成二，
     * 遥控器/触摸都能滚到底。
     *
     * @return 面板句柄：{@code body} 是内容容器，{@code close()} 收面板
     */
    public static Sheet sheet(Activity activity, String title, String subtitle) {
        Dialog dialog = new Dialog(activity);
        dialog.requestWindowFeature(Window.FEATURE_NO_TITLE);

        LinearLayout panel = new LinearLayout(activity);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setBackground(Background.top(activity, Theme.SURFACE, dp(activity, 18)));

        LinearLayout header = new LinearLayout(activity);
        header.setOrientation(LinearLayout.HORIZONTAL);
        header.setGravity(Gravity.CENTER_VERTICAL);
        header.setPadding(dp(activity, 20), dp(activity, 16), dp(activity, 12), dp(activity, 6));
        panel.addView(header, width(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        LinearLayout headings = new LinearLayout(activity);
        headings.setOrientation(LinearLayout.VERTICAL);
        header.addView(headings, weight(activity));
        headings.addView(text(activity, title, 21, Typeface.BOLD, Theme.TEXT));
        if (subtitle != null && !subtitle.isEmpty()) {
            headings.addView(text(activity, subtitle, 13, Typeface.NORMAL, Theme.MUTED));
        }
        Button close = ghost(activity, "关闭");
        header.addView(close, width(dp(activity, 84), dp(activity, 42)));
        close.setOnClickListener(view -> dialog.dismiss());

        LinearLayout content = new LinearLayout(activity);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setPadding(dp(activity, 20), dp(activity, 6), dp(activity, 20), dp(activity, 24));

        ScrollView scroll = new ScrollView(activity);
        scroll.addView(content, width(ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT));
        panel.addView(scroll, width(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f));

        dialog.setContentView(panel, width(ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT));
        dialog.setCancelable(true);

        Window window = dialog.getWindow();
        if (window != null) {
            int height = Math.round(activity.getResources().getDisplayMetrics().heightPixels * 0.82f);
            window.setLayout(ViewGroup.LayoutParams.MATCH_PARENT, height);
            window.setGravity(Gravity.BOTTOM);
            window.setBackgroundDrawableResource(android.R.color.transparent);
            WindowManager.LayoutParams params = window.getAttributes();
            params.dimAmount = 0.55f;
            params.windowAnimations = android.R.style.Animation_InputMethod;
            window.setAttributes(params);
            window.addFlags(WindowManager.LayoutParams.FLAG_DIM_BEHIND);
        }
        dialog.show();
        return new Sheet(dialog, content);
    }

    /** 一个弹层句柄：内容容器 + 收掉它 + 收掉时要跑的清理（比如停掉局域网扫描）。 */
    public static final class Sheet {
        public final Dialog dialog;
        public final LinearLayout body;
        private Runnable dismissAction;

        Sheet(Dialog dialog, LinearLayout body) {
            this.dialog = dialog;
            this.body = body;
            dialog.setOnDismissListener(ignored -> {
                if (dismissAction != null) {
                    dismissAction.run();
                }
            });
        }

        public void onDismiss(Runnable action) {
            dismissAction = action;
        }

        public void close() {
            dialog.dismiss();
        }
    }

    public static TextView section(Activity activity, String value) {
        TextView label = text(activity, value, 15, Typeface.BOLD, Theme.MUTED);
        label.setPadding(0, dp(activity, 20), 0, dp(activity, 8));
        return label;
    }

    public static TextView text(Activity activity, String value, int sp, int style, int color) {
        TextView view = new TextView(activity);
        view.setText(value == null ? "" : value);
        view.setTextSize(sp);
        view.setTypeface(Typeface.DEFAULT, style);
        view.setTextColor(color);
        return view;
    }

    public static TextView body(Activity activity, String value) {
        TextView view = text(activity, value, 14, Typeface.NORMAL, Theme.MUTED);
        view.setPadding(0, dp(activity, 2), 0, dp(activity, 6));
        return view;
    }

    public static EditText field(Activity activity, String hint, String value, boolean password) {
        EditText view = new EditText(activity);
        view.setText(value == null ? "" : value);
        view.setHint(hint);
        view.setSingleLine(true);
        view.setTextSize(16);
        view.setTextColor(Theme.TEXT);
        view.setHintTextColor(Theme.SUBTLE);
        view.setPadding(dp(activity, 14), 0, dp(activity, 14), 0);
        view.setMinHeight(dp(activity, 52));
        view.setBackground(Background.flat(activity, Theme.SURFACE_ALT, dp(activity, 8)));
        if (password) {
            view.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        }
        return view;
    }

    public static Button primary(Activity activity, String label) {
        Button button = new Button(activity);
        button.setText(label);
        button.setAllCaps(false);
        button.setTextSize(16);
        button.setTextColor(Theme.DARK ? Color.WHITE : Color.WHITE);
        button.setBackground(Background.flat(activity, Theme.FOCUS, dp(activity, 10)));
        button.setMinHeight(dp(activity, 50));
        return button;
    }

    public static Button ghost(Activity activity, String label) {
        Button button = new Button(activity);
        button.setText(label);
        button.setAllCaps(false);
        button.setTextSize(14);
        button.setTextColor(Theme.TEXT);
        button.setBackground(Background.flat(activity, Theme.SURFACE_ALT, dp(activity, 10)));
        button.setMinHeight(dp(activity, 42));
        button.setPadding(0, 0, 0, 0);
        return button;
    }

    /** 一行按钮：横向排列，按钮之间留缝。 */
    public static LinearLayout buttonRow(Activity activity) {
        LinearLayout row = new LinearLayout(activity);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        return row;
    }

    /** 一张卡片：媒体源、设备、候选条目都用它。 */
    public static LinearLayout card(Activity activity, LinearLayout.LayoutParams params) {
        LinearLayout card = new LinearLayout(activity);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(activity, 16), dp(activity, 14), dp(activity, 16), dp(activity, 14));
        card.setBackground(Background.flat(activity, Theme.SURFACE, dp(activity, 12)));
        card.setLayoutParams(params);
        return card;
    }

    public static LinearLayout.LayoutParams block(Activity activity) {
        return width(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
    }

    public static LinearLayout.LayoutParams block(Activity activity, int top, int bottom) {
        LinearLayout.LayoutParams params =
                width(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        params.setMargins(0, top, 0, bottom);
        return params;
    }

    public static LinearLayout.LayoutParams weight(Activity activity) {
        return new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
    }

    public static LinearLayout.LayoutParams width(int width, int height) {
        return new LinearLayout.LayoutParams(width, height);
    }

    public static LinearLayout.LayoutParams margin(LinearLayout.LayoutParams params,
                                                   int left, int top, int right, int bottom) {
        params.setMargins(left, top, right, bottom);
        return params;
    }

    public static LinearLayout.LayoutParams width(int width, int height, float weight) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(width, height);
        params.weight = weight;
        return params;
    }

    public static LinearLayout.LayoutParams button(Activity activity, int widthDp, int heightDp) {
        return width(dp(activity, widthDp), dp(activity, heightDp));
    }

    public static LinearLayout.LayoutParams gap(Activity activity, int topDp) {
        return block(activity, dp(activity, topDp), 0);
    }

    /** 让系统输入法/分享之类的东西能在弹层里被叫出来。 */
    public static void openBrowser(Activity activity, String url) {
        try {
            activity.startActivity(new Intent(Intent.ACTION_VIEW, android.net.Uri.parse(url)));
        } catch (Exception ignored) {
        }
    }

    /** 背景工厂：圆角与描边都在这儿，避免每个弹层自己拼 GradientDrawable。 */
    static final class Background {
        private Background() {
        }

        static GradientDrawable flat(Activity activity, int color, int radius) {
            GradientDrawable drawable = new GradientDrawable();
            drawable.setColor(color);
            drawable.setCornerRadius(radius);
            return drawable;
        }

        static GradientDrawable top(Activity activity, int color, int radius) {
            GradientDrawable drawable = new GradientDrawable();
            drawable.setColor(color);
            drawable.setCornerRadii(new float[]{radius, radius, radius, radius, 0, 0, 0, 0});
            return drawable;
        }
    }

    /** 点空白处也能收起来的辅助：给面板外的 scrim 用。 */
    public static void keepScreenOn(Activity activity, boolean on) {
        if (on) {
            activity.getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        } else {
            activity.getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        }
    }

    /** 弹层里的状态行（登录中、报错、进度都写这儿）。 */
    public static TextView statusLine(Activity activity, String value) {
        TextView view = text(activity, value, 13, Typeface.BOLD, Theme.ACCENT);
        view.setPadding(0, dp(activity, 8), 0, 0);
        return view;
    }
}
