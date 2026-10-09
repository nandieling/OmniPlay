package com.omniplay.tv.local;

import android.content.Context;
import android.os.Handler;
import android.os.Looper;
import android.os.StatFs;

import com.omniplay.tv.data.Models;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.InterruptedIOException;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Base64;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

import jcifs.smb.SmbFile;

/**
 * Offline cache ported from the mac build: NAS files (WebDAV / SMB) are copied to a
 * local directory so playback stops depending on the network. Tasks run strictly one
 * at a time in episode order, downloads land in a {@code .part} file and are renamed
 * only when complete, and every task pre-flights the target disk for its own size plus
 * a 1 GiB head-room.
 */
public final class OfflineCacheManager {
    private static final long RESERVED_FREE_BYTES = 1024L * 1024L * 1024L;
    private static final int BUFFER_BYTES = 256 * 1024;

    public interface Listener {
        /** Called on the main thread whenever queue state or progress changes. */
        void onCacheProgress(String statusText, int percent, int queuedCount);
    }

    private static OfflineCacheManager instance;

    private final LocalEngine engine;
    private final LocalMediaLibrary library;
    private final TvSettings settings;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final ExecutorService executor = Executors.newSingleThreadExecutor();
    private final ArrayDeque<Job> queue = new ArrayDeque<>();

    private volatile Job current;
    private volatile boolean cancelRequested;
    private volatile Listener listener;
    private volatile String statusText = "";
    private volatile int percent;

    private static final class Job {
        final String fileId;
        final String label;
        final String playbackUrl;
        final String fileName;

        Job(String fileId, String label, String playbackUrl, String fileName) {
            this.fileId = fileId;
            this.label = label;
            this.playbackUrl = playbackUrl;
            this.fileName = fileName;
        }
    }

    private OfflineCacheManager(Context context, LocalEngine engine, LocalMediaLibrary library,
                                TvSettings settings) {
        this.engine = engine;
        this.library = library;
        this.settings = settings;
    }

    static synchronized OfflineCacheManager shared(Context context, LocalEngine engine,
                                                   LocalMediaLibrary library, TvSettings settings) {
        if (instance == null) {
            instance = new OfflineCacheManager(context, engine, library, settings);
        }
        return instance;
    }

    public void setListener(Listener listener) {
        this.listener = listener;
        if (listener != null) {
            main.post(() -> listener.onCacheProgress(statusText, percent, queuedCount()));
        }
    }

    public String statusText() {
        return statusText;
    }

    public int percent() {
        return percent;
    }

    public synchronized int queuedCount() {
        return queue.size() + (current == null ? 0 : 1);
    }

    public String cachedPathFor(String fileId) {
        LocalMediaLibrary.CacheRecord record = library.cacheRecord(fileId);
        if (record == null) {
            return null;
        }
        File file = new File(record.localPath);
        if (!file.isFile()) {
            library.removeCacheRecord(fileId);
            return null;
        }
        return record.localPath;
    }

    public List<String> cachedFileIds() {
        List<String> result = new ArrayList<>();
        for (LocalMediaLibrary.CacheRecord record : library.allCacheRecords().values()) {
            if (new File(record.localPath).isFile()) {
                result.add(record.fileId);
            }
        }
        return result;
    }

    public long cachedBytes() {
        long total = 0;
        for (LocalMediaLibrary.CacheRecord record : library.allCacheRecords().values()) {
            File file = new File(record.localPath);
            total += file.isFile() ? file.length() : record.bytes;
        }
        return total;
    }

    /** Queues files in episode order; duplicates and already-cached files are skipped. */
    public String enqueue(List<Models.VideoFile> files, String groupLabel) {
        File directory = settings.offlineCacheDirectory();
        if (!directory.isDirectory() && !directory.mkdirs()) {
            return "无法写入缓存目录：" + directory.getAbsolutePath();
        }
        int added = 0;
        int skipped = 0;
        StringBuilder unsupported = new StringBuilder();
        for (Models.VideoFile file : files) {
            if (file == null || file.id == null || file.id.isEmpty()) {
                continue;
            }
            if (cachedPathFor(file.id) != null) {
                skipped++;
                continue;
            }
            String url = file.playbackUrl;
            if (url == null || url.isEmpty() || url.startsWith("file://")) {
                if (unsupported.length() == 0) {
                    unsupported.append("本地目录的文件已在设备上，无需缓存。");
                }
                skipped++;
                continue;
            }
            boolean supported = url.startsWith("http://") || url.startsWith("https://") || url.startsWith("smb://");
            if (!supported) {
                if (unsupported.length() == 0) {
                    unsupported.append("该媒体源暂不支持离线缓存。");
                }
                skipped++;
                continue;
            }
            boolean alreadyQueued;
            synchronized (queue) {
                alreadyQueued = containsFile(file.id);
            }
            if (alreadyQueued) {
                skipped++;
                continue;
            }
            synchronized (queue) {
                queue.add(new Job(file.id, cacheLabel(file, groupLabel), url, file.fileName));
            }
            added++;
        }
        executor.execute(this::drain);
        publish("已加入离线缓存 " + added + " 个文件" + (skipped > 0 ? "（跳过 " + skipped + " 个）" : "")
                + (unsupported.length() == 0 ? "" : " · " + unsupported), percent, queuedCount());
        if (added == 0) {
            return skipped > 0 ? "没有需要缓存的文件（已缓存、本地源或重复任务）。" : "没有可缓存的文件。";
        }
        return null;
    }

