package com.omniplay.tv.ui;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Rect;
import android.graphics.drawable.Drawable;
import android.graphics.Typeface;
import android.text.SpannableStringBuilder;
import android.text.Spanned;
import android.text.TextUtils;
import android.text.style.ForegroundColorSpan;
import android.text.style.RelativeSizeSpan;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;

/**
 * The shared control set: the only buttons, labels and rows a screen is allowed to build with.
 * <p>
 * On the mac side these are SwiftUI views (button, section header, meta pill, card). Here they are
 * factories over a {@link Theme}, which keeps a toolbar button on the library screen looking the
 * same as one inside a dialog and keeps every label on the same type ramp.
 */
import java.util.ArrayList;
import java.util.List;
import java.util.function.IntConsumer;

public final class Widgets {
    /** Type ramp, in scaled sp. One place so headings cannot drift between screens. */
    public static final int TEXT_DISPLAY = 32;
    public static final int TEXT_TITLE = 24;
    public static final int TEXT_HEADLINE = 18;
    public static final int TEXT_BODY = 16;
    public static final int TEXT_LABEL = 14;
    public static final int TEXT_CAPTION = 13;
    public static final int TEXT_MICRO = 12;

    /** Standard control heights. */
    public static final int HEIGHT_BUTTON = 56;
    public static final int HEIGHT_COMPACT = 48;
    public static final int HEIGHT_ROW = 78;
    public static final int HEIGHT_INPUT = 58;

    private final Theme theme;
    private final Context context;

    public Widgets(Theme theme) {
        this.theme = theme;
        this.context = theme.context();
    }

    public Theme theme() {
        return theme;
    }

    // MARK: layout helpers

