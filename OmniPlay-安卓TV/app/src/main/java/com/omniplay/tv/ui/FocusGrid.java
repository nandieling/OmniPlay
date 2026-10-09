package com.omniplay.tv.ui;

import android.view.KeyEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.ViewParent;
import android.graphics.Rect;
import android.widget.EditText;
import android.widget.HorizontalScrollView;
import android.widget.LinearLayout;
import android.widget.ScrollView;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/**
 * D-pad navigation for the whole app.
 * <p>
 * Android's default focus search is unusable on a TV: it walks whatever happens to be next in the
 * view tree, so arrow keys jump across a poster wall or get stuck inside an EditText. A screen
 * therefore describes its controls as rows (top to bottom, left to right) and this class wires the
 * explicit next-focus links and routes the arrow keys. Overlays {@link #push push} a grid and
 * {@link #pop pop} it, so closing a dialog always lands the focus back where it was, and grids
 * may nest (a dialog opening a second dialog no longer corrupts the backup).
 */
public final class FocusGrid {
    /** Focused rows stay this far from the edge of a TV screen; overscan eats the outer band. */
    private static final int FOCUS_MARGIN_DP = 12;

    /** Grids of the panels layered on top of the screen, innermost last. */
    private final List<List<List<View>>> stack = new ArrayList<>();
    /** The screen's own grid, kept current even while a panel covers it. */
    private List<List<View>> base = Collections.emptyList();
    private List<List<View>> active = Collections.emptyList();
    /** Last cell the remote moved to; the fallback when a rebuilt control lost its cell. */
    private int lastRow;
    private int lastColumn;

    /**
     * Replaces the grid of the screen underneath. Safe to call while a panel is open: an
     * overlay's grid stays active, and the panel's rows are refreshed so that closing it lands
     * on the current layout instead of the one from before the panel opened.
     */
    public void apply(List<List<View>> rows) {
        List<List<View>> cleaned = clean(rows);
        link(cleaned);
        base = cleaned;
        if (stack.isEmpty()) {
            active = cleaned;
        } else {
            stack.set(0, cleaned);
        }
    }

    /** Activates the grid of {@code rows} and focuses {@code initial} (first button if null). */
    public void push(List<List<View>> rows, View initial) {
        List<List<View>> cleaned = clean(rows);
        link(cleaned);
        if (!active.isEmpty()) {
            stack.add(active);
        }
        active = cleaned;
        View target = initial != null ? initial : firstButton(cleaned);
        if (target != null) {
            target.requestFocus();
        }
    }

    /**
     * Replaces the grid on top of the stack: a panel that rebuilt its own controls re-declares
     * its rows without touching the screen grid underneath and without stacking a copy.
     */
    public void replaceTop(List<List<View>> rows) {
        List<List<View>> cleaned = clean(rows);
        link(cleaned);
        active = cleaned;
    }

    /**
     * Leaves the top grid and restores the one below it.
     *
     * @param restore the control that had focus before the overlay opened; when it is gone (the
     *                screen redrew underneath) the first usable control of the restored grid wins
     */
    public View pop(View restore) {
        if (!stack.isEmpty()) {
            active = stack.remove(stack.size() - 1);
        } else {
            active = base;
        }
        View target = usable(restore) ? restore : firstButton(active);
        if (target != null) {
            target.requestFocus();
        }
        return target;
    }

    /** Drops every grid, e.g. when the screen is replaced wholesale. */
    public void clear() {
        stack.clear();
        active = Collections.emptyList();
        base = Collections.emptyList();
    }

    public boolean isEmpty() {
        return active.isEmpty();
    }

    public List<List<View>> rows() {
        return active;
    }

    /** Every control of the active grid, in reading order. */
    public List<View> flat() {
        List<View> flat = new ArrayList<>();
        for (List<View> row : active) {
            flat.addAll(row);
        }
        return flat;
    }

