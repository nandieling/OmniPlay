package com.omniplay.tv.local;

import jcifs.CIFSContext;
import jcifs.config.PropertyConfiguration;
import jcifs.context.BaseContext;
import jcifs.smb.NtlmPasswordAuthentication;
import jcifs.smb.SmbFile;
import jcifs.smb.SmbFileFilter;
import jcifs.smb.SmbRandomAccessFile;

import java.io.IOException;
import java.util.Locale;

/**
 * Minimal pure-Java SMB client (jcifs-ng) used to walk a share during scanning and
 * to read bytes for the local HTTP range proxy. Mirrors {@link WebDavClient}.
 */
public final class SmbClient {
    /** Walks the share tree, yielding video files with their group folder. */
    public interface SmbVideoSink {
        void onVideo(String fileName, String relativePath, long sizeBytes);
    }

    private final String root;
    private final String username;
    private final String password;
    // 光盘的 BDMV / STREAM / DISC1 也要占三层，深度太小会让整盘看不见；
    // 本地目录走 12 层，这里对齐。这条只当兜底：真正的「不重走」由 visited 集合保证。
    private int maxDepth = 12;
    /** 一轮遍历最多列多少个目录：共享里有环回链接时，宁可少扫一片，也别把电视跑死。 */
    private static final int MAX_DIRECTORIES = 20000;

    public SmbClient(String root, String username, String password) {
        this.root = normalize(root);
        this.username = username == null ? "" : username;
        this.password = password == null ? "" : password;
    }

    public String root() {
        return root;
    }

    /**
     * Loads the parts of jcifs that a browse or a playback needs, away from the UI thread.
     * The setup screen calls this on startup: if the library cannot initialize on this device the
     * reason shows up right away instead of only when the user happens to tap SMB.
     */
    public static String warmUp() throws IOException {
        CIFSContext ctx = null;
        try {
            Class.forName("jcifs.smb.SmbFile");
            Class.forName("jcifs.smb.SmbRandomAccessFile");
            ctx = new BaseContext(new PropertyConfiguration(new java.util.Properties()));
            return "jcifs-ng";
        } catch (Throwable error) {
            throw translation(error);
        } finally {
            if (ctx != null) {
                try {
                    ctx.close();
                } catch (Throwable ignored) {
                }
            }
        }
    }

    /** Normalizes a share reference to an smb:// URL (host optional port + share). */
    public static String normalize(String value) {
        if (value == null) {
            return "";
        }
        String url = value.trim();
        if (url.isEmpty()) {
            return "";
        }
        if (!url.toLowerCase(Locale.ROOT).startsWith("smb://")) {
            url = "smb://" + url;
        }
        while (url.endsWith("/")) {
            url = url.substring(0, url.length() - 1);
        }
        return url;
    }

    /** Opens a share root, verifying credentials / reachability. */
    public void testConnection() throws IOException {
        try {
            SmbFile dir = new SmbFile(root, context());
            dir.listFiles(ACCEPT_ALL);
        } catch (Throwable error) {
            throw translation(error);
        }
    }

    /**
     * Lists the top-level shares of an smb:// host (host part may include a
     * port). Used by the login flow so the user can pick which share to mount.
     */
    public java.util.List<BrowseEntry> listShares(String hostUrl) throws IOException {
        String host = hostOf(normalize(hostUrl));
        if (host.isEmpty()) {
            throw new IOException("请填写 SMB 主机地址，例如 smb://192.168.0.10。");
        }
        try {
            return listEntries("smb://" + host + "/");
        } catch (Throwable error) {
            throw translation(error);
        }
    }

    /**
     * Lists the entries (folders first) of an smb:// directory for browsing.
     * Accepts a share root or a nested directory URL.
     */
    public java.util.List<BrowseEntry> list(String url) throws IOException {
        try {
            return listEntries(ensureTrailingSlash(normalize(url)));
        } catch (Throwable error) {
            throw translation(error);
        }
    }

    /**
     * Turns anything jcifs throws, including linkage errors, into an IOException carrying a
     * readable reason. Without this a class-loading failure inside the SMB library kills the
     * process instead of showing up as a login error.
     */
    private static IOException translation(Throwable error) {
        if (error instanceof IOException) {
            return (IOException) error;
        }
        if (error instanceof RuntimeException) {
            return new IOException(CrashLog.summarize(error), error);
        }
        return new IOException("SMB 组件无法启动：" + CrashLog.summarize(error), error);
    }

