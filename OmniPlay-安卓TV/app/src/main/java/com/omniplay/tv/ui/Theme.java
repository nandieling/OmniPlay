package com.omniplay.tv.ui;

import android.content.Context;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.view.ViewGroup;
import android.widget.ImageButton;
import android.widget.ImageView;
import android.widget.TextView;

import java.util.Arrays;
import java.util.Collections;
import java.util.List;

/**
 * The single source of truth for how the TV interface looks.
 * <p>
 * The mac app keeps its appearance in ThemeManager; this is the same idea for Android: colours,
 * the TV size scaling, panel shapes and the focus styles live here instead of being re-typed in
 * every screen. Screens ask for a style instead of building a GradientDrawable by hand, so a
 * panel in the library and a panel in a dialog cannot drift apart.
 */
public final class Theme {
    /**
     * One appearance. The five light palettes are the mac app's ThemeManager values verbatim;
     * the three dark ones are what a TV needs in a dark room (the panel fill lifts off a black
     * background so panels stay separable, and the accent brightens to keep contrast).
     */
    public static final class Variant {
        public final String id;
        public final String name;
        public final int accent;
        public final int background;
        public final int surface;
        public final int textPrimary;
        public final int textSecondary;
        /** 页面底色往强调色那侧偏一点的「晕染色」，只用在背景渐变的两端。 */
        public final int wash;
        public final boolean dark;

        Variant(String id, String name, String accent, String background, String surface,
                String textPrimary, String textSecondary, String wash, boolean dark) {
            this.id = id;
            this.name = name;
            this.accent = Color.parseColor(accent);
            this.background = Color.parseColor(background);
            this.surface = Color.parseColor(surface);
            this.textPrimary = Color.parseColor(textPrimary);
            this.textSecondary = Color.parseColor(textSecondary);
            this.wash = Color.parseColor(wash);
            this.dark = dark;
        }
    }

    public static final List<Variant> VARIANTS = Collections.unmodifiableList(Arrays.asList(
            new Variant("appleLight", "原生经典", "#0A7AFF", "#F1F2F6", "#FFFFFF",
                    "#0A0A0C", "#86868E", "#E6EBF5", false),
            new Variant("crystal", "晶透白", "#0A84FF", "#F7F9FC", "#FFFFFF",
                    "#07080C", "#5C6572", "#E4EFFF", false),
            new Variant("linen", "亚麻灰", "#0F8F83", "#F4F3F0", "#FCFCFA",
                    "#22262C", "#6B7280", "#E7E4DC", false),
            new Variant("mint", "薄荷清", "#0F9F6E", "#EFF6F3", "#FFFFFF",
                    "#0C3B30", "#557F72", "#DBEFE4", false),
            new Variant("rose", "北欧粉", "#DB2777", "#FCF3F7", "#FFFFFF",
                    "#45202E", "#96657A", "#F9DFEA", false),
            new Variant("midnight", "午夜蓝", "#6BA5FF", "#070D1A", "#121B2C",
                    "#EFF3FA", "#93A6C0", "#102347", true),
            new Variant("graphite", "石墨黑", "#34D3B0", "#101113", "#1C1E22",
                    "#E7E9EC", "#99A0AA", "#1A2126", true),
            new Variant("oled", "纯黑", "#0A84FF", "#000000", "#0C0D10",
                    "#F2F2F5", "#94919C", "#060A14", true)));

    public static final String DEFAULT_VARIANT = com.omniplay.tv.local.TvSettings.DEFAULT_THEME_ID;

    // MARK: palette (the applied variant; see {@link #apply(String)})
    public static int BACKGROUND = Color.rgb(245, 247, 250);
    public static int SURFACE = Color.rgb(255, 255, 255);
    public static int SURFACE_ALT = Color.rgb(243, 244, 246);
    public static int BORDER = Color.rgb(221, 226, 235);
    public static int TEXT = Color.rgb(17, 24, 39);
    public static int MUTED = Color.rgb(75, 85, 99);
    public static int SUBTLE = Color.rgb(107, 114, 128);
    /** 背景渐变的晕染色，随主题切换；见 {@link #appBackground()}。 */
    public static int WASH = Color.rgb(230, 235, 245);
    public static int FOCUS = Color.rgb(37, 99, 235);
    public static int ACCENT = Color.rgb(20, 184, 166);
    public static int FOCUS_SOFT = Color.rgb(232, 240, 255);
    public static int DANGER = Color.rgb(185, 28, 28);
    public static int RATING = Color.rgb(217, 119, 6);
    public static int RATING_DOUBAN = Color.rgb(0, 166, 41);
    public static final int PLAYER_TEXT = Color.WHITE;
    public static final int PLAYER_MUTED = Color.rgb(229, 231, 235);

