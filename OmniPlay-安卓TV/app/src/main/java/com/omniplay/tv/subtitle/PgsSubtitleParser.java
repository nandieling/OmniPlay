package com.omniplay.tv.subtitle;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * PGS 位图字幕（.sup）解析。
 *
 * <p>段头现场见过两种排法，逐段自校验着试，哪种读得通就用哪种：
 * <ul>
 *   <li>蓝光原盘、以及 MKV 里原样存着的段：{@code 'PG' + 段长(2) + 段类型(1) + PTS(8)}，
 *       段长从「段长字段之后」数起，把类型和 PTS 一起算进去，载荷固定从第 13 字节开始；</li>
 *   <li>转档服务和 ffmpeg/mplayer 搬运出来的 .sup：
 *       {@code 'PG' + PTS(4) + PTS(4) + 段类型(1) + 载荷长(2)}，两个 32 位时间戳等值，
 *       载荷长只算载荷，载荷同样从第 13 字节开始。</li>
 * </ul>
 * 两种排法把段类型放在不同的字节上（第 5 与第 11 字节），所以认段时先校验类型，再看声明的长度
 * 是否正好落在下一段的 {@code 'PG'} 上，读不通就换另一种，都读不通才往后找一个 {@code 'PG'}。
 * 段类型按蓝光规范：0x14 调色板、0x15 对象、0x16 窗口、0x17 呈现（PCS）、0x80 结束——注意
 * 0x16 是窗口不是呈现段，早先的实现把两者搞混了，于是真实 .sup 一条字幕都出不来。
 *
 * <p>载荷字段也照同样的思路读：对象段按候选的头部长短逐个试（实盘的包在对象号与序列号之间
 * 多塞了一个字节），拿「声明的 RLE 长度 = 段里剩下的字节 + 宽高那 4 个字节」当尺子挑；呈现段
 * 标准解不出对象时，退化成把载荷末尾 8 字节当作合成框 {@code (x, y, 宽, 高)}——实测这样正好
 * 对上对象尺寸。
 *
 * <p>计时按「显示集」算，一个显示集 = 到结束段为止的一批段，实盘一条字幕通常不是一个集而是两个：
 * 编码器每隔约 2 秒会把正在显示的字幕重发一遍（防止播放器丢字幕），字幕该消失时再发一个不带对象的
 * 擦除集。所以带新对象的集才开一条字幕，不带对象的集要么算重播（结束时间往后推），要么算擦除（就地收口）；
 * 对象也只认本显示集里新定义的那些，绝不回退到「见过的全部对象」，否则几分钟前的字幕会突然冒出来。
 */
public final class PgsSubtitleParser {
    private static final int SEGMENT_PALETTE = 0x14;
    private static final int SEGMENT_OBJECT = 0x15;
    private static final int SEGMENT_WINDOW = 0x16;
    private static final int SEGMENT_PRESENTATION = 0x17;
    private static final int SEGMENT_END = 0x80;
    private static final double PTS_TIMESCALE = 90000d;
    /** 编码器防丢字幕的重播节奏，真片量出来约 2 秒一次；没观测到时按这个估。 */
    private static final double DEFAULT_CADENCE_SECONDS = 2.5d;
    /** 判「重播」的宽限：合成框位置对得上时，最晚等到这个间隔之后才当擦除。 */
    private static final double REPAINT_SLACK_SECONDS = 6d;
    /** 单条字幕最短时长，别几帧就消失。 */
    private static final double MIN_CUE_SECONDS = 1d;
    /** 单条字幕最长时长，一直没等到擦除集时也别把字幕挂死。 */
    private static final double MAX_CUE_SECONDS = 40d;
    /** 判断合成框是否还在老位置的像素容差。 */
    private static final int RECT_TOLERANCE = 24;
    private static final int SYNC_A = 0x50;
    private static final int SYNC_B = 0x47;
    private static final int MAX_INPUT_BYTES = 96 * 1024 * 1024;
    private static final int MAX_OBJECT_PIXELS = 16 * 1024 * 1024;
    private static final int MAX_CUES = 8000;
    /** 对象段里 RLE 数据的起点候选：先试实盘量出来的 11，再退到规范里的 10 与 12。 */
    private static final int[] OBJECT_HEADER_SIZES = {11, 10, 12};

