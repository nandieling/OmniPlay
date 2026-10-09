package com.omniplay.tv.local;

import android.os.Handler;
import android.os.Looper;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.InterfaceAddress;
import java.net.NetworkInterface;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.URLDecoder;
import java.nio.charset.StandardCharsets;
import java.util.Enumeration;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.ThreadFactory;

/**
 * LAN facing micro HTTP server that lets a phone browser type on the TV's behalf: an
 * overlay shows a QR code, the phone opens the form and POSTs the values back into the
 * fields the overlay registered. Same in-process server pattern as {@link SmbStreamServer}.
 *
 * <p>The page is only reachable from a private network address and the path carries a
 * per-install token, so a random device on the LAN cannot push text into the app.</p>
 */
public final class PhoneInputServer {
    private static final int MAX_BODY_BYTES = 64 * 1024;
    private static final int MAX_HEADER_BYTES = 32 * 1024;
    private static final String NOTE_KEY = "_note";
    private static PhoneInputServer instance;

    /** Applies one submitted value to the widget it belongs to. Runs on the main thread. */
    public interface Sink {
        void accept(String value);
    }

    /** One input row on the phone page. */
    public static final class Field {
        public final String key;
        public final String label;
        public final String initial;
        public final boolean secret;
        public final Sink sink;

        public Field(String key, String label, String initial, boolean secret, Sink sink) {
            this.key = key;
            this.label = label;
            this.initial = initial == null ? "" : initial;
            this.secret = secret;
            this.sink = sink;
        }
    }

    /** The form currently offered to phones. */
    public static final class Session {
        public final String title;
        public final String subtitle;
        public final List<Field> fields;
        /** Adds a free text box; used to feed a single long field (API keys, paths). */
        public final boolean allowNote;

        public Session(String title, String subtitle, List<Field> fields, boolean allowNote) {
            this.title = title;
            this.subtitle = subtitle;
            this.fields = fields;
            this.allowNote = allowNote;
        }
    }

    /** Reports what a phone just submitted. Runs on the main thread. */
    public interface Listener {
        void onSubmitted(int appliedCount, String phoneAddress);
    }

    private final Handler main = new Handler(Looper.getMainLooper());
    private final TvSettings settings;

    private ExecutorService executor;
    private ServerSocket serverSocket;
    private volatile Session session;
    private volatile Listener listener;

    private PhoneInputServer(TvSettings settings) {
        this.settings = settings;
    }

    public static synchronized PhoneInputServer shared(TvSettings settings) {
        if (instance == null) {
            instance = new PhoneInputServer(settings);
        }
        return instance;
    }

    /** Token that guards the page; stable across launches so a saved bookmark keeps working. */
    public String token() {
        return settings.phoneInputToken();
    }

    /** Full URL for the current session, or null when there is no LAN address yet. */
    public String pageUrl() {
        ServerSocket socket = serverSocket;
        String host = localIpv4Address();
        if (socket == null || socket.isClosed() || host == null) {
            return null;
        }
        return "http://" + host + ":" + socket.getLocalPort() + "/p/" + token();
    }

    public synchronized void start() {
        if (serverSocket != null && !serverSocket.isClosed()) {
            return;
        }
        try {
            serverSocket = new ServerSocket(0, 8);
        } catch (IOException error) {
            serverSocket = null;
            return;
        }
        executor = Executors.newCachedThreadPool(new ThreadFactory() {
            private int index;

            @Override
            public synchronized Thread newThread(Runnable runnable) {
                Thread thread = new Thread(runnable, "OmniPlay-Phone-Input-" + (++index));
                thread.setDaemon(true);
                return thread;
            }
        });
        Thread thread = new Thread(this::acceptLoop, "OmniPlay-Phone-Input");
        thread.setDaemon(true);
        thread.start();
    }

    public synchronized void stop() {
        ServerSocket socket = serverSocket;
        serverSocket = null;
        ExecutorService pool = executor;
        executor = null;
        if (pool != null) {
            pool.shutdownNow();
        }
        if (socket != null) {
            try {
                socket.close();
            } catch (IOException ignored) {
            }
        }
    }