    /**
     * Routes an arrow key through the grid.
     *
     * @param focused the control that currently holds focus, so the move starts from a known cell
     * @return true when the key was handled, i.e. focus moved
     */
    public boolean handleKey(KeyEvent event, View focused) {
        if (event == null || event.getAction() != KeyEvent.ACTION_DOWN || active.isEmpty()) {
            return false;
        }
        if (directionOf(event.getKeyCode()) == 0) {
            return false;
        }
        int[] position = locate(active, locateFallback(focused));
        if (position == null) {
            // The control that had focus is not part of this grid any more (the panel rebuilt
            // itself). Continuing from the cell the user was on beats throwing them to the top.
            position = new int[]{clampRow(lastRow), clampColumn(lastRow, lastColumn)};
        }
        int row = position[0];
        int column = position[1];
        switch (event.getKeyCode()) {
            case KeyEvent.KEYCODE_DPAD_UP:
                row = Math.max(0, row - 1);
                break;
            case KeyEvent.KEYCODE_DPAD_DOWN:
                row = Math.min(active.size() - 1, row + 1);
                break;
            case KeyEvent.KEYCODE_DPAD_LEFT:
                column = Math.max(0, column - 1);
                break;
            default:
                column = Math.min(sizeOf(row) - 1, column + 1);
                break;
        }
        column = Math.min(column, Math.max(0, sizeOf(row) - 1));
        List<View> targetRow = active.get(row);
        if (targetRow.isEmpty()) {
            return false;
        }
        View target = targetRow.get(Math.min(column, targetRow.size() - 1));
        lastRow = row;
        lastColumn = Math.min(column, targetRow.size() - 1);
        target.requestFocus();
        if (row == 0) {
            scrollToEdge(target, true);
        } else if (row == active.size() - 1) {
            scrollToEdge(target, false);
        } else {
            scrollToVisible(target);
        }
        return true;
    }

    /** Focuses the first non-text control of the grid: the remote should never wake on a keyboard. */
    public void focusFirst() {
        View target = firstButton(active);
        if (target != null) {
            target.requestFocus();
        }
    }

    /** The control that should take focus when a grid has nothing better to offer. */
    public static View firstButton(List<List<View>> rows) {
        for (List<View> row : rows) {
            for (View view : row) {
                if (!(view instanceof EditText) && usable(view) && !isScrollContainer(view)) {
                    return view;
                }
            }
        }
        for (List<View> row : rows) {
            for (View view : row) {
                if (usable(view)) {
                    return view;
                }
            }
        }
        return null;
    }

    /**
     * Focuses a control that may not be laid out yet. A freshly added panel refuses the first
     * requestFocus(), and the window then falls back to whatever container is still focusable,
     * which shows no focus ring at all; retrying once after layout keeps the opening focus
     * deliberate.
     */
    public static void grab(View view) {
        if (view == null) {
            return;
        }
        view.requestFocus();
        if (view.isFocused()) {
            return;
        }
        view.post(() -> {
            if (!view.isFocused() && view.getWindowToken() != null) {
                view.requestFocus();
            }
        });
    }

    /**
     * A control that redrew itself keeps its identity but can lose its cell; falling back to
     * whichever control of this grid reports focus keeps navigation sane instead of silently
     * restarting from the top row.
     */
    private View locateFallback(View focused) {
        if (focused != null && locate(active, focused) != null) {
            return focused;
        }
        for (List<View> row : active) {
            for (View view : row) {
                if (view.isFocused()) {
                    return view;
                }
            }
        }
        return focused;
    }

    private int sizeOf(int row) {
        return row < active.size() ? active.get(row).size() : 0;
    }

    private int clampRow(int row) {
        return Math.max(0, Math.min(Math.max(0, active.size() - 1), row));
    }

    private int clampColumn(int row, int column) {
        return Math.max(0, Math.min(Math.max(0, sizeOf(row) - 1), column));
    }

    private static int directionOf(int keyCode) {
        switch (keyCode) {
            case KeyEvent.KEYCODE_DPAD_UP:
                return View.FOCUS_UP;
            case KeyEvent.KEYCODE_DPAD_DOWN:
                return View.FOCUS_DOWN;
            case KeyEvent.KEYCODE_DPAD_LEFT:
                return View.FOCUS_LEFT;
            case KeyEvent.KEYCODE_DPAD_RIGHT:
                return View.FOCUS_RIGHT;
            default:
                return 0;
        }
    }

