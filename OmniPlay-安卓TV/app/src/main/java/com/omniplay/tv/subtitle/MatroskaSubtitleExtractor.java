package com.omniplay.tv.subtitle;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

/**
 * Matroska（MKV / WEBM）内封字幕抽取器：把一条字幕轨抽成能当 sidecar 直接播放的字节。
 *
 * <p>为什么不能只读一小段就拿到全部字幕：Matroska 把字幕块和音视频块交错写在同一条字节流里，
 * Cues 索引只定位到簇，不定位到某条轨的某个块。所以要覆盖到第 80 分钟，就得把那段字节翻过去。
 * 这里按簇顺序走、每簇一次分块 Range 读、读过的字节不回读，并支持「领先播放头足够多就停在某个簇」
 * 的续跑法：既能增量出字幕，也能中断后接着跑。
 *
 * <p>输出：S_TEXT/UTF8 与 S_TEXT/WEBVTT 抽成 SRT；S_TEXT/ASS 保留 ASS（连同容器里的脚本头，
 * 样式不丢）；S_HDMV/PGS 与 S_DVBSUB 原样拼成 .sup 段流，交给 {@link PgsSubtitleParser} 画。
 */
public final class MatroskaSubtitleExtractor {
    private static final long ID_SEGMENT = 0x18538067L;
    private static final long ID_INFOS = 0x1549A966L;
    private static final long ID_TIMECODE_SCALE = 0xADL;
    private static final long ID_DURATION = 0x4489L;
    private static final long ID_TRACKS = 0x1654AE6BL;
    private static final long ID_TRACK_ENTRY = 0xAEL;
    private static final long ID_TRACK_NUMBER = 0xD7L;
    private static final long ID_TRACK_TYPE = 0x83L;
    private static final long ID_FLAG_DEFAULT = 0x55AAL;
    private static final long ID_CODEC_ID = 0x86L;
    private static final long ID_CODEC_PRIVATE = 0x63A2L;
    private static final long ID_LANGUAGE_LEGACY = 0x22B59CL;
    private static final long ID_LANGUAGES = 0x4D7B1CL;
    private static final long ID_LANGUAGE = 0x4D7BBL;
    private static final long ID_TRACK_NAME = 0x536EL;
    private static final long ID_DEFAULT_DURATION = 0x23E383L;
    private static final long ID_CLUSTER = 0x1C53BB6BL;
    private static final long ID_TIMECODE = 0xE7L;
    private static final long ID_SIMPLE_BLOCK = 0xA3L;
    private static final long ID_BLOCK_GROUP = 0xA0L;
    private static final long ID_BLOCK = 0xA1L;
    private static final long ID_BLOCK_DURATION = 0x9BL;

    private static final int CHUNK_BYTES = 1024 * 1024;
    private static final int INLINE_LIMIT = 2 * 1024 * 1024;
    private static final double FALLBACK_SECONDS = 4d;

    /** 一条内封字幕轨。 */
    public static final class Track {
        public final int trackNumber;
        public final String codecId;
        public final String language;
        public final String name;
        public final boolean isDefault;
        /** srt / ass / sup，决定 sidecar 后缀。 */
        public final String format;
        public final byte[] codecPrivate;

        Track(int trackNumber, String codecId, String language, String name, boolean isDefault,
              String format, byte[] codecPrivate) {
            this.trackNumber = trackNumber;
            this.codecId = codecId;
            this.language = language;
            this.name = name;
            this.isDefault = isDefault;
            this.format = format;
            this.codecPrivate = codecPrivate == null ? new byte[0] : codecPrivate;
        }

        public boolean isBitmap() {
            return "sup".equals(format);
        }
    }

    /** 一条抽出来的字幕：文本轨带 text，位图轨带 pgsPacket。 */
    public static final class Cue {
        public final double startSeconds;
        public double endSeconds;
        public final String text;
        public final byte[] pgsPacket;

        public Cue(double startSeconds, double endSeconds, String text, byte[] pgsPacket) {
            this.startSeconds = startSeconds;
            this.endSeconds = endSeconds;
            this.text = text;
            this.pgsPacket = pgsPacket;
        }
    }

    /** 抽取进度：续跑位置、已覆盖到的影片时间、条目数、因交织被跳过的块数。 */
    public static final class Result {
        public final long resumeOffset;
        public final double coveredSeconds;
        public final int cueCount;
        public final int skippedLacedBlocks;