    public static FrameLayout.LayoutParams fill() {
        return new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT);
    }

    public static LinearLayout.LayoutParams column() {
        return new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT);
    }

    public static LinearLayout.LayoutParams row(float weight) {
        return new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, weight);
    }

    public static LinearLayout.LayoutParams rowItem(float widthDp, float heightDp) {
        return new LinearLayout.LayoutParams(Math.round(widthDp), Math.round(heightDp));
    }

    public static <P extends ViewGroup.MarginLayoutParams> P margins(P params, int left, int top,
                                                                    int right, int bottom) {
        params.setMargins(left, top, right, bottom);
        return params;
    }

    // MARK: labels

    public TextView text(String value, int sizePx, int style, int color) {
        TextView view = new TextView(context);
        view.setText(value == null ? "" : value);
        view.setTextSize(sizePx);
        view.setTypeface(Typeface.DEFAULT, style);
        view.setTextColor(color);
        view.setIncludeFontPadding(false);
        view.setFontFeatureSettings("kern");
        view.setPaintFlags(view.getPaintFlags()
                | Paint.ANTI_ALIAS_FLAG | Paint.SUBPIXEL_TEXT_FLAG | Paint.LINEAR_TEXT_FLAG);
        return view;
    }

    public TextView display(String value) {
        TextView view = text(value, theme.ssp(TEXT_DISPLAY), Typeface.BOLD, Theme.TEXT);
        view.setLineSpacing(theme.sdp(3), 1f);
        return view;
    }

    public TextView sectionTitle(String value) {
        TextView view = text(value, theme.ssp(TEXT_TITLE), Typeface.BOLD, Theme.TEXT);
        view.setPadding(0, theme.sdp(12), 0, theme.sdp(14));
        view.setLineSpacing(theme.sdp(2), 1f);
        return view;
    }

    /** Small uppercase-ish group label used above toolbars and lists. */
    public TextView eyebrow(String value) {
        return text(value, theme.ssp(TEXT_LABEL), Typeface.BOLD, Theme.MUTED);
    }

    public TextView body(String value) {
        TextView view = text(value, theme.ssp(TEXT_BODY), Typeface.NORMAL, Theme.MUTED);
        view.setLineSpacing(theme.sdp(3), 1f);
        view.setPadding(0, theme.sdp(8), 0, theme.sdp(18));
        return view;
    }

    public TextView caption(String value) {
        return caption(value, Theme.SUBTLE);
    }

    public TextView caption(String value, int color) {
        TextView view = text(value, theme.ssp(TEXT_CAPTION), Typeface.NORMAL, color);
        view.setLineSpacing(theme.sdp(2), 1f);
        return view;
    }

    public TextView empty(String value) {
        TextView view = text(value, theme.ssp(TEXT_BODY), Typeface.NORMAL, Theme.SUBTLE);
        view.setPadding(0, 0, 0, theme.sdp(28));
        return view;
    }

    /** A rounded metadata chip (year, rating, genre). */
    public TextView metaPill(String value) {
        return metaPill(value, Theme.MUTED);
    }

    public TextView metaPill(String value, int color) {
        TextView pill = text(value, theme.ssp(TEXT_LABEL), Typeface.BOLD, color);
        pill.setGravity(Gravity.CENTER);
        pill.setPadding(theme.sdp(13), 0, theme.sdp(13), 0);
        pill.setBackground(theme.panel(Theme.GLASS_SOFT, Theme.BORDER, Theme.STROKE, theme.sdp(999)));
        pill.setMinHeight(theme.sdp(32));
        return pill;
    }

    /** Small chip overlaid on artwork (rating badges). */
    public TextView badge(String value, int color) {
        TextView badge = text(value, theme.ssp(TEXT_CAPTION), Typeface.BOLD, color);
        badge.setGravity(Gravity.CENTER);
        badge.setPadding(theme.sdp(8), 0, theme.sdp(8), 0);
        badge.setMinHeight(theme.sdp(30));
        badge.setBackground(theme.panel(Color.argb(230, 255, 255, 255), Color.TRANSPARENT, 0,
                theme.sdp(7)));
        return badge;
    }

    /** Title over a quieter second line, used by every two-line list row. */
    public static CharSequence stacked(String title, String detail) {
        if (detail == null || detail.isEmpty() || detail.equals(title)) {
            return title;
        }
        SpannableStringBuilder builder = new SpannableStringBuilder();
        builder.append(title).append('\n');
        int start = builder.length();
        builder.append(detail);
        int flags = Spanned.SPAN_EXCLUSIVE_EXCLUSIVE;
        builder.setSpan(new RelativeSizeSpan(0.74f), start, builder.length(), flags);
        builder.setSpan(new ForegroundColorSpan(Theme.SUBTLE), start, builder.length(), flags);
        return builder;
    }

    // MARK: buttons

    /** The standard pill button; {@code active} renders it in the selected state. */
    public Button button(String label, boolean active) {
        Button button = new Button(context);
        button.setText(label);
        button.setTextSize(theme.ssp(17));
        button.setAllCaps(false);
        button.setTextColor(active ? Theme.FOCUS : Theme.TEXT);
        button.setGravity(Gravity.CENTER);
        button.setPadding(theme.sdp(14), 0, theme.sdp(14), 0);
        button.setMinWidth(0);
        button.setMinHeight(0);
        button.setMinimumWidth(0);
        button.setMinimumHeight(0);
        button.setIncludeFontPadding(false);
        button.setBackgroundTintList(null);
        button.setStateListAnimator(null);
        button.setClipToOutline(true);
        button.setBackground(theme.panel(active ? Theme.FOCUS_SOFT : Theme.GLASS,
                active ? Theme.FOCUS : Theme.BORDER, Theme.STROKE, theme.sdp(9)));
        button.setFocusable(true);
        button.setFocusableInTouchMode(false);
        theme.styleButtonFocus(button, active, theme.sdp(9));
        return button;
    }

    public Button button(String label) {
        return button(label, false);
    }

    /** Toolbar button: same shape, one step smaller. */
    public Button toolbarButton(String label, boolean active) {
        Button button = button(label, active);
        button.setTextSize(theme.ssp(15));
        return button;
    }

    public Button toolbarButton(String label) {
        return toolbarButton(label, false);
    }

    /** A two-line row in a list (saved connections, discovered devices, folder entries). */
    public Button listRow(String title, String detail) {
        Button button = button(title);
        button.setText(stacked(title, detail));
        button.setMaxLines(2);
        button.setEllipsize(TextUtils.TruncateAt.END);
        button.setGravity(Gravity.CENTER_VERTICAL | Gravity.LEFT);
        button.setPadding(theme.sdp(16), theme.sdp(10), theme.sdp(16), theme.sdp(10));
        return button;
    }

    /** Layout params for a {@link #listRow(String, String)}: tall enough for both lines. */
    public LinearLayout.LayoutParams listRowParams() {
        LinearLayout.LayoutParams params = margins(column(), 0, 0, 0, theme.sdp(8));
        params.height = theme.sdp(HEIGHT_ROW);
        return params;
    }

    public LinearLayout.LayoutParams buttonParams(int widthDp, int heightDp) {
        return new LinearLayout.LayoutParams(widthDp > 0 ? theme.sdp(widthDp)
                : ViewGroup.LayoutParams.MATCH_PARENT, theme.sdp(heightDp));
    }

    /** Thin progress bar used on continue-watching cards and the player scrubber. */
    public FrameLayout progressBar(int percent, int width, int height) {
        FrameLayout track = new FrameLayout(context);
        track.setBackground(theme.panel(Theme.SURFACE_ALT, Theme.BORDER, Theme.STROKE, height / 2));
        FrameLayout fillView = new FrameLayout(context);
        fillView.setBackground(theme.panel(Theme.FOCUS, Color.TRANSPARENT, 0, height / 2));
        FrameLayout.LayoutParams fillParams = new FrameLayout.LayoutParams(0, height);
        fillParams.width = Math.max(0, Math.min(width, Math.round(width * percent / 100f)));
        track.addView(fillView, fillParams);
        return track;
    }

    // MARK: settings form
    //
    // The mac settings pane is a stack of grouped cards. On TV the same grouping matters even
    // more: without plates the sheet reads as one long pile of identical pills and the eye has
    // nowhere to stop.

    /** Group label above a card. */
    public TextView sectionCaption(String value) {
        TextView view = text(value, theme.ssp(TEXT_LABEL), Typeface.BOLD, Theme.MUTED);
        view.setPadding(theme.sdp(4), theme.sdp(12), 0, theme.sdp(6));
        return view;
    }

    /** One grouped plate; the rows of a single section sit inside it. */
    public LinearLayout settingsCard() {
        LinearLayout card = new LinearLayout(context);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setClipChildren(false);
        card.setClipToPadding(false);
        card.setPadding(theme.sdp(10), theme.sdp(8), theme.sdp(10), theme.sdp(10));
        card.setBackground(theme.panel(Theme.SURFACE_ALT, Theme.BORDER, Theme.STROKE, theme.sdp(12)));
        card.setLayoutParams(margins(column(), 0, 0, 0, theme.sdp(4)));
        return card;
    }

    /** Adds a row to a card, with a hairline between rows so the plate stays readable. */
    public void addCardRow(LinearLayout card, View row) {
        // 按钮如果用 wrap_content 会被压成一行文字的高度，遥控器上几乎按不中。
        addCardRow(card, row, row instanceof Button
                ? buttonParams(0, HEIGHT_COMPACT) : margins(column(), 0, 0, 0, 0));
    }

    public void addCardRow(LinearLayout card, View row, LinearLayout.LayoutParams params) {
        if (card.getChildCount() > 0) {
            View divider = new View(context);
            divider.setBackgroundColor(Theme.withAlpha(Theme.BORDER, 160));
            LinearLayout.LayoutParams dividerParams =
                    margins(column(), 0, theme.sdp(7), 0, theme.sdp(7));
            dividerParams.height = Math.max(1, theme.dp(1));
            card.addView(divider, dividerParams);
        }
        card.addView(row, params);
    }

    /** A row of buttons inside a card: equal weights, tight gaps. */
    public LinearLayout cardButtonRow(View... buttons) {
        LinearLayout row = new LinearLayout(context);
        row.setClipChildren(false);
        row.setClipToPadding(false);
        for (int index = 0; index < buttons.length; index++) {
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(0,
                    theme.sdp(HEIGHT_COMPACT), 1f);
            if (index > 0) {
                params.setMargins(theme.sdp(7), 0, 0, 0);
            }
            row.addView(buttons[index], params);
        }
        return row;
    }

    /** A boolean row: label on the left, a drawn switch on the right. Flip it with {@link SwitchRow#setOn}. */
    public SwitchRow switchRow(String label, boolean on) {
        return new SwitchRow(label, on);
    }

    /** A segmented choice: one plate, one slice per option. */
    public Segment segment(List<String> labels, int selectedIndex, IntConsumer onSelected) {
        return new Segment(labels, selectedIndex, onSelected);
    }

    /** Boolean settings row; {@link #setOn} repaints the switch in place, no panel rebuild. */
    /**
     * A boolean row: label on the left, a drawn switch on the right. Flip it with
     * {@link SwitchRow#setOn}. 开关会直接写出「开 / 关」，因为遥控器上看一个滑块的左右
     * 位置太费眼，尤其是没勾选的时候——用户反馈过「看不出到底关没关」。
     */
    public final class SwitchRow {
        public final Button view;
        private final SwitchArtwork artwork;
        private boolean on;

        SwitchRow(String label, boolean on) {
            view = new Button(context);
            view.setText(label);
            view.setTextSize(theme.ssp(TEXT_BODY));
            view.setAllCaps(false);
            view.setTextColor(Theme.TEXT);
            view.setGravity(Gravity.CENTER_VERTICAL | Gravity.LEFT);
            view.setSingleLine(true);
            view.setEllipsize(TextUtils.TruncateAt.END);
            view.setMinWidth(0);
            view.setMinHeight(0);
            view.setMinimumWidth(0);
            view.setMinimumHeight(0);
            view.setIncludeFontPadding(false);
            view.setPadding(theme.sdp(12), 0, theme.sdp(4), 0);
            view.setBackgroundTintList(null);
            view.setStateListAnimator(null);
            view.setClipToOutline(true);
            view.setFocusable(true);
            view.setFocusableInTouchMode(false);
            artwork = new SwitchArtwork();
            artwork.setBounds(0, 0, theme.sdp(64), theme.sdp(30));
            view.setCompoundDrawables(null, null, artwork, null);
            view.setCompoundDrawablePadding(theme.sdp(12));
            setOn(on);
            theme.styleRowFocus(view);
            view.setLayoutParams(margins(column(), 0, 0, 0, 0));
            view.setMinimumHeight(theme.sdp(HEIGHT_COMPACT));
        }

        public void setOn(boolean on) {
            this.on = on;
            artwork.invalidateSelf();
        }

        private final class SwitchArtwork extends Drawable {
            private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);

            @Override
            public void draw(Canvas canvas) {
                Rect bounds = getBounds();
                float radius = bounds.height() / 2f;
                float inset = theme.dp(1);
                float left = bounds.left + inset;
                float top = bounds.top + inset;
                float right = bounds.right - inset;
                float bottom = bounds.bottom - inset;

                paint.setStyle(Paint.Style.FILL);
                paint.setColor(on ? Theme.FOCUS
                        : Theme.blend(Theme.SURFACE, Theme.MUTED, theme.isDark() ? 0.5f : 0.3f));
                canvas.drawRoundRect(left, top, right, bottom, radius, radius, paint);
                if (!on) {
                    paint.setStyle(Paint.Style.STROKE);
                    paint.setStrokeWidth(theme.dp(1));
                    paint.setColor(Theme.withAlpha(Theme.MUTED, 170));
                    canvas.drawRoundRect(left, top, right, bottom, radius, radius, paint);
                }

                float knobRadius = (bottom - top) / 2f - theme.dp(3);
                float middle = (top + bottom) / 2f;
                float knobX = on ? right - theme.dp(3) - knobRadius : left + theme.dp(3) + knobRadius;
                paint.setStyle(Paint.Style.FILL);
                paint.setColor(on ? Color.WHITE : Theme.SURFACE);
                canvas.drawCircle(knobX, middle, knobRadius, paint);
                if (!on) {
                    paint.setStyle(Paint.Style.STROKE);
                    paint.setStrokeWidth(theme.dp(1));
                    paint.setColor(Theme.MUTED);
                    canvas.drawCircle(knobX, middle, knobRadius, paint);
                }

                // 状态词写在滑块的另一侧，一眼就能读出开 / 关。
                String word = on ? "\u5f00" : "\u5173";
                paint.setStyle(Paint.Style.FILL);
                paint.setColor(on ? Color.WHITE : Theme.MUTED);
                paint.setTextSize(theme.sdp(TEXT_CAPTION));
                paint.setTextAlign(on ? Paint.Align.LEFT : Paint.Align.RIGHT);
                Paint.FontMetrics metrics = paint.getFontMetrics();
                float textY = middle - (metrics.ascent + metrics.descent) / 2f;
                float textX = on ? left + theme.sdp(9) : right - theme.sdp(9);
                canvas.drawText(word, textX, textY, paint);
            }

            @Override
            public void setAlpha(int alpha) {
            }

            @Override
            public void setColorFilter(android.graphics.ColorFilter colorFilter) {
            }

            @Override
            public int getOpacity() {
                return android.graphics.PixelFormat.TRANSLUCENT;
            }
        }
    }

    /**
     * Segmented control. Selecting a slice repaints every slice, so changing a preference costs
     * one key press and no panel rebuild - the sheet keeps its scroll and its focus.
     */
    public final class Segment {
        public final LinearLayout view;
        private final List<Button> slices = new ArrayList<>();
        private final IntConsumer onSelected;
        private int selectedIndex = -1;

        Segment(List<String> labels, int selectedIndex, IntConsumer onSelected) {
            this.onSelected = onSelected;
            view = new LinearLayout(context);
            view.setClipChildren(false);
            view.setClipToPadding(false);
            view.setPadding(theme.sdp(3), theme.sdp(3), theme.sdp(3), theme.sdp(3));
            view.setBackground(theme.panel(Theme.withAlpha(Theme.SURFACE, 220), Theme.BORDER,
                    Theme.STROKE, theme.sdp(11)));
            view.setLayoutParams(margins(column(), 0, 0, 0, 0));
            for (int index = 0; index < labels.size(); index++) {
                final int position = index;
                Button slice = new Button(context);
                slice.setText(labels.get(index));
                slice.setTextSize(theme.ssp(TEXT_LABEL));
                slice.setAllCaps(false);
                slice.setSingleLine(true);
                slice.setEllipsize(TextUtils.TruncateAt.END);
                slice.setGravity(Gravity.CENTER);
                slice.setMinWidth(0);
                slice.setMinHeight(0);
                slice.setMinimumWidth(0);
                slice.setMinimumHeight(0);
                slice.setIncludeFontPadding(false);
                slice.setPadding(theme.sdp(6), 0, theme.sdp(6), 0);
                slice.setBackgroundTintList(null);
                slice.setStateListAnimator(null);
                slice.setClipToOutline(true);
                slice.setFocusable(true);
                slice.setFocusableInTouchMode(false);
                LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(0,
                        theme.sdp(HEIGHT_COMPACT) - theme.sdp(6), 1f);
                if (index > 0) {
                    params.setMargins(theme.sdp(4), 0, 0, 0);
                }
                view.addView(slice, params);
                slice.setOnFocusChangeListener((focusedView, hasFocus) -> {
                    theme.animateFocusScale(focusedView, hasFocus);
                    paint(position);
                });
                slice.setOnClickListener(clicked -> {
                    setSelectedIndex(position);
                    if (onSelected != null) {
                        onSelected.accept(position);
                    }
                });
                slices.add(slice);
            }
            setSelectedIndex(selectedIndex);
        }

        public void setSelectedIndex(int index) {
            selectedIndex = index;
            for (int position = 0; position < slices.size(); position++) {
                paint(position);
            }
        }

        public int selectedIndex() {
            return selectedIndex;
        }

        private void paint(int index) {
            Button slice = slices.get(index);
            boolean focused = slice.isFocused();
            boolean selected = index == selectedIndex;
            slice.setTextColor(focused ? Theme.FOCUS : (selected ? Theme.TEXT : Theme.MUTED));
            slice.setBackground(focused
                    ? theme.panel(Theme.GLASS_STRONG, Theme.FOCUS, theme.STROKE_FOCUS, theme.sdp(8))
                    : selected
                    ? theme.panel(Theme.GLASS_STRONG, Theme.BORDER, Theme.STROKE, theme.sdp(8))
                    : theme.panel(Color.TRANSPARENT, Color.TRANSPARENT, 0, theme.sdp(8)));
            slice.setElevation(focused ? theme.dp(6) : 0);
        }
    }

    /** Single-line text field, styled like the mac app's settings fields. */
    public EditText input(String hint, String value) {
        EditText field = new EditText(context);
        field.setText(value == null ? "" : value);
        field.setHint(hint);
        field.setSingleLine(true);
        // 遥控器上输入的是 Key、URL 这类长串，居中会让首字母看起来被吃掉一截。
        field.setGravity(Gravity.LEFT | Gravity.CENTER_VERTICAL);
        field.setTextSize(theme.ssp(TEXT_HEADLINE));
        field.setTextColor(Theme.TEXT);
        field.setHintTextColor(Theme.SUBTLE);
        field.setPadding(theme.dp(18), 0, theme.dp(18), 0);
        field.setSelectAllOnFocus(false);
        field.setBackground(theme.panel(Theme.SURFACE, Theme.BORDER, theme.dp(2), theme.dp(8)));
        field.setMinHeight(theme.dp(HEIGHT_INPUT));
        field.setFocusable(true);
        field.setFocusableInTouchMode(true);
        theme.styleInputFocus(field);
        return field;
    }
}