    /** Translucent fills used on top of video; kept here so the player cannot invent its own. */
    public static int GLASS = Color.argb(238, 255, 255, 255);
    public static int GLASS_STRONG = Color.argb(248, 255, 255, 255);
    public static int GLASS_SOFT = Color.argb(184, 255, 255, 255);
    public static int SCRIM = Color.argb(96, 15, 23, 42);

    /** True while a dark variant is applied; panels over video and rating badges follow it. */
    public static boolean DARK = false;

    public static final float FOCUS_SCALE = 1.045f;
    /** How much deeper a control's own fill goes when the remote lands on it. */
    /**
     * 聚焦时底色压深的比例。0 = 不压深：焦点只由「轻微放大 + 抬起 + 边框换成强调色」表达，
     * 遥控器下按钮不会一闪一闪地变暗，深色主题里的底色也保持稳定。
     */
    public static final float FOCUS_DARKEN = 0f;
    public static final int FOCUS_ANIMATION_MS = 120;
    /** Border every control draws, at rest and while focused; only its colour ever changes. */
    public static int STROKE = 1;
    /** Fully rounded ends for pills. */
    public static final int PILL = 9999;

    private static String appliedVariant = DEFAULT_VARIANT;

    public static Variant variant(String id) {
        for (Variant candidate : VARIANTS) {
            if (candidate.id.equals(id)) {
                return candidate;
            }
        }
        return variant(DEFAULT_VARIANT);
    }

    public static String appliedId() {
        return appliedVariant;
    }

    public static boolean isDark() {
        return DARK;
    }

    /** Applies one of {@link #VARIANTS}; every colour below is derived from it, nothing is a literal. */
    public static void apply(String id) {
        Variant variant = variant(id);
        appliedVariant = variant.id;
        DARK = variant.dark;
        int white = Color.WHITE;
        int black = Color.BLACK;
        ACCENT = variant.accent;
        BACKGROUND = variant.background;
        WASH = variant.wash;
        SURFACE = variant.surface;
        TEXT = variant.textPrimary;
        SUBTLE = variant.textSecondary;
        SURFACE_ALT = variant.dark ? blend(variant.surface, white, 0.05f)
                : blend(variant.surface, variant.textPrimary, 0.045f);
        BORDER = variant.dark ? blend(variant.surface, white, 0.17f)
                : blend(variant.surface, variant.textPrimary, 0.14f);
        MUTED = variant.dark ? blend(variant.textSecondary, white, 0.14f) : variant.textSecondary;
        // Focus has to survive three metres of living room, so it is the loudest colour on screen.
        FOCUS = variant.dark ? blend(variant.accent, white, 0.14f) : variant.accent;
        FOCUS_SOFT = variant.dark ? blend(variant.surface, variant.accent, 0.22f)
                : blend(variant.surface, variant.accent, 0.10f);
        DANGER = variant.dark ? blend(Color.parseColor("#E05252"), white, 0.12f)
                : Color.parseColor("#B91C1C");
        RATING = variant.dark ? blend(Color.parseColor("#FFAC00"), white, 0.16f)
                : Color.parseColor("#D97706");
        RATING_DOUBAN = variant.dark ? blend(Color.parseColor("#00B51D"), white, 0.20f)
                : Color.parseColor("#00A629");
        int glassBase = variant.dark ? blend(variant.surface, black, 0.12f) : white;
        GLASS = Color.argb(238, Color.red(glassBase), Color.green(glassBase), Color.blue(glassBase));
        GLASS_STRONG = Color.argb(248, Color.red(glassBase), Color.green(glassBase),
                Color.blue(glassBase));
        GLASS_SOFT = Color.argb(184, Color.red(glassBase), Color.green(glassBase),
                Color.blue(glassBase));
        int scrimBase = blend(variant.background, black, variant.dark ? 0.55f : 0.45f);
        SCRIM = Color.argb(variant.dark ? 150 : 96, Color.red(scrimBase), Color.green(scrimBase),
                Color.blue(scrimBase));
    }