    /** Removes null and empty rows; an empty row used to index out of bounds during navigation. */
    private static List<List<View>> clean(List<List<View>> rows) {
        List<List<View>> cleaned = new ArrayList<>();
        if (rows == null) {
            return cleaned;
        }
        for (List<View> row : rows) {
            if (row == null || row.isEmpty()) {
                continue;
            }
            List<View> views = new ArrayList<>();
            for (View view : row) {
                if (view != null) {
                    views.add(view);
                }
            }
            if (!views.isEmpty()) {
                cleaned.add(views);
            }
        }
        return cleaned;
    }

    /** Explicit links: the framework's own search would cross the poster wall at random. */
    private static void link(List<List<View>> rows) {
        for (List<View> row : rows) {
            for (View view : row) {
                if (view.getId() == View.NO_ID) {
                    view.setId(View.generateViewId());
                }
            }
        }
        for (int r = 0; r < rows.size(); r++) {
            List<View> row = rows.get(r);
            for (int c = 0; c < row.size(); c++) {
                View view = row.get(c);
                view.setNextFocusLeftId(c > 0 ? row.get(c - 1).getId() : view.getId());
                view.setNextFocusRightId(c < row.size() - 1 ? row.get(c + 1).getId() : view.getId());
                int downColumn = Math.min(c, size(rows, r + 1) - 1);
                int upColumn = Math.min(c, size(rows, r - 1) - 1);
                view.setNextFocusDownId(r + 1 < rows.size() ? pick(rows, r + 1, downColumn) : view.getId());
                view.setNextFocusUpId(r > 0 ? pick(rows, r - 1, upColumn) : view.getId());
            }
        }
    }

    private static int size(List<List<View>> rows, int index) {
        return index >= 0 && index < rows.size() ? rows.get(index).size() : 0;
    }

    private static int pick(List<List<View>> rows, int rowIndex, int column) {
        List<View> row = rows.get(rowIndex);
        return row.get(Math.min(Math.max(column, 0), row.size() - 1)).getId();
    }

    /**
     * A scroll container takes focus only so the framework can scroll it, and it draws no focus
     * ring of its own, so it is never the control a panel should open on.
     */
    private static boolean isScrollContainer(View view) {
        return view instanceof ScrollView || view instanceof HorizontalScrollView;
    }

    private static boolean usable(View view) {
        return view != null && view.getVisibility() == View.VISIBLE && view.isEnabled()
                && attached(view);
    }

    private static boolean attached(View view) {
        ViewParent parent = view.getParent();
        while (parent instanceof ViewGroup) {
            parent = parent.getParent();
        }
        return parent != null;
    }

    /**
     * Keeps the focused control on screen inside whichever scroll container holds it.
     * <p>
     * The wanted offset is computed from layout coordinates only, never from the live scroll
     * offset: a ScrollView has already scrolled the newly focused control to its own edge by the
     * time this runs, and reading that offset again is what used to scroll the page twice and read
     * as a bounce. A margin keeps the focused row off the very edge of a TV screen.
     */
    public static void scrollToVisible(View target) {
        final ScrollView scroll = scrollAncestorOf(target);
        if (scroll == null) {
            return;
        }
        followVertically(scroll, target);
        scroll.post(() -> followVertically(scroll, target));
    }

    /**
     * Parks the viewport at the top (or the bottom) of a panel: the first row of a sheet should
     * show the panel title, and the last row should show its footer. Without this the framework
     * aligns the first focusable control with the top edge, which leaves the header scrolled away
     * and reads as "this dialog cannot be scrolled back to the top".
     */
    public static void scrollToEdge(View target, boolean top) {
        ScrollView scroll = scrollAncestorOf(target);
        if (scroll == null) {
            return;
        }
        View content = contentOf(scroll);
        if (content == null) {
            return;
        }
        scroll.scrollTo(0, top ? 0 : maxScrollY(scroll, content));
    }

    private static void followVertically(ScrollView scroll, View view) {
        View content = contentOf(scroll);
        if (content == null || scroll.getHeight() <= 0 || view.getHeight() <= 0) {
            return;
        }
        int contentY = contentOffset(scroll, content, view);
        int viewport = scroll.getHeight();
        int margin = Math.min(view.getHeight() / 2, FOCUS_MARGIN_DP * density(scroll));
        int scrollY = scroll.getScrollY();
        int top = contentY - scrollY;
        int wanted = scrollY;
        if (top + view.getHeight() > viewport - margin) {
            wanted = contentY + view.getHeight() - viewport + margin;
        } else if (top < margin) {
            wanted = contentY - margin;
        }
        if (contentY <= firstRowTop(scroll, content) + margin) {
            // 焦点已经在第一行：让弹层标题露出来，而不是把它顶出屏幕。
            wanted = 0;
        }
        scroll.scrollTo(0, Math.max(0, Math.min(maxScrollY(scroll, content), wanted)));
    }