    /** Passing a null session keeps the server up but answers "TV 端已离开该页面". */
    public void setSession(Session session, Listener listener) {
        this.session = session;
        this.listener = listener;
    }

    public void clearSession() {
        setSession(null, null);
    }

    public boolean isRunning() {
        ServerSocket socket = serverSocket;
        return socket != null && !socket.isClosed();
    }

    private void acceptLoop() {
        ServerSocket socket = serverSocket;
        while (socket != null && !socket.isClosed()) {
            try {
                Socket client = socket.accept();
                ExecutorService pool = executor;
                if (pool == null) {
                    return;
                }
                pool.execute(() -> handle(client));
            } catch (IOException ignored) {
                return;
            }
        }
    }

    private void handle(Socket socket) {
        try (Socket closeable = socket;
             InputStream input = closeable.getInputStream();
             OutputStream output = closeable.getOutputStream()) {
            Request request = readRequest(input);
            if (request == null) {
                return;
            }
            if (closeable.getInetAddress() != null && !isLocalAddress(closeable.getInetAddress())) {
                sendNotice(output, 403, "只有同一局域网内的设备可以操作本页面。");
                return;
            }
            if (request.path.equals("/favicon.ico")) {
                output.write("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"
                        .getBytes(StandardCharsets.ISO_8859_1));
                output.flush();
                return;
            }
            String[] parts = request.path.split("/");
            if (parts.length != 3 || !"p".equals(parts[1]) || !token().equals(parts[2])) {
                sendNotice(output, 404, "地址不对，请在电视上重新扫码或重新输入页面地址。");
                return;
            }
            if ("GET".equals(request.method) || "HEAD".equals(request.method)) {
                Session current = session;
                if (current == null) {
                    sendNotice(output, 200, "电视端已经离开这个输入页，请在电视上重新打开「手机输入」。");
                    return;
                }
                sendHtml(output, 200, form(current));
                return;
            }
            if (!"POST".equals(request.method)) {
                sendNotice(output, 405, "只支持 GET 与 POST。");
                return;
            }
            String body = request.body == null ? "" : request.body;
            Map<String, String> values = parseForm(body);
            int applied = apply(values);
            main.post(() -> {
                Listener current = listener;
                if (current != null) {
                    current.onSubmitted(applied, socketAddress(socket));
                }
            });
            sendHtml(output, 200, "<div class=\"ok\"><h1>已发送到电视</h1><p class=\"sub\">电视已填入 "
                    + applied + " 项，可以回到电视继续操作。</p><p><a href=\".\">继续填写</a></p></div>");
        } catch (Exception ignored) {
        }
    }

    private int apply(Map<String, String> values) {
        Session current = session;
        if (current == null) {
            return 0;
        }
        int applied = 0;
        for (Field field : current.fields) {
            String value = values.get(field.key);
            if (value == null || value.isEmpty() || field.sink == null) {
                continue;
            }
            applied++;
            field.sink.accept(value);
        }
        String note = values.get(NOTE_KEY);
        if (applied == 0 && note != null && !note.isEmpty() && current.fields.size() == 1
                && current.fields.get(0).sink != null) {
            current.fields.get(0).sink.accept(note);
            applied = 1;
        }
        return applied;
    }

    // ---- page rendering ----