    private final Context context;
    private final float density;
    private final int screenWidth;

    public Theme(Context context) {
        this.context = context;
        this.density = context.getResources().getDisplayMetrics().density;
        this.screenWidth = context.getResources().getDisplayMetrics().widthPixels;
        STROKE = Math.max(1, Math.round(density));
    }

    public Context context() {
        return context;
    }

    /** Plain density units, for anything that must not grow on a 4K panel. */
    public int dp(float value) {
        return Math.round(value * density);
    }

    /** Layout units: nudged up on 4K/8K panels so a 1080p layout stays readable from a sofa. */
    public int sdp(float value) {
        return Math.round(dp(value) * uiScale());
    }

    /** Font sizes in pixels, on the same scaling curve as {@link #sdp(float)}. */
    public int ssp(float value) {
        return Math.round(value * textScale());
    }

    public float uiScale() {
        if (screenWidth >= 3600) {
            return 1.18f;
        }
        return screenWidth >= 2500 ? 1.10f : 1f;
    }

    private float textScale() {
        if (screenWidth >= 3600) {
            return 1.12f;
        }
        return screenWidth >= 2500 ? 1.06f : 1f;
    }

    // MARK: shapes

    public GradientDrawable panel(int fill, int strokeColor, int strokeWidth, int radius) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(fill);
        drawable.setCornerRadius(radius);
        if (strokeWidth > 0) {
            drawable.setStroke(strokeWidth, strokeColor);
        }
        return drawable;
    }

    /** The app background: a soft daylight wash, used by every screen. */
    public GradientDrawable appBackground() {
        // 左上角只是微微染一点颜色，右下角才让强调色显出来：海报墙滚起来时背景有方向感，
        // 又不会跟海报抢视线。中间一段保持纯底色，整屏不会发灰。
        return new GradientDrawable(
                GradientDrawable.Orientation.TL_BR,
                new int[]{
                        blend(BACKGROUND, WASH, 0.55f),
                        BACKGROUND,
                        BACKGROUND,
                        blend(WASH, ACCENT, DARK ? 0.30f : 0.22f)
                });
    }

    public static int withAlpha(int color, int alpha) {
        return Color.argb(Math.max(0, Math.min(255, alpha)), Color.red(color), Color.green(color),
                Color.blue(color));
    }

    /**
     * The same colour pushed towards black, alpha kept: what a focused control paints with,
     * so "focused" reads as a deeper version of the control itself rather than as a ring drawn
     * around it.
     */
    public static int darker(int color, float amount) {
        float keep = 1f - Math.max(0f, Math.min(1f, amount));
        return Color.argb(Color.alpha(color),
                Math.round(Color.red(color) * keep),
                Math.round(Color.green(color) * keep),
                Math.round(Color.blue(color) * keep));
    }

    public static int blend(int from, int to, float amount) {
        float clamped = Math.max(0f, Math.min(1f, amount));
        int red = Math.round(Color.red(from) + (Color.red(to) - Color.red(from)) * clamped);
        int green = Math.round(Color.green(from) + (Color.green(to) - Color.green(from)) * clamped);
        int blue = Math.round(Color.blue(from) + (Color.blue(to) - Color.blue(from)) * clamped);
        return Color.rgb(red, green, blue);
    }

    // MARK: focus styles
    //
    // Every focusable control on TV has to say "I am focused" the same way, otherwise the
    // remote feels random. These are the only focus styles the app is allowed to use.

    /**
     * The focus scale, wrapped in a hardware layer. Scaling a card that holds a poster bitmap
     * otherwise re-rasterises poster, text and gradient on every frame of the animation, and that
     * is what reads as a sticky wall on a TV box; with a layer the scale is a plain GPU transform.
     */
    public void animateFocusScale(View view, boolean focused) {
        if (focused) {
            view.setLayerType(View.LAYER_TYPE_HARDWARE, null);
        }
        view.animate()
                .scaleX(focused ? FOCUS_SCALE : 1f)
                .scaleY(focused ? FOCUS_SCALE : 1f)
                .setDuration(FOCUS_ANIMATION_MS)
                .withEndAction(() -> {
                    if (!view.isFocused()) {
                        view.setLayerType(View.LAYER_TYPE_NONE, null);
                    }
                })
                .start();
    }

    /**
     * Focus handling that only swaps two pre-built backgrounds. Building the GradientDrawable
     * inside the listener allocated one per arrow key and forced the view to rebuild its outline,
     * and therefore its shadow, on every single focus move.
     */
    /**
     * 聚焦时的底色：在原底色里掺一点强调色。以前「选中」是靠一圈强调色边框说的，遥控器每走
     * 一格就有一圈蓝线冒出来；现在只留轻微放大、一层阴影和这一点底色，边框颜色一动不动。
     */
    public static int focusFill(int base) {
        return blend(base, FOCUS, DARK ? 0.20f : 0.10f);
    }