    /**
     * jcifs needs the trailing slash to treat the URL as a container, and each entry is
     * returned with the full smb:// URL so the picker can navigate without string math.
     */
    private java.util.List<BrowseEntry> listEntries(String directoryUrl) throws IOException {
        SmbFile[] entries = new SmbFile(directoryUrl, context()).listFiles(ACCEPT_ALL);
        java.util.List<BrowseEntry> result = new java.util.ArrayList<>();
        if (entries == null) {
            return result;
        }
        for (SmbFile entry : entries) {
            String rawName = entry.getName();
            if (rawName == null || rawName.isEmpty()) {
                continue;
            }
            // jcifs appends "/" to directory names; keep it in the URL, drop it from the label.
            boolean directory = entry.isDirectory() || rawName.endsWith("/");
            String name = rawName.endsWith("/") ? rawName.substring(0, rawName.length() - 1) : rawName;
            if (name.isEmpty() || isBrowseHidden(name)) {
                continue;
            }
            result.add(new BrowseEntry(name, directoryUrl + name + (directory ? "/" : ""), directory));
        }
        java.util.Collections.sort(result);
        return result;
    }

    private static final SmbFileFilter ACCEPT_ALL = file -> true;

    private static String ensureTrailingSlash(String url) {
        return url.endsWith("/") ? url : url + "/";
    }

    /** Dot-prefixed folders and administrative shares are never a useful media root. */
    private static boolean isBrowseHidden(String name) {
        if (name.startsWith(".")) {
            return true;
        }
        String lower = name.toLowerCase(Locale.ROOT);
        return lower.equals("ipc$") || lower.equals("admin$") || lower.equals("print$")
                || (lower.endsWith("$") && lower.length() <= 5);
    }

    /**
     * Recursively walks the share, emitting every video file with its share-relative path.
     * Blu-ray selection happens after the walk, so the disc folders are walked like any other.
     */
    public void walk(SmbVideoSink sink) throws IOException {
        try {
            java.util.Deque<PendingShare> pending = new java.util.ArrayDeque<>();
            java.util.Set<String> visited = new java.util.HashSet<>();
            pending.add(new PendingShare(new SmbFile(root, context()), "", 0));
            int listed = 0;
            while (!pending.isEmpty()) {
                PendingShare directory = pending.pollFirst();
                // 同一个目录只列一次（SMB 路径不区分大小写，比对前先折一下）。没有这道判断，
                // 共享里的环回链接会把同一个文件夹走出无数条记录来。
                if (!visited.add(directory.relative.toLowerCase(Locale.ROOT))) {
                    continue;
                }
                if (++listed > MAX_DIRECTORIES) {
                    throw new IOException("这个媒体源的目录超过 " + MAX_DIRECTORIES
                            + " 个，这一轮先停在这里。");
                }
                SmbFile[] entries = directory.dir.listFiles();
                if (entries == null) {
                    continue;
                }
                for (SmbFile entry : entries) {
                    // jcifs 给目录名带上结尾的「/」，不先去掉的话 BDMV/ 匹配不上任何判断，
                    // 整张光盘的分卷就会被当成普通视频文件全部入库。
                    String name = trimTrailingSlash(entry.getName());
                    if (name.isEmpty() || name.startsWith(".") || name.startsWith("_")) {
                        continue;
                    }
                    String childRelative = directory.relative.isEmpty()
                            ? name : directory.relative + "/" + name;
                    if (entry.isDirectory()) {
                        if (isSkippableCollection(name)
                                || childRelative.equalsIgnoreCase(directory.relative)
                                || directory.depth + 1 > maxDepth) {
                            continue;
                        }
                        pending.add(new PendingShare(entry, childRelative, directory.depth + 1));
                        continue;
                    }
                    if (LocalMediaNameParser.isVideoFile(name)) {
                        sink.onVideo(name, childRelative, lengthOf(entry));
                    }
                }
            }
        } catch (LocalEngine.ScanAborted stopped) {
            // 遍历是宿主发起的，这一种不是 SMB 的错，原样抛回去。
            throw stopped;
        } catch (Throwable error) {
            throw translation(error);
        }
    }

    /** 待走的共享目录（广度优先，与 WebDAV 遍历、mac 版同一走法）。 */
    private static final class PendingShare {
        final SmbFile dir;
        final String relative;
        final int depth;

        PendingShare(SmbFile dir, String relative, int depth) {
            this.dir = dir;
            this.relative = relative;
            this.depth = depth;
        }
    }

    private static String trimTrailingSlash(String rawName) {
        if (rawName == null) {
            return "";
        }
        String name = rawName;
        while (name.endsWith("/")) {
            name = name.substring(0, name.length() - 1);
        }
        return name;
    }

    /** 体积只用于挑蓝光主片，服务端不配合时当 0 处理，不能让整个扫描因此中断。 */
    private static long lengthOf(SmbFile entry) {
        try {
            return entry.length();
        } catch (Throwable error) {
            return 0;
        }
    }