        Result(long resumeOffset, double coveredSeconds, int cueCount, int skippedLacedBlocks) {
            this.resumeOffset = resumeOffset;
            this.coveredSeconds = coveredSeconds;
            this.cueCount = cueCount;
            this.skippedLacedBlocks = skippedLacedBlocks;
        }
    }

    /** 每覆盖满一个窗口回调一次，sidecar 就重写一次：第一次看也能边跑边有字幕。 */
    public interface Sink {
        void onWindow(List<Cue> cues, Result progress);
    }

    /** 抽取结果里的字幕轨（含不支持的位图轨之外的过滤留给调用方）。 */
    public static List<Track> tracks(SeekableSource source) throws IOException {
        return new MatroskaSubtitleExtractor(source).structure().tracks;
    }

    public static double durationSeconds(SeekableSource source) {
        try {
            return new MatroskaSubtitleExtractor(source).structure().durationSeconds;
        } catch (IOException ignored) {
            return 0d;
        }
    }

    public static boolean looksLikeMatroska(SeekableSource source) {
        try {
            byte[] head = source.readAt(0, 4);
            return head.length == 4 && (head[0] & 0xff) == 0x1A && (head[1] & 0xff) == 0x45
                    && (head[2] & 0xff) == 0xDF && (head[3] & 0xff) == 0xA3;
        } catch (IOException ignored) {
            return false;
        }
    }

    /**
     * @param trackNumber   目标轨号
     * @param resumeOffset  从哪个字节续跑（上次返回的 resumeOffset；0 表示从头）
     * @param windowSeconds 每覆盖多少秒回调一次
     * @param leadSeconds   领先到这里就收工并报告续跑位置；&lt;=0 表示整轨跑完
     */
    public static Result run(SeekableSource source, int trackNumber, long resumeOffset,
                             int windowSeconds, double leadSeconds, Sink sink) throws IOException {
        return run(source, trackNumber, resumeOffset, windowSeconds, leadSeconds, sink, null);
    }

    /**
     * 同上，但可以带上已经抽好的条目：续跑时把盘上那份 sidecar 解析回来当种子，
     * 整份重写才不会把上次转好的前半段冲掉。
     */
    public static Result run(SeekableSource source, int trackNumber, long resumeOffset,
                             int windowSeconds, double leadSeconds, Sink sink,
                             List<Cue> seed) throws IOException {
        MatroskaSubtitleExtractor extractor = new MatroskaSubtitleExtractor(source);
        Structure structure = extractor.structure();
        Track track = null;
        for (Track candidate : structure.tracks) {
            if (candidate.trackNumber == trackNumber) {
                track = candidate;
            }
        }
        if (track == null) {
            throw new IOException("这个文件里没有字幕轨 " + trackNumber + "。");
        }
        return extractor.demux(structure, track, resumeOffset,
                Math.max(15, windowSeconds), leadSeconds, sink, seed);
    }

    private final SeekableSource source;
    private final long fileSize;
    /** 一个时间码单位等于多少秒：绝大多数 MKV 写 1ms，个别母带用别的 TimecodeScale。 */
    private double unitSeconds = 0.001d;

    private MatroskaSubtitleExtractor(SeekableSource source) throws IOException {
        this.source = source;
        this.fileSize = source.size();
    }

    private static final class Structure {
        final ArrayList<Track> tracks = new ArrayList<>();
        final ArrayList<long[]> clusters = new ArrayList<>();
        double timecodeScaleNanoseconds = 1000000d;
        double durationSeconds;
    }

    private Structure structure() throws IOException {
        Structure found = new Structure();
        Walker top = new Walker(0, fileSize, null);
        while (top.advance()) {
            if (top.id != ID_SEGMENT) {
                continue;
            }
            Walker segment = new Walker(top.payloadStart, top.payloadEnd, null);
            while (segment.advance()) {
                if (segment.id == ID_TRACKS) {
                    readTracks(segment, found);
                } else if (segment.id == ID_INFOS) {
                    readInfos(segment, found);
                } else if (segment.id == ID_CLUSTER) {
                    found.clusters.add(new long[]{segment.payloadStart, segment.payloadEnd});
                }
            }
        }
        return found;
    }

