package com.omniplay.tv.local;

import com.omniplay.tv.subtitle.MatroskaSubtitleExtractor;
import com.omniplay.tv.subtitle.SeekableSource;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.io.RandomAccessFile;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * 内封字幕转档：把 MKV 里的内封字幕抽成贴着视频放的 sidecar（{@code 片名.zh.srt} /
 * {@code 片名.zh.ass} / {@code 片名.zh.sup}）写回媒体源。之后播放读的是这个小文件，走现成的字幕
 * 覆盖层，于是高性能直出模式下也能看字幕。
 *
 * <p>文本轨每个窗口重写一次整份 sidecar（几十 KB 而已），所以第一次看这部片也能边转边有字幕；
 * PGS 位图轨先追加到本机临时文件、跑完一次性 PUT，全片能有几百 MB，不进内存。
 */
public final class SubtitleSidecarService {
    /** 一个媒体源的读写入口。三种源各给一个实现，服务本身不认识具体协议。 */
    public interface Access {
        SeekableSource open(String relativePath) throws IOException;

        void write(String relativePath, byte[] body) throws IOException;

        void writeFile(String relativePath, File file) throws IOException;

        boolean exists(String relativePath);

        byte[] read(String relativePath) throws IOException;

        /** 能直接给 HttpURLConnection 用的地址；返回空串表示得走 {@link #read}。 */
        String playbackUrl(String relativePath);
    }

    public interface Progress {
        /** @return false 表示请停下，下次从 progress.resumeOffset 续跑 */
        boolean onWindow(int cueCount, MatroskaSubtitleExtractor.Result progress);
    }

    /** 一次转档的结果。 */
    public static final class Outcome {
        public String sidecarPath;
        public String format;
        public String language;
        public int trackNumber;
        public int cueCount;
        public long byteOffset;
        public double coveredSeconds;
        public boolean complete;
    }

    /** 播放时整份读进来的上限：文本轨几十 KB，PGS 能有几百 MB，太大就别硬塞给电视。 */
    static final long MAX_READ_BYTES = 64L << 20;

    private SubtitleSidecarService() {
    }

    static void requirePlayableSize(long length) throws IOException {
        if (length > MAX_READ_BYTES) {
            throw new IOException("转档出来的字幕有 " + (length >> 20) + " MB，太大了，这条先不用它。");
        }
    }

    /** {@code Movies/A.mkv} + zh + srt → {@code Movies/A.zh.srt} */
    public static String sidecarPathFor(String videoRelativePath, String language, String format) {
        String relative = videoRelativePath == null ? "" : videoRelativePath.replace('\\', '/');
        int slash = relative.lastIndexOf('/');
        String directory = slash < 0 ? "" : relative.substring(0, slash + 1);
        String fileName = slash < 0 ? relative : relative.substring(slash + 1);
        int dot = fileName.lastIndexOf('.');
        return directory + (dot <= 0 ? fileName : fileName.substring(0, dot)) + "." + language + "." + format;
    }

    /** 探一下这个文件里有哪些能转的字幕轨（只读文件头，开销几十 KB）。 */
    public static List<MatroskaSubtitleExtractor.Track> probe(Access access, String relativePath) {
        SeekableSource source = null;
        try {
            source = access.open(relativePath);
            return MatroskaSubtitleExtractor.looksLikeMatroska(source)
                    ? MatroskaSubtitleExtractor.tracks(source) : Collections.emptyList();
        } catch (IOException ignored) {
            return Collections.emptyList();
        } finally {
            close(source);
        }
    }

