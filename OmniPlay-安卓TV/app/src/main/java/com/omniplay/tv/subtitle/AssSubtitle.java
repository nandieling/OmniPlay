package com.omniplay.tv.subtitle;

import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

/**
 * ASS / SSA 文本字幕读取：只取台词，不做样式与定位。转档产物（{@code 片名.zh.ass}）是喂给播放页
 * 字幕覆盖层的，覆盖层只会画纯文本，所以这里把 {@code {\pos}}、{@code \N} 这些控制序列抹平就够。
 */
public final class AssSubtitle {
    public static final class Cue {
        public final double startSeconds;
        public final double endSeconds;
        public final String text;

        Cue(double startSeconds, double endSeconds, String text) {
            this.startSeconds = startSeconds;
            this.endSeconds = endSeconds;
            this.text = text;
        }
    }

    private AssSubtitle() {
    }

    /** 头部几个段落标记、或者出现 Dialogue 行，就按 ASS 处理；SRT 里没有这些。 */
    public static boolean looksLikeAss(String text) {
        if (text == null || text.isEmpty()) {
            return false;
        }
        String head = text.length() > 8192 ? text.substring(0, 8192) : text;
        String lower = head.toLowerCase(Locale.ROOT);
        return lower.contains("[script info]") || lower.contains("[v4+ styles]")
                || lower.contains("[v4 styles]") || lower.contains("[events]")
                || text.contains("Dialogue:");
    }

    public static List<Cue> parse(String text) {
        ArrayList<Cue> cues = new ArrayList<>();
        if (text == null || text.isEmpty()) {
            return cues;
        }

        String[] lines = text
                .replace("\uFEFF", "")
                .replace("\r\n", "\n")
                .replace('\r', '\n')
                .split("\n", -1);
        boolean inEvents = false;
        // 真实文件都会先给一行 Format: 说明字段顺序，按它来；万一没有就按 v4.00+ 的默认顺序。
        int startField = 1;
        int endField = 2;
        int textField = 9;
        for (String raw : lines) {
            String line = raw.trim();
            if (line.isEmpty()) {
                continue;
            }
            if (line.startsWith("[")) {
                inEvents = line.toLowerCase(Locale.ROOT).startsWith("[events]");
                continue;
            }
            if (!inEvents) {
                continue;
            }
            if (line.toLowerCase(Locale.ROOT).startsWith("format:")) {
                int[] fields = readFormat(line.substring(line.indexOf(':') + 1));
                if (fields != null) {
                    startField = fields[0];
                    endField = fields[1];
                    textField = fields[2];
                }
                continue;
            }
            int colon = line.indexOf(':');
            if (colon <= 0 || !line.substring(0, colon).toLowerCase(Locale.ROOT).startsWith("dialogue")) {
                // Comment、DialogueParsed 之类一概不理
                continue;
            }
            String body = line.substring(colon + 1);
            String[] parts = splitFields(body, textField);
            if (parts.length <= textField) {
                continue;
            }
            double start = parseTimestamp(valueAt(parts, startField));
            double end = parseTimestamp(valueAt(parts, endField));
            String cue = plainText(valueAt(parts, textField));
            if (end > start && !cue.isEmpty()) {
                cues.add(new Cue(start, end, cue));
            }
        }
        return cues;
    }

    /** {@code Format: Layer, Start, End, Style, …, Text} → 需要的三个下标；认不出来返回 null。 */
    private static int[] readFormat(String format) {
        int start = -1;
        int end = -1;
        int text = -1;
        String[] names = format.split(",");
        for (int index = 0; index < names.length; index++) {
            String name = names[index].trim().toLowerCase(Locale.ROOT);
            if ("start".equals(name) && start < 0) {
                start = index;
            } else if ("end".equals(name) && end < 0) {
                end = index;
            } else if ("text".equals(name)) {
                text = index;
            }
        }
        return start >= 0 && end >= 0 && text > 0 ? new int[]{start, end, text} : null;
    }

    /** 只切到 Text 那一段为止：台词里本来就能有逗号。 */
    private static String[] splitFields(String body, int textField) {
        String[] parts = new String[textField + 1];
        int cursor = 0;
        for (int index = 0; index < textField; index++) {
            int comma = body.indexOf(',', cursor);
            if (comma < 0) {
                return new String[0];
            }
            parts[index] = body.substring(cursor, comma);
            cursor = comma + 1;
        }
        parts[textField] = body.substring(cursor);
        return parts;
    }

    private static String valueAt(String[] parts, int index) {
        return index >= 0 && index < parts.length && parts[index] != null ? parts[index].trim() : "";
    }

    /** {@code 0:01:22.35} → 秒。 */
    private static double parseTimestamp(String value) {
        String[] parts = value.trim().split(":");
        try {
            if (parts.length == 3) {
                return Integer.parseInt(parts[0].trim()) * 3600d
                        + Integer.parseInt(parts[1].trim()) * 60d
                        + Double.parseDouble(parts[2].trim().replace(',', '.'));
            }
            if (parts.length == 2) {
                return Integer.parseInt(parts[0].trim()) * 60d
                        + Double.parseDouble(parts[1].trim().replace(',', '.'));
            }
        } catch (NumberFormatException ignored) {
        }
        return -1;
    }

    /** 去掉样式控制序列，只留能念出来的字。 */
    private static String plainText(String value) {
        String cleaned = value
                .replaceAll("\\{[^}]*\\}", "")
                .replace("\\N", "\n")
                .replace("\\n", "\n")
                .replace("\\h", " ")
                .replace("\\r", "")
                .trim();
        while (cleaned.contains("\n\n")) {
            cleaned = cleaned.replace("\n\n", "\n");
        }
        return cleaned;
    }
}