    /** The content offset of the first focusable control of a scroll content. */
    private static int firstRowTop(ScrollView scroll, View content) {
        int top = Integer.MAX_VALUE;
        if (content instanceof ViewGroup) {
            top = firstFocusableOffset(scroll, content, (ViewGroup) content, content.getTop());
        }
        return top == Integer.MAX_VALUE ? 0 : top;
    }

    private static int firstFocusableOffset(ScrollView scroll, View content, ViewGroup group, int base) {
        int top = Integer.MAX_VALUE;
        for (int index = 0; index < group.getChildCount(); index++) {
            View child = group.getChildAt(index);
            if (child.getVisibility() != View.VISIBLE) {
                continue;
            }
            int offset = base + child.getTop() - (child == content ? 0 : 0);
            if (child.isFocusable()) {
                top = Math.min(top, offset);
            } else if (child instanceof ViewGroup) {
                top = Math.min(top, firstFocusableOffset(scroll, content, (ViewGroup) child, offset));
            }
        }
        return top;
    }

    private static View contentOf(ScrollView scroll) {
        return scroll.getChildCount() > 0 ? scroll.getChildAt(0) : null;
    }

    private static int maxScrollY(ScrollView scroll, View content) {
        return Math.max(0, content.getTop() + content.getHeight() - scroll.getHeight());
    }

    private static int density(View view) {
        return Math.max(1, Math.round(view.getResources().getDisplayMetrics().density));
    }

    private static ScrollView scrollAncestorOf(View view) {
        ViewParent parent = view.getParent();
        while (parent != null) {
            if (parent instanceof ScrollView) {
                return (ScrollView) parent;
            }
            parent = parent.getParent();
        }
        return null;
    }

    /** Where {@code view} sits inside the scrollable content, independent of the scroll offset. */
    private static int contentOffset(ScrollView scroll, View content, View view) {
        return viewportOffset(scroll, view)[1] - content.getTop();
    }

    /**
     * The raw chain of {@code getTop()}/{@code getLeft()} values up to {@code scroll}, which is
     * what layout-based grouping needs (a stable, scroll-independent position).
     */
    public static int[] viewportOffset(View scroll, View view) {
        int left = 0;
        int top = 0;
        View current = view;
        while (current != null && current != scroll) {
            left += current.getLeft();
            top += current.getTop();
            ViewParent parent = current.getParent();
            current = parent instanceof View ? (View) parent : null;
        }
        return new int[]{left, top};
    }

    /**
     * Rows for a panel that stacks its controls in a column, so the arrow keys walk the sheet in
     * the order the user sees. Panels used to hand-maintain their row list; every control added in
     * the middle silently desynchronised the D-pad from the layout.
     * <p>
     * Before the first layout pass there are no coordinates to group by, so siblings of a
     * horizontal container become one row instead; {@code afterLayout} re-declares with the real
     * geometry as soon as the panel has been measured.
     */
    public static List<List<View>> rowsFrom(ViewGroup panel) {
        List<List<View>> rows = new ArrayList<>();
        if (panel == null) {
            return rows;
        }
        // Before the first layout pass there is nothing to group by; fall back to the structure.
        if (panel.isLaidOut() && !panel.isLayoutRequested()) {
            List<Rect> boxes = new ArrayList<>();
            List<View> leaves = new ArrayList<>();
            collectBoxes(panel, panel, boxes, leaves);
            List<Integer> order = new ArrayList<>();
            for (int index = 0; index < leaves.size(); index++) {
                order.add(index);
            }
            Collections.sort(order, (left, right) -> {
                Rect a = boxes.get(left);
                Rect b = boxes.get(right);
                if (a.top != b.top) {
                    return a.top - b.top;
                }
                return a.left - b.left;
            });
            List<View> current = new ArrayList<>();
            int anchorCenter = 0;
            int rowHeight = 0;
            for (int index : order) {
                Rect box = boxes.get(index);
                // 行首控件的中线做锚点：锚点不随合并长大，才不会一路把后面的行都吞进来。
                boolean sameRow = !current.isEmpty()
                        && Math.abs((box.top + box.bottom) / 2 - anchorCenter)
                        <= Math.max(box.height(), rowHeight) / 2;
                if (!current.isEmpty() && !sameRow) {
                    sortRowByLeft(panel, current);
                    rows.add(current);
                    current = new ArrayList<>();
                }
                if (current.isEmpty()) {
                    anchorCenter = (box.top + box.bottom) / 2;
                    rowHeight = box.height();
                }
                current.add(leaves.get(index));
            }
            if (!current.isEmpty()) {
                sortRowByLeft(panel, current);
                rows.add(current);
            }
            return rows;
        }
        collectStructuralRows(panel, rows);
        return rows;
    }

