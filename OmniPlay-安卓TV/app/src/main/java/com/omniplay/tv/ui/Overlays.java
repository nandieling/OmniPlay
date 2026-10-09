package com.omniplay.tv.ui;

import android.graphics.Color;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/**
 * The one implementation of "a panel on top of the current screen".
 * <p>
 * The TV app used to hand-roll this six times (login, folder picker, settings, media sources,
 * phone input, text input), each with its own dim view, its own focus save/restore and its own
 * place in the back-key chain, which is exactly how dialogs ended up stealing focus, surviving
 * their own close, or being skipped by the back button. Screens now {@link #open open} an
 * {@link Overlay} and the back key walks {@link #closeTop()}, always in the right order.
 */
public final class Overlays {
    /** How a panel should look and behave; build it, then fill {@link Overlay#body}. */
    public static final class Options {
        public String title = "";
        public String subtitle = "";
        public int widthDp = 560;
        /** Side sheets (media sources, settings) hug the right edge; decisions sit centred. */
        public boolean sideSheet;
        public boolean dismissOnBackdrop = true;
        /** Control to focus once the panel closes; normally the control that opened it. */
        public View restoreFocus;
        public int maxHeightDp = 0;
        /**
         * False for fixed-height panels that host their own scrolling list (the folder picker).
         * Those sit directly in the host so two scroll containers never fight over the D-pad.
         */
        public boolean scrollable = true;
    }

    /** An open panel. Add controls to {@link #body}, then call {@link #focus}. */
    public static final class Overlay {
        public final LinearLayout body;
        /** Null for {@link Options#scrollable} panels that scroll themselves. */
        public final ScrollView scroll;
        public final View backdrop;
        final Options options;
        /**
         * Runs whenever this panel goes away, including when the back key closes it through
         * {@link Overlays#closeTop()}. Screens keep a field pointing at their own panel to guard
         * against opening it twice; without this hook that field survives a BACK dismissal and
         * the button that opens the panel looks dead.
         */
        public Runnable onClose;
        private final Overlays owner;
        private final List<List<View>> rows = new ArrayList<>();
        private boolean open;
        private boolean gridPushed;

        Overlay(Overlays owner, Options options, View backdrop, ScrollView scroll, LinearLayout body) {
            this.owner = owner;
            this.options = options;
            this.backdrop = backdrop;
            this.scroll = scroll;
            this.body = body;
            this.open = true;
        }

        public boolean isOpen() {
            return open;
        }

        /** Declares the D-pad layout of this panel and focuses the first control. */
        public void focus(List<List<View>> focusRows, View initial) {
            rows.clear();
            rows.addAll(focusRows);
            if (gridPushed) {
                // Already the top grid: re-link in place instead of stacking another copy,
                // and leave the screen grid underneath alone.
                owner.grid.replaceTop(rows);
            } else {
                owner.grid.push(rows, initial);
                gridPushed = true;
            }
            if (initial != null) {
                FocusGrid.grab(initial);
            }
            if (scroll != null) {
                // scrollTo, not fullScroll: fullScroll moves focus to the container itself when
                // nothing is focusable above, which leaves a panel open with no visible focus ring.
                scroll.scrollTo(0, 0);
            }
        }

        public void focus(View initial) {
            focus(Collections.singletonList(Collections.singletonList(initial)), initial);
        }

        /**
         * Re-links the grid after the panel's own contents changed (a folder list reloaded).
         * Focus stays where the user left it.
         */
        public void redeclare(List<List<View>> focusRows) {
            // Re-declaring is a relink, not a navigation event: the page must not move.
            int keep = scroll == null ? 0 : scroll.getScrollY();
            focus(focusRows, null);
            if (scroll != null) {
                scroll.scrollTo(0, keep);
            }
        }

        /** Removes the panel and hands focus back; safe to call twice. */
        public boolean close() {
            if (!open) {
                return false;
            }
            open = false;
            owner.host.removeView(body);
            if (scroll != null) {
                owner.host.removeView(scroll);
            }
            owner.host.removeView(backdrop);
            owner.stack.remove(this);
            if (gridPushed) {
                owner.grid.pop(options.restoreFocus);
            }
            if (onClose != null) {
                onClose.run();
            }
            return true;
        }

        /**
         * The status line every panel gets, so progress and errors are reported the same way
         * everywhere. It shows itself when something is written to it and hides when cleared,
         * which keeps panels from reserving space for a message that never comes.
         */
        public android.widget.TextView statusLine() {
            return status;
        }

        StatusText status;
    }

    /** A label that appears only while it has something to say. */
    public static final class StatusText extends android.widget.TextView {
        public StatusText(android.content.Context context, int color, int sizePx, Theme theme) {
            super(context);
            setTextSize(sizePx);
            setTextColor(color);
            setLineSpacing(theme.sdp(2), 1f);
            setVisibility(GONE);
        }

        @Override
        public void setText(CharSequence text, BufferType type) {
            super.setText(text, type);
            setVisibility(text == null || text.length() == 0 ? GONE : VISIBLE);
        }
    }

