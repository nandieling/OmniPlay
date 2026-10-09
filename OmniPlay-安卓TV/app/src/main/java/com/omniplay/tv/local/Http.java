package com.omniplay.tv.local;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.security.cert.X509Certificate;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Locale;
import java.util.Map;

import javax.net.ssl.SNIHostName;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLParameters;
import javax.net.ssl.SSLSocket;
import javax.net.ssl.SSLSocketFactory;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

/**
 * Small HTTP/1.1 client for the WebDAV paths, written over plain sockets.
 * <p>
 * {@code HttpURLConnection} is not usable here: several Android builds reject {@code PROPFIND}
 * with {@code ProtocolException: Expected one of [OPTIONS, GET, HEAD, ...]}, and their stock trust
 * store refuses the self-signed certificates NAS boxes ship with. One request per connection, no
 * redirects, no proxies; the response body is always read into memory because PROPFIND replies are
 * directory listings, not media streams.
 */
public final class Http {
    /** One finished response: status line, headers with lower-case names, and the body bytes. */
    public static final class Response {
        public final int status;
        public final Map<String, String> headers;
        public final byte[] body;

        Response(int status, Map<String, String> headers, byte[] body) {
            this.status = status;
            this.headers = headers;
            this.body = body;
        }

        public String header(String name) {
            return headers.get(name.toLowerCase(Locale.ROOT));
        }

        public String bodyText() {
            return new String(body, StandardCharsets.UTF_8);
        }
    }

    private Http() {
    }

    /**
     * Sends {@code method} (any token, including PROPFIND) to {@code url}.
     *
     * @param headers   extra request headers, may be null
     * @param body      request body, may be null
     * @param timeoutMs used for both connecting and each read
     * @throws IOException when the socket fails or the reply cannot be parsed
     */
    public static Response request(String url, String method,
                                   Map<String, String> headers, byte[] body,
                                   int timeoutMs) throws IOException {
        Target target = Target.parse(url);
        Socket socket = target.tls ? secureFactory().createSocket() : new Socket();
        try {
            socket.connect(new InetSocketAddress(target.host, target.port), timeoutMs);
            socket.setSoTimeout(timeoutMs);
            if (socket instanceof SSLSocket) {
                applyServerName((SSLSocket) socket, target.host);
                ((SSLSocket) socket).startHandshake();
            }
            BufferedStreams streams = new BufferedStreams(socket);
            IOException earlyResponse = null;
            try {
                writeRequest(streams.output, target, method, headers, body);
            } catch (IOException refused) {
                // A server that answers 401 before reading the body still has a status worth
                // reporting, so keep reading and only fail if the reply is unreadable too.
                earlyResponse = refused;
            }
            try {
                return readResponse(streams.input);
            } catch (IOException unreadable) {
                if (earlyResponse != null) {
                    unreadable.addSuppressed(earlyResponse);
                }
                throw unreadable;
            }
        } finally {
            try {
                socket.close();
            } catch (IOException ignored) {
            }
        }
    }

    /** One buffered stream pair per connection: headers are read a byte at a time. */
    private static final class BufferedStreams {
        final java.io.BufferedInputStream input;
        final java.io.BufferedOutputStream output;

        BufferedStreams(Socket socket) throws IOException {
            input = new java.io.BufferedInputStream(socket.getInputStream(), 16384);
            output = new java.io.BufferedOutputStream(socket.getOutputStream(), 4096);
        }
    }

    private static void writeRequest(OutputStream output, Target target, String method,
                                     Map<String, String> headers, byte[] body) throws IOException {
        StringBuilder request = new StringBuilder(256);
        request.append(method).append(' ').append(target.requestTarget).append(" HTTP/1.1\r\n");
        request.append("Host: ").append(target.authority).append("\r\n");
        request.append("User-Agent: ").append(USER_AGENT).append("\r\n");
        request.append("Connection: close\r\n");
        if (headers != null) {
            for (Map.Entry<String, String> header : headers.entrySet()) {
                if (header.getKey() != null && header.getValue() != null) {
                    request.append(header.getKey()).append(": ").append(header.getValue()).append("\r\n");
                }
            }
        }
        if (body != null) {
            request.append("Content-Length: ").append(body.length).append("\r\n");
        }
        request.append("\r\n");
        output.write(request.toString().getBytes(StandardCharsets.US_ASCII));
        if (body != null && body.length > 0) {
            output.write(body);
        }
        output.flush();
    }