    /**
     * 抽一条轨并写回媒体源。
     *
     * @param resumeOffset 上次留下的续跑位置（0 从头）
     * @param leadSeconds  领先这么多就收工；&lt;=0 表示整轨跑完
     */
    public static Outcome convert(Access access, String videoRelativePath,
                                 MatroskaSubtitleExtractor.Track track, long resumeOffset,
                                 double leadSeconds, File scratchDir, Progress progress)
            throws IOException {
        Outcome outcome = new Outcome();
        outcome.trackNumber = track.trackNumber;
        outcome.language = track.language;
        outcome.format = track.format;
        outcome.sidecarPath = sidecarPathFor(videoRelativePath, track.language, track.format);
        File scratch = track.isBitmap() ? scratchFile(scratchDir, videoRelativePath, track) : null;
        if (scratch != null && resumeOffset <= 0 && scratch.exists()
                && !access.exists(outcome.sidecarPath)) {
            //noinspection ResultOfMethodCallIgnored
            scratch.delete();
        }
        if (scratch != null && resumeOffset > 0 && !scratch.exists() && access.exists(outcome.sidecarPath)) {
            // 本机缓存被清过：把盘上转好的前半段拉回来接着追加；拉不动就从这一段重新写。
            try {
                writeToScratch(scratch, access.read(outcome.sidecarPath));
            } catch (IOException ignored) {
                //noinspection ResultOfMethodCallIgnored
                scratch.delete();
            }
        }
        List<MatroskaSubtitleExtractor.Cue> seed = Collections.emptyList();
        if (!track.isBitmap() && resumeOffset > 0 && access.exists(outcome.sidecarPath)) {
            try {
                seed = readExisting(access.read(outcome.sidecarPath), track);
            } catch (IOException ignored) {
                seed = Collections.emptyList();
            }
        }
        WindowSink sink = new WindowSink(access, outcome.sidecarPath, track, scratch, progress);
        SeekableSource source = access.open(videoRelativePath);
        try {
            MatroskaSubtitleExtractor.Result result = MatroskaSubtitleExtractor.run(
                    source, track.trackNumber, resumeOffset, 45, leadSeconds, sink, seed);
            outcome.byteOffset = result.resumeOffset;
            outcome.coveredSeconds = result.coveredSeconds;
            outcome.cueCount = result.cueCount;
            outcome.complete = true;
        } catch (Paused paused) {
            // 领先够了或被叫停：盘上的 sidecar 覆盖到 coveredSeconds 为止，仍然可用。
            outcome.byteOffset = sink.lastOffset;
            outcome.coveredSeconds = sink.lastCovered;
            outcome.cueCount = sink.lastCount;
            outcome.complete = false;
        } catch (WriteFailure failure) {
            close(source);
            throw failure.causedBy();
        }
        close(source);
        if (scratch != null && sink.writtenCues > 0) {
            access.writeFile(outcome.sidecarPath, scratch);
        }
        return outcome;
    }

    /** 转档中途停下来（领先够了、或者播放里腾出带宽），不是错误。 */
    private static final class Paused extends RuntimeException {
        Paused() {
            super("转档暂停", null, false, false);
        }
    }

    private static final class WriteFailure extends RuntimeException {
        private final IOException cause;

        WriteFailure(IOException cause) {
            super(cause.getMessage(), cause, false, false);
            this.cause = cause;
        }

        IOException causedBy() {
            return cause;
        }
    }

    /**
     * 窗口回调：文本轨整份重写（几十 KB 而已，重写比补丁简单也不容易写坏），位图轨只追加新增的
     * 包（全片能有几百 MB，整份不进内存）。
     */
    private static final class WindowSink implements MatroskaSubtitleExtractor.Sink {
        private final Access access;
        private final String sidecarPath;
        private final MatroskaSubtitleExtractor.Track track;
        private final File scratch;
        private final Progress progress;
        int writtenCues;
        long lastOffset;
        double lastCovered;
        int lastCount;

        WindowSink(Access access, String sidecarPath, MatroskaSubtitleExtractor.Track track,
                   File scratch, Progress progress) {
            this.access = access;
            this.sidecarPath = sidecarPath;
            this.track = track;
            this.scratch = scratch;
            this.progress = progress;
        }

        @Override
        public void onWindow(List<MatroskaSubtitleExtractor.Cue> cues,
                             MatroskaSubtitleExtractor.Result progressOf) {
            try {
                if (scratch == null) {
                    access.write(sidecarPath, MatroskaSubtitleExtractor.serialize(track, cues)
                            .getBytes(StandardCharsets.UTF_8));
                } else {
                    append(cues);
                }
                lastOffset = progressOf.resumeOffset;
                lastCovered = progressOf.coveredSeconds;
                lastCount = progressOf.cueCount;
                if (progress != null && !progress.onWindow(progressOf.cueCount, progressOf)) {
                    throw new Paused();
                }
            } catch (IOException error) {
                throw new WriteFailure(error);
            }
        }

