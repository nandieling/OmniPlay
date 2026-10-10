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
    private final LruCache<String, Bitmap> cache = new LruCache<String, Bitmap>(memoryCacheSizeKb()) {
        @Override
        protected int sizeOf(String key, Bitmap value) {
            return Math.max(1, value.getByteCount() / 1024);
        }
    };

    public ImageLoader(Context context) {
        diskDirectory = new File(context.getApplicationContext().getCacheDir(), "image-loader");
    }

    public void load(ImageView imageView, String url, String cookieHeader) {
        int width = imageView.getWidth() > 0 ? imageView.getWidth() : layoutSize(imageView.getLayoutParams() == null ? 0 : imageView.getLayoutParams().width);
        int height = imageView.getHeight() > 0 ? imageView.getHeight() : layoutSize(imageView.getLayoutParams() == null ? 0 : imageView.getLayoutParams().height);
        load(imageView, url, cookieHeader, width, height);
    }

    public void load(ImageView imageView, String url, String cookieHeader, int targetWidth, int targetHeight) {
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
        Bitmap cached = cache.get(cacheKey);
        if (cached != null) {
            imageView.setBackground(null);
            imageView.setImageBitmap(cached);
            return;
        }

        imageView.setImageDrawable(null);
        imageView.setBackground(placeholder());
        executor.execute(() -> {
            Bitmap bitmap = loadFromDisk(url, decodeWidth, decodeHeight);
            if (bitmap == null) {
                bitmap = fetch(url, cookieHeader, decodeWidth, decodeHeight);
            }
            if (bitmap == null) {
                return;
            }

            cache.put(cacheKey, bitmap);
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

    private Bitmap fetch(String url, String cookieHeader, int targetWidth, int targetHeight) {
        HttpURLConnection connection = null;
        File tempFile = null;
        try {
            connection = (HttpURLConnection) new URL(url).openConnection();
            connection.setConnectTimeout(8000);
            connection.setReadTimeout(20000);
            connection.setRequestProperty("User-Agent", com.omniplay.tv.local.Http.USER_AGENT);
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
            }
            return bitmap;
        } catch (Exception ignored) {
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
        }
        return bitmap;
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
