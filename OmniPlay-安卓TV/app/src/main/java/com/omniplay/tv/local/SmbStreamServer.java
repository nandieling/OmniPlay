package com.omniplay.tv.local;

import jcifs.smb.SmbRandomAccessFile;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Tiny local HTTP server that exposes a single SMB file as a Range-capable stream so
 * libmpv can play the original file directly. Mirrors
 * {@link com.omniplay.tv.iso.RemoteIsoStreamServer}.
 */
public final class SmbStreamServer {
    private static final int CHUNK_SIZE = 512 * 1024;
    private static final SmbStreamServer INSTANCE = new SmbStreamServer();

    private final Map<String, Route> routes = new ConcurrentHashMap<>();
    private final ExecutorService executor = Executors.newCachedThreadPool();
    private ServerSocket serverSocket;
    private Thread serverThread;

    private SmbStreamServer() {
    }

    public static SmbStreamServer shared() {
        return INSTANCE;
    }

    /** Opens the SMB file, sizes it, registers a route and returns a local http URL. */
    public String prepare(SmbClient client, String absoluteUrl) throws IOException {
        SmbRandomAccessFile reader = client.randomAccessFile(absoluteUrl);
        long fileSize = reader.length();
        ensureStarted();
        String routeId = UUID.randomUUID().toString().replace("-", "");
        routes.put(routeId, new Route(reader, fileSize));
        return "http://127.0.0.1:" + serverSocket.getLocalPort() + "/smbfile/" + routeId;
    }

    public void clear() {
        for (Route route : routes.values()) {
            try {
                route.reader.close();
            } catch (IOException ignored) {
            }
        }
        routes.clear();
    }

    private synchronized void ensureStarted() throws IOException {
        if (serverSocket != null && !serverSocket.isClosed()) {
            return;
        }
        serverSocket = new ServerSocket(0, 16);
        serverThread = new Thread(this::acceptLoop, "OmniPlay-SMB-Proxy");
        serverThread.setDaemon(true);
        serverThread.start();
    }

    private void acceptLoop() {
        while (serverSocket != null && !serverSocket.isClosed()) {
            try {
                Socket socket = serverSocket.accept();
                executor.execute(() -> handle(socket));
            } catch (IOException ignored) {
                return;
            }
        }
    }

    private void handle(Socket socket) {
        try (Socket closeable = socket;
             InputStream input = new BufferedInputStream(closeable.getInputStream());
             OutputStream output = new BufferedOutputStream(closeable.getOutputStream())) {
            Request request = readRequest(input);
            if (request == null || (!"GET".equals(request.method) && !"HEAD".equals(request.method))) {
                sendError(output, 405, "Method Not Allowed");
                return;
            }
            String[] parts = request.path.split("/");
            if (parts.length < 3 || !"smbfile".equals(parts[1])) {
                sendError(output, 404, "Not Found");
                return;
            }
            Route route = routes.get(parts[2]);
            if (route == null) {
                sendError(output, 404, "Not Found");
                return;
            }
            Range range = parseRange(request.headers.get("range"), route.fileSize);
            if (range == null) {
                sendError(output, 416, "Range Not Satisfiable");
                return;
            }
            boolean partial = request.headers.containsKey("range");
            writeHeaders(output, partial ? 206 : 200, route.fileSize, range);
            if ("HEAD".equals(request.method)) {
                output.flush();
                return;
            }
            long sent = 0;
            byte[] buffer = new byte[CHUNK_SIZE];
            while (sent < range.length) {
                int wanted = (int) Math.min(CHUNK_SIZE, range.length - sent);
                int read = route.readAt(range.offset + sent, buffer, wanted);
                if (read <= 0) {
                    break;
                }
                output.write(buffer, 0, read);
                sent += read;
                if (read < wanted) {
                    break;
                }
            }
            output.flush();
        } catch (Exception ignored) {
        }
    }