    private static Response readResponse(InputStream input) throws IOException {
        int status = -1;
        Map<String, String> headers = new LinkedHashMap<>();
        while (true) {
            String statusLine = readLine(input);
            if (statusLine == null || !statusLine.regionMatches(true, 0, "HTTP/", 0, 5)) {
                throw new IOException("服务器返回了无法解析的响应。");
            }
            String[] parts = statusLine.split(" ", 3);
            try {
                status = Integer.parseInt(parts[1]);
            } catch (Exception error) {
                throw new IOException("服务器返回了无法解析的状态行：" + statusLine);
            }
            headers.clear();
            String line;
            while ((line = readLine(input)) != null && !line.isEmpty()) {
                int colon = line.indexOf(':');
                if (colon > 0) {
                    headers.put(line.substring(0, colon).toLowerCase(Locale.ROOT),
                            line.substring(colon + 1).trim());
                }
            }
            // 1xx interim replies are not the answer; keep reading until the real one.
            if (status >= 200 || status < 100) {
                break;
            }
        }
        byte[] body = status == 204 || status == 304
                ? new byte[0] : readBody(input, headers.get("transfer-encoding"), headers.get("content-length"));
        return new Response(status, headers, body);
    }

    public static final String USER_AGENT = "OmniPlay-Android/1.17";

