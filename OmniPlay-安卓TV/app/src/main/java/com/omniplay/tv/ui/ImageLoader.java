package com.omniplay.tv.ui;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.os.Handler;
import android.os.Looper;
import android.util.LruCache;
import android.widget.ImageView;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class ImageLoader {
    private static final int DEFAULT_TARGET_WIDTH = 360;
    private static final int DEFAULT_TARGET_HEIGHT = 540;
    private static final float DECODE_QUALITY_MULTIPLIER = 1.75f;
    private static final int MAX_DECODE_WIDTH = 900;
    private static final int MAX_DECODE_HEIGHT = 1350;

    private final ExecutorService executor = Executors.newFixedThreadPool(4);
    private final Handler main = new Handler(Looper.getMainLooper());
    private final File diskDirectory;
    /** 进程级共享：从详情页退回首页、从电视主页回来，已经解好的海报不该重新变回占位块。 */
    private static final LruCache<String, Bitmap> MEMORY =
            new LruCache<String, Bitmap>(memoryCacheSizeKb()) {
                @Override
                protected int sizeOf(String key, Bitmap value) {
                    return Math.max(1, value.getByteCount() / 1024);
                }
            };

    /** 缩略图只比卡片稍大一点：冷启动那一屏靠它同步解码才解得动。 */
    private static final float THUMB_SCALE = 1.3f;
    private static final int THUMB_QUALITY = 82;
    /** 一秒钟内允许在主线程上同步解多少字节：够铺满首屏，又不会把首帧拖到卡顿。 */
    private static final long SYNC_DECODE_BYTE_BUDGET = 14L * 1024 * 1024;
    private static long syncDecodeBytes;
    private static long syncDecodeWindowStart;

    public ImageLoader(Context context) {
        diskDirectory = new File(context.getApplicationContext().getCacheDir(), "image-loader");
    }

    public void load(ImageView imageView, String url, String cookieHeader) {
        int width = imageView.getWidth() > 0 ? imageView.getWidth() : layoutSize(imageView.getLayoutParams() == null ? 0 : imageView.getLayoutParams().width);
        int height = imageView.getHeight() > 0 ? imageView.getHeight() : layoutSize(imageView.getLayoutParams() == null ? 0 : imageView.getLayoutParams().height);
        load(imageView, url, cookieHeader, width, height);
    }

    public void load(ImageView imageView, String url, String cookieHeader,
                     int targetWidth, int targetHeight) {
        load(imageView, url, cookieHeader, targetWidth, targetHeight, null);
    }

    /**
     * {@code fallbackUrl} 只在正地址取不到东西时试一次，用于「海报走 CDN 失败、改走同一条
     * 反代」这类兜底：成功的图按正地址入缓存，所以每张图一生只多花一次失败请求。
     */
    public void load(ImageView imageView, String url, String cookieHeader, int targetWidth,
                     int targetHeight, String fallbackUrl) {
        load(imageView, url, cookieHeader, targetWidth, targetHeight, fallbackUrl, false);
    }

    /**
     * {@code syncFromDisk} 给首页海报墙用：磁盘上已经有海报时当场解出来贴上去，不先插一块
     * 占位白板再等异步回来——冷启动时那一闪看起来像整面墙重新刷了一遍。
     */
    public void load(ImageView imageView, String url, String cookieHeader, int targetWidth,
                     int targetHeight, String fallbackUrl, boolean syncFromDisk) {
        imageView.setAdjustViewBounds(false);
        imageView.setCropToPadding(false);
        imageView.setTag(url);
        if (url == null || url.isEmpty()) {
            imageView.setImageDrawable(null);
            imageView.setBackground(placeholder());
            return;
        }

        int decodeWidth = qualityTarget(targetWidth, DEFAULT_TARGET_WIDTH);
        int decodeHeight = qualityTarget(targetHeight, DEFAULT_TARGET_HEIGHT);
        String cacheKey = cacheKey(url, decodeWidth, decodeHeight);
        Bitmap cached = MEMORY.get(cacheKey);
        if (cached != null) {
            imageView.setBackground(null);
            imageView.setImageBitmap(cached);
            return;
        }
        if (syncFromDisk) {
            Bitmap ready = decodeThumbSync(url, decodeWidth, decodeHeight);
            if (ready != null) {
                MEMORY.put(cacheKey, ready);
                imageView.setBackground(null);
                imageView.setImageBitmap(ready);
                return;
            }
        }

        imageView.setImageDrawable(null);
        imageView.setBackground(placeholder());
        executor.execute(() -> {
            Bitmap bitmap;
            try {
                bitmap = loadFromDisk(url, decodeWidth, decodeHeight);
                if (bitmap == null) {
                    bitmap = fetch(url, cookieHeader, decodeWidth, decodeHeight);
                }
                if (bitmap == null && fallbackUrl != null && !fallbackUrl.isEmpty()) {
                    bitmap = loadFromDisk(fallbackUrl, decodeWidth, decodeHeight);
                    if (bitmap == null) {
                        bitmap = fetch(fallbackUrl, cookieHeader, decodeWidth, decodeHeight);
                    }
                }
            } catch (Throwable error) {
                // 解码一张海报时内存吃紧（扫描正把整片目录清单攥在手里）不该让整个应用退出：
                // 这一张丢掉就好，线程保住，用户看到的只是一格占位图，而不是被踢回电视主页。
                android.util.Log.w("OmniPlayPoster", "取图异常 " + url, error);
                return;
            }
            if (bitmap == null) {
                android.util.Log.d("OmniPlayPoster", "海报为空 " + url);
                return;
            }

            MEMORY.put(cacheKey, bitmap);
            final Bitmap decodedBitmap = bitmap;
            main.post(() -> {
                Object tag = imageView.getTag();
                if (url.equals(tag) && imageView.isAttachedToWindow()) {
                    imageView.setBackground(null);
                    imageView.setImageBitmap(decodedBitmap);
                }
            });
        });
    }

    /**
     * 豆瓣图床（含 {@code qnmob3-sign} 那个签名域名）要带站内 Referer 才给图，
     * TMDB 与本地/服务端地址不需要，保持不发。
     */
    static String refererFor(String url) {
        String lower = url.toLowerCase(java.util.Locale.ROOT);
        return lower.contains("doubanio.com") || lower.contains("douban.com")
                ? "https://movie.douban.com/" : null;
    }

    private Bitmap fetch(String url, String cookieHeader, int targetWidth, int targetHeight) {
        HttpURLConnection connection = null;
        File tempFile = null;
        try {
            connection = (HttpURLConnection) new URL(url).openConnection();
            connection.setConnectTimeout(8000);
            connection.setReadTimeout(20000);
            connection.setRequestProperty("User-Agent", com.omniplay.tv.local.Http.USER_AGENT);
            String referer = refererFor(url);
            if (referer != null) {
                // 豆瓣图床认 Referer：不带它回的是 418（不是 403），看上去跟「这张图没了」一样。
                connection.setRequestProperty("Referer", referer);
            }
            if (cookieHeader != null && !cookieHeader.isEmpty()) {
                connection.setRequestProperty("Cookie", cookieHeader);
            }

            File target = diskFile(url);
            if (target == null || !ensureDiskDirectory()) {
                try (InputStream input = connection.getInputStream()) {
                    return BitmapFactory.decodeStream(input);
                }
            }

            tempFile = new File(target.getParentFile(), target.getName() + "." + System.nanoTime() + ".tmp");
            try (InputStream input = connection.getInputStream()) {
                try (FileOutputStream output = new FileOutputStream(tempFile, false)) {
                    byte[] buffer = new byte[32 * 1024];
                    int read;
                    while ((read = input.read(buffer)) >= 0) {
                        output.write(buffer, 0, read);
                    }
                }
            }

            if (!tempFile.renameTo(target)) {
                copyFile(tempFile, target);
            }
            Bitmap bitmap = decodeFile(target, targetWidth, targetHeight);
            if (bitmap == null) {
                //noinspection ResultOfMethodCallIgnored
                target.delete();
            } else {
                writeThumb(thumbFile(url), bitmap);
            }
            return bitmap;
        } catch (Throwable error) {
            // OutOfMemoryError 也是从这里冒出来的：接住它，一张海报换一次告警，不换整个应用。
            android.util.Log.d("OmniPlayPoster", "取图失败 " + url + " -> " + error);
            return null;
        } finally {
            if (connection != null) {
                connection.disconnect();
            }
            if (tempFile != null && tempFile.exists()) {
                //noinspection ResultOfMethodCallIgnored
                tempFile.delete();
            }
        }
    }

    private Bitmap loadFromDisk(String url, int targetWidth, int targetHeight) {
        File file = diskFile(url);
        if (file == null || !file.isFile() || file.length() <= 0) {
            return null;
        }

        Bitmap bitmap = decodeFile(file, targetWidth, targetHeight);
        if (bitmap == null) {
            //noinspection ResultOfMethodCallIgnored
            file.delete();
        } else if (thumbFile(url) != null && !thumbFile(url).isFile()) {
            // 上一版的缓存只有整图：这一趟顺手补一张缩略图，下次冷启动就能同步贴上去。
            writeThumb(thumbFile(url), bitmap);
        }
        return bitmap;
    }

    /** 主线程上的快速命中：只读缩略图，而且每秒有预算，解完首屏就收手。 */
    private Bitmap decodeThumbSync(String url, int targetWidth, int targetHeight) {
        long now = System.currentTimeMillis();
        if (now - syncDecodeWindowStart > 1000L) {
            syncDecodeWindowStart = now;
            syncDecodeBytes = 0;
        }
        if (syncDecodeBytes >= SYNC_DECODE_BYTE_BUDGET) {
            return null;
        }
        File thumb = thumbFile(url);
        if (thumb == null || !thumb.isFile() || thumb.length() <= 0) {
            return null;
        }
        Bitmap bitmap = decodeFile(thumb, targetWidth, targetHeight);
        if (bitmap == null) {
            return null;
        }
        syncDecodeBytes += bitmap.getByteCount();
        return bitmap;
    }

    private static void writeThumb(File thumb, Bitmap source) {
        if (thumb == null) {
            return;
        }
        float ratio = THUMB_SCALE / DECODE_QUALITY_MULTIPLIER;
        Bitmap small = Bitmap.createScaledBitmap(source,
                Math.max(1, Math.round(source.getWidth() * ratio)),
                Math.max(1, Math.round(source.getHeight() * ratio)), true);
        try (FileOutputStream output = new FileOutputStream(thumb, false)) {
            small.compress(Bitmap.CompressFormat.JPEG, THUMB_QUALITY, output);
        } catch (Throwable ignored) {
            // 缩略图写不出去最多是下次冷启动多一次占位，不值得把刷前成功的海报丢掉。
        }
        if (small != source) {
            small.recycle();
        }
    }

    private File thumbFile(String url) {
        if (url == null || url.isEmpty()) {
            return null;
        }
        return new File(diskDirectory, sha256(url) + ".thumb.jpg");
    }

    private File diskFile(String url) {
        if (url == null || url.isEmpty()) {
            return null;
        }
        return new File(diskDirectory, sha256(url) + ".img");
    }

    private boolean ensureDiskDirectory() {
        return diskDirectory.isDirectory() || diskDirectory.mkdirs();
    }

    private static void copyFile(File source, File target) throws IOException {
        try (InputStream input = new java.io.FileInputStream(source);
             FileOutputStream output = new FileOutputStream(target, false)) {
            byte[] buffer = new byte[32 * 1024];
            int read;
            while ((read = input.read(buffer)) >= 0) {
                output.write(buffer, 0, read);
            }
        }
    }

    private static int memoryCacheSizeKb() {
        long maxKb = Runtime.getRuntime().maxMemory() / 1024L;
        long targetKb = Math.max(32L * 1024L, Math.min(maxKb / 5L, 128L * 1024L));
        return (int) Math.max(24L * 1024L, targetKb);
    }

    private static Bitmap decodeFile(File file, int targetWidth, int targetHeight) {
        try {
            BitmapFactory.Options bounds = new BitmapFactory.Options();
            bounds.inJustDecodeBounds = true;
            BitmapFactory.decodeFile(file.getAbsolutePath(), bounds);
            if (bounds.outWidth <= 0 || bounds.outHeight <= 0) {
                return null;
            }

            int decodeWidth = qualityTarget(targetWidth, DEFAULT_TARGET_WIDTH);
            int decodeHeight = qualityTarget(targetHeight, DEFAULT_TARGET_HEIGHT);

            BitmapFactory.Options options = new BitmapFactory.Options();
            options.inSampleSize = sampleSize(bounds.outWidth, bounds.outHeight, decodeWidth, decodeHeight);
            options.inPreferredConfig = Bitmap.Config.ARGB_8888;
            options.inDither = true;
            options.inScaled = false;
            return BitmapFactory.decodeFile(file.getAbsolutePath(), options);
        } catch (RuntimeException ignored) {
            return null;
        }
    }

    private static int sampleSize(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight) {
        int width = targetWidth > 0 ? targetWidth : qualityTarget(0, DEFAULT_TARGET_WIDTH);
        int height = targetHeight > 0 ? targetHeight : qualityTarget(0, DEFAULT_TARGET_HEIGHT);
        int sample = 1;
        while (sourceHeight / (sample * 2) >= height && sourceWidth / (sample * 2) >= width) {
            sample *= 2;
        }
        return Math.max(1, sample);
    }

    private static int qualityTarget(int value, int fallback) {
        int base = value > 0 ? value : fallback;
        int cap = fallback == DEFAULT_TARGET_WIDTH ? MAX_DECODE_WIDTH : MAX_DECODE_HEIGHT;
        return Math.max(1, Math.min(cap, Math.round(base * DECODE_QUALITY_MULTIPLIER)));
    }

    private static int layoutSize(int value) {
        return value > 0 ? value : 0;
    }

    private static String cacheKey(String url, int width, int height) {
        return url + "#" + Math.max(1, width) + "x" + Math.max(1, height);
    }

    private static String sha256(String value) {
        try {
            MessageDigest digest = MessageDigest.getInstance("SHA-256");
            byte[] bytes = digest.digest(value.getBytes(java.nio.charset.StandardCharsets.UTF_8));
            StringBuilder builder = new StringBuilder(bytes.length * 2);
            char[] hex = "0123456789abcdef".toCharArray();
            for (byte b : bytes) {
                builder.append(hex[(b >> 4) & 0x0f]);
                builder.append(hex[b & 0x0f]);
            }
            return builder.toString();
        } catch (NoSuchAlgorithmException error) {
            return Integer.toHexString(value.hashCode());
        }
    }

    /** 未刮削条目的海报占位：跟着当前主题的面板色走，深色主题下不会出现亮块。 */
    private static GradientDrawable placeholder() {
        GradientDrawable drawable = new GradientDrawable(
                GradientDrawable.Orientation.TOP_BOTTOM,
                new int[]{Theme.SURFACE_ALT, Theme.blend(Theme.SURFACE_ALT,
                        Theme.DARK ? Color.WHITE : Color.BLACK, 0.07f)});
        drawable.setCornerRadius(10);
        return drawable;
    }
}