    /** Runs {@code action} once {@code view} has really been laid out, not merely posted. */
    public static void afterLayout(View view, Runnable action) {
        afterLayout(view, action, 6);
    }

    private static void afterLayout(View view, Runnable action, int retries) {
        view.post(() -> {
            if (view.isLayoutRequested() && retries > 0) {
                afterLayout(view, action, retries - 1);
            } else {
                action.run();
            }
        });
    }

    private static void sortRowByLeft(View panel, List<View> row) {
        Collections.sort(row, (left, right) ->
                viewportOffset(panel, left)[0] - viewportOffset(panel, right)[0]);
    }

    /** Focusable controls of {@code panel} with their box in the panel's own coordinates. */
    private static void collectBoxes(ViewGroup panel, ViewGroup group, List<Rect> boxes,
                                     List<View> leaves) {
        for (int index = 0; index < group.getChildCount(); index++) {
            View child = group.getChildAt(index);
            if (child.getVisibility() != View.VISIBLE) {
                continue;
            }
            if (child.isFocusable()) {
                // 逐层累加 getTop()/getLeft()：坐标完全由布局决定，比 getHitRect +
                // offsetDescendantRectToMyCoords 可靠（后者在带滚动与缩放的层级里会飘）。
                int[] offset = viewportOffset(panel, child);
                boxes.add(new Rect(offset[0], offset[1],
                        offset[0] + child.getWidth(), offset[1] + child.getHeight()));
                leaves.add(child);
            } else if (child instanceof ViewGroup) {
                collectBoxes(panel, (ViewGroup) child, boxes, leaves);
            }
        }
    }

    /** Fallback before layout: one row per horizontal container, one per vertical child. */
    private static void collectStructuralRows(ViewGroup group, List<List<View>> rows) {
        boolean horizontal = !(group instanceof LinearLayout)
                || ((LinearLayout) group).getOrientation() == LinearLayout.HORIZONTAL;
        if (horizontal) {
            List<View> leaves = new ArrayList<>();
            collectLeaves(group, leaves);
            if (!leaves.isEmpty()) {
                rows.add(leaves);
            }
            return;
        }
        for (int index = 0; index < group.getChildCount(); index++) {
            View child = group.getChildAt(index);
            if (child.getVisibility() != View.VISIBLE) {
                continue;
            }
            if (child.isFocusable()) {
                rows.add(Collections.singletonList(child));
            } else if (child instanceof ViewGroup) {
                collectStructuralRows((ViewGroup) child, rows);
            }
        }
    }

    private static void collectLeaves(ViewGroup group, List<View> out) {
        for (int index = 0; index < group.getChildCount(); index++) {
            View child = group.getChildAt(index);
            if (child.getVisibility() != View.VISIBLE) {
                continue;
            }
            if (child.isFocusable()) {
                out.add(child);
            } else if (child instanceof ViewGroup) {
                collectLeaves((ViewGroup) child, out);
            }
        }
    }

    /** Where {@code view} sits in {@code rows}, or null when it is not part of that grid. */
    public static int[] locate(List<List<View>> rows, View view) {
        if (view == null) {
            return null;
        }
        for (int r = 0; r < rows.size(); r++) {
            List<View> row = rows.get(r);
            for (int c = 0; c < row.size(); c++) {
                if (row.get(c) == view) {
                    return new int[]{r, c};
                }
            }
        }
        return null;
    }
}
