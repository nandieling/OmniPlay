package com.omniplay.tv.local;

import android.content.Context;
import android.content.SharedPreferences;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.OutputStreamWriter;
import java.io.PrintWriter;
import java.io.Writer;
import java.nio.charset.StandardCharsets;

/**
 * Remembers uncaught throwables so a TV that dies silently still has something to show.
 * <p>
 * A TV box is usually not attached to adb, so an exception that escapes a worker thread just
 * closes the app and the user has no idea what happened. Every throwable we catch on a worker
 * thread, plus anything the default handler catches, is written here; the setup screen reads it
 * once and clears it, so the next launch states the reason instead of restarting blank.
 */
public final class CrashLog {
    private static final String KEY = "last_crash";
    private static final String FILE_NAME = "omniplay-tv-crash.txt";
    private static final int MAX_REPORT_CHARS = 4000;

    private CrashLog() {
    }

    /** Chains a handler that records the failure and still lets the platform restart the app. */
    public static void install(final Context context) {
        final Thread.UncaughtExceptionHandler previous =
                Thread.getDefaultUncaughtExceptionHandler();
        Thread.setDefaultUncaughtExceptionHandler((thread, error) -> {
            try {
                record(context, "线程 " + (thread == null ? "?" : thread.getName()), error);
            } catch (Throwable ignored) {
            }
            if (previous != null) {
                previous.uncaughtException(thread, error);
            }
        });
    }

    /** Stores {@code error}; never throws, because callers are usually already on fire. */
    public static void record(Context context, String where, Throwable error) {
        if (context == null || error == null) {
            return;
        }
        String report = format(where, error);
        try {
            SharedPreferences preferences = context.getApplicationContext()
                    .getSharedPreferences(TvSettings.PREFS, Context.MODE_PRIVATE);
            String existing = preferences.getString(KEY, "");
            // Keep only the newest failure: it is the one that stopped the app.
            preferences.edit().putString(KEY, report).apply();
            if (existing == null || !existing.equals(report)) {
                writeToFile(context, report);
            }
        } catch (Throwable ignored) {
        }
    }

    /** Returns the stored report and clears it, so a crash is shown exactly once. */
    public static String take(Context context) {
        if (context == null) {
            return "";
        }
        try {
            SharedPreferences preferences = context.getApplicationContext()
                    .getSharedPreferences(TvSettings.PREFS, Context.MODE_PRIVATE);
            String report = preferences.getString(KEY, "");
            if (report != null && !report.isEmpty()) {
                preferences.edit().remove(KEY).apply();
            }
            return report == null ? "" : report;
        } catch (Throwable ignored) {
        }
        return "";
    }

    /** Short single-paragraph form for a status line: class, message, root cause. */
    public static String summarize(Throwable error) {
        if (error == null) {
            return "未知错误";
        }
        Throwable root = rootCause(error);
        String message = root.getMessage();
        String label = root.getClass().getName();
        int packageDot = label.lastIndexOf('.');
        if (packageDot > 0 && (label.startsWith("java.") || label.startsWith("javax."))) {
            label = label.substring(packageDot + 1);
        }
        StringBuilder builder = new StringBuilder(label);
        if (message != null && !message.isEmpty()) {
            builder.append("：").append(firstLine(message));
        }
        // A message-less NPE is useless on screen: name the place that threw.
        StackTraceElement[] frames = root.getStackTrace();
        if (frames.length > 0) {
            builder.append("（").append(frames[0].getMethodName()).append(':')
                    .append(frames[0].getLineNumber()).append("）");
        }
        if (root != error) {
            builder.append("（来自 ").append(error.getClass().getSimpleName()).append("）");
        }
        return builder.toString();
    }

    private static String format(String where, Throwable error) {
        StringBuilder builder = new StringBuilder();
        builder.append(where == null || where.isEmpty() ? "未捕获异常" : where).append('\n');
        builder.append(summarize(error)).append('\n');
        Throwable root = rootCause(error);
        StackTraceElement[] frames = root.getStackTrace();
        int limit = Math.min(frames.length, 12);
        for (int index = 0; index < limit; index++) {
            builder.append("    at ").append(frames[index]).append('\n');
        }
        if (frames.length > limit) {
            builder.append("    ... ").append(frames.length - limit).append(" more\n");
        }
        if (builder.length() > MAX_REPORT_CHARS) {
            return builder.substring(0, MAX_REPORT_CHARS);
        }
        return builder.toString();
    }

    private static void writeToFile(Context context, String report) {
        File directory = context.getFilesDir();
        if (directory == null) {
            return;
        }
        try (Writer writer = new OutputStreamWriter(
                new FileOutputStream(new File(directory, FILE_NAME)), StandardCharsets.UTF_8)) {
            writer.write(report);
        } catch (IOException ignored) {
        }
    }

    private static Throwable rootCause(Throwable error) {
        Throwable current = error;
        int guard = 0;
        while (current.getCause() != null && current.getCause() != current && guard++ < 10) {
            current = current.getCause();
        }
        return current;
    }

    private static String firstLine(String value) {
        int newline = value.indexOf('\n');
        String line = newline > 0 ? value.substring(0, newline) : value;
        return line.length() > 260 ? line.substring(0, 260) + "..." : line;
    }
}