    private void readInfos(Walker infos, Structure found) throws IOException {
        Walker cursor = new Walker(infos.payloadStart, infos.payloadEnd, infos);
        while (cursor.advance()) {
            if (cursor.id == ID_TIMECODE_SCALE) {
                found.timecodeScaleNanoseconds = Math.max(1d, (double) unsigned(cursor.payload()));
            } else if (cursor.id == ID_DURATION) {
                found.durationSeconds = readFloat(cursor.payload())
                        * found.timecodeScaleNanoseconds / 1000000000d;
            }
        }
    }

    private void readTracks(Walker tracksElement, Structure found) throws IOException {
        Walker entries = new Walker(tracksElement.payloadStart, tracksElement.payloadEnd, tracksElement);
        while (entries.advance()) {
            if (entries.id == ID_TRACK_ENTRY) {
                readTrackEntry(entries, found);
            }
        }
    }

    private void readTrackEntry(Walker entry, Structure found) throws IOException {
        int trackNumber = 0;
        int trackType = 0;
        int flagDefault = 0;
        String codecId = "";
        String language = "und";
        String name = "";
        byte[] codecPrivate = new byte[0];
        Walker cursor = new Walker(entry.payloadStart, entry.payloadEnd, entry);
        while (cursor.advance()) {
            if (cursor.id == ID_TRACK_NUMBER) {
                trackNumber = (int) unsigned(cursor.payload());
            } else if (cursor.id == ID_TRACK_TYPE) {
                trackType = (int) unsigned(cursor.payload());
            } else if (cursor.id == ID_FLAG_DEFAULT) {
                flagDefault = (int) unsigned(cursor.payload());
            } else if (cursor.id == ID_CODEC_ID) {
                codecId = new String(cursor.payload(), StandardCharsets.UTF_8).trim();
            } else if (cursor.id == ID_CODEC_PRIVATE) {
                codecPrivate = cursor.payload();
            } else if (cursor.id == ID_LANGUAGE_LEGACY || cursor.id == ID_LANGUAGE) {
                language = new String(cursor.payload(), StandardCharsets.UTF_8).trim();
            } else if (cursor.id == ID_LANGUAGES) {
                Walker nested = new Walker(cursor.payloadStart, cursor.payloadEnd, cursor);
                while (nested.advance() && nested.id == ID_LANGUAGE) {
                    language = new String(nested.payload(), StandardCharsets.UTF_8).trim();
                }
            } else if (cursor.id == ID_TRACK_NAME) {
                name = new String(cursor.payload(), StandardCharsets.UTF_8).trim();
            }
        }
        if (trackType != 0x11 || codecId.isEmpty()) {
            // 0x11 = subtitles
            return;
        }
        String format = formatFor(codecId);
        if (format == null) {
            return;
        }
        found.tracks.add(new Track(trackNumber, codecId, normalizeLanguage(language), name,
                flagDefault != 0, format, codecPrivate));
    }

    private static String formatFor(String codecId) {
        String id = codecId.toUpperCase(Locale.ROOT);
        if ("S_TEXT/ASS".equals(id) || "S_TEXT/USF".equals(id)) {
            return "ass";
        }
        if ("S_TEXT/UTF8".equals(id) || "S_TEXT/WEBVTT".equals(id)) {
            return "srt";
        }
        if ("S_HDMV/PGS".equals(id) || "S_DVBSUB".equals(id)) {
            return "sup";
        }
        return null;
    }

    /** Matroska 里中文常写 chi / zho / zh-CN，统一成文件名用的两三个字母。 */
    public static String normalizeLanguage(String raw) {
        String value = raw == null ? "" : raw.trim().toLowerCase(Locale.ROOT);
        if (value.isEmpty()) {
            return "und";
        }
        String primary = value.split("[-_]", 2)[0];
        switch (primary) {
            case "zh":
            case "chi":
            case "zho":
            case "cmn":
            case "yue":
            // 压制组常标的简中 / 繁中，菜单里都该写「中文」
            case "chs":
            case "cht":
            case "zhs":
                return "zh";
            case "en":
            case "eng":
                return "en";
            case "ja":
            case "jpn":
                return "ja";
            case "ko":
            case "kor":
                return "ko";
            default:
                return primary.length() > 3 ? primary.substring(0, 3) : primary;
        }
    }