/** Repaints the parts of a control that are not its background (label colour, tint). */
    public interface FocusRepaint {
        void apply(boolean hasFocus);
    }

    private void swapOnFocus(View target, GradientDrawable rest, GradientDrawable focused,
                             int restElevation, int focusedElevation,
                             FocusRepaint repaint, Runnable onFocused) {
        target.setBackground(rest);
        target.setElevation(restElevation);
        repaint.apply(false);
        target.setOnFocusChangeListener((focusedView, hasFocus) -> {
            focusedView.setBackground(hasFocus ? focused : rest);
            focusedView.setElevation(hasFocus ? focusedElevation : restElevation);
            repaint.apply(hasFocus);
            animateFocusScale(focusedView, hasFocus);
            if (hasFocus && onFocused != null) {
                onFocused.run();
            }
        });
    }

    private static FocusRepaint noRepaint() {
        return focused -> {
        };
    }

    // Focus is {@link #FOCUS_SCALE} plus a lift, and the control's own border switching to the
    // accent colour. The fill stays put (FOCUS_DARKEN is 0): a fill that dips on every arrow key
    // reads as flicker two metres away. The border keeps its rest thickness -- only its colour
    // changes -- so nothing on screen grows a box around it.

    /** Filled pill: slight grow, lifted, border turns accent; toolbar and dialog buttons. */
    public void styleButtonFocus(Button button, boolean active, int radius) {
        int restFill = active ? GLASS_STRONG : GLASS;
        GradientDrawable rest = panel(restFill, BORDER, STROKE, radius);
        GradientDrawable focused = panel(focusFill(restFill), active ? FOCUS : BORDER, STROKE, radius);
        swapOnFocus(button, rest, focused, 0, dp(6), hasFocus ->
                button.setTextColor(hasFocus || active ? FOCUS : TEXT), null);
    }

    /**
     * Icon-only toolbar button: same focus language as the text buttons, only the glyph is
     * repainted (a monochrome Canvas drawable simply takes the accent tint through a colour filter).
     */
    public void styleIconFocus(View target, boolean active, int radius) {
        // mac 版这排工具栏按钮是 .buttonStyle(.plain)：平时看不见底，图标直接浮在背景上。
        // TV 上只有拿到焦点才给出圆角底与强调色描边，激活态靠图标自己染成强调色表示。
        GradientDrawable rest = panel(Color.TRANSPARENT, Color.TRANSPARENT, STROKE, radius);
        GradientDrawable focused = panel(focusFill(GLASS), Color.TRANSPARENT, 0, radius);
        swapOnFocus(target, rest, focused, 0, dp(6), hasFocus -> {
            if (!(target instanceof ImageView)) {
                return;
            }
            ImageView icon = (ImageView) target;
            if (hasFocus || active) {
                icon.setColorFilter(FOCUS);
            } else {
                icon.clearColorFilter();
            }
        }, null);
    }

    /** Settings and list rows: same shape, but the label never changes colour. */
    public void styleRowFocus(Button button) {
        GradientDrawable rest = panel(GLASS, BORDER, STROKE, dp(10));
        GradientDrawable focused = panel(focusFill(GLASS), BORDER, STROKE, dp(10));
        swapOnFocus(button, rest, focused, 0, dp(6),
                hasFocus -> button.setTextColor(TEXT), null);
    }

    public void styleInputFocus(EditText editText) {
        GradientDrawable rest = panel(SURFACE, BORDER, dp(1), dp(8));
        GradientDrawable focused = panel(focusFill(SURFACE), BORDER, dp(1), dp(8));
        editText.setBackground(rest);
        editText.setOnFocusChangeListener((focusedView, hasFocus) -> {
            focusedView.setBackground(hasFocus ? focused : rest);
            animateFocusScale(focusedView, hasFocus);
            if (hasFocus && focusedView instanceof EditText) {
                // 默认会把光标放到整串文字最前面，遥控器上删除键按下去毫无反应。
                EditText field = (EditText) focusedView;
                field.setSelection(field.getText().length());
            }
        });
    }

    /** A plain card that lifts when focused (list rows, source cards). */
    public void styleCardFocus(View card, Runnable onFocused) {
        swapOnFocus(card, panel(withAlpha(SURFACE, 0), Color.TRANSPARENT, 0, dp(10)),
                panel(focusFill(withAlpha(SURFACE, 235)), Color.TRANSPARENT, 0, dp(10)),
                0, dp(10), noRepaint(), onFocused);
    }

    /**
     * Poster card: the frame carries the border, the card carries the lift. One shadow, not two -
     * a blurred shadow for both the card and its frame doubled the cost of every focus move.
     */
    public void stylePosterFocus(View card, FrameLayout frame, Runnable onFocused) {
        GradientDrawable frameRest = panel(SURFACE_ALT, BORDER, dp(1), dp(12));
        GradientDrawable transparent = panel(Color.TRANSPARENT, Color.TRANSPARENT, 0, dp(8));
        frame.setBackground(frameRest);
        frame.setElevation(0);
        card.setBackground(transparent);
        card.setElevation(0);
        // 选中一张海报只说两件事：轻微放大 + 一层阴影。以前额外盖了一层暗色遮罩，
        // 遥控器每走一格海报就暗一下，看着像图片在闪，用户要的恰恰是「原图别动」。
        card.setOnFocusChangeListener((focusedView, hasFocus) -> {
            focusedView.setElevation(hasFocus ? dp(8) : 0);
            animateFocusScale(focusedView, hasFocus);
            if (hasFocus && onFocused != null) {
                onFocused.run();
            }
        });
    }

    public void styleBadgeFocus(TextView badge) {
        swapOnFocus(badge, panel(GLASS_STRONG, BORDER, 0, dp(8)),
                panel(focusFill(GLASS_STRONG), BORDER, STROKE, dp(8)),
                0, dp(8), noRepaint(), null);
    }

    // Player controls sit on video: they keep a translucent plate, but focus is stated the same
    // loud way as everywhere else, or the remote feels random in a dark scene.

    public void stylePlayerButtonFocus(Button button) {
        int restFill = withAlpha(DARK ? SURFACE : Color.WHITE, 74);
        GradientDrawable rest = panel(restFill, withAlpha(Color.WHITE, 96), STROKE, dp(24));
        GradientDrawable focused = panel(focusFill(restFill),
                withAlpha(Color.WHITE, 96), STROKE, dp(24));
        swapOnFocus(button, rest, focused, 0, 0, hasFocus ->
                button.setTextColor(hasFocus ? FOCUS : PLAYER_TEXT), null);
    }

    public void stylePlayerIconFocus(ImageButton button) {
        int restFill = withAlpha(Color.WHITE, 34);
        GradientDrawable rest = panel(restFill, withAlpha(Color.WHITE, 140), STROKE, dp(24));
        GradientDrawable focused = panel(focusFill(restFill),
                withAlpha(Color.WHITE, 140), STROKE, dp(24));
        swapOnFocus(button, rest, focused, 0, 0, hasFocus -> {
            if (hasFocus) {
                button.setColorFilter(FOCUS);
            } else {
                button.clearColorFilter();
            }
        }, null);
    }

    /** Rows inside the player's audio / subtitle menu: glass over video, accent when selected. */
    public void stylePlayerMenuFocus(Button button, boolean selected) {
        int plate = DARK ? SURFACE : Color.WHITE;
        int restFill = withAlpha(plate, selected ? 46 : 22);
        GradientDrawable rest = panel(restFill, withAlpha(Color.WHITE, 120), STROKE, dp(10));
        GradientDrawable focused = panel(focusFill(restFill),
                withAlpha(Color.WHITE, 120), STROKE, dp(10));
        swapOnFocus(button, rest, focused, 0, 0, hasFocus -> button.setTextColor(
                hasFocus ? FOCUS : (selected ? ACCENT : PLAYER_TEXT)), null);
    }

    /** The frame around a poster image or episode still. */
    public void styleMediaFrame(ImageView view, int radius) {
        view.setClipToOutline(true);
        view.setBackground(panel(SURFACE_ALT, BORDER, dp(1), radius));
    }
}
