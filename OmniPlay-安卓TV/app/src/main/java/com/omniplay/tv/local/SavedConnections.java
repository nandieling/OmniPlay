package com.omniplay.tv.local;

import android.content.Context;
import android.content.SharedPreferences;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;

/**
 * Connections that logged in successfully, kept per server so the setup screen can offer
 * "connect" instead of typing a username and password again. Stored in the same prefs file
 * as the other device-local settings; passwords are plain text, exactly like saved sources.
 */
public final class SavedConnections {
    public static final String KEY = "saved_connections";
    private static final int MAX_ENTRIES = 12;
    private static final int MAX_PATHS = 4;

    /** One server, its credentials, and the directories that were mounted from it. */
    public static final class Entry {
        public final String kind;
        public final String hostKey;
        public final String label;
        public final String username;
        public final String password;
        public final List<String> paths;
        public final long updatedAt;

        Entry(String kind, String hostKey, String label, String username,
              String password, List<String> paths, long updatedAt) {
            this.kind = kind;
            this.hostKey = hostKey;
            this.label = label == null ? "" : label;
            this.username = username == null ? "" : username;
            this.password = password == null ? "" : password;
            this.paths = paths == null ? new ArrayList<>() : paths;
            this.updatedAt = updatedAt;
        }

        public String primaryPath() {
            return paths.isEmpty() ? "" : paths.get(0);
        }

        public String title() {
            String kindLabel = "smb".equals(kind) ? "SMB" : "WebDAV";
            String host = hostKey.indexOf("://") >= 0 ? hostKey.substring(hostKey.indexOf("://") + 3) : hostKey;
            return kindLabel + " · " + (label.isEmpty() ? host : label);
        }

        public String detail() {
            // WebDAV 的目录是编码过的 URL，显示出来是一串 %E5%BD%B1%E8%A7%86，读不出目录名。
            return primaryPath().isEmpty()
                    ? "只保存了账号，点选后选择目录" : UrlLabels.displayPath(primaryPath());
        }
    }

    private final SharedPreferences preferences;

    public SavedConnections(Context context) {
        preferences = context.getApplicationContext()
                .getSharedPreferences(TvSettings.PREFS, Context.MODE_PRIVATE);
    }

    /** Newest first. */
    public List<Entry> all() {
        List<Entry> entries = read();
        entries.sort((left, right) -> Long.compare(right.updatedAt, left.updatedAt));
        return entries;
    }

    public Entry find(String kind, String url) {
        String key = hostKey(kind, url);
        for (Entry entry : read()) {
            if (entry.kind.equals(kind) && entry.hostKey.equalsIgnoreCase(key)) {
                return entry;
            }
        }
        return null;
    }

    /** Remembers credentials after a successful login; keeps any known directory. */
    public void recordLogin(String kind, String url, String label, String username, String password) {
        record(kind, url, label, username, password, null);
    }

    /** Remembers the directory the user actually mounted, so next time is one key press. */
    public void recordMount(String kind, String mountedPath, String label, String username, String password) {
        record(kind, mountedPath, label, username, password, mountedPath);
    }

    public void remove(String kind, String url) {
        String key = hostKey(kind, url);
        List<Entry> entries = read();
        entries.removeIf(entry -> entry.kind.equals(kind) && entry.hostKey.equalsIgnoreCase(key));
        write(entries);
    }

    public void clear() {
        preferences.edit().remove(KEY).apply();
    }