    private Result demux(Structure structure, Track track, long resumeOffset, int windowSeconds,
                         double leadSeconds, Sink sink, List<Cue> seed) throws IOException {
        unitSeconds = Math.max(1e-9d, structure.timecodeScaleNanoseconds / 1000000000d);
        ArrayList<Cue> cues = new ArrayList<>();
        if (seed != null) {
            cues.addAll(seed);
        }
        double checkpointSeconds = 0;
        double coveredSeconds = 0;
        long resume = 0;
        int skippedLaced = 0;
        for (int index = 0; index < structure.clusters.size(); index++) {
            long[] cluster = structure.clusters.get(index);
            if (cluster[1] <= resumeOffset) {
                continue;
            }
            ClusterPass pass = walkCluster(cluster[0], cluster[1], track, cues);
            skippedLaced += pass.skippedLaced;
            coveredSeconds = Math.max(coveredSeconds, pass.endSeconds);
            resume = cluster[1];
            boolean lastCluster = index == structure.clusters.size() - 1;
            if (coveredSeconds >= checkpointSeconds + windowSeconds || lastCluster) {
                checkpointSeconds = coveredSeconds;
                if (sink != null) {
                    sink.onWindow(cues, new Result(resume, coveredSeconds, cues.size(), skippedLaced));
                }
            }
            if (leadSeconds > 0 && coveredSeconds >= leadSeconds) {
                break;
            }
        }
        return new Result(resume, coveredSeconds, cues.size(), skippedLaced);
    }

    private static final class ClusterPass {
        double endSeconds;
        int skippedLaced;
    }

    /** 本簇里这条轨最后一条字幕的结束时间；没抽到就是 0。 */
    private static double latestCueEnd(List<Cue> cues, int from) {
        double latest = 0;
        for (int index = from; index < cues.size(); index++) {
            latest = Math.max(latest, cues.get(index).endSeconds);
        }
        return latest;
    }

    private ClusterPass walkCluster(long payloadStart, long payloadEnd, Track track,
                                    List<Cue> cues) throws IOException {
        ClusterPass pass = new ClusterPass();
        long clusterTimecode = 0;
        int cuesBefore = cues.size();
        Walker cursor = new Walker(payloadStart, payloadEnd, null);
        while (cursor.advance()) {
            if (cursor.id == ID_TIMECODE) {
                clusterTimecode = unsigned(cursor.payload());
            } else if (cursor.id == ID_SIMPLE_BLOCK) {
                pass.skippedLaced += handleBlock(cursor.payload(), clusterTimecode, track, cues, 0);
            } else if (cursor.id == ID_BLOCK_GROUP) {
                pass.skippedLaced += handleBlockGroup(cursor, clusterTimecode, track, cues);
            }
        }
        pass.endSeconds = Math.max(clusterTimecode * unitSeconds, latestCueEnd(cues, cuesBefore));
        return pass;
    }

    private int handleBlockGroup(Walker blockGroup, long clusterTimecode, Track track,
                                 List<Cue> cues) throws IOException {
        byte[] block = null;
        long durationMilliseconds = 0;
        Walker cursor = new Walker(blockGroup.payloadStart, blockGroup.payloadEnd, blockGroup);
        while (cursor.advance()) {
            if (cursor.id == ID_BLOCK && block == null) {
                block = cursor.payload();
            } else if (cursor.id == ID_BLOCK_DURATION) {
                durationMilliseconds = unsigned(cursor.payload());
            }
        }
        return block == null ? 0 : handleBlock(block, clusterTimecode, track, cues, durationMilliseconds);
    }