    /** Rebuilds the absolute smb:// URL for a share-relative path. */
    public String absoluteUrl(String relativePath) {
        String rel = relativePath == null ? "" : relativePath.trim();
        while (rel.startsWith("/")) {
            rel = rel.substring(1);
        }
        if (rel.isEmpty()) {
            return root;
        }
        return root.endsWith("/") ? root + rel : root + "/" + rel;
    }

    /** Opens a random-access reader for an absolute smb:// file URL. */
    public SmbRandomAccessFile randomAccessFile(String absoluteUrl) throws IOException {
        try {
            SmbFile file = new SmbFile(absoluteUrl, context());
            return new SmbRandomAccessFile(file, "r");
        } catch (Throwable error) {
            throw translation(error);
        }
    }

    /** 写一小段内容（字幕 sidecar 就几十 KB 到几百 MB，整份进内存）。 */
    public void write(String absoluteUrl, byte[] body) throws IOException {
        try {
            try (java.io.OutputStream output = new SmbFile(absoluteUrl, context()).getOutputStream()) {
                output.write(body);
            }
        } catch (Throwable error) {
            throw translation(error);
        }
    }

    /** 写一个本机文件（PGS sidecar 能有几百 MB，流式写）。 */
    public void writeFile(String absoluteUrl, java.io.File file) throws IOException {
        try (java.io.OutputStream output = new SmbFile(absoluteUrl, context()).getOutputStream();
             java.io.InputStream input = new java.io.FileInputStream(file)) {
            byte[] buffer = new byte[65536];
            int read;
            while ((read = input.read(buffer)) > 0) {
                output.write(buffer, 0, read);
            }
        } catch (Throwable error) {
            throw translation(error);
        }
    }

    public boolean exists(String absoluteUrl) {
        try {
            return new SmbFile(absoluteUrl, context()).exists();
        } catch (Throwable ignored) {
            return false;
        }
    }

    public long length(String absoluteUrl) throws IOException {
        try {
            return new SmbFile(absoluteUrl, context()).length();
        } catch (Throwable error) {
            throw translation(error);
        }
    }

    public byte[] readAll(String absoluteUrl) throws IOException {
        try (java.io.InputStream input = new SmbFile(absoluteUrl, context()).getInputStream()) {
            java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream();
            byte[] buffer = new byte[32768];
            int read;
            while ((read = input.read(buffer)) > 0) {
                out.write(buffer, 0, read);
            }
            return out.toByteArray();
        } catch (Throwable error) {
            throw translation(error);
        }
    }

    /** 区间读：转档要按字节跳读容器。 */
    public byte[] readAt(String absoluteUrl, long offset, int length) throws IOException {
        try {
            SmbFile file = new SmbFile(absoluteUrl, context());
            long size = file.length();
            byte[] buffer = new byte[(int) Math.max(0, Math.min(length, size - offset))];
            if (buffer.length == 0) {
                return buffer;
            }
            try (jcifs.smb.SmbRandomAccessFile access = new jcifs.smb.SmbRandomAccessFile(file, "r")) {
                access.seek(offset);
                access.readFully(buffer);
            }
            return buffer;
        } catch (Throwable error) {
            throw translation(error);
        }
    }

    /** Opens the absolute smb:// URL so callers can stream or stat it (offline cache). */
    public SmbFile open(String absoluteUrl) throws IOException {
        try {
            return new SmbFile(absoluteUrl, context());
        } catch (Throwable error) {
            throw translation(error);
        }
    }

    /** Host[:port] portion of the stored root, e.g. "192.168.0.10" or "192.168.0.10:445". */
    public String host() {
        return hostOf(root);
    }

    private static String hostOf(String smbUrl) {
        int start = smbUrl.indexOf("://");
        if (start < 0) {
            return "";
        }
        String rest = smbUrl.substring(start + 3);
        int slash = rest.indexOf('/');
        return slash < 0 ? rest : rest.substring(0, slash);
    }

    /** 和 mac 版一样，只剪系统垃圾目录；光盘结构必须走进去才可能挑出主片。 */
    private static boolean isSkippableCollection(String name) {
        String lower = name.toLowerCase(Locale.ROOT);
        return lower.equals("lost+found") || lower.equals("lost & found")
                || lower.equals("@eadir") || lower.endsWith(".nomedia");
    }

    private CIFSContext context() throws IOException {
        CIFSContext ctx;
        try {
            ctx = new BaseContext(new PropertyConfiguration(new java.util.Properties()));
        } catch (Throwable error) {
            throw translation(error);
        }
        // jcifs NTLM auth is (domain, user, password). For Synology/Samba the
        // "domain" should be the server host (workgroup), so both plain user and
        // user qualified by the host keep working.
        String host = hostOf(root);
        ctx = ctx.withCredentials(new NtlmPasswordAuthentication(ctx, host, username, password));
        if (username.isEmpty()) {
            ctx = ctx.withAnonymousCredentials();
        }
        return ctx;
    }
}