    private final FrameLayout host;
    private final Theme theme;
    private final Widgets widgets;
    private final FocusGrid grid;
    private final List<Overlay> stack = new ArrayList<>();

    public Overlays(FrameLayout host, Theme theme, Widgets widgets, FocusGrid grid) {
        this.host = host;
        this.theme = theme;
        this.widgets = widgets;
        this.grid = grid;
    }

    /** Adds a dimmed panel and returns it; nothing is focused until {@link Overlay#focus}. */
    public Overlay open(Options options) {
        FrameLayout dim = new FrameLayout(theme.context());
        dim.setBackgroundColor(Theme.SCRIM);
        dim.setFocusable(false);
        if (options.dismissOnBackdrop) {
            dim.setOnClickListener(view -> closeTop());
        }
        host.addView(dim, Widgets.fill());

        LinearLayout panel = new LinearLayout(theme.context());
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(theme.sdp(28), theme.sdp(24), theme.sdp(28), theme.sdp(24));
        panel.setClipChildren(false);
        panel.setClipToPadding(false);
        // A side sheet is a real drawer: full height, flush with the right edge, no rounded
        // corners on the flush side. Everything else is a centred card.
        panel.setBackground(theme.panel(Theme.SURFACE, Theme.BORDER, theme.dp(2),
                options.sideSheet ? 0 : theme.dp(14)));
        panel.setElevation(theme.dp(18));

        int width = theme.sdp(options.widthDp);
        FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(width,
                options.sideSheet ? ViewGroup.LayoutParams.MATCH_PARENT
                        : ViewGroup.LayoutParams.WRAP_CONTENT,
                options.sideSheet ? (Gravity.TOP | Gravity.RIGHT) : Gravity.CENTER);
        if (options.sideSheet) {
            params.setMargins(0, 0, 0, 0);
        }
        if (options.maxHeightDp > 0 && !options.sideSheet) {
            params.height = theme.sdp(options.maxHeightDp);
        }
        ScrollView scroll = null;
        if (options.scrollable) {
            scroll = new ScrollView(theme.context());
            scroll.setFocusable(false);
            scroll.setFocusableInTouchMode(false);
            scroll.setClipChildren(false);
            scroll.setClipToPadding(false);
            scroll.setFillViewport(true);
            host.addView(scroll, params);
            scroll.addView(panel, new ScrollView.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,
                    ViewGroup.LayoutParams.WRAP_CONTENT));
        } else {
            host.addView(panel, params);
        }

        if (options.title != null && !options.title.isEmpty()) {
            panel.addView(widgets.text(options.title, theme.ssp(Widgets.TEXT_TITLE),
                    android.graphics.Typeface.BOLD, Theme.TEXT),
                    Widgets.margins(Widgets.column(), 0, 0, 0, theme.sdp(4)));
        }
        if (options.subtitle != null && !options.subtitle.isEmpty()) {
            panel.addView(widgets.body(options.subtitle),
                    Widgets.margins(Widgets.column(), 0, 0, 0, theme.sdp(10)));
        }

        Overlay overlay = new Overlay(this, options, dim, scroll, panel);
        overlay.status = new StatusText(theme.context(), Theme.DANGER,
                theme.ssp(Widgets.TEXT_LABEL), theme);
        overlay.status.setMaxLines(3);
        panel.addView(overlay.status, Widgets.margins(Widgets.column(), 0, 0, 0, theme.sdp(8)));
        stack.add(overlay);
        return overlay;
    }

    /** True when at least one panel is open. */
    public boolean hasOpen() {
        return !stack.isEmpty();
    }

    public Overlay top() {
        return stack.isEmpty() ? null : stack.get(stack.size() - 1);
    }

    /** Closes the panel the user would expect the back button to close. */
    public boolean closeTop() {
        return !stack.isEmpty() && stack.get(stack.size() - 1).close();
    }

    /** Used when the screen underneath is replaced entirely. */
    public void closeAll() {
        while (!stack.isEmpty()) {
            stack.get(stack.size() - 1).close();
        }
    }

    /** A horizontal button row: the standard footer of every panel. */
    public LinearLayout footer(LinearLayout container, View... buttons) {
        LinearLayout row = new LinearLayout(theme.context());
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setClipChildren(false);
        row.setClipToPadding(false);
        container.addView(row, Widgets.margins(Widgets.column(), 0, theme.sdp(6), 0, 0));
        for (int index = 0; index < buttons.length; index++) {
            View button = buttons[index];
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(0,
                    theme.sdp(Widgets.HEIGHT_BUTTON), 1f);
            if (index > 0) {
                params.setMargins(theme.sdp(10), 0, 0, 0);
            }
            row.addView(button, params);
        }
        return row;
    }

    /** A translucent full-bleed scrim used by the player, which owns its own layout. */
    public View scrim() {
        FrameLayout dim = new FrameLayout(theme.context());
        dim.setBackgroundColor(Color.argb(150, 0, 0, 0));
        dim.setFocusable(false);
        return dim;
    }
}