    /** @return 因交织（lacing）被跳过的块数 */
    private int handleBlock(byte[] payload, long clusterTimecode, Track track,
                            List<Cue> cues, long durationMilliseconds) {
        Vint trackField = Vint.decode(payload, 0);
        if (trackField == null || trackField.bytes + 3 > payload.length
                || trackField.value != track.trackNumber) {
            return 0;
        }
        int offset = trackField.bytes;
        int blockTimecode = (short) (((payload[offset] & 0xff) << 8) | (payload[offset + 1] & 0xff));
        int lacing = ((payload[offset + 2] & 0xff) >> 1) & 0x07;
        offset += 3;
        if (lacing != 0) {
            // 字幕轨极少用 lacing，遇到就跳过并计数：宁可少一条，也别画错时间。
            return 1;
        }
        double startSeconds = clusterTimecode * unitSeconds + blockTimecode / 1000d;
        double durationSeconds = durationMilliseconds > 0
                ? durationMilliseconds / 1000d
                : (track.isBitmap() ? 8d : FALLBACK_SECONDS);
        cues.add("sup".equals(track.format)
                ? new Cue(startSeconds, startSeconds + durationSeconds, null, tail(payload, offset))
                : new Cue(startSeconds, startSeconds + durationSeconds,
                        textBlock(payload, offset, track, startSeconds, startSeconds + durationSeconds), null));
        return 0;
    }

    private static byte[] tail(byte[] payload, int offset) {
        byte[] packet = new byte[Math.max(0, payload.length - offset)];
        System.arraycopy(payload, offset, packet, 0, packet.length);
        return packet;
    }

    private static String textBlock(byte[] payload, int offset, Track track,
                                    double startSeconds, double endSeconds) {
        String text = new String(payload, offset, payload.length - offset, StandardCharsets.UTF_8)
                .replace("\r\n", "\n");
        if (text.trim().isEmpty()) {
            return null;
        }
        if ("sup".equals(track.format)) {
            return text;
        }
        if ("ass".equals(track.format)) {
            return assDialogue(text, startSeconds, endSeconds);
        }
        // S_TEXT/WEBVTT 的块里可能自带一行时间轴，用抽出来的值替掉。
        if (text.matches("(?s)^\\d\\d:\\d\\d:.*-->.*\\n.*")) {
            int newline = text.indexOf('\n');
            return text.substring(newline + 1);
        }
        return text;
    }

    /** Matroska 的 ASS 块是不带 Dialogue: 前缀的对话行：补前缀，并把时间轴换成抽出来的值。 */
    private static String assDialogue(String block, double startSeconds, double endSeconds) {
        String body = block.startsWith("Dialogue:") ? block.substring("Dialogue:".length()).trim() : block;
        String[] fields = body.split(",", 9);
        if (fields.length < 8) {
            return "Dialogue: 0," + assTime(startSeconds) + "," + assTime(endSeconds)
                    + ",Default,,0,0,0,," + body;
        }
        fields[1] = assTime(startSeconds);
        fields[2] = assTime(endSeconds);
        StringBuilder rebuilt = new StringBuilder("Dialogue: ");
        for (int index = 0; index < fields.length; index++) {
            if (index > 0) {
                rebuilt.append(',');
            }
            rebuilt.append(fields[index]);
        }
        return rebuilt.toString();
    }

    private static String assTime(double seconds) {
        long total = Math.max(0, Math.round(seconds * 100));
        return String.format(Locale.ROOT, "%d:%02d:%02d.%02d",
                total / 360000, (total / 6000) % 60, (total / 100) % 60, total % 100);
    }

    static String srtTime(double seconds) {
        long total = Math.max(0, Math.round(seconds * 1000));
        return String.format(Locale.ROOT, "%02d:%02d:%02d,%03d",
                total / 3600000, (total / 60000) % 60, (total / 1000) % 60, total % 1000);
    }

    /** 把抽到的条目写成 sidecar 文本。ASS 带上容器里的脚本头，样式不丢；PGS 由调用方按包写文件。 */
    public static String serialize(Track track, List<Cue> cues) {
        StringBuilder out = new StringBuilder();
        if ("sup".equals(track.format)) {
            return "";
        }
        if ("ass".equals(track.format)) {
            String header = new String(track.codecPrivate, StandardCharsets.UTF_8);
            out.append(header.endsWith("\n") ? header : header + "\n");
            if (!header.contains("[Events]")) {
                out.append("\n[Events]\nFormat: Marked, Start, End, Style, Name, MarginL, MarginR, "
                        + "MarginV, Effect, Text\n");
            }
            for (Cue cue : cues) {
                if (cue.text != null) {
                    out.append(cue.text).append('\n');
                }
            }
            return out.toString();
        }
        int index = 1;
        for (Cue cue : cues) {
            if (cue.text == null) {
                continue;
            }
            out.append(index++).append('\n')
                    .append(srtTime(cue.startSeconds)).append(" --> ").append(srtTime(cue.endSeconds))
                    .append('\n').append(cue.text).append("\n\n");
        }
        return out.toString();
    }