    private static String cacheLabel(Models.VideoFile file, String groupLabel) {
        if (groupLabel != null && !groupLabel.isEmpty()) {
            String episode = file.displayLabel == null || file.displayLabel.isEmpty()
                    ? "" : " " + file.displayLabel;
            return groupLabel + episode;
        }
        return file.fileName == null ? file.id : file.fileName;
    }

    private boolean containsFile(String fileId) {
        if (current != null && current.fileId.equals(fileId)) {
            return true;
        }
        for (Job job : queue) {
            if (job.fileId.equals(fileId)) {
                return true;
            }
        }
        return false;
    }

    public boolean cancelCurrent() {
        if (current == null) {
            return false;
        }
        cancelRequested = true;
        return true;
    }

    public synchronized void cancelAll() {
        queue.clear();
        cancelRequested = true;
    }

    /** Removes every cached copy and its database record. */
    public String clearAll() {
        cancelAll();
        int removed = 0;
        long freed = 0;
        for (LocalMediaLibrary.CacheRecord record : library.allCacheRecords().values()) {
            File file = new File(record.localPath);
            freed += file.length();
            if (file.isFile() && !file.delete()) {
                continue;
            }
            library.removeCacheRecord(record.fileId);
            removed++;
        }
        return "已清理 " + removed + " 个缓存文件，释放 " + formatSize(freed) + "。";
    }

    private void drain() {
        while (true) {
            Job job;
            synchronized (this) {
                if (queue.isEmpty()) {
                    current = null;
                    cancelRequested = false;
                    publish("", 0, 0);
                    return;
                }
                job = queue.poll();
                current = job;
                cancelRequested = false;
            }
            try {
                String error = runJob(job);
                if (error != null) {
                    publish("缓存失败：" + error, 0, queuedCount());
                }
            } catch (InterruptedIOException interrupted) {
                publish("已取消离线缓存。", 0, queuedCount());
            } catch (Exception error) {
                publish("缓存失败：" + safeMessage(error), 0, queuedCount());
            } finally {
                synchronized (this) {
                    current = null;
                }
            }
        }
    }

    /** @return an error message, or null when the file was cached (or intentionally skipped). */
    private String runJob(Job job) throws IOException {
        File directory = settings.offlineCacheDirectory();
        File target = new File(directory, targetName(job));
        File partFile = new File(target.getAbsolutePath() + ".part");

        long expectedBytes = expectedSize(job.playbackUrl);
        if (expectedBytes > 0 && freeBytes(directory) < expectedBytes + RESERVED_FREE_BYTES) {
            return "目标磁盘空间不足（需要 " + formatSize(expectedBytes) + " 外加 1 GiB 余量）。";
        }

        long written = 0;
        long total = expectedBytes;
        int lastPercent = -1;
        try (InputStream input = openInput(job.playbackUrl);
             FileOutputStream output = new FileOutputStream(partFile)) {
            byte[] buffer = new byte[BUFFER_BYTES];
            while (true) {
                if (cancelRequested) {
                    throw new InterruptedIOException("cancelled");
                }
                int read = input.read(buffer);
                if (read < 0) {
                    break;
                }
                output.write(buffer, 0, read);
                written += read;
                if (total > 0) {
                    int next = (int) Math.min(99, written * 100 / total);
                    if (next != lastPercent) {
                        lastPercent = next;
                        publish("离线缓存 " + job.label + " · " + formatSize(written) + " / "
                                + formatSize(total), next, queuedCount());
                    }
                } else if (written / (4L * 1024 * 1024) != (written - read) / (4L * 1024 * 1024)) {
                    publish("离线缓存 " + job.label + " · " + formatSize(written), 0, queuedCount());
                }
            }
        } catch (IOException error) {
            // Keep the disk clean when the download was cancelled or broke off.
            if (partFile.isFile()) {
                //noinspection ResultOfMethodCallIgnored
                partFile.delete();
            }
            throw error;
        }

        if (!partFile.renameTo(target)) {
            //noinspection ResultOfMethodCallIgnored
            partFile.delete();
            return "无法完成缓存文件写入：" + target.getAbsolutePath();
        }
        library.saveCacheRecord(job.fileId, target.getAbsolutePath(), job.playbackUrl, written);
        return null;
    }