    private static String form(Session session) {
        StringBuilder builder = new StringBuilder(1024);
        builder.append(head(session.title));
        builder.append("<div class=\"brand\"><span class=\"dot\"></span>OmniPlay 手机输入</div>");
        builder.append("<h1>").append(escape(session.title)).append("</h1>");
        if (session.subtitle != null && !session.subtitle.isEmpty()) {
            builder.append("<p class=\"sub\">").append(escape(session.subtitle)).append("</p>");
        }
        builder.append("<form method=\"post\" accept-charset=\"utf-8\" autocomplete=\"off\">");
        for (Field field : session.fields) {
            builder.append("<label><span>").append(escape(field.label)).append("</span>");
            builder.append("<input name=\"").append(escape(field.key)).append("\" value=\"")
                    .append(escape(field.initial)).append("\" type=\"")
                    .append(field.secret ? "password" : "text")
                    .append("\" autocomplete=\"off\" autocapitalize=\"none\" autocorrect=\"off\"")
                    .append(" spellcheck=\"false\" enterkeyhint=\"next\">");
            builder.append("</label>");
        }
        if (session.allowNote) {
            builder.append("<label><span>补充内容（整段发送到电视）</span>")
                    .append("<textarea name=\"").append(NOTE_KEY)
                    .append("\" rows=\"4\" autocapitalize=\"none\" autocorrect=\"off\"")
                    .append(" spellcheck=\"false\" placeholder=\"长串密钥、路径可以直接粘贴在这里\"></textarea></label>");
        }
        builder.append("<button type=\"submit\">发送到电视</button></form>");
        builder.append("<p class=\"hint\">手机与电视需在同一个局域网。提交后电视上的输入框会立即更新。</p>");
        builder.append("</div></body></html>");
        return builder.toString();
    }

    private static String head(String title) {
        return "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">"
                + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1, viewport-fit=cover\">"
                + "<title>" + escape(title) + " · OmniPlay</title><style>"
                + "*{box-sizing:border-box}"
                + "body{margin:0;padding:22px 16px 44px;background:#f5f7fa;color:#111827;"
                + "font:16px/1.5 -apple-system,BlinkMacSystemFont,'PingFang SC','Microsoft YaHei',sans-serif;"
                + "-webkit-text-size-adjust:100%}"
                + ".wrap{max-width:520px;margin:0 auto}"
                + ".brand{display:flex;align-items:center;gap:8px;color:#0f766e;font-size:13px;font-weight:600;margin-bottom:12px}"
                + ".dot{width:9px;height:9px;border-radius:50%;background:#14b8a6}"
                + "h1{font-size:20px;margin:0 0 6px}"
                + "p.sub{margin:0 0 18px;color:#6b7280;font-size:14px}"
                + "label{display:block;margin:0 0 14px}"
                + "label span{display:block;font-size:13px;font-weight:600;color:#4b5563;margin-bottom:6px}"
                + "input,textarea{width:100%;padding:14px 12px;font-size:16px;color:#111827;background:#fff;"
                + "border:1px solid #dde2eb;border-radius:8px;outline:none;font-family:inherit}"
                + "input:focus,textarea:focus{border-color:#2563eb;box-shadow:0 0 0 3px rgba(37,99,235,.16)}"
                + "textarea{resize:vertical;min-height:96px}"
                + "button{width:100%;margin-top:4px;padding:15px;font-size:17px;font-weight:600;color:#fff;"
                + "background:#14b8a6;border:0;border-radius:8px}"
                + "button:active{background:#0f9488}"
                + "a{color:#2563eb}"
                + ".ok{background:#fff;border:1px solid #dde2eb;border-radius:10px;padding:22px}"
                + ".hint{font-size:12px;color:#9ca3af;margin-top:16px}"
                + "</style></head><body><div class=\"wrap\">";
    }

    private static String escape(String value) {
        if (value == null) {
            return "";
        }
        return value.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
                .replace("\"", "&quot;").replace("'", "&#39;");
    }

    private static void sendHtml(OutputStream output, int status, String html) throws IOException {
        byte[] payload = html.getBytes(StandardCharsets.UTF_8);
        StringBuilder header = new StringBuilder();
        header.append("HTTP/1.1 ").append(status).append(status == 200 ? " OK" : " Not Found").append("\r\n")
                .append("Content-Type: text/html; charset=utf-8\r\n")
                .append("Content-Length: ").append(payload.length).append("\r\n")
                .append("Cache-Control: no-store\r\n")
                .append("Connection: close\r\n\r\n");
        output.write(header.toString().getBytes(StandardCharsets.ISO_8859_1));
        output.write(payload);
        output.flush();
    }

    /** Plain status page, e.g. wrong token or a non-LAN client. */
    private static void sendNotice(OutputStream output, int status, String message) throws IOException {
        sendHtml(output, status, head("OmniPlay 手机输入") + "<h1>OmniPlay 手机输入</h1><p class=\"sub\">"
                + escape(message) + "</p></div></body></html>");
    }