    private static Request readRequest(InputStream input) throws IOException {
        ByteArrayOutputStream buffer = new ByteArrayOutputStream();
        int p3 = -1, p2 = -1, p1 = -1, value;
        while ((value = input.read()) >= 0 && buffer.size() < 256 * 1024) {
            buffer.write(value);
            if (p3 == '\r' && p2 == '\n' && p1 == '\r' && value == '\n') {
                break;
            }
            p3 = p2;
            p2 = p1;
            p1 = value;
        }
        if (buffer.size() == 0) {
            return null;
        }
        String text = buffer.toString(StandardCharsets.ISO_8859_1.name());
        String[] lines = text.split("\\r?\\n");
        if (lines.length == 0) {
            return null;
        }
        String[] requestLine = lines[0].split(" ");
        if (requestLine.length < 2) {
            return null;
        }
        Map<String, String> headers = new HashMap<>();
        for (int index = 1; index < lines.length; index++) {
            int colon = lines[index].indexOf(':');
            if (colon <= 0) {
                continue;
            }
            headers.put(lines[index].substring(0, colon).trim().toLowerCase(Locale.ROOT),
                    lines[index].substring(colon + 1).trim());
        }
        String path = requestLine[1];
        int query = path.indexOf('?');
        return new Request(requestLine[0].toUpperCase(Locale.ROOT),
                query >= 0 ? path.substring(0, query) : path, headers);
    }

    private static Range parseRange(String header, long fileSize) {
        if (fileSize <= 0) {
            return null;
        }
        if (header == null || !header.toLowerCase(Locale.ROOT).startsWith("bytes=")) {
            return new Range(0, fileSize);
        }
        String range = header.substring(6).split(",", 2)[0].trim();
        String[] parts = range.split("-", -1);
        if (parts.length != 2) {
            return null;
        }
        try {
            if (parts[0].isEmpty()) {
                long suffix = Long.parseLong(parts[1]);
                if (suffix <= 0) {
                    return null;
                }
                long length = Math.min(suffix, fileSize);
                return new Range(fileSize - length, length);
            }
            long start = Long.parseLong(parts[0]);
            long end = parts[1].isEmpty() ? fileSize - 1 : Math.min(Long.parseLong(parts[1]), fileSize - 1);
            if (start < 0 || start >= fileSize || end < start) {
                return null;
            }
            return new Range(start, end - start + 1);
        } catch (NumberFormatException ignored) {
            return null;
        }
    }

    private static void writeHeaders(OutputStream output, int status, long fileSize, Range range) throws IOException {
        StringBuilder builder = new StringBuilder();
        builder.append("HTTP/1.1 ").append(status).append(status == 206 ? " Partial Content" : " OK").append("\r\n");
        builder.append("Content-Type: video/mp2t\r\n");
        builder.append("Accept-Ranges: bytes\r\n");
        builder.append("Content-Length: ").append(range.length).append("\r\n");
        if (status == 206) {
            builder.append("Content-Range: bytes ")
                    .append(range.offset).append('-').append(range.offset + range.length - 1)
                    .append('/').append(fileSize).append("\r\n");
        }
        builder.append("Connection: close\r\n\r\n");
        output.write(builder.toString().getBytes(StandardCharsets.ISO_8859_1));
    }

    private static void sendError(OutputStream output, int status, String message) throws IOException {
        byte[] body = message.getBytes(StandardCharsets.UTF_8);
        String headers = "HTTP/1.1 " + status + " " + message + "\r\n"
                + "Content-Length: " + body.length + "\r\n"
                + "Connection: close\r\n\r\n";
        output.write(headers.getBytes(StandardCharsets.ISO_8859_1));
        output.write(body);
    }

    private static final class Route {
        final SmbRandomAccessFile reader;
        final long fileSize;

        Route(SmbRandomAccessFile reader, long fileSize) {
            this.reader = reader;
            this.fileSize = fileSize;
        }

        /** Synchronous read: mpv may issue parallel Range requests on one file. */
        synchronized int readAt(long offset, byte[] buffer, int length) throws IOException {
            reader.seek(offset);
            return reader.read(buffer, 0, length);
        }
    }

    private static final class Request {
        final String method;
        final String path;
        final Map<String, String> headers;

        Request(String method, String path, Map<String, String> headers) {
            this.method = method;
            this.path = path;
            this.headers = headers;
        }
    }

    private static final class Range {
        final long offset;
        final long length;

        Range(long offset, long length) {
            this.offset = offset;
            this.length = length;
        }
    }
}
