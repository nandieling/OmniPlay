package com.omniplay.tv.iso;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.URL;
import java.nio.charset.Charset;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class RemoteIsoStreamServer {
    private static final int SECTOR_SIZE = 2048;
    private static final int CHUNK_SIZE = 512 * 1024;
    private static final RemoteIsoStreamServer INSTANCE = new RemoteIsoStreamServer();

    private final Map<String, Route> routes = new ConcurrentHashMap<>();
    private final ExecutorService executor = Executors.newCachedThreadPool();
    private ServerSocket serverSocket;
    private Thread serverThread;

    private RemoteIsoStreamServer() {
    }

    public static RemoteIsoStreamServer shared() {
        return INSTANCE;
    }

    public synchronized String prepare(String sourceUrl, String cookieHeader) throws IOException {
        ByteReader reader = new ByteReader(sourceUrl, cookieHeader);
        long isoSize = reader.contentLength();
        List<IsoFile> files = new IsoImageParser(reader, isoSize).bluRayStreamFiles();
        if (files.isEmpty()) {
            throw new IOException("这个 ISO 里没有找到 BDMV/STREAM 视频流（目前只支持蓝光 ISO，"
                    + "DVD 的 VIDEO_TS 暂不识别）。");
        }

        IsoFile selected = Collections.max(files, Comparator.comparingLong(file -> file.size));
        ensureStarted();
        String routeId = UUID.randomUUID().toString().replace("-", "");
        routes.put(routeId, new Route(reader, selected));
        return "http://127.0.0.1:" + serverSocket.getLocalPort() + "/remoteiso/" + routeId + "/stream.ts";
    }

    public void clear() {
        routes.clear();
    }

    private synchronized void ensureStarted() throws IOException {
        if (serverSocket != null && !serverSocket.isClosed()) {
            return;
        }

        serverSocket = new ServerSocket(0, 16);
        serverThread = new Thread(this::acceptLoop, "OmniPlay-ISO-Proxy");
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
            if (parts.length < 3 || !"remoteiso".equals(parts[1])) {
                sendError(output, 404, "Not Found");
                return;
            }

            Route route = routes.get(parts[2]);
            if (route == null) {
                sendError(output, 404, "Not Found");
                return;
            }

            Range range = parseRange(request.headers.get("range"), route.file.size);
            if (range == null) {
                sendError(output, 416, "Range Not Satisfiable");
                return;
            }

            boolean partial = request.headers.containsKey("range");
            writeHeaders(output, partial ? 206 : 200, route.file.size, range);
            if ("HEAD".equals(request.method)) {
                output.flush();
                return;
            }

            long sent = 0;
            while (sent < range.length) {
                int length = (int) Math.min(CHUNK_SIZE, range.length - sent);
                byte[] data = route.reader.read(route.file, range.offset + sent, length);
                if (data.length == 0) {
                    break;
                }
                output.write(data);
                sent += data.length;
                if (data.length < length) {
                    break;
                }
            }
            output.flush();
        } catch (IOException ignored) {
        }
    }

    private static Request readRequest(InputStream input) throws IOException {
        ByteArrayOutputStream buffer = new ByteArrayOutputStream();
        int previous3 = -1;
        int previous2 = -1;
        int previous1 = -1;
        int value;
        while ((value = input.read()) >= 0 && buffer.size() < 256 * 1024) {
            buffer.write(value);
            if (previous3 == '\r' && previous2 == '\n' && previous1 == '\r' && value == '\n') {
                break;
            }
            previous3 = previous2;
            previous2 = previous1;
            previous1 = value;
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
            headers.put(
                    lines[index].substring(0, colon).trim().toLowerCase(Locale.ROOT),
                    lines[index].substring(colon + 1).trim());
        }
        String path = requestLine[1];
        int query = path.indexOf('?');
        return new Request(requestLine[0].toUpperCase(Locale.ROOT), query >= 0 ? path.substring(0, query) : path, headers);
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
        builder.append("Content-Type: video/MP2T\r\n");
        builder.append("Accept-Ranges: bytes\r\n");
        builder.append("Content-Length: ").append(range.length).append("\r\n");
        if (status == 206) {
            builder.append("Content-Range: bytes ")
                    .append(range.offset)
                    .append("-")
                    .append(range.offset + range.length - 1)
                    .append("/")
                    .append(fileSize)
                    .append("\r\n");
        }
        builder.append("Connection: close\r\n\r\n");
        output.write(builder.toString().getBytes(StandardCharsets.ISO_8859_1));
    }

    private static void sendError(OutputStream output, int status, String message) throws IOException {
        byte[] body = message.getBytes(StandardCharsets.UTF_8);
        String headers = "HTTP/1.1 " + status + " " + message + "\r\n" +
                "Content-Length: " + body.length + "\r\n" +
                "Connection: close\r\n\r\n";
        output.write(headers.getBytes(StandardCharsets.ISO_8859_1));
        output.write(body);
        output.flush();
    }

    private static final class ByteReader {
        private final String sourceUrl;
        private final String cookieHeader;

        ByteReader(String sourceUrl, String cookieHeader) {
            this.sourceUrl = sourceUrl;
            this.cookieHeader = cookieHeader == null ? "" : cookieHeader;
        }

        long contentLength() throws IOException {
            HttpURLConnection head = openConnection("HEAD", -1, -1);
            try {
                int status = head.getResponseCode();
                String length = head.getHeaderField("Content-Length");
                if (status >= 200 && status < 300 && length != null) {
                    long parsed = Long.parseLong(length);
                    if (parsed > 0) {
                        return parsed;
                    }
                }
            } catch (NumberFormatException ignored) {
            } finally {
                head.disconnect();
            }

            HttpURLConnection range = openConnection("GET", 0, 0);
            try {
                int status = range.getResponseCode();
                String contentRange = range.getHeaderField("Content-Range");
                long total = contentRangeTotal(contentRange);
                if (status == 206 && total > 0) {
                    return total;
                }
                String length = range.getHeaderField("Content-Length");
                if (length != null) {
                    long parsed = Long.parseLong(length);
                    if (parsed > 1) {
                        return parsed;
                    }
                }
            } catch (NumberFormatException ignored) {
            } finally {
                range.disconnect();
            }
            throw new IOException("无法读取 ISO 文件大小。");
        }

        byte[] read(long offset, int length) throws IOException {
            if (length <= 0) {
                return new byte[0];
            }
            HttpURLConnection connection = openConnection("GET", offset, offset + length - 1L);
            try (InputStream input = new BufferedInputStream(connection.getInputStream())) {
                int status = connection.getResponseCode();
                if (status != 206 && !(status == 200 && offset == 0)) {
                    throw new IOException("ISO Range 请求失败：" + status);
                }
                return readAtMost(input, length);
            } finally {
                connection.disconnect();
            }
        }

        byte[] read(IsoFile file, long offset, int length) throws IOException {
            if (length <= 0 || offset >= file.size) {
                return new byte[0];
            }

            long remaining = Math.min(length, file.size - offset);
            long skip = offset;
            ByteArrayOutputStream output = new ByteArrayOutputStream((int) Math.min(remaining, length));
            for (Extent extent : file.extents) {
                if (skip >= extent.length) {
                    skip -= extent.length;
                    continue;
                }

                long extentOffset = extent.offset + skip;
                long extentRemaining = extent.length - skip;
                skip = 0;
                while (remaining > 0 && extentRemaining > 0) {
                    int chunkLength = (int) Math.min(Math.min(remaining, extentRemaining), CHUNK_SIZE);
                    byte[] data = read(extentOffset, chunkLength);
                    if (data.length == 0) {
                        return output.toByteArray();
                    }
                    output.write(data, 0, data.length);
                    extentOffset += data.length;
                    extentRemaining -= data.length;
                    remaining -= data.length;
                    if (data.length < chunkLength) {
                        return output.toByteArray();
                    }
                }
                if (remaining <= 0) {
                    break;
                }
            }
            return output.toByteArray();
        }

        private HttpURLConnection openConnection(String method, long start, long end) throws IOException {
            HttpURLConnection connection = (HttpURLConnection) new URL(sourceUrl).openConnection();
            connection.setConnectTimeout(8000);
            connection.setReadTimeout(60000);
            connection.setRequestMethod(method);
            connection.setRequestProperty("User-Agent", "OmniPlay-Android/0.1");
            if (!cookieHeader.isEmpty()) {
                connection.setRequestProperty("Cookie", cookieHeader);
            }
            if (start >= 0 && end >= start) {
                connection.setRequestProperty("Range", "bytes=" + start + "-" + end);
            }
            return connection;
        }

        private static long contentRangeTotal(String value) {
            if (value == null) {
                return -1;
            }
            int slash = value.lastIndexOf('/');
            if (slash < 0 || slash + 1 >= value.length()) {
                return -1;
            }
            try {
                return Long.parseLong(value.substring(slash + 1).trim());
            } catch (NumberFormatException ignored) {
                return -1;
            }
        }

        private static byte[] readAtMost(InputStream input, int maxLength) throws IOException {
            ByteArrayOutputStream output = new ByteArrayOutputStream(maxLength);
            byte[] buffer = new byte[Math.min(maxLength, 64 * 1024)];
            int remaining = maxLength;
            while (remaining > 0) {
                int read = input.read(buffer, 0, Math.min(buffer.length, remaining));
                if (read < 0) {
                    break;
                }
                output.write(buffer, 0, read);
                remaining -= read;
            }
            return output.toByteArray();
        }
    }

    private static final class IsoImageParser {
        private final ByteReader reader;
        private final long isoSize;
        private long logicalBlockSize = SECTOR_SIZE;
        private final Map<Integer, UdfPartition> partitionsByNumber = new HashMap<>();
        private final Map<Integer, UdfPartition> partitionsByReference = new HashMap<>();
        private final Map<Integer, UdfMetadataPartitionMap> metadataPartitionMapsByReference = new HashMap<>();
        private final Map<Integer, List<Extent>> metadataExtentsByReference = new HashMap<>();

        IsoImageParser(ByteReader reader, long isoSize) {
            this.reader = reader;
            this.isoSize = isoSize;
        }

        List<IsoFile> bluRayStreamFiles() throws IOException {
            try {
                List<IsoFile> udfFiles = parseUdfBluRayStreamFiles();
                if (!udfFiles.isEmpty()) {
                    return udfFiles;
                }
            } catch (IOException ignored) {
            }
            return parseIso9660BluRayStreamFiles();
        }

        private List<IsoFile> parseUdfBluRayStreamFiles() throws IOException {
            partitionsByNumber.clear();
            partitionsByReference.clear();
            metadataPartitionMapsByReference.clear();
            metadataExtentsByReference.clear();

            Data anchor = findUdfAnchor();
            long mainLength = anchor.uint32LE(16);
            long mainLocation = anchor.uint32LE(20);
            if (mainLength <= 0 || mainLocation <= 0) {
                throw new IOException("invalid udf");
            }

            UdfLongAd fileSetAd = null;
            Map<Integer, Integer> partitionMap = new HashMap<>();
            long descriptorSectors = Math.min((mainLength + SECTOR_SIZE - 1L) / SECTOR_SIZE, 4096L);
            for (long sectorOffset = 0; sectorOffset < descriptorSectors; sectorOffset++) {
                Data descriptor = readSector(mainLocation + sectorOffset);
                int tag = descriptor.uint16LE(0);
                if (tag == 8) {
                    break;
                }
                if (tag == 5) {
                    int number = descriptor.uint16LE(22);
                    long start = descriptor.uint32LE(188);
                    long length = descriptor.uint32LE(192);
                    partitionsByNumber.put(number, new UdfPartition(number, start, length));
                } else if (tag == 6) {
                    long blockSize = descriptor.uint32LE(212);
                    if (blockSize > 0) {
                        logicalBlockSize = blockSize;
                    }
                    fileSetAd = parseLongAd(descriptor, 248);
                    partitionMap = parsePartitionMaps(descriptor);
                }
            }

            for (Map.Entry<Integer, Integer> entry : partitionMap.entrySet()) {
                UdfPartition partition = partitionsByNumber.get(entry.getValue());
                if (partition != null) {
                    partitionsByReference.put(entry.getKey(), partition);
                }
            }
            if (partitionsByReference.isEmpty()) {
                ArrayList<UdfPartition> partitions = new ArrayList<>(partitionsByNumber.values());
                Collections.sort(partitions, Comparator.comparingInt(partition -> partition.number));
                for (int index = 0; index < partitions.size(); index++) {
                    partitionsByReference.put(index, partitions.get(index));
                }
            }
            resolveMetadataPartitions();

            if (fileSetAd == null) {
                throw new IOException("invalid udf");
            }
            Data fileSetDescriptor = readDescriptor(fileSetAd);
            if (fileSetDescriptor.uint16LE(0) != 256) {
                throw new IOException("invalid udf");
            }

            UdfLongAd rootIcb = parseLongAd(fileSetDescriptor, 400);
            UdfLongAd streamIcb = locateUdfBluRayStreamDirectory(rootIcb);
            List<UdfDirectoryEntry> entries = readUdfDirectory(streamIcb);
            ArrayList<IsoFile> files = new ArrayList<>();
            for (UdfDirectoryEntry entry : entries) {
                String lower = entry.name.toLowerCase(Locale.ROOT);
                if (entry.directory || !(lower.endsWith(".m2ts") || lower.endsWith(".m2t") || lower.endsWith(".ts"))) {
                    continue;
                }
                UdfFileEntry fileEntry = readUdfFileEntry(entry.icb);
                List<Extent> extents = limitedExtents(fileEntry.extents, fileEntry.informationLength);
                if (fileEntry.informationLength > 0 && !extents.isEmpty()) {
                    files.add(new IsoFile("BDMV/STREAM/" + entry.name, entry.name, fileEntry.informationLength, extents));
                }
            }
            return files;
        }

        private Data findUdfAnchor() throws IOException {
            long sectors = Math.max(0, isoSize / SECTOR_SIZE);
            long[] candidates = new long[]{256, sectors - 256, sectors - 1};
            for (long sector : candidates) {
                if (sector < 0) {
                    continue;
                }
                Data data = readSector(sector);
                if (data.uint16LE(0) == 2) {
                    return data;
                }
            }
            throw new IOException("invalid udf");
        }

        private Map<Integer, Integer> parsePartitionMaps(Data descriptor) {
            int mapTableLength = (int) descriptor.uint32LE(264);
            int mapCount = (int) descriptor.uint32LE(268);
            HashMap<Integer, Integer> result = new HashMap<>();
            int offset = 440;
            for (int reference = 0; reference < mapCount; reference++) {
                if (offset + 2 > descriptor.length() || offset >= 440 + mapTableLength) {
                    break;
                }
                int type = descriptor.uint8(offset);
                int length = descriptor.uint8(offset + 1);
                if (length <= 0 || offset + length > descriptor.length()) {
                    break;
                }
                if (type == 1 && length >= 6) {
                    result.put(reference, descriptor.uint16LE(offset + 4));
                } else if (type == 2 && length >= 59) {
                    String identifier = descriptor.string(offset + 5, 23, StandardCharsets.US_ASCII);
                    if (identifier.contains("UDF Metadata")) {
                        metadataPartitionMapsByReference.put(
                                reference,
                                new UdfMetadataPartitionMap(
                                        descriptor.uint16LE(offset + 38),
                                        descriptor.uint32LE(offset + 40),
                                        descriptor.uint32LE(offset + 44)));
                    }
                }
                offset += length;
            }
            return result;
        }

        private void resolveMetadataPartitions() {
            for (Map.Entry<Integer, UdfMetadataPartitionMap> entry : metadataPartitionMapsByReference.entrySet()) {
                UdfMetadataPartitionMap map = entry.getValue();
                long[] locations = new long[]{map.metadataFileLocation, map.metadataMirrorFileLocation};
                for (long location : locations) {
                    if (location <= 0) {
                        continue;
                    }
                    try {
                        UdfFileEntry fileEntry = readUdfFileEntry(new UdfLongAd(logicalBlockSize, location, map.physicalPartitionNumber));
                        List<Extent> extents = limitedExtents(fileEntry.extents, fileEntry.informationLength);
                        if (!extents.isEmpty()) {
                            metadataExtentsByReference.put(entry.getKey(), extents);
                            break;
                        }
                    } catch (IOException ignored) {
                    }
                }
            }
        }

        private UdfLongAd locateUdfBluRayStreamDirectory(UdfLongAd rootIcb) throws IOException {
            try {
                return traverseUdfPath(rootIcb, new String[]{"BDMV", "STREAM"});
            } catch (IOException ignored) {
            }
            UdfLongAd found = searchUdfBluRayStreamDirectory(rootIcb, 0, new int[]{0});
            if (found != null) {
                return found;
            }
            throw new IOException("BDMV/STREAM not found");
        }

        private UdfLongAd searchUdfBluRayStreamDirectory(UdfLongAd directoryIcb, int depth, int[] visited) throws IOException {
            if (depth > 4 || visited[0] >= 256) {
                return null;
            }
            visited[0]++;
            List<UdfDirectoryEntry> entries = readUdfDirectory(directoryIcb);
            for (UdfDirectoryEntry entry : entries) {
                if (entry.directory && "BDMV".equalsIgnoreCase(entry.name)) {
                    for (UdfDirectoryEntry bdmvEntry : readUdfDirectory(entry.icb)) {
                        if (bdmvEntry.directory && "STREAM".equalsIgnoreCase(bdmvEntry.name)) {
                            return bdmvEntry.icb;
                        }
                    }
                }
            }
            for (UdfDirectoryEntry entry : entries) {
                String lower = entry.name.toLowerCase(Locale.ROOT);
                if (!entry.directory || "certificate".equals(lower) || "any!".equals(lower)) {
                    continue;
                }
                UdfLongAd found = searchUdfBluRayStreamDirectory(entry.icb, depth + 1, visited);
                if (found != null) {
                    return found;
                }
            }
            return null;
        }

        private UdfLongAd traverseUdfPath(UdfLongAd rootIcb, String[] components) throws IOException {
            UdfLongAd current = rootIcb;
            for (String component : components) {
                UdfLongAd next = null;
                for (UdfDirectoryEntry entry : readUdfDirectory(current)) {
                    if (entry.directory && component.equalsIgnoreCase(entry.name)) {
                        next = entry.icb;
                        break;
                    }
                }
                if (next == null) {
                    throw new IOException("path not found");
                }
                current = next;
            }
            return current;
        }

        private List<UdfDirectoryEntry> readUdfDirectory(UdfLongAd icb) throws IOException {
            UdfFileEntry fileEntry = readUdfFileEntry(icb);
            byte[] bytes = fileEntry.inlineData == null
                    ? readAbsoluteExtents(fileEntry.extents, (int) Math.min(fileEntry.informationLength, 64L * 1024L * 1024L))
                    : fileEntry.inlineData;
            Data data = new Data(bytes);
            ArrayList<UdfDirectoryEntry> entries = new ArrayList<>();
            int offset = 0;
            while (offset + 38 <= data.length()) {
                int tag = data.uint16LE(offset);
                if (tag != 257) {
                    offset += 4;
                    continue;
                }
                int characteristics = data.uint8(offset + 18);
                int nameLength = data.uint8(offset + 19);
                UdfLongAd entryIcb = parseLongAd(data, offset + 20);
                int implementationUseLength = data.uint16LE(offset + 36);
                int nameOffset = offset + 38 + implementationUseLength;
                int entryLength = align4(38 + implementationUseLength + nameLength);
                if (entryLength <= 0 || offset + entryLength > data.length()) {
                    break;
                }
                String name = decodeOstaCompressedUnicode(data.subdata(nameOffset, nameLength));
                boolean isParent = (characteristics & 0x08) != 0;
                if (!name.isEmpty() && !isParent) {
                    entries.add(new UdfDirectoryEntry(name, (characteristics & 0x02) != 0, entryIcb));
                }
                offset += entryLength;
            }
            return entries;
        }

        private UdfFileEntry readUdfFileEntry(UdfLongAd icb) throws IOException {
            Data descriptor = readDescriptor(icb);
            int tag = descriptor.uint16LE(0);
            if (tag != 261 && tag != 266) {
                throw new IOException("invalid udf file entry");
            }
            long informationLength = descriptor.uint64LE(56);
            int flags = descriptor.uint16LE(34) & 0x0007;
            int lengthEAOffset = tag == 261 ? 168 : 208;
            int lengthADOffset = tag == 261 ? 172 : 212;
            int allocationOffset = tag == 261 ? 176 : 216;
            int lengthEA = (int) descriptor.uint32LE(lengthEAOffset);
            int lengthAD = (int) descriptor.uint32LE(lengthADOffset);
            Data adData = new Data(descriptor.subdata(allocationOffset + lengthEA, lengthAD));
            if (flags == 3) {
                return new UdfFileEntry(adData.length(), Collections.emptyList(), adData.bytes);
            }

            int descriptorLength;
            if (flags == 0) {
                descriptorLength = 8;
            } else if (flags == 1) {
                descriptorLength = 16;
            } else if (flags == 2) {
                descriptorLength = 20;
            } else {
                descriptorLength = 0;
            }

            ArrayList<Extent> extents = new ArrayList<>();
            for (int offset = 0; descriptorLength > 0 && offset + descriptorLength <= adData.length(); offset += descriptorLength) {
                long rawLength = adData.uint32LE(offset);
                long extentType = rawLength >>> 30;
                long length = rawLength & 0x3fffffffL;
                if (length <= 0 || extentType == 3) {
                    continue;
                }
                long block;
                int partitionRef;
                if (flags == 0) {
                    block = adData.uint32LE(offset + 4);
                    partitionRef = icb.partitionRef;
                } else if (flags == 1) {
                    block = adData.uint32LE(offset + 4);
                    partitionRef = adData.uint16LE(offset + 8);
                } else {
                    block = adData.uint32LE(offset + 12);
                    partitionRef = adData.uint16LE(offset + 16);
                }
                Long absoluteOffset = absoluteOffset(block, partitionRef);
                if (absoluteOffset != null) {
                    extents.add(new Extent(absoluteOffset, length));
                }
            }
            return new UdfFileEntry(informationLength, extents, null);
        }

        private UdfLongAd parseLongAd(Data data, int offset) {
            long rawLength = data.uint32LE(offset);
            return new UdfLongAd(rawLength & 0x3fffffffL, data.uint32LE(offset + 4), data.uint16LE(offset + 8));
        }

        private Data readDescriptor(UdfLongAd ad) throws IOException {
            Long offset = absoluteOffset(ad.block, ad.partitionRef);
            if (offset == null) {
                throw new IOException("invalid udf offset");
            }
            return new Data(reader.read(offset, (int) logicalBlockSize));
        }

        private Long absoluteOffset(long block, int partitionRef) {
            List<Extent> metadataExtents = metadataExtentsByReference.get(partitionRef);
            if (metadataExtents != null) {
                Long metadataOffset = offsetInExtents(metadataExtents, block * logicalBlockSize);
                if (metadataOffset != null) {
                    return metadataOffset;
                }
            }
            UdfPartition partition = partitionsByReference.get(partitionRef);
            if (partition == null) {
                partition = partitionsByNumber.get(partitionRef);
            }
            if (partition == null && !partitionsByReference.isEmpty()) {
                partition = partitionsByReference.values().iterator().next();
            }
            return partition == null ? null : (partition.startBlock + block) * logicalBlockSize;
        }

        private Long offsetInExtents(List<Extent> extents, long fileOffset) {
            long remaining = fileOffset;
            for (Extent extent : extents) {
                if (remaining < extent.length) {
                    return extent.offset + remaining;
                }
                remaining -= extent.length;
            }
            return null;
        }

        private byte[] readAbsoluteExtents(List<Extent> extents, int maxLength) throws IOException {
            ByteArrayOutputStream output = new ByteArrayOutputStream(maxLength);
            int remaining = maxLength;
            for (Extent extent : extents) {
                if (remaining <= 0) {
                    break;
                }
                long extentOffset = extent.offset;
                int extentRemaining = (int) Math.min(remaining, extent.length);
                while (extentRemaining > 0) {
                    int chunkLength = Math.min(extentRemaining, CHUNK_SIZE);
                    byte[] data = reader.read(extentOffset, chunkLength);
                    if (data.length == 0) {
                        return output.toByteArray();
                    }
                    output.write(data, 0, data.length);
                    extentOffset += data.length;
                    extentRemaining -= data.length;
                    remaining -= data.length;
                    if (data.length < chunkLength) {
                        return output.toByteArray();
                    }
                }
            }
            return output.toByteArray();
        }

        private List<Extent> limitedExtents(List<Extent> extents, long length) {
            long remaining = length;
            ArrayList<Extent> result = new ArrayList<>();
            for (Extent extent : extents) {
                if (remaining <= 0) {
                    break;
                }
                long resolvedLength = Math.min(extent.length, remaining);
                result.add(new Extent(extent.offset, resolvedLength));
                remaining -= resolvedLength;
            }
            return result;
        }

        private List<IsoFile> parseIso9660BluRayStreamFiles() throws IOException {
            Data pvd = readSector(16);
            if (!"CD001".equals(pvd.string(1, 5, StandardCharsets.US_ASCII))) {
                return Collections.emptyList();
            }
            Iso9660Entry root = parseIso9660DirectoryRecord(pvd, 156);
            if (root == null) {
                return Collections.emptyList();
            }
            Iso9660Entry stream = locateIso9660BluRayStreamDirectory(root);
            ArrayList<IsoFile> files = new ArrayList<>();
            for (Iso9660Entry entry : readIso9660Directory(stream)) {
                String lower = entry.name.toLowerCase(Locale.ROOT);
                if (entry.directory || !(lower.endsWith(".m2ts") || lower.endsWith(".m2t") || lower.endsWith(".ts"))) {
                    continue;
                }
                long offset = entry.extent * (long) SECTOR_SIZE;
                long size = entry.size;
                files.add(new IsoFile("BDMV/STREAM/" + entry.name, entry.name, size, Collections.singletonList(new Extent(offset, size))));
            }
            return files;
        }

        private Iso9660Entry locateIso9660BluRayStreamDirectory(Iso9660Entry root) throws IOException {
            try {
                Iso9660Entry bdmv = findIso9660Entry(root, "BDMV", true);
                return findIso9660Entry(bdmv, "STREAM", true);
            } catch (IOException ignored) {
            }
            Iso9660Entry found = searchIso9660BluRayStreamDirectory(root, 0, new int[]{0});
            if (found != null) {
                return found;
            }
            throw new IOException("BDMV/STREAM not found");
        }

        private Iso9660Entry searchIso9660BluRayStreamDirectory(Iso9660Entry directory, int depth, int[] visited) throws IOException {
            if (depth > 4 || visited[0] >= 256) {
                return null;
            }
            visited[0]++;
            List<Iso9660Entry> entries = readIso9660Directory(directory);
            for (Iso9660Entry entry : entries) {
                if (entry.directory && "BDMV".equalsIgnoreCase(entry.name)) {
                    for (Iso9660Entry bdmvEntry : readIso9660Directory(entry)) {
                        if (bdmvEntry.directory && "STREAM".equalsIgnoreCase(bdmvEntry.name)) {
                            return bdmvEntry;
                        }
                    }
                }
            }
            for (Iso9660Entry entry : entries) {
                String lower = entry.name.toLowerCase(Locale.ROOT);
                if (!entry.directory || "certificate".equals(lower) || "any!".equals(lower)) {
                    continue;
                }
                Iso9660Entry found = searchIso9660BluRayStreamDirectory(entry, depth + 1, visited);
                if (found != null) {
                    return found;
                }
            }
            return null;
        }

        private Iso9660Entry findIso9660Entry(Iso9660Entry directory, String name, boolean directoryExpected) throws IOException {
            for (Iso9660Entry entry : readIso9660Directory(directory)) {
                if (entry.directory == directoryExpected && name.equalsIgnoreCase(entry.name)) {
                    return entry;
                }
            }
            throw new IOException("not found");
        }

        private List<Iso9660Entry> readIso9660Directory(Iso9660Entry directory) throws IOException {
            Data data = new Data(reader.read(directory.extent * (long) SECTOR_SIZE, (int) directory.size));
            ArrayList<Iso9660Entry> entries = new ArrayList<>();
            int cursor = 0;
            while (cursor < data.length()) {
                int recordLength = data.uint8(cursor);
                if (recordLength == 0) {
                    cursor = ((cursor / SECTOR_SIZE) + 1) * SECTOR_SIZE;
                    continue;
                }
                Iso9660Entry entry = parseIso9660DirectoryRecord(data, cursor);
                if (entry != null && !".".equals(entry.name) && !"..".equals(entry.name)) {
                    entries.add(entry);
                }
                cursor += recordLength;
            }
            return entries;
        }

        private Iso9660Entry parseIso9660DirectoryRecord(Data data, int offset) {
            if (offset + 34 > data.length()) {
                return null;
            }
            int recordLength = data.uint8(offset);
            if (recordLength < 34 || offset + recordLength > data.length()) {
                return null;
            }
            long extent = data.uint32LE(offset + 2);
            long size = data.uint32LE(offset + 10);
            int flags = data.uint8(offset + 25);
            int nameLength = data.uint8(offset + 32);
            byte[] rawName = data.subdata(offset + 33, nameLength);
            String name;
            if (rawName.length == 1 && rawName[0] == 0) {
                name = ".";
            } else if (rawName.length == 1 && rawName[0] == 1) {
                name = "..";
            } else {
                name = new String(rawName, StandardCharsets.US_ASCII)
                        .replaceAll("(?i);1", "")
                        .replaceAll("^\\.+|\\.+$", "");
            }
            return new Iso9660Entry(name, extent, size, (flags & 0x02) != 0);
        }

        private Data readSector(long sector) throws IOException {
            return new Data(reader.read(sector * SECTOR_SIZE, SECTOR_SIZE));
        }

        private static int align4(int value) {
            return (value + 3) & ~3;
        }

        private static String decodeOstaCompressedUnicode(byte[] data) {
            if (data.length == 0) {
                return "";
            }
            int compressionId = data[0] & 0xff;
            if (compressionId == 8) {
                byte[] payload = copyOfRange(data, 1, data.length);
                return new String(payload, StandardCharsets.UTF_8);
            }
            if (compressionId == 16) {
                StringBuilder builder = new StringBuilder();
                for (int offset = 1; offset + 1 < data.length; offset += 2) {
                    int value = ((data[offset] & 0xff) << 8) | (data[offset + 1] & 0xff);
                    if (value > 0) {
                        builder.append((char) value);
                    }
                }
                return builder.toString();
            }
            return new String(data, StandardCharsets.UTF_8);
        }
    }

    private static final class Data {
        final byte[] bytes;

        Data(byte[] bytes) {
            this.bytes = bytes == null ? new byte[0] : bytes;
        }

        int length() {
            return bytes.length;
        }

        int uint8(int offset) {
            return offset >= 0 && offset < bytes.length ? bytes[offset] & 0xff : 0;
        }

        int uint16LE(int offset) {
            return uint8(offset) | (uint8(offset + 1) << 8);
        }

        long uint32LE(int offset) {
            return (uint16LE(offset) & 0xffffL) | ((uint16LE(offset + 2) & 0xffffL) << 16);
        }

        long uint64LE(int offset) {
            return uint32LE(offset) | (uint32LE(offset + 4) << 32);
        }

        int uint16BE(int offset) {
            return (uint8(offset) << 8) | uint8(offset + 1);
        }

        byte[] subdata(int offset, int length) {
            if (offset < 0 || length <= 0 || offset >= bytes.length) {
                return new byte[0];
            }
            return copyOfRange(bytes, offset, Math.min(bytes.length, offset + length));
        }

        String string(int offset, int length, Charset charset) {
            return new String(subdata(offset, length), charset);
        }
    }

    private static byte[] copyOfRange(byte[] source, int start, int end) {
        int safeStart = Math.max(0, Math.min(source.length, start));
        int safeEnd = Math.max(safeStart, Math.min(source.length, end));
        byte[] result = new byte[safeEnd - safeStart];
        System.arraycopy(source, safeStart, result, 0, result.length);
        return result;
    }

    private static final class Route {
        final ByteReader reader;
        final IsoFile file;

        Route(ByteReader reader, IsoFile file) {
            this.reader = reader;
            this.file = file;
        }
    }

    private static final class IsoFile {
        final String path;
        final String fileName;
        final long size;
        final List<Extent> extents;

        IsoFile(String path, String fileName, long size, List<Extent> extents) {
            this.path = path;
            this.fileName = fileName;
            this.size = size;
            this.extents = extents;
        }
    }

    private static final class Extent {
        final long offset;
        final long length;

        Extent(long offset, long length) {
            this.offset = offset;
            this.length = length;
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

    private static final class UdfPartition {
        final int number;
        final long startBlock;
        final long blockCount;

        UdfPartition(int number, long startBlock, long blockCount) {
            this.number = number;
            this.startBlock = startBlock;
            this.blockCount = blockCount;
        }
    }

    private static final class UdfMetadataPartitionMap {
        final int physicalPartitionNumber;
        final long metadataFileLocation;
        final long metadataMirrorFileLocation;

        UdfMetadataPartitionMap(int physicalPartitionNumber, long metadataFileLocation, long metadataMirrorFileLocation) {
            this.physicalPartitionNumber = physicalPartitionNumber;
            this.metadataFileLocation = metadataFileLocation;
            this.metadataMirrorFileLocation = metadataMirrorFileLocation;
        }
    }

    private static final class UdfLongAd {
        final long length;
        final long block;
        final int partitionRef;

        UdfLongAd(long length, long block, int partitionRef) {
            this.length = length;
            this.block = block;
            this.partitionRef = partitionRef;
        }
    }

    private static final class UdfFileEntry {
        final long informationLength;
        final List<Extent> extents;
        final byte[] inlineData;

        UdfFileEntry(long informationLength, List<Extent> extents, byte[] inlineData) {
            this.informationLength = informationLength;
            this.extents = extents;
            this.inlineData = inlineData;
        }
    }

    private static final class UdfDirectoryEntry {
        final String name;
        final boolean directory;
        final UdfLongAd icb;

        UdfDirectoryEntry(String name, boolean directory, UdfLongAd icb) {
            this.name = name;
            this.directory = directory;
            this.icb = icb;
        }
    }

    private static final class Iso9660Entry {
        final String name;
        final long extent;
        final long size;
        final boolean directory;

        Iso9660Entry(String name, long extent, long size, boolean directory) {
            this.name = name;
            this.extent = extent;
            this.size = size;
            this.directory = directory;
        }
    }
}