    private PgsSubtitleParser() {
    }

    public static List<PgsSubtitleCue> parse(InputStream input) throws IOException {
        return new Parser(readAll(input)).run();
    }

    private static byte[] readAll(InputStream input) throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream(64 * 1024);
        byte[] buffer = new byte[16 * 1024];
        int read;
        while ((read = input.read(buffer)) > 0) {
            if (out.size() + read > MAX_INPUT_BYTES) {
                break;
            }
            out.write(buffer, 0, read);
        }
        return out.toByteArray();
    }

    private static final class Segment {
        final int type;
        final double seconds;
        final int payloadAt;
        final int payloadLength;
        final int next;

        Segment(int type, double seconds, int payloadAt, int payloadLength, int next) {
            this.type = type;
            this.seconds = seconds;
            this.payloadAt = payloadAt;
            this.payloadLength = payloadLength;
            this.next = next;
        }
    }

    private static final class Rect {
        final int objectId;
        final int x;
        final int y;
        final int width;
        final int height;

        Rect(int objectId, int x, int y, int width, int height) {
            this.objectId = objectId;
            this.x = Math.max(0, x);
            this.y = Math.max(0, y);
            this.width = width;
            this.height = height;
        }
    }

    private static final class Pending {
        final double seconds;
        final List<Rect> rects;

        Pending(double seconds, List<Rect> rects) {
            this.seconds = seconds;
            this.rects = rects;
        }
    }

    private static final class Parser {
        private final byte[] data;
        private final int length;
        private final ArrayList<PgsSubtitleCue> cues = new ArrayList<>();
        private final Map<Integer, ObjectBuilder> builders = new HashMap<>();
        private final Map<Integer, ObjectData> objects = new HashMap<>();
        /** 本显示集里新定义出来的对象号：只认这些，绝不回退到「见过的全部对象」。 */
        private final ArrayList<Integer> setObjectIds = new ArrayList<>();
        private int[] palette = new int[256];
        private int planeWidth;
        private int planeHeight;
        private Pending pending;
        private PgsSubtitleCue openCue;
        /** 正在显示这条字幕最后一次出现（含重播）的时间，以及被重播的次数。 */
        private double chainLastSeconds;
        private int chainRepeats;

        Parser(byte[] data) {
            this.data = data;
            this.length = data.length;
        }

        List<PgsSubtitleCue> run() {
            int position = syncAt(0);
            while (position >= 0 && position + 13 <= length && cues.size() < MAX_CUES) {
                Segment segment = decode(position);
                if (segment == null) {
                    position = syncAt(position + 1);
                    continue;
                }
                handle(segment);
                position = syncAt(segment.next);
            }
            closeOpenCue(Double.POSITIVE_INFINITY);
            return cues;
        }

        /** 从 from 起找下一个段起点；找不到返回 -1。 */
        private int syncAt(int from) {
            for (int index = from; index + 1 < length; index++) {
                if ((data[index] & 0xff) == SYNC_A && (data[index + 1] & 0xff) == SYNC_B) {
                    return index;
                }
            }
            return -1;
        }

        private boolean atBoundary(int offset) {
            if (offset == length) {
                return true;
            }
            return offset + 1 < length
                    && (data[offset] & 0xff) == SYNC_A
                    && (data[offset + 1] & 0xff) == SYNC_B;
        }

        private Segment decode(int at) {
            // 排法一：转档服务和 ffmpeg/mplayer 的 .sup，PG + PTS(4) + PTS(4) + 类型 + 载荷长(2)
            int type = u8(at + 10);
            if (isSegmentType(type)) {
                int payloadLength = u16(at + 11);
                long first = u32(at + 2);
                long second = u32(at + 6);
                boolean ptsAgree = first == second || first == 0 || second == 0;
                if (ptsAgree && at + 13 + payloadLength <= length
                        && atBoundary(at + 13 + payloadLength)) {
                    return new Segment(type, Math.max(first, second) / PTS_TIMESCALE,
                            at + 13, payloadLength, at + 13 + payloadLength);
                }
            }
            // 排法二：蓝光原盘与 MKV 里原样存着的段，PG + 段长(2) + 类型 + PTS(8)，
            // 段长把类型和 PTS 一起算进去，所以载荷固定从第 13 字节开始。
            int bdType = u8(at + 4);
            if (isSegmentType(bdType)) {
                int segmentLength = u16(at + 2);
                // 段长从「段长字段之后」数起：类型(1) + PTS(8) + 载荷，所以段尾是 at + 4 + 段长
                if (segmentLength >= 9 && at + 4 + segmentLength <= length
                        && atBoundary(at + 4 + segmentLength)) {
                    return new Segment(bdType, pts8(at + 5), at + 13, segmentLength - 9,
                            at + 4 + segmentLength);
                }
            }
            return null;
        }

        /** 蓝光段里的 8 字节时间戳：带 MPEG 标记位的按标记位解，不带的当 90kHz 计数读。 */
        private double pts8(int at) {
            if ((data[at] & 0xE1) == 0x21) {
                return mpegPts(at) / PTS_TIMESCALE;
            }
            long value = 0;
            for (int index = 0; index < 8; index++) {
                value = (value << 8) | (data[at + index] & 0xff);
            }
            return (value & 0x1FFFFFFFFL) / PTS_TIMESCALE;
        }

        private static boolean isSegmentType(int type) {
            return type == SEGMENT_PALETTE || type == SEGMENT_OBJECT || type == SEGMENT_WINDOW
                    || type == SEGMENT_PRESENTATION || type == SEGMENT_END;
        }

        private void handle(Segment segment) {
            byte[] payload = payloadOf(segment);
            switch (segment.type) {
                case SEGMENT_PALETTE:
                    parsePalette(payload);
                    break;
                case SEGMENT_OBJECT:
                    parseObject(payload);
                    break;
                case SEGMENT_WINDOW:
                    parseWindow(payload);
                    break;
                case SEGMENT_PRESENTATION:
                    // 上一显示集迟迟等不到结束段（截断的 .sup 会这样），先把它结算掉
                    if (pending != null) {
                        emitCue();
                    }
                    pending = parsePresentation(payload, segment.seconds);
                    break;
                case SEGMENT_END:
                    emitCue();
                    break;
                default:
                    break;
            }
        }

        private byte[] payloadOf(Segment segment) {
            int size = Math.min(segment.payloadLength, length - segment.payloadAt);
            return size <= 0 ? new byte[0] : Arrays.copyOfRange(data, segment.payloadAt, segment.payloadAt + size);
        }

        private void parsePalette(byte[] payload) {
            // 标准：调色板号(2) + 版本(1) + 若干条 5 字节（序号、Y、Cr、Cb、透明度）
            int start = payload.length >= 8 && (payload.length - 3) % 5 != 0 ? 2 : 3;
            if (payload.length < start + 5) {
                start = 2;
            }
            if (payload.length < start + 5) {
                return;
            }

            int[] nextPalette = palette.clone();
            int written = 0;
            for (int offset = start; offset + 4 < payload.length; offset += 5) {
                int id = payload[offset] & 0xff;
                int y = payload[offset + 1] & 0xff;
                int cr = payload[offset + 2] & 0xff;
                int cb = payload[offset + 3] & 0xff;
                int alpha = payload[offset + 4] & 0xff;
                int red = clamp((int) Math.round(y + 1.402d * (cr - 128)));
                int green = clamp((int) Math.round(y - 0.34414d * (cb - 128) - 0.71414d * (cr - 128)));
                int blue = clamp((int) Math.round(y + 1.772d * (cb - 128)));
                nextPalette[id] = alpha == 0 ? 0 : (alpha << 24) | (red << 16) | (green << 8) | blue;
                written++;
            }
            if (written > 0) {
                palette = nextPalette;
            }
        }

        private void parseObject(byte[] payload) {
            if (payload.length < 12) {
                return;
            }
            int objectId = u16From(payload, 0);
            // 对象段头部的长短现场见过不止一种（对象号与序列号之间可能多塞一个字节）。
            // 规范里「声明的 RLE 长度」正好等于段里剩下的字节加上宽高那 4 个字节，拿这条当尺子量。
            for (boolean strict : new boolean[]{true, false}) {
                for (int rleAt : OBJECT_HEADER_SIZES) {
                    ObjectBuilder carried = builders.get(objectId);
                    if (rleAt < 9 || rleAt + 1 >= payload.length) {
                        continue;
                    }
                    if (carried != null && carried.rleAt != rleAt) {
                        continue;
                    }
                    int flags = payload[rleAt - 8] & 0xff;
                    boolean first = (flags & 0x80) != 0;
                    boolean last = (flags & 0x40) != 0;
                    if (!first && carried == null) {
                        continue;
                    }
                    int declared = u24From(payload, rleAt - 7);
                    int width = carried == null ? u16From(payload, rleAt - 4) : carried.width;
                    int height = carried == null ? u16From(payload, rleAt - 2) : carried.height;
                    int available = payload.length - rleAt;
                    if (available <= 0 || !saneObjectSize(width, height)) {
                        continue;
                    }
                    if (declared != available + 4 && (strict || declared > available + 64)) {
                        continue;
                    }
                    ObjectBuilder builder = carried != null
                            ? carried : new ObjectBuilder(objectId, rleAt, width, height, declared);
                    builder.append(payload, rleAt, available);
                    builders.put(objectId, builder);
                    if ((last || builder.isComplete()) && builder.hasData()) {
                        objects.put(objectId, builder.build());
                        builders.remove(objectId);
                        if (setObjectIds.size() < 32 && !setObjectIds.contains(objectId)) {
                            setObjectIds.add(objectId);
                        }
                    }
                    return;
                }
            }
        }

        private void parseWindow(byte[] payload) {
            // 标准：窗口号(1) + x(2) + y(2) + 宽(2) + 高(2)
            if (payload.length >= 9) {
                int width = u16From(payload, 4);
                int height = u16From(payload, 6);
                if (saneFrameSize(width, height)) {
                    planeWidth = width;
                    planeHeight = height;
                    return;
                }
            }
            // 实盘：头两个 16 位就是画面宽高
            if (payload.length >= 4) {
                int width = u16From(payload, 0);
                int height = u16From(payload, 2);
                if (saneFrameSize(width, height)) {
                    planeWidth = width;
                    planeHeight = height;
                }
            }
        }

        private Pending parsePresentation(byte[] payload, double seconds) {
            List<Rect> standard = standardRects(payload);
            if (!standard.isEmpty()) {
                return new Pending(seconds, standard);
            }
            return new Pending(seconds, scanRects(payload));
        }

        /**
         * 实盘 PCS 的排法不止一种：见过「对象数(2) + 一个合成框(8)」，也见过「对象数(1) +
         * 每个对象各带一个合成框」。逐字节扫过去，把读起来合理的 (x, y, 宽, 高) 全收下来当候选，
         * 再由 rectFor 拿对象自己的宽高去配——一条 PCS 带两个对象时，只取末尾 8 字节会把两个
         * 对象画到同一个框上。
         */
        private List<Rect> scanRects(byte[] payload) {
            ArrayList<Rect> rects = new ArrayList<>();
            int planeW = planeWidth > 0 ? planeWidth : 1920;
            int planeH = planeHeight > 0 ? planeHeight : 1080;
            for (int offset = 0; offset + 8 <= payload.length && rects.size() < 16; offset++) {
                int x = u16From(payload, offset);
                int y = u16From(payload, offset + 2);
                int width = u16From(payload, offset + 4);
                int height = u16From(payload, offset + 6);
                if (!saneObjectSize(width, height) || x + width > planeW + 32 || y + height > planeH + 32) {
                    continue;
                }
                boolean known = false;
                for (Rect existing : rects) {
                    if (existing.x == x && existing.y == y && existing.width == width && existing.height == height) {
                        known = true;
                        break;
                    }
                }
                if (!known) {
                    rects.add(new Rect(-1, x, y, width, height));
                }
            }
            return rects;
        }

        /** 标准 PCS：合成窗若干 + 合成对象若干。 */
        private List<Rect> standardRects(byte[] payload) {
            ArrayList<Rect> rects = new ArrayList<>();
            if (payload.length < 8) {
                return rects;
            }
            int windowCount = u16From(payload, 4);
            if (windowCount > 8) {
                return rects;
            }
            int offset = 6 + windowCount * 9;
            if (offset >= payload.length) {
                return rects;
            }
            int objectCount = payload[offset++] & 0xff;
            if (objectCount == 0 || objectCount > 16) {
                return rects;
            }
            for (int index = 0; index < objectCount && offset + 5 <= payload.length; index++) {
                int objectId = u16From(payload, offset);
                offset += 2;
                offset += 2; // 合成对象号 + 调色板号
                int flags = payload[offset++] & 0xff;
                int x = 0;
                int y = 0;
                int width = -1;
                int height = -1;
                if ((flags & 0x80) != 0 && offset + 8 <= payload.length) {
                    x = u16From(payload, offset);
                    y = u16From(payload, offset + 2);
                    width = u16From(payload, offset + 4);
                    height = u16From(payload, offset + 6);
                    offset += 8;
                }
                rects.add(new Rect(objectId, x, y, width, height));
            }
            if (rects.size() != objectCount) {
                return new ArrayList<>();
            }
            return rects;
        }


        private void emitCue() {
            Pending presentation = pending;
            pending = null;
            if (presentation == null) {
                return;
            }
            ArrayList<Integer> ids = new ArrayList<>(setObjectIds);
            setObjectIds.clear();
            if (ids.isEmpty()) {
                // 本显示集没带新对象：要么是把正在显示的这条重画一遍，要么是把屏幕擦干净
                repaintOrErase(presentation);
                return;
            }
            ArrayList<PgsSubtitleCue.Image> images = new ArrayList<>();
            int[] cuePalette = palette.clone();
            for (int index = 0; index < ids.size(); index++) {
                ObjectData object = objects.get(ids.get(index));
                if (object == null || object.width <= 0 || object.height <= 0 || object.rleData.length == 0) {
                    continue;
                }
                Rect rect = rectFor(presentation.rects, index, object);
                images.add(new PgsSubtitleCue.Image(
                        object.objectId, rect.x, rect.y, object.width, object.height, cuePalette, object.rleData));
            }
            if (images.isEmpty()) {
                // 对象都残缺，这一集什么也没画出来，那就别去动正在显示的那条
                return;
            }
            closeOpenCue(presentation.seconds);
            PgsSubtitleCue cue = new PgsSubtitleCue(
                    presentation.seconds, Double.POSITIVE_INFINITY,
                    planeWidth > 0 ? planeWidth : 1920,
                    planeHeight > 0 ? planeHeight : 1080,
                    images);
            cues.add(cue);
            openCue = cue;
            chainLastSeconds = presentation.seconds;
            chainRepeats = 0;
            if (objects.size() > 64) {
                objects.clear();
            }
        }

        /**
         * 不带新对象的显示集有两种可能：编码器每隔约 2 秒把正在显示的字幕重发一遍（防播放器丢字幕），
         * 或者字幕到点了一集不带对象把屏幕擦干净。两者长得一样，只能靠「合成框位置 + 重播节奏」区分：
         * 位置和正在显示的那条对得上、时间也踩在节奏上就算重播，把结束时间往后推；否则当擦除就地收口。
         * 早先这里是把「见过的全部对象」都画一遍，于是几分钟前的字幕会突然冒出来，位置还全落在兜底位置上。
         */
        private void repaintOrErase(Pending presentation) {
            if (openCue == null) {
                return;
            }
            double sinceLast = presentation.seconds - chainLastSeconds;
            if (sinceLast < 0d) {
                return;
            }
            double cadence = chainCadence();
            double allowance = samePlace(presentation)
                    ? Math.max(cadence * 2.5d, REPAINT_SLACK_SECONDS)
                    : cadence * 1.2d + 0.5d;
            if (sinceLast <= allowance) {
                chainLastSeconds = presentation.seconds;
                chainRepeats++;
                return;
            }
            closeOpenCue(presentation.seconds);
        }

        /**
         * 收口当前字幕。结束时间取「最后一次出现（含重播）的时间 + 一个重播节奏」，再往前压到下一条字幕
         * （或擦除集）的时间——不能简单接在下一条的开始时间上，否则上一条会一直挂到下一条出现才消失。
         */
        private void closeOpenCue(double nextStart) {
            if (openCue == null) {
                return;
            }
            double start = openCue.startSeconds;
            double end = chainLastSeconds + cueTailSeconds();
            if (nextStart < end) {
                end = nextStart;
            }
            double floor = Math.min(nextStart, start + MIN_CUE_SECONDS);
            openCue.endSeconds = Math.max(floor, Math.min(end, start + MAX_CUE_SECONDS));
            openCue = null;
        }

        /** 停止重播之后还该让字幕停留多久：有节奏就按自己的节奏，没观测到就假设它还会重播一次。 */
        private double cueTailSeconds() {
            if (chainRepeats > 0) {
                return chainCadence();
            }
            return Math.min(Math.max(DEFAULT_CADENCE_SECONDS * 2.5d, REPAINT_SLACK_SECONDS), 8d);
        }

        /** 本条字幕自己的重播节奏：从出现到最后一次重播平摊；不像话就当没观测到。 */
        private double chainCadence() {
            if (chainRepeats <= 0 || openCue == null) {
                return DEFAULT_CADENCE_SECONDS;
            }
            double span = chainLastSeconds - openCue.startSeconds;
            if (span <= 0d) {
                return DEFAULT_CADENCE_SECONDS;
            }
            double cadence = span / (double) chainRepeats;
            return cadence >= 0.5d && cadence <= 10d ? cadence : DEFAULT_CADENCE_SECONDS;
        }

        /** 这个显示集的合成框，和正在显示的那条是否还在老位置。 */
        private boolean samePlace(Pending presentation) {
            if (openCue == null) {
                return false;
            }
            Rect here = unionRect(presentation.rects);
            if (here == null) {
                return false;
            }
            int left = Integer.MAX_VALUE;
            int top = Integer.MAX_VALUE;
            int right = -1;
            int bottom = -1;
            for (PgsSubtitleCue.Image image : openCue.images) {
                left = Math.min(left, image.x);
                top = Math.min(top, image.y);
                right = Math.max(right, image.x + image.width);
                bottom = Math.max(bottom, image.y + image.height);
            }
            if (right <= left || bottom <= top) {
                return false;
            }
            return Math.abs(left - here.x) <= RECT_TOLERANCE
                    && Math.abs(top - here.y) <= RECT_TOLERANCE
                    && Math.abs(right - left - here.width) <= RECT_TOLERANCE
                    && Math.abs(bottom - top - here.height) <= RECT_TOLERANCE;
        }

        private Rect unionRect(List<Rect> rects) {
            int left = Integer.MAX_VALUE;
            int top = Integer.MAX_VALUE;
            int right = -1;
            int bottom = -1;
            for (Rect rect : rects) {
                if (rect.width <= 0 || rect.height <= 0) {
                    continue;
                }
                left = Math.min(left, rect.x);
                top = Math.min(top, rect.y);
                right = Math.max(right, rect.x + rect.width);
                bottom = Math.max(bottom, rect.y + rect.height);
            }
            if (right <= left || bottom <= top) {
                return null;
            }
            return new Rect(-1, left, top, right - left, bottom - top);
        }

        private Rect rectFor(List<Rect> rects, int index, ObjectData object) {
            if (rects != null && !rects.isEmpty()) {
                for (Rect rect : rects) {
                    if (rect.objectId == object.objectId && rect.width > 0 && rect.height > 0) {
                        return normalized(rect, object);
                    }
                }
                // 合成框和对象未必按号对上（一条实盘 PCS 可以带多个框），拿对象自己的宽高去配
                for (Rect rect : rects) {
                    if (rect.width == object.width && rect.height == object.height) {
                        return normalized(rect, object);
                    }
                }
                if (index < rects.size()) {
                    return normalized(rects.get(index), object);
                }
            }
            // 没有位置信息：按对象尺寸贴在画面下方居中，至少比丢字幕好
            int planeH = planeHeight > 0 ? planeHeight : 1080;
            int planeW = planeWidth > 0 ? planeWidth : 1920;
            return new Rect(object.objectId, Math.max(0, (planeW - object.width) / 2),
                    Math.max(0, planeH - object.height - planeH / 12), object.width, object.height);
        }

        private Rect normalized(Rect rect, ObjectData object) {
            int width = rect.width > 0 ? rect.width : object.width;
            int height = rect.height > 0 ? rect.height : object.height;
            return new Rect(object.objectId, rect.x, rect.y, width, height);
        }


        private int u8(int at) {
            return data[at] & 0xff;
        }

        private int u16(int at) {
            return ((data[at] & 0xff) << 8) | (data[at + 1] & 0xff);
        }

        private long u32(int at) {
            return ((long) (data[at] & 0xff) << 24) | ((long) (data[at + 1] & 0xff) << 16)
                    | ((long) (data[at + 2] & 0xff) << 8) | (long) (data[at + 3] & 0xff);
        }

        private long mpegPts(int at) {
            int high = ((data[at + 1] & 0xff) << 8) | (data[at + 2] & 0xff);
            int low = ((data[at + 3] & 0xff) << 8) | (data[at + 4] & 0xff);
            return ((long) (high >>> 1) << 15) | (long) ((low >>> 1) & 0x7FFF);
        }
    }

    private static int u16From(byte[] data, int at) {
        return ((data[at] & 0xff) << 8) | (data[at + 1] & 0xff);
    }

    private static int u24From(byte[] data, int at) {
        return ((data[at] & 0xff) << 16) | ((data[at + 1] & 0xff) << 8) | (data[at + 2] & 0xff);
    }

    private static boolean saneObjectSize(int width, int height) {
        if (width <= 0 || height <= 0 || width > 4096 || height > 1024) {
            return false;
        }
        return (long) width * (long) height <= MAX_OBJECT_PIXELS;
    }

    private static boolean saneFrameSize(int width, int height) {
        return width >= 64 && height >= 64 && width <= 8192 && height <= 4320;
    }

    private static int clamp(int value) {
        return Math.max(0, Math.min(255, value));
    }

    private static final class ObjectBuilder {
        final int objectId;
        final int rleAt;
        final int width;
        final int height;
        final int expectedLength;
        final ByteArrayOutputStream data;

        ObjectBuilder(int objectId, int rleAt, int width, int height, int expectedLength) {
            this.objectId = objectId;
            this.rleAt = rleAt;
            this.width = Math.max(0, width);
            this.height = Math.max(0, height);
            this.expectedLength = Math.max(0, expectedLength);
            this.data = new ByteArrayOutputStream(Math.max(32, Math.min(this.expectedLength, 256 * 1024)));
        }

        void append(byte[] source, int offset, int count) {
            int remaining = expectedLength <= 0 ? count : expectedLength - data.size();
            int take = Math.max(0, Math.min(count, remaining));
            if (take > 0) {
                data.write(source, offset, take);
            }
        }

        boolean hasData() {
            return data.size() > 0;
        }

        boolean isComplete() {
            return expectedLength > 0 && data.size() >= expectedLength;
        }

        ObjectData build() {
            byte[] bytes = data.toByteArray();
            if (expectedLength > 0 && bytes.length > expectedLength) {
                bytes = Arrays.copyOf(bytes, expectedLength);
            }
            return new ObjectData(objectId, width, height, bytes);
        }
    }

    private static final class ObjectData {
        final int objectId;
        final int width;
        final int height;
        final byte[] rleData;

        ObjectData(int objectId, int width, int height, byte[] rleData) {
            this.objectId = objectId;
            this.width = Math.max(1, width);
            this.height = Math.max(1, height);
            this.rleData = rleData;
        }
    }
}
