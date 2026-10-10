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
        public final boolean dark;

        Variant(String id, String name, String accent, String background, String surface,
                String textPrimary, String textSecondary, boolean dark) {
            this.id = id;
            this.name = name;
            this.accent = Color.parseColor(accent);
            this.background = Color.parseColor(background);
            this.surface = Color.parseColor(surface);
            this.textPrimary = Color.parseColor(textPrimary);
            this.textSecondary = Color.parseColor(textSecondary);
            this.dark = dark;
        }
    }

    public static final List<Variant> VARIANTS = Collections.unmodifiableList(Arrays.asList(
            new Variant("appleLight", "原生经典", "#007AFF", "#F2F2F7", "#FFFFFF",
                    "#000000", "#8E8E93", false),
            new Variant("crystal", "晶透白", "#007AFF", "#FFFFFF", "#F2F2F7",
                    "#000000", "#3C3C43", false),
            new Variant("linen", "亚麻灰", "#0D9488", "#F9F9F9", "#F1F1F1",
                    "#1F2937", "#6B7280", false),
            new Variant("mint", "薄荷清", "#10B981", "#F1FAF7", "#E0F2F1",
                    "#064E3B", "#689F88", false),
            new Variant("rose", "北欧粉", "#D946EF", "#FFF7FA", "#FFE4EC",
                    "#701A75", "#A855F7", false),
            new Variant("midnight", "午夜蓝", "#4F8CFF", "#0B1220", "#161F30",
                    "#F2F5FA", "#9AA8BC", true),
            new Variant("graphite", "石墨黑", "#2DD4BF", "#16171A", "#222428",
                    "#E8EAED", "#9CA3AF", true),
            new Variant("oled", "纯黑", "#0A84FF", "#000000", "#101012",
                    "#F5F5F7", "#98989F", true)));

    public static final String DEFAULT_VARIANT = com.omniplay.tv.local.TvSettings.DEFAULT_THEME_ID;

    // MARK: palette (the applied variant; see {@link #apply(String)})
    public static int BACKGROUND = Color.rgb(245, 247, 250);
    public static int SURFACE = Color.rgb(255, 255, 255);
    public static int SURFACE_ALT = Color.rgb(243, 244, 246);
    public static int BORDER = Color.rgb(221, 226, 235);
    public static int TEXT = Color.rgb(17, 24, 39);
    public static int MUTED = Color.rgb(75, 85, 99);
    public static int SUBTLE = Color.rgb(107, 114, 128);
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
    public static final float FOCUS_DARKEN = 0.42f;
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
        return new GradientDrawable(
                GradientDrawable.Orientation.TL_BR,
                new int[]{
                        blend(BACKGROUND, ACCENT, DARK ? 0.05f : 0.03f),
                        BACKGROUND,
                        blend(BACKGROUND, ACCENT, DARK ? 0.16f : 0.11f)
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

    // Focus is the control's own colour made deeper plus {@link #FOCUS_SCALE}, and its border
    // stays exactly as thick as it is at rest: an accent ring around every control reads as noise
    // on a TV sitting two meters away, and it made the whole screen look boxed in.

    /** Filled pill: darker fill, slight grow; used for toolbar and dialog buttons. */
    public void styleButtonFocus(Button button, boolean active, int radius) {
        int restFill = active ? GLASS_STRONG : GLASS;
        GradientDrawable rest = panel(restFill, BORDER, STROKE, radius);
        GradientDrawable focused = panel(darker(restFill, FOCUS_DARKEN), BORDER, STROKE, radius);
        swapOnFocus(button, rest, focused, 0, dp(6), hasFocus ->
                button.setTextColor(hasFocus || active ? FOCUS : TEXT), null);
    }

    /** Settings and list rows: same shape, but the label never changes colour. */
    public void styleRowFocus(Button button) {
        GradientDrawable rest = panel(GLASS, BORDER, STROKE, dp(10));
        GradientDrawable focused = panel(darker(GLASS, FOCUS_DARKEN), BORDER, STROKE, dp(10));
        swapOnFocus(button, rest, focused, 0, dp(6),
                hasFocus -> button.setTextColor(TEXT), null);
    }

    public void styleInputFocus(EditText editText) {
        GradientDrawable rest = panel(SURFACE, BORDER, dp(1), dp(8));
        GradientDrawable focused = panel(darker(SURFACE, FOCUS_DARKEN), BORDER, dp(1), dp(8));
        editText.setBackground(rest);
        editText.setOnFocusChangeListener((focusedView, hasFocus) -> focusedView.setBackground(
                hasFocus ? focused : rest));
    }

    /** A plain card that lifts when focused (list rows, source cards). */
    public void styleCardFocus(View card, Runnable onFocused) {
        swapOnFocus(card, panel(withAlpha(SURFACE, 0), Color.TRANSPARENT, 0, dp(10)),
                panel(darker(withAlpha(SURFACE, 235), FOCUS_DARKEN), BORDER, STROKE, dp(10)),
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
        // 海报是图片，光换底色看不出来；盖一层预先画好的暗色遮罩，聚焦时把它显出来。
        View dim = new View(frame.getContext());
        dim.setBackground(panel(withAlpha(Color.rgb(8, 12, 20), 140), BORDER, dp(1), dp(12)));
        dim.setVisibility(View.INVISIBLE);
        dim.setFocusable(false);
        dim.setClickable(false);
        frame.addView(dim, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        card.setOnFocusChangeListener((focusedView, hasFocus) -> {
            focusedView.setElevation(hasFocus ? dp(8) : 0);
            // 卡片是先建的、海报是后塞进来的，遮罩得在聚焦时自己排到最上面才盖得住图。
            if (hasFocus) {
                dim.bringToFront();
            }
            dim.setVisibility(hasFocus ? View.VISIBLE : View.INVISIBLE);
            animateFocusScale(focusedView, hasFocus);
            if (hasFocus && onFocused != null) {
                onFocused.run();
            }
        });
    }

    public void styleBadgeFocus(TextView badge) {
        swapOnFocus(badge, panel(GLASS_STRONG, BORDER, 0, dp(8)),
                panel(darker(GLASS_STRONG, FOCUS_DARKEN), BORDER, 0, dp(8)),
                0, dp(8), noRepaint(), null);
    }

    // Player controls sit on video: they keep a translucent plate, but focus is stated the same
    // loud way as everywhere else, or the remote feels random in a dark scene.

    public void stylePlayerButtonFocus(Button button) {
        int restFill = withAlpha(DARK ? SURFACE : Color.WHITE, 74);
        GradientDrawable rest = panel(restFill, withAlpha(Color.WHITE, 96), STROKE, dp(24));
        GradientDrawable focused = panel(darker(restFill, FOCUS_DARKEN * 1.6f),
                withAlpha(Color.WHITE, 96), STROKE, dp(24));
        swapOnFocus(button, rest, focused, 0, 0, hasFocus ->
                button.setTextColor(hasFocus ? FOCUS : PLAYER_TEXT), null);
    }

    public void stylePlayerIconFocus(ImageButton button) {
        int restFill = withAlpha(Color.WHITE, 34);
        GradientDrawable rest = panel(restFill, withAlpha(Color.WHITE, 140), STROKE, dp(24));
        GradientDrawable focused = panel(darker(restFill, FOCUS_DARKEN * 1.6f),
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
        GradientDrawable focused = panel(darker(restFill, FOCUS_DARKEN * 1.6f),
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
