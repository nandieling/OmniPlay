package com.omniplay.tv.phone;

import android.content.Context;
import android.graphics.Color;
import android.graphics.Typeface;
import android.view.Gravity;
import android.view.ViewGroup;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;

import com.omniplay.tv.subtitle.AssSubtitle;
import com.omniplay.tv.subtitle.PgsSubtitleCue;
import com.omniplay.tv.subtitle.PgsSubtitleView;

import java.util.ArrayList;
import java.util.List;

/**
 * 手机端字幕覆盖层。高性能直出时 mpv 不参与渲染，内封字幕转出来的
 * {@code 片名.语言.srt/.ass/.sup} 就靠这一层画：文本轨走 TextView，位图轨走
 * {@link PgsSubtitleView}，两边都按播放位置取当前那一帧。
 */
public final class PhoneSubtitleOverlay {
    private final LinearLayout textLayer;
    private final TextView textView;
    private final PgsSubtitleView pgsView;
    private final List<AssSubtitle.Cue> cues = new ArrayList<>();
    private int shownIndex = -1;

    public PhoneSubtitleOverlay(Context context, FrameLayout root) {
        textLayer = new LinearLayout(context);
        textLayer.setOrientation(LinearLayout.VERTICAL);
        textLayer.setGravity(Gravity.CENTER_HORIZONTAL);
        textLayer.setPadding(PhoneUi.dp((android.app.Activity) context, 24), 0,
                PhoneUi.dp((android.app.Activity) context, 24), 0);
        textView = new TextView(context);
        textView.setTextSize(20);
        textView.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        textView.setTextColor(Color.WHITE);
        textView.setShadowLayer(6f, 0f, 2f, Color.argb(220, 0, 0, 0));
        textView.setGravity(Gravity.CENTER);
        textLayer.addView(textView, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        pgsView = new PgsSubtitleView(context);

        FrameLayout.LayoutParams textParams = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT,
                Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL);
        FrameLayout.LayoutParams pgsParams = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT,
                Gravity.CENTER);
        root.addView(pgsView, pgsParams);
        root.addView(textLayer, textParams);
        textLayer.setVisibility(android.view.View.GONE);
        pgsView.setVisibility(android.view.View.GONE);
    }

    /** 抬高到画面底部、控制条之上；letterbox 是画面自己的黑边高度。 */
    public void setBottomInset(int controlsHeightPx, int letterboxPx) {
        FrameLayout.LayoutParams params = (FrameLayout.LayoutParams) textLayer.getLayoutParams();
        params.bottomMargin = controlsHeightPx + letterboxPx;
        textLayer.setLayoutParams(params);
    }

    /** 位图字幕（.sup）：整份交给自己按 PTS 定时。 */
    public void showPgs(List<PgsSubtitleCue> supCues) {
        cues.clear();
        shownIndex = -1;
        textView.setText("");
        textLayer.setVisibility(android.view.View.GONE);
        pgsView.setCues(supCues);
        pgsView.setVisibility(android.view.View.VISIBLE);
    }

    /** 文本字幕：SRT 与 ASS 都收成同一套 cue。 */
    public void showText(String raw, String format) {
        pgsView.clear();
        pgsView.setVisibility(android.view.View.GONE);
        cues.clear();
        String body = raw == null ? "" : raw;
        if ("ass".equals(format) || AssSubtitle.looksLikeAss(body)) {
            cues.addAll(AssSubtitle.parse(body));
        } else {
            cues.addAll(parseSrt(body));
        }
        shownIndex = -1;
        textLayer.setVisibility(android.view.View.VISIBLE);
        textView.setText("");
    }

    public void clear() {
        cues.clear();
        shownIndex = -1;
        textView.setText("");
        textLayer.setVisibility(android.view.View.GONE);
        pgsView.clear();
        pgsView.setVisibility(android.view.View.GONE);
    }

    /** 播放器每 tick 调一次，把当前时间该显示的那条摆出来。 */
    public void update(double positionSeconds) {
        if (pgsView.getVisibility() == android.view.View.VISIBLE) {
            pgsView.setPlaybackPosition(positionSeconds);
            return;
        }
        if (cues.isEmpty()) {
            return;
        }
        int index = -1;
        for (int i = 0; i < cues.size(); i++) {
            AssSubtitle.Cue cue = cues.get(i);
            if (cue.startSeconds <= positionSeconds && positionSeconds < cue.endSeconds) {
                index = i;
                break;
            }
        }
        if (index == shownIndex) {
            return;
        }
        shownIndex = index;
        textView.setText(index < 0 ? "" : cues.get(index).text);
    }

    /** 极简 SRT：序号行丢掉，``00:00:01,000 --> 00:00:03,000`` 那行取时间，其余拼成文本。 */
    static List<AssSubtitle.Cue> parseSrt(String raw) {
        List<AssSubtitle.Cue> cues = new ArrayList<>();
        String text = raw.replace("\r\n", "\n").replace("\r", "\n");
        String[] blocks = text.split("\n\\s*\n");
        for (String block : blocks) {
            String[] lines = block.split("\n");
            double start = -1;
            double end = -1;
            int bodyStart = 0;
            for (int i = 0; i < lines.length; i++) {
                int arrow = lines[i].indexOf("-->");
                if (arrow > 0) {
                    start = time(lines[i].substring(0, arrow));
                    end = time(lines[i].substring(arrow + 3));
                    bodyStart = i + 1;
                    break;
                }
            }
            if (start < 0 || end <= start) {
                continue;
            }
            StringBuilder builder = new StringBuilder();
            for (int i = bodyStart; i < lines.length; i++) {
                if (builder.length() > 0) {
                    builder.append('\n');
                }
                builder.append(stripTags(lines[i]));
            }
            if (builder.length() > 0) {
                cues.add(new AssSubtitle.Cue(start, end, builder.toString().trim()));
            }
        }
        return cues;
    }

    /** ``<i>`` / ``{\pos(x,y)}`` 这类标记不进覆盖层。 */
    private static String stripTags(String line) {
        return line.replaceAll("<[^>]*>", "").replaceAll("\\{[^}]*\\}", "");
    }

    private static double time(String value) {
        String trimmed = value.trim().replace(',', '.');
        String[] parts = trimmed.split(":");
        if (parts.length < 2) {
            return -1;
        }
        double seconds = 0;
        for (String part : parts) {
            try {
                seconds = seconds * 60 + Double.parseDouble(part.trim());
            } catch (NumberFormatException invalid) {
                return -1;
            }
        }
        return seconds;
    }
}