    private static long unsigned(byte[] data) {
        long value = 0;
        for (byte b : data) {
            value = (value << 8) | (b & 0xffL);
        }
        return value;
    }

    private static double readFloat(byte[] data) {
        if (data.length == 4) {
            return Float.intBitsToFloat((int) unsigned(data));
        }
        if (data.length == 8) {
            return Double.longBitsToDouble(unsigned(data));
        }
        return 0d;
    }

    // ---- EBML 原语 -------------------------------------------------------------------

    private static final class Vint {
        final long value;
        /** 带长度标记位的原值：元素 ID 要用它比对，数据 vint 用 {@link #value}。 */
        final long raw;
        final int bytes;

        Vint(long value, long raw, int bytes) {
            this.value = value;
            this.raw = raw;
            this.bytes = bytes;
        }

        static Vint decode(byte[] data, int offset) {
            if (data == null || offset < 0 || offset >= data.length) {
                return null;
            }
            int first = data[offset] & 0xff;
            int length = 1;
            int mask = 0x80;
            while (length <= 8 && (first & mask) == 0) {
                mask >>= 1;
                length++;
            }
            if (length > 8 || offset + length > data.length) {
                return null;
            }
            long value = first & (mask - 1);
            for (int index = 1; index < length; index++) {
                value = (value << 8) | (data[offset + index] & 0xffL);
            }
            return new Vint(value, value | (mask << (8 * (length - 1))), length);
        }
    }

    /**
     * 顺序遍历 {@code [start, end)} 里的元素。负载直接从当前分块取，取不到就补一次区间读；
     * 头部跨过分块边界时重新按元素边界对齐，所以每个元素只可能重读几十字节。
     */
    private final class Walker {
        private final long end;
        private final Walker parent;
        private long position;
        private byte[] chunk = new byte[0];
        private long chunkStart;
        long id;
        long payloadStart;
        long payloadEnd;

        Walker(long start, long end, Walker parent) {
            this.position = start;
            this.end = end;
            this.parent = parent;
        }

        boolean advance() throws IOException {
            while (position < end) {
                if (!chunkCovers(position, 16)) {
                    int want = (int) Math.min(CHUNK_BYTES, Math.max(16, end - position));
                    chunk = source.readAt(position, want);
                    chunkStart = position;
                    if (chunk.length == 0) {
                        return false;
                    }
                }
                int offset = (int) (position - chunkStart);
                Vint idField = Vint.decode(chunk, offset);
                Vint sizeField = idField == null ? null : Vint.decode(chunk, offset + idField.bytes);
                if (idField == null || sizeField == null) {
                    // 头部不完整（分块刚好切在中间）：挪一字节重新对齐。
                    position += 1;
                    continue;
                }
                long payloadStart = position + idField.bytes + sizeField.bytes;
                long marker = (1L << (7 * sizeField.bytes)) - 1;
                long size = sizeField.value;
                long payloadEnd = size == marker || size == 0 ? end
                        : Math.min(end, payloadStart + size);
                if (payloadEnd < payloadStart || payloadStart > end) {
                    position += 1;
                    continue;
                }
                id = idField.raw;
                this.payloadStart = payloadStart;
                this.payloadEnd = payloadEnd;
                position = payloadEnd;
                return true;
            }
            return false;
        }

        byte[] payload() throws IOException {
            int length = (int) Math.min(INLINE_LIMIT, payloadEnd - payloadStart);
            if (chunkCovers(payloadStart, length) && length > 0) {
                byte[] copy = new byte[length];
                System.arraycopy(chunk, (int) (payloadStart - chunkStart), copy, 0, length);
                return copy;
            }
            if (parent != null && parent.chunkCovers(payloadStart, length) && length > 0) {
                return parent.slice(payloadStart, length);
            }
            return source.readAt(payloadStart, length);
        }

        private byte[] slice(long offset, int length) {
            byte[] copy = new byte[length];
            System.arraycopy(chunk, (int) (offset - chunkStart), copy, 0, length);
            return copy;
        }

        private boolean chunkCovers(long offset, int length) {
            return offset >= chunkStart && offset - chunkStart + length <= chunk.length;
        }
    }
}