    private static byte[] readBody(InputStream input, String transferEncoding, String contentLength)
            throws IOException {
        if ("chunked".equalsIgnoreCase(contentLength == null && transferEncoding != null
                ? transferEncoding : null)) {
            ByteArrayOutputStream chunked = new ByteArrayOutputStream();
            while (true) {
                String sizeLine = readLine(input);
                if (sizeLine == null) {
                    break;
                }
                int semicolon = sizeLine.indexOf(';');
                if (semicolon >= 0) {
                    sizeLine = sizeLine.substring(0, semicolon);
                }
                int size;
                try {
                    size = Integer.parseInt(sizeLine.trim(), 16);
                } catch (NumberFormatException error) {
                    throw new IOException("分块响应格式不正确。");
                }
                if (size <= 0) {
                    break;
                }
                copyExact(input, chunked, size);
                readLine(input); // the CRLF that closes every chunk
            }
            return chunked.toByteArray();
        }
        if (contentLength != null) {
            int size;
            try {
                size = Integer.parseInt(contentLength.trim());
            } catch (NumberFormatException error) {
                size = -1;
            }
            if (size >= 0) {
                ByteArrayOutputStream sized = new ByteArrayOutputStream(Math.min(size, 1 << 16));
                copyExact(input, sized, size);
                return sized.toByteArray();
            }
        }
        ByteArrayOutputStream rest = new ByteArrayOutputStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = input.read(buffer)) > 0) {
            rest.write(buffer, 0, read);
        }
        return rest.toByteArray();
    }

    private static void copyExact(InputStream input, ByteArrayOutputStream sink, int size)
            throws IOException {
        byte[] buffer = new byte[Math.min(size, 8192)];
        int remaining = size;
        while (remaining > 0) {
            int read = input.read(buffer, 0, Math.min(buffer.length, remaining));
            if (read < 0) {
                throw new IOException("响应在内容读完之前就中断了。");
            }
            sink.write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static String readLine(InputStream input) throws IOException {
        ByteArrayOutputStream line = new ByteArrayOutputStream(128);
        int value;
        boolean any = false;
        while ((value = input.read()) >= 0) {
            any = true;
            if (value == '\n') {
                break;
            }
            if (value != '\r') {
                line.write(value);
            }
        }
        if (!any) {
            return null;
        }
        return new String(line.toByteArray(), StandardCharsets.ISO_8859_1);
    }

    private static void applyServerName(SSLSocket socket, String host) {
        if (host.indexOf(':') >= 0 || host.matches("\\d+\\.\\d+\\.\\d+\\.\\d+")) {
            return; // SNI is for host names only
        }
        try {
            SSLParameters parameters = socket.getSSLParameters();
            parameters.setServerNames(Collections.singletonList(new SNIHostName(host)));
            socket.setSSLParameters(parameters);
        } catch (Exception ignored) {
        }
    }

    private static volatile SSLSocketFactory socketFactory;

    /**
     * Trust-all TLS: NAS boxes almost always serve WebDAV over a self-signed certificate, and the
     * alternative is that the source simply cannot be added. Traffic stays inside the LAN.
     */
    private static SSLSocketFactory secureFactory() throws IOException {
        SSLSocketFactory factory = socketFactory;
        if (factory != null) {
            return factory;
        }
        synchronized (Http.class) {
            if (socketFactory == null) {
                try {
                    SSLContext context = SSLContext.getInstance("TLS");
                    context.init(null, new TrustManager[]{
                            new X509TrustManager() {
                                @Override
                                public void checkClientTrusted(X509Certificate[] chain, String authType) {
                                }

                                @Override
                                public void checkServerTrusted(X509Certificate[] chain, String authType) {
                                }

                                @Override
                                public X509Certificate[] getAcceptedIssuers() {
                                    return new X509Certificate[0];
                                }
                            }
                    }, new SecureRandom());
                    socketFactory = context.getSocketFactory();
                } catch (Exception error) {
                    throw new IOException("无法初始化 TLS：" + error.getMessage());
                }
            }
            return socketFactory;
        }
    }

    /** Split URL, with the path percent-encoded so Chinese folder names survive the request line. */
    private static final class Target {
        final String host;
        final int port;
        final boolean tls;
        final String authority;
        final String requestTarget;

        Target(String host, int port, boolean tls, String authority, String requestTarget) {
            this.host = host;
            this.port = port;
            this.tls = tls;
            this.authority = authority;
            this.requestTarget = requestTarget;
        }

        static Target parse(String url) throws IOException {
            String trimmed = url == null ? "" : url.trim();
            if (trimmed.isEmpty()) {
                throw new IOException("地址为空。");
            }
            URL parsed;
            try {
                parsed = new URL(trimmed.contains("://") ? trimmed : "http://" + trimmed);
            } catch (Exception error) {
                throw new IOException("地址格式不正确：" + url);
            }
            String scheme = parsed.getProtocol() == null
                    ? "http" : parsed.getProtocol().toLowerCase(Locale.ROOT);
            boolean tls = scheme.equals("https");
            if (!scheme.equals("http") && !tls) {
                throw new IOException("不支持的协议：" + scheme);
            }
            String host = parsed.getHost();
            if (host == null || host.isEmpty()) {
                throw new IOException("地址中缺少主机名：" + url);
            }
            int port = parsed.getPort() >= 0 ? parsed.getPort() : (tls ? 443 : 80);
            String path = parsed.getFile() == null || parsed.getFile().isEmpty() ? "/" : parsed.getFile();
            String authority = host.indexOf(':') >= 0 ? "[" + host + "]" : host;
            if (port != (tls ? 443 : 80)) {
                authority += ":" + port;
            }
            return new Target(host, port, tls, authority, encodePath(path));
        }

        private static String encodePath(String path) {
            StringBuilder builder = new StringBuilder(path.length() + 16);
            for (byte value : path.getBytes(StandardCharsets.UTF_8)) {
                int c = value & 0xFF;
                boolean safe = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                        || "/:@-._~!$&'()*+,;=%".indexOf(c) >= 0;
                if (safe) {
                    builder.append((char) c);
                } else {
                    builder.append('%');
                    builder.append(Character.toUpperCase(Character.forDigit((c >> 4) & 0xF, 16)));
                    builder.append(Character.toUpperCase(Character.forDigit(c & 0xF, 16)));
                }
            }
            return builder.toString();
        }
    }
}