    private String targetName(Job job) {
        String name = job.fileName == null || job.fileName.isEmpty() ? job.fileId : job.fileName;
        name = name.replaceAll("[\\\\/:*?\"<>|]", "_");
        String suffix = Integer.toHexString(job.fileId.hashCode());
        int dot = name.lastIndexOf('.');
        if (dot > 0 && name.length() - dot <= 8) {
            return name.substring(0, dot) + "-" + suffix + name.substring(dot);
        }
        return name + "-" + suffix;
    }

    private InputStream openInput(String playbackUrl) throws IOException {
        if (playbackUrl.startsWith("smb://")) {
            SmbFile file = engine.smbFile(playbackUrl);
            return file.getInputStream();
        }
        URL url = new URL(playbackUrl);
        String userInfo = url.getUserInfo();
        String plainUrl = playbackUrl;
        String authorization = null;
        if (userInfo != null && !userInfo.isEmpty()) {
            authorization = "Basic " + Base64.getEncoder().encodeToString(
                    userInfo.getBytes(StandardCharsets.UTF_8));
            plainUrl = playbackUrl.replaceFirst("//[^@/]+@", "//");
        }
        HttpURLConnection connection = (HttpURLConnection) new URL(plainUrl).openConnection();
        connection.setConnectTimeout(15000);
        connection.setReadTimeout(30000);
        connection.setRequestProperty("User-Agent", "OmniPlay-Android/1.7");
        if (authorization != null) {
            connection.setRequestProperty("Authorization", authorization);
        }
        int status = connection.getResponseCode();
        if (status < 200 || status >= 300) {
            connection.disconnect();
            throw new IOException("媒体服务器返回 " + status);
        }
        return connection.getInputStream();
    }

    private long expectedSize(String playbackUrl) {
        try {
            if (playbackUrl.startsWith("smb://")) {
                return engine.smbFile(playbackUrl).length();
            }
            String plainUrl = playbackUrl;
            String authorization = null;
            URL parsed = new URL(playbackUrl);
            if (parsed.getUserInfo() != null && !parsed.getUserInfo().isEmpty()) {
                authorization = "Basic " + Base64.getEncoder().encodeToString(
                        parsed.getUserInfo().getBytes(StandardCharsets.UTF_8));
                plainUrl = playbackUrl.replaceFirst("//[^@/]+@", "//");
            }
            HttpURLConnection connection = (HttpURLConnection) new URL(plainUrl).openConnection();
            connection.setRequestMethod("HEAD");
            connection.setConnectTimeout(10000);
            connection.setReadTimeout(10000);
            if (authorization != null) {
                connection.setRequestProperty("Authorization", authorization);
            }
            long length = connection.getContentLengthLong();
            connection.disconnect();
            return Math.max(0, length);
        } catch (Exception ignored) {
            return 0;
        }
    }

    public static long freeBytes(File directory) {
        File probe = directory;
        while (probe != null && !probe.exists()) {
            probe = probe.getParentFile();
        }
        if (probe == null) {
            return 0;
        }
        try {
            return new StatFs(probe.getAbsolutePath()).getAvailableBytes();
        } catch (Exception ignored) {
            return 0;
        }
    }

    public static String formatSize(long bytes) {
        if (bytes <= 0) {
            return "0 B";
        }
        if (bytes >= 1024L * 1024L * 1024L) {
            return String.format(Locale.CHINA, "%.2f GiB", bytes / (1024.0 * 1024.0 * 1024.0));
        }
        if (bytes >= 1024L * 1024L) {
            return String.format(Locale.CHINA, "%.1f MiB", bytes / (1024.0 * 1024.0));
        }
        return bytes + " B";
    }

    private static String safeMessage(Exception error) {
        String message = error.getMessage();
        return message == null || message.isEmpty() ? error.getClass().getSimpleName() : message;
    }

    private void publish(String text, int progress, int queued) {
        statusText = text == null ? "" : text;
        percent = progress;
        Listener callback = listener;
        if (callback == null) {
            return;
        }
        int snapshot = queued;
        String snapshotText = statusText;
        int snapshotPercent = percent;
        main.post(() -> callback.onCacheProgress(snapshotText, snapshotPercent, snapshot));
    }
}