    // ---- request plumbing ----

    private static final class Request {
        final String method;
        final String path;
        final String body;

        Request(String method, String path, String body) {
            this.method = method;
            this.path = path;
            this.body = body;
        }
    }

    private static Request readRequest(InputStream input) throws IOException {
        ByteArrayOutputStream head = new ByteArrayOutputStream();
        int previous3 = -1, previous2 = -1, previous1 = -1, value;
        while ((value = input.read()) >= 0 && head.size() < MAX_HEADER_BYTES) {
            head.write(value);
            if (previous3 == '\r' && previous2 == '\n' && previous1 == '\r' && value == '\n') {
                break;
            }
            previous3 = previous2;
            previous2 = previous1;
            previous1 = value;
        }
        if (head.size() == 0) {
            return null;
        }
        String[] lines = head.toString(StandardCharsets.ISO_8859_1.name()).split("\\r?\\n");
        String[] requestLine = lines[0].split(" ");
        if (requestLine.length < 2) {
            return null;
        }
        String method = requestLine[0].toUpperCase(Locale.ROOT);
        String path = requestLine[1];
        int query = path.indexOf('?');
        path = query >= 0 ? path.substring(0, query) : path;
        int contentLength = 0;
        for (int index = 1; index < lines.length; index++) {
            int colon = lines[index].indexOf(':');
            if (colon <= 0) {
                continue;
            }
            String name = lines[index].substring(0, colon).trim().toLowerCase(Locale.ROOT);
            if (name.equals("content-length")) {
                try {
                    contentLength = Integer.parseInt(lines[index].substring(colon + 1).trim());
                } catch (NumberFormatException ignored) {
                    contentLength = 0;
                }
            }
        }
        String body = null;
        if (contentLength > 0) {
            int wanted = Math.min(contentLength, MAX_BODY_BYTES);
            byte[] buffer = new byte[wanted];
            int read = 0;
            while (read < wanted) {
                int step = input.read(buffer, read, wanted - read);
                if (step <= 0) {
                    break;
                }
                read += step;
            }
            body = new String(buffer, 0, read, StandardCharsets.UTF_8);
        }
        return new Request(method, path, body);
    }

    private static Map<String, String> parseForm(String body) {
        Map<String, String> values = new LinkedHashMap<>();
        for (String pair : body.split("&")) {
            if (pair.isEmpty()) {
                continue;
            }
            int equals = pair.indexOf('=');
            String name = equals < 0 ? pair : pair.substring(0, equals);
            String raw = equals < 0 ? "" : pair.substring(equals + 1);
            try {
                values.put(URLDecoder.decode(name, "UTF-8"), URLDecoder.decode(raw, "UTF-8"));
            } catch (Exception ignored) {
            }
        }
        return values;
    }

    private static String socketAddress(Socket socket) {
        InetAddress address = socket.getInetAddress();
        return address == null ? "" : address.getHostAddress();
    }

    private static boolean isLocalAddress(InetAddress address) {
        return address.isSiteLocalAddress() || address.isLoopbackAddress();
    }

    /** First private IPv4 of this device, or null while the box has no LAN address. */
    private static String localIpv4Address() {
        try {
            Enumeration<NetworkInterface> interfaces = NetworkInterface.getNetworkInterfaces();
            while (interfaces != null && interfaces.hasMoreElements()) {
                NetworkInterface networkInterface = interfaces.nextElement();
                if (!networkInterface.isUp() || networkInterface.isLoopback() || networkInterface.isVirtual()) {
                    continue;
                }
                List<InterfaceAddress> addresses;
                try {
                    addresses = networkInterface.getInterfaceAddresses();
                } catch (Exception error) {
                    continue;
                }
                for (InterfaceAddress address : addresses) {
                    InetAddress inet = address.getAddress();
                    if (inet instanceof Inet4Address && isLocalAddress(inet)) {
                        return inet.getHostAddress();
                    }
                }
            }
        } catch (Exception ignored) {
        }
        return null;
    }
}