        private void append(List<MatroskaSubtitleExtractor.Cue> cues) throws IOException {
            File parent = scratch.getParentFile();
            if (parent != null) {
                //noinspection ResultOfMethodCallIgnored
                parent.mkdirs();
            }
            boolean fresh = !scratch.isFile();
            try (OutputStream output = new FileOutputStream(scratch, !fresh)) {
                for (int index = fresh ? 0 : writtenCues; index < cues.size(); index++) {
                    MatroskaSubtitleExtractor.Cue cue = cues.get(index);
                    if (cue.pgsPacket != null) {
                        output.write(cue.pgsPacket);
                    }
                }
                writtenCues = cues.size();
            }
        }
    }

    // ---- 三种媒体源的 Access --------------------------------------------------------------

    /** 本地目录媒体源。 */
    public static final class LocalAccess implements Access {
        private final File root;

        public LocalAccess(File root) {
            this.root = root;
        }

        private File file(String relativePath) {
            return new File(root, relativePath == null ? ""
                    : relativePath.replace('/', File.separatorChar));
        }

        @Override
        public SeekableSource open(String relativePath) throws IOException {
            return new FileSource(file(relativePath));
        }

        @Override
        public void write(String relativePath, byte[] body) throws IOException {
            File target = file(relativePath);
            File parent = target.getParentFile();
            if (parent != null) {
                //noinspection ResultOfMethodCallIgnored
                parent.mkdirs();
            }
            try (OutputStream output = new FileOutputStream(target, false)) {
                output.write(body);
            }
        }

        @Override
        public void writeFile(String relativePath, File file) throws IOException {
            requirePlayableSize(file.length());
            File target = file(relativePath);
            File parent = target.getParentFile();
            if (parent != null) {
                //noinspection ResultOfMethodCallIgnored
                parent.mkdirs();
            }
            try (InputStream input = new FileInputStream(file);
                 OutputStream output = new FileOutputStream(target, false)) {
                byte[] buffer = new byte[131072];
                int read;
                while ((read = input.read(buffer)) > 0) {
                    output.write(buffer, 0, read);
                }
            }
        }

        @Override
        public boolean exists(String relativePath) {
            return file(relativePath).isFile();
        }

        @Override
        public byte[] read(String relativePath) throws IOException {
            requirePlayableSize(file(relativePath).length());
            return readAll(file(relativePath));
        }

        @Override
        public String playbackUrl(String relativePath) {
            return "file://" + file(relativePath).getAbsolutePath();
        }
    }

    /** WebDAV 媒体源：读用 Range，写用 PUT。 */
    public static final class WebDavAccess implements Access {
        private final WebDavClient client;

        public WebDavAccess(String rootUrl, String username, String password) {
            this.client = new WebDavClient(rootUrl, new WebDavClient.Credential(username, password));
        }

        @Override
        public SeekableSource open(String relativePath) {
            return new HttpRangeSource(client.streamUrl(relativePath));
        }

        @Override
        public void write(String relativePath, byte[] body) throws IOException {
            require(Http.request(client.streamUrl(relativePath), "PUT", null, body, 120000).status);
        }

        @Override
        public void writeFile(String relativePath, File file) throws IOException {
            require(Http.putFile(client.streamUrl(relativePath), null, file, 600000).status);
        }

        @Override
        public boolean exists(String relativePath) {
            try {
                return Http.request(client.streamUrl(relativePath), "HEAD", null, null, 8000).status < 400;
            } catch (IOException ignored) {
                return false;
            }
        }

        private long length(String relativePath) {
            try {
                Http.Response head = Http.request(client.streamUrl(relativePath), "HEAD", null, null, 8000);
                String value = head.header("content-length");
                return value == null ? 0 : Math.max(0, Long.parseLong(value.trim()));
            } catch (Exception ignored) {
                return 0;
            }
        }

        @Override
        public byte[] read(String relativePath) throws IOException {
            requirePlayableSize(length(relativePath));
            Http.Response response = Http.request(client.streamUrl(relativePath), "GET", null, null, 60000);
            if (response.status >= 400) {
                throw new IOException("读字幕文件返回 HTTP " + response.status);
            }
            return response.body;
        }

        @Override
        public String playbackUrl(String relativePath) {
            return client.streamUrl(relativePath);
        }