    private synchronized void record(String kind, String url, String label,
                                     String username, String password, String mountedPath) {
        String key = hostKey(kind, url);
        if (key.isEmpty()) {
            return;
        }
        List<Entry> entries = read();
        List<String> paths = new ArrayList<>();
        String keptLabel = label == null ? "" : label;
        for (int index = 0; index < entries.size(); index++) {
            Entry existing = entries.get(index);
            if (existing.kind.equals(kind) && existing.hostKey.equalsIgnoreCase(key)) {
                paths.addAll(existing.paths);
                if (keptLabel.isEmpty()) {
                    keptLabel = existing.label;
                }
                entries.remove(index);
                break;
            }
        }
        if (mountedPath != null && !mountedPath.isEmpty()) {
            paths.remove(mountedPath);
            paths.add(0, mountedPath);
            while (paths.size() > MAX_PATHS) {
                paths.remove(paths.size() - 1);
            }
        }
        entries.add(new Entry(kind, key, keptLabel, username, password, paths, System.currentTimeMillis()));
        while (entries.size() > MAX_ENTRIES) {
            Entry oldest = null;
            for (Entry candidate : entries) {
                if (oldest == null || candidate.updatedAt < oldest.updatedAt) {
                    oldest = candidate;
                }
            }
            if (oldest == null) {
                break;
            }
            entries.remove(oldest);
        }
        write(entries);
    }

    /** {@code scheme://host[:port]} without the path, so one row covers a whole server. */
    public static String hostKey(String kind, String url) {
        String value = url == null ? "" : url.trim();
        if (value.isEmpty()) {
            return "";
        }
        if ("smb".equals(kind)) {
            String normalized = SmbClient.normalize(value);
            int schemeEnd = normalized.indexOf("://");
            if (schemeEnd < 0) {
                return normalized;
            }
            int pathStart = normalized.indexOf('/', schemeEnd + 3);
            return pathStart < 0 ? normalized : normalized.substring(0, pathStart);
        }
        try {
            java.net.URL parsed = new java.net.URL(value.contains("://") ? value : "https://" + value);
            int port = parsed.getPort() >= 0 ? parsed.getPort() : parsed.getDefaultPort();
            return parsed.getProtocol() + "://" + parsed.getHost() + (port > 0 ? ":" + port : "");
        } catch (Exception error) {
            int schemeEnd = value.indexOf("://");
            int pathStart = schemeEnd < 0 ? -1 : value.indexOf('/', schemeEnd + 3);
            return pathStart < 0 ? value : value.substring(0, pathStart);
        }
    }

    private List<Entry> read() {
        List<Entry> entries = new ArrayList<>();
        String stored = preferences.getString(KEY, "");
        if (stored == null || stored.isEmpty()) {
            return entries;
        }
        try {
            JSONArray array = new JSONArray(stored);
            for (int index = 0; index < array.length(); index++) {
                JSONObject object = array.optJSONObject(index);
                if (object == null) {
                    continue;
                }
                List<String> paths = new ArrayList<>();
                JSONArray storedPaths = object.optJSONArray("paths");
                if (storedPaths != null) {
                    for (int pathIndex = 0; pathIndex < storedPaths.length(); pathIndex++) {
                        String path = storedPaths.optString(pathIndex, "");
                        if (!path.isEmpty()) {
                            paths.add(path);
                        }
                    }
                }
                entries.add(new Entry(object.optString("kind", "webdav"),
                        object.optString("hostKey", ""),
                        object.optString("label", ""),
                        object.optString("username", ""),
                        object.optString("password", ""),
                        paths,
                        object.optLong("updatedAt", 0)));
            }
        } catch (Exception error) {
            preferences.edit().remove(KEY).apply();
        }
        return entries;
    }

    private void write(List<Entry> entries) {
        JSONArray array = new JSONArray();
        for (Entry entry : entries) {
            try {
                JSONObject object = new JSONObject();
                object.put("kind", entry.kind);
                object.put("hostKey", entry.hostKey);
                object.put("label", entry.label);
                object.put("username", entry.username);
                object.put("password", entry.password);
                object.put("paths", new JSONArray(entry.paths));
                object.put("updatedAt", entry.updatedAt);
                array.put(object);
            } catch (Exception ignored) {
            }
        }
        preferences.edit().putString(KEY, array.toString()).apply();
    }
}