        private static void require(int status) throws IOException {
            if (status >= 400) {
                throw new IOException("媒体源不接受写入（HTTP " + status + "），共享可能是只读的。");
            }
        }
    }

    /** SMB 媒体源。 */
    public static final class SmbAccess implements Access {
        private final SmbClient client;

        public SmbAccess(SmbClient client) {
            this.client = client;
        }

        @Override
        public SeekableSource open(String relativePath) throws IOException {
            return new SmbSource(client, client.absoluteUrl(relativePath));
        }

        @Override
        public void write(String relativePath, byte[] body) throws IOException {
            client.write(client.absoluteUrl(relativePath), body);
        }

        @Override
        public void writeFile(String relativePath, File file) throws IOException {
            client.writeFile(client.absoluteUrl(relativePath), file);
        }

        @Override
        public boolean exists(String relativePath) {
            return client.exists(client.absoluteUrl(relativePath));
        }

        @Override
        public byte[] read(String relativePath) throws IOException {
            requirePlayableSize(client.length(client.absoluteUrl(relativePath)));
            return client.readAll(client.absoluteUrl(relativePath));
        }

        @Override
        public String playbackUrl(String relativePath) {
            // SMB 没有能直接交给 HttpURLConnection 的地址，播放侧改用 read()。
            return "";
        }
    }

    private static File scratchFile(File scratchDir, String videoRelativePath,
                                   MatroskaSubtitleExtractor.Track track) {
        File directory = new File(scratchDir, "sidecars");
        //noinspection ResultOfMethodCallIgnored
        directory.mkdirs();
        String safe = videoRelativePath.replaceAll("[^A-Za-z0-9._-]", "_")
                + "." + track.trackNumber + ".sup";
        return new File(directory, safe.length() > 120 ? safe.substring(safe.length() - 120) : safe);
    }

    private static void writeToScratch(File scratch, byte[] body) throws IOException {
        File parent = scratch.getParentFile();
        if (parent != null) {
            //noinspection ResultOfMethodCallIgnored
            parent.mkdirs();
        }
        try (OutputStream output = new FileOutputStream(scratch, false)) {
            output.write(body);
        }
    }

    /**
     * 把自己写出去的 sidecar 再读成条目：续跑时当作已有结果。
     * 只认自家写的格式（SRT 空行分段、ASS 认 Dialogue 行），不需要通用字幕解析器。
     */
    static List<MatroskaSubtitleExtractor.Cue> readExisting(byte[] body,
                                                           MatroskaSubtitleExtractor.Track track) {
        ArrayList<MatroskaSubtitleExtractor.Cue> cues = new ArrayList<>();
        String text = new String(body, StandardCharsets.UTF_8).replace("\r\n", "\n");
        if ("ass".equals(track.format)) {
            for (String line : text.split("\n")) {
                if (!line.startsWith("Dialogue:")) {
                    continue;
                }
                String[] fields = line.substring("Dialogue:".length()).trim().split(",", 10);
                if (fields.length < 3) {
                    continue;
                }
                cues.add(new MatroskaSubtitleExtractor.Cue(timestamp(fields[1], 2),
                        timestamp(fields[2], 2), line, null));
            }
            return cues;
        }
        for (String entry : text.split("\n\s*\n")) {
            String[] lines = entry.trim().split("\n");
            if (lines.length < 2) {
                continue;
            }
            int arrow = lines[1].indexOf("-->");
            if (arrow < 0) {
                continue;
            }
            StringBuilder content = new StringBuilder();
            for (int index = 2; index < lines.length; index++) {
                if (content.length() > 0) {
                    content.append('\n');
                }
                content.append(lines[index]);
            }
            cues.add(new MatroskaSubtitleExtractor.Cue(timestamp(lines[1].substring(0, arrow), 3),
                    timestamp(lines[1].substring(arrow + 3), 3), content.toString(), null));
        }
        return cues;
    }

    /** {@code 00:01:02,500} / {@code 0:01:02.50} 都按「几位小数」解析成秒。 */
    private static double timestamp(String value, int fractionDigits) {
        String[] parts = value.trim().split("[:,.]");
        if (parts.length < 4) {
            return 0;
        }
        try {
            long hours = Long.parseLong(parts[0].trim());
            long minutes = Long.parseLong(parts[1].trim());
            long seconds = Long.parseLong(parts[2].trim());
            String fraction = parts[3].trim();
            fraction = fraction.length() > fractionDigits
                    ? fraction.substring(0, fractionDigits)
                    : padRight(fraction, fractionDigits);
            double scale = fractionDigits == 3 ? 1000d : 100d;
            return hours * 3600 + minutes * 60 + seconds
                    + (fraction.isEmpty() ? 0 : Long.parseLong(fraction)) / scale;
        } catch (Exception ignored) {
            return 0;
        }
    }

    private static String padRight(String value, int length) {
        StringBuilder out = new StringBuilder(value);
        while (out.length() < length) {
            out.append('0');
        }
        return out.toString();
    }

    private static void close(SeekableSource source) {
        if (source == null) {
            return;
        }
        try {
            source.close();
        } catch (IOException ignored) {
        }
    }

    /** 本地文件。 */
    public static final class FileSource implements SeekableSource {
        private final File file;
        private RandomAccessFile access;

        public FileSource(File file) throws IOException {
            this.file = file;
            if (!file.isFile()) {
                throw new IOException("找不到文件：" + file.getAbsolutePath());
            }
        }

        @Override
        public long size() {
            return file.length();
        }

        @Override
        public synchronized byte[] readAt(long offset, int length) throws IOException {
            if (access == null) {
                access = new RandomAccessFile(file, "r");
            }
            byte[] buffer = new byte[(int) Math.max(0, Math.min(length, file.length() - offset))];
            if (buffer.length == 0) {
                return buffer;
            }
            access.seek(offset);
            int read = 0;
            while (read < buffer.length) {
                int step = access.read(buffer, read, buffer.length - read);
                if (step <= 0) {
                    break;
                }
                read += step;
            }
            return read == buffer.length ? buffer : Arrays.copyOf(buffer, read);
        }

        @Override
        public void close() throws IOException {
            if (access != null) {
                access.close();
                access = null;
            }
        }
    }

    /** HTTP / WebDAV：每次一次 Range 请求。 */
    public static final class HttpRangeSource implements SeekableSource {
        private final String url;
        private long size = -1;

        public HttpRangeSource(String url) {
            this.url = url;
        }

        @Override
        public long size() throws IOException {
            if (size < 0) {
                Map<String, String> headers = new HashMap<>();
                headers.put("Range", "bytes=0-0");
                Http.Response response = Http.request(url, "GET", headers, null, 15000);
                String range = response.header("content-range");
                size = range != null && range.contains("/")
                        ? parseLong(range.substring(range.lastIndexOf('/') + 1))
                        : response.body.length;
                if (size <= 0) {
                    throw new IOException("读不到文件长度：" + url);
                }
            }
            return size;
        }

        @Override
        public byte[] readAt(long offset, int length) throws IOException {
            long end = Math.min(size() - 1, offset + length - 1);
            Map<String, String> headers = new HashMap<>();
            headers.put("Range", "bytes=" + offset + "-" + end);
            Http.Response response = Http.request(url, "GET", headers, null, 60000);
            if (response.status >= 400) {
                throw new IOException("读取媒体源返回 HTTP " + response.status);
            }
            return response.body;
        }

        @Override
        public void close() {
        }

        private static long parseLong(String value) {
            try {
                return Long.parseLong(value.trim());
            } catch (Exception ignored) {
                return -1;
            }
        }
    }

    /** SMB：一个随机访问句柄从头用到尾，免得每个块重开一次连接。 */
    public static final class SmbSource implements SeekableSource {
        private final SmbClient client;
        private final String url;

        public SmbSource(SmbClient client, String url) {
            this.client = client;
            this.url = url;
        }

        @Override
        public long size() throws IOException {
            return client.length(url);
        }

        @Override
        public byte[] readAt(long offset, int length) throws IOException {
            return client.readAt(url, offset, length);
        }

        @Override
        public void close() {
        }
    }

    static byte[] readAll(File file) throws IOException {
        try (InputStream input = new FileInputStream(file)) {
            return drain(input);
        }
    }

    static byte[] drain(InputStream input) throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        byte[] buffer = new byte[32768];
        int read;
        while ((read = input.read(buffer)) > 0) {
            out.write(buffer, 0, read);
        }
        input.close();
        return out.toByteArray();
    }
}
