package com.omniplay.tv.local;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.InterfaceAddress;
import java.net.NetworkInterface;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Enumeration;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ExecutorCompletionService;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;

/**
 * LAN scanner for the setup screen: finds WebDAV and SMB servers on the local
 * subnets. Every (host, port) pair is probed as its own task so one slow host
 * cannot starve the rest of the scan: SMB by TCP 445 plus a best-effort NetBIOS
 * name query, WebDAV by PROPFIND (falling back to an OPTIONS DAV header check)
 * against the common WebDAV ports. Devices are delivered through the listener as
 * soon as they are confirmed, then a final onDone; callbacks fire on the scan thread.
 */
public final class LanDiscovery {

    public static final class LanDevice {
        public final String kind; // "webdav" | "smb"
        public final String ip;
        public final int port;
        public final String name; // friendly name (machine name or ip)
        public final String url;  // base url: https://ip:port[/path] or smb://ip[:port]

        public LanDevice(String kind, String ip, int port, String name, String url) {
            this.kind = kind;
            this.ip = ip;
            this.port = port;
            this.name = name;
            this.url = url;
        }

        public String label() {
            String kindLabel = kind.equals("smb") ? "SMB" : "WebDAV";
            String hostPart = name == null || name.isEmpty() || name.equals(ip)
                    ? ip : name + "（" + ip + "）";
            return kindLabel + " · " + hostPart + portSuffix();
        }

        /** The port is only shown when it differs from the scheme default. */
        private String portSuffix() {
            if (kind.equals("smb") || port <= 0) {
                return "";
            }
            boolean standard = (url.startsWith("https") && port == 443) || (url.startsWith("http") && port == 80);
            return standard ? "" : " · " + port;
        }
    }

    public interface Listener {
        void onDevice(LanDevice device);

        void onDone(List<LanDevice> devices);
    }

    private static final int PORT_SMB = 445;
    /** Ordered by how likely the port is a WebDAV endpoint, so hits surface early. */
    private static final int[] WEBDAV_PORTS = {5006, 5005, 8443, 8010, 8011, 8000, 5244, 8080, 443, 80};
    private static final int TCP_PROBE_TIMEOUT_MS = 400;
    private static final int PROPFIND_TIMEOUT_MS = 1200;
    private static final int NB_QUERY_TIMEOUT_MS = 900;
    private static final int MAX_HOSTS = 512;
    private static final long GLOBAL_DEADLINE_MS = 45000L;
    private static final int MAX_WEBDAV_PER_HOST = 2;
    private static final String PROPFIND_BODY =
            "<propfind xmlns='DAV:'><prop><resourcetype/></prop></propfind>";

    private static final Object tokenLock = new Object();
    private static int nextToken = 1;
    private static volatile int activeToken = 0;
    private static java.lang.ref.WeakReference<android.content.Context> appContext =
            new java.lang.ref.WeakReference<>(null);

    private LanDiscovery() {
    }

    /** Starts a scan on the given pool; returns a token that identifies this scan. */
    public static int start(android.content.Context context, ExecutorService pool, Listener listener) {
        appContext = new java.lang.ref.WeakReference<>(
                context == null ? null : context.getApplicationContext());
        int token;
        synchronized (tokenLock) {
            token = nextToken++;
            activeToken = token;
        }
        Thread runner = new Thread(() -> run(pool, token, listener), "omniplay-lan-discovery");
        runner.setDaemon(true);
        runner.start();
        return token;
    }

    /** Aborts the scan identified by the token (no-op when it is no longer active). */
    public static void cancel(int token) {
        synchronized (tokenLock) {
            if (activeToken == token) {
                activeToken = -token;
            }
        }
    }

    private static boolean isActive(int token) {
        return token == activeToken;
    }

    private static void run(ExecutorService pool, int token, Listener listener) {
        try {
            scan(pool, token, listener);
        } catch (Throwable error) {
            // Never leave the setup screen waiting on a scan thread that already died.
            CrashLog.record(appContext.get(), "局域网扫描", error);
            if (isActive(token)) {
                try {
                    listener.onDone(new ArrayList<>());
                } catch (Throwable ignored) {
                }
            }
        }
    }

    private static void scan(ExecutorService pool, int token, Listener listener) {
        Map<String, LanDevice> devices = new LinkedHashMap<>();
        List<InetAddress> hosts = enumerateSubnetHosts();
        if (hosts.isEmpty()) {
            listener.onDone(new ArrayList<>());
            return;
        }
        // Machine names come from the NetBIOS task and are reused for WebDAV labels.
        Map<String, String> namesByIp = new ConcurrentHashMap<>();

        ExecutorCompletionService<List<LanDevice>> results = new ExecutorCompletionService<>(pool);
        int submitted = 0;
        for (InetAddress host : hosts) {
            if (!isActive(token)) {
                return;
            }
            // One task per host+port: a host that drops packets must not hold up the other
            // ports of the same host, which is what used to starve the WebDAV probing.
            results.submit(() -> probePort(host, PORT_SMB, namesByIp));
            submitted++;
            for (int port : WEBDAV_PORTS) {
                results.submit(() -> probePort(host, port, namesByIp));
                submitted++;
            }
        }

        long deadline = System.currentTimeMillis() + GLOBAL_DEADLINE_MS;
        Map<String, Integer> webDavPerHost = new LinkedHashMap<>();
        for (int index = 0; index < submitted; index++) {
            long remaining = deadline - System.currentTimeMillis();
            if (remaining <= 0 || !isActive(token)) {
                break;
            }
            List<LanDevice> found;
            try {
                Future<List<LanDevice>> future = results.poll(remaining, TimeUnit.MILLISECONDS);
                if (future == null) {
                    break;
                }
                found = future.get();
            } catch (Throwable error) {
                continue;
            }
            if (found == null || found.isEmpty()) {
                continue;
            }
            for (LanDevice device : found) {
                if (device.kind.equals("webdav")) {
                    int seen = webDavPerHost.getOrDefault(device.ip, 0);
                    if (seen >= MAX_WEBDAV_PER_HOST) {
                        continue;
                    }
                    webDavPerHost.put(device.ip, seen + 1);
                }
                String machine = namesByIp.get(device.ip);
                if (machine != null && device.name.equals(device.ip)) {
                    device = new LanDevice(device.kind, device.ip, device.port, machine, device.url);
                }
                if (devices.putIfAbsent(device.kind + ":" + device.url, device) == null) {
                    listener.onDevice(device);
                }
            }
        }
        if (isActive(token)) {
            listener.onDone(new ArrayList<>(devices.values()));
        }
    }

    private static List<InetAddress> enumerateSubnetHosts() {
        Set<String> seen = new LinkedHashSet<>();
        List<InetAddress> hosts = new ArrayList<>();
        try {
            Enumeration<NetworkInterface> interfaces = NetworkInterface.getNetworkInterfaces();
            while (interfaces != null && interfaces.hasMoreElements()) {
                NetworkInterface iface = interfaces.nextElement();
                if (!iface.isUp() || iface.isLoopback() || iface.isVirtual()) {
                    continue;
                }
                List<InterfaceAddress> addresses;
                try {
                    addresses = iface.getInterfaceAddresses();
                } catch (Exception error) {
                    continue;
                }
                for (InterfaceAddress address : addresses) {
                    InetAddress inet = address.getAddress();
                    if (!(inet instanceof Inet4Address) || !isPrivateIp(inet.getHostAddress())) {
                        continue;
                    }
                    int[] networkRange = networkRange(address);
                    if (networkRange == null) {
                        continue;
                    }
                    for (int value = networkRange[0] + 1; value < networkRange[1]; value++) {
                        if (hosts.size() >= MAX_HOSTS) {
                            return hosts;
                        }
                        String candidate = fromInt(value);
                        if (candidate.equals(inet.getHostAddress())) {
                            continue;
                        }
                        if (seen.add(candidate)) {
                            try {
                                hosts.add(InetAddress.getByName(candidate));
                            } catch (Exception ignored) {
                            }
                        }
                    }
                }
            }
        } catch (Exception ignored) {
        }
        return hosts;
    }

    private static boolean isPrivateIp(String ip) {
        int[] parts = ipParts(ip);
        if (parts == null) {
            return false;
        }
        if (parts[0] == 10) {
            return true;
        }
        if (parts[0] == 172 && parts[1] >= 16 && parts[1] <= 31) {
            return true;
        }
        return parts[0] == 192 && parts[1] == 168;
    }

    private static int[] networkRange(InterfaceAddress address) {
        InetAddress inet = address.getAddress();
        int prefix = address.getNetworkPrefixLength();
        byte[] maskBytes = prefix > 0 ? maskFromPrefix(prefix) : null;
        int[] ipParts = ipParts(inet.getHostAddress());
        if (ipParts == null || maskBytes == null) {
            return null;
        }
        int ip = ipParts[0] << 24 | ipParts[1] << 16 | ipParts[2] << 8 | ipParts[3];
        int mask = (maskBytes[0] & 0xFF) << 24 | (maskBytes[1] & 0xFF) << 16
                | (maskBytes[2] & 0xFF) << 8 | (maskBytes[3] & 0xFF);
        if (mask == 0) {
            return null;
        }
        int network = ip & mask;
        int broadcast = network | ~mask;
        if (broadcast - network > 4096) {
            return null;
        }
        return new int[]{network, broadcast};
    }

    private static byte[] maskFromPrefix(int prefix) {
        if (prefix <= 0 || prefix > 32) {
            return null;
        }
        int mask = prefix == 32 ? -1 : (-1 << (32 - prefix));
        return new byte[]{
                (byte) (mask >>> 24), (byte) (mask >>> 16), (byte) (mask >>> 8), (byte) mask
        };
    }

    private static int[] ipParts(String ip) {
        String[] parts = ip.split("\\.");
        if (parts.length != 4) {
            return null;
        }
        try {
            int[] result = new int[4];
            for (int i = 0; i < 4; i++) {
                result[i] = Integer.parseInt(parts[i]);
                if (result[i] < 0 || result[i] > 255) {
                    return null;
                }
            }
            return result;
        } catch (NumberFormatException error) {
            return null;
        }
    }

    private static String fromInt(int value) {
        return ((value >>> 24) & 0xFF) + "." + ((value >>> 16) & 0xFF) + "."
                + ((value >>> 8) & 0xFF) + "." + (value & 0xFF);
    }

    /** Probes a single host:port pair and returns the device it implies, possibly none. */
    private static List<LanDevice> probePort(InetAddress host, int port, Map<String, String> namesByIp) {
        String ip = host.getHostAddress();
        if (!tcpOpen(host, port)) {
            return Collections.emptyList();
        }
        if (port == PORT_SMB) {
            String name = queryNetBiosName(host);
            if (name != null && !name.isEmpty()) {
                namesByIp.put(ip, name);
            }
            String shown = name == null || name.isEmpty() ? ip : name;
            return Collections.singletonList(new LanDevice("smb", ip, port, shown, "smb://" + ip));
        }
        String scheme = tlsPort(port) ? "https" : "http";
        for (String path : webdavProbePaths(port)) {
            Detection detection = detectWebDav(scheme + "://" + ip + ":" + port + path, port);
            if (detection == Detection.WEBDAV) {
                String machine = namesByIp.get(ip);
                String base = scheme + "://" + ip + ":" + port
                        + (path.equals("/") ? "" : path.substring(0, path.length() - 1));
                return Collections.singletonList(
                        new LanDevice("webdav", ip, port, machine == null ? ip : machine, base));
            }
            if (detection == Detection.UNREACHABLE) {
                // Nothing is speaking HTTP on this port; other paths will not do better.
                break;
            }
        }
        return Collections.emptyList();
    }

    private enum Detection { WEBDAV, NOT_WEBDAV, UNREACHABLE }

    /**
     * A port counts as WebDAV when OPTIONS advertises the DAV header, when a PROPFIND answers
     * 207, or when a locked (401 / 403) reply comes back on a port that only serves WebDAV;
     * shared ports such as 80 / 443 / 8080 need the header or a real 207 because plain web apps
     * answer 401 and 405 there all the time. UNREACHABLE is reserved for ports that answer
     * nothing at all, on either scheme, so the other candidate paths are not abandoned just
     * because one path is not a DAV root.
     */
    private static Detection detectWebDav(String url, int port) {
        Answer options = exchange(url, "OPTIONS", null);
        String flipped = null;
        if (options == null) {
            flipped = flipScheme(url);
            options = flipped == null ? null : exchange(flipped, "OPTIONS", null);
            if (options == null) {
                return Detection.UNREACHABLE;   // no HTTP answer on either scheme
            }
        }
        if (options.advertisesDav()) {
            return Detection.WEBDAV;
        }
        if (plausibleLockedRoot(options.status, port)) {
            return Detection.WEBDAV;
        }
        Answer propfind = exchange(url, "PROPFIND", PROPFIND_BODY);
        if (propfind == null) {
            propfind = flipped == null ? null : exchange(flipped, "PROPFIND", PROPFIND_BODY);
            if (propfind == null) {
                // The port does speak HTTP, this path is simply not a DAV root, so other
                // candidate paths on the same port are still worth a try.
                return Detection.NOT_WEBDAV;
            }
        }
        if (propfind.advertisesDav() || propfind.status == 207) {
            return Detection.WEBDAV;
        }
        return plausibleLockedRoot(propfind.status, port) ? Detection.WEBDAV : Detection.NOT_WEBDAV;
    }

    /**
     * Shared ports answer 401 for ordinary web apps, so a locked reply only counts as WebDAV on
     * the ports a NAS uses exclusively for WebDAV. On 80 / 443 / 8080 the user can still type the
     * address by hand; guessing there would list half the LAN.
     */
    private static boolean plausibleLockedRoot(int status, int port) {
        if (status != 401 && status != 403) {
            return false;
        }
        return port != 80 && port != 443 && port != 8080;
    }

    private static boolean tlsPort(int port) {
        return port == 443 || port == 5006 || port == 8443 || port == 8011;
    }

    private static String flipScheme(String url) {
        if (url.startsWith("https://")) {
            return "http://" + url.substring("https://".length());
        }
        return url.startsWith("http://") ? "https://" + url.substring("http://".length()) : null;
    }

    private static String[] webdavProbePaths(int port) {
        if (port == 80 || port == 443 || port == 8443 || port == 8080) {
            return new String[]{"/", "/webdav/", "/dav/", "/remote.php/dav/files/"};
        }
        return new String[]{"/", "/webdav/", "/dav/"};
    }

    private static boolean tcpOpen(InetAddress host, int port) {
        try (Socket socket = new Socket()) {
            socket.connect(new java.net.InetSocketAddress(host, port), TCP_PROBE_TIMEOUT_MS);
            return true;
        } catch (Exception error) {
            return false;
        }
    }

    /** Best-effort NetBIOS workstation name query; returns null on any failure. */
    private static String queryNetBiosName(InetAddress host) {
        byte[] queryName;
        try {
            queryName = new byte[15];
            for (int i = 0; i < 15; i++) {
                char c = i < "OMNIPLAYPROBE".length() ? "OMNIPLAYPROBE".charAt(i) : ' ';
                queryName[i] = (byte) (c | 0x80);
            }
        } catch (Exception ignored) {
            return null;
        }
        byte[] packet = new byte[33];
        packet[5] = 1; // one question
        System.arraycopy(queryName, 0, packet, 12, 15);
        packet[27] = 0x20; // workstation suffix
        packet[30] = 0x20; // question type
        packet[32] = 0x01; // question class
        int hostInt = hostToInt(host);
        try (DatagramSocket socket = new DatagramSocket()) {
            socket.setSoTimeout(NB_QUERY_TIMEOUT_MS);
            socket.setBroadcast(true);
            socket.send(new DatagramPacket(packet, packet.length, host, 137));
            byte[] buffer = new byte[256];
            long deadline = System.currentTimeMillis() + NB_QUERY_TIMEOUT_MS;
            while (System.currentTimeMillis() < deadline) {
                DatagramPacket response = new DatagramPacket(buffer, buffer.length);
                socket.receive(response);
                byte[] data = response.getData();
                int length = response.getLength();
                int sentinel = -1;
                for (int i = 0; i + 1 < length; i++) {
                    if (data[i] == 0x00 && data[i + 1] == (byte) 0xFC) {
                        sentinel = i;
                        break;
                    }
                }
                if (sentinel < 8 || hostInt == -1) {
                    continue;
                }
                int ipInt = (data[sentinel - 4] & 0xFF) << 24 | (data[sentinel - 3] & 0xFF) << 16
                        | (data[sentinel - 2] & 0xFF) << 8 | (data[sentinel - 1] & 0xFF);
                if (ipInt != hostInt) {
                    continue;
                }
                if (length >= 47 && data[44] == 0x00 && data[45] == 0x10) {
                    // 32-byte DNS name area: two bytes per 7-bit character.
                    StringBuilder builder = new StringBuilder();
                    for (int i = 0; i < 15; i++) {
                        int high = data[12 + i * 2] & 0x7F;
                        int low = data[13 + i * 2] & 0x0F;
                        int c = (high << 4) | low;
                        if (c >= ' ' && c < 0x7F) {
                            builder.append((char) c);
                        }
                    }
                    String name = builder.toString().trim();
                    return name.isEmpty() ? null : name;
                }
                // Compact 33-byte layout: one byte per character at offset 12.
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < 15; i++) {
                    int c = data[12 + i] & 0x7F;
                    if (c >= ' ' && c < 0x7F) {
                        builder.append((char) c);
                    }
                }
                String name = builder.toString().trim();
                return name.isEmpty() ? null : name;
            }
        } catch (Exception ignored) {
        }
        return null;
    }

    private static int hostToInt(InetAddress host) {
        byte[] bytes = host.getAddress();
        if (bytes.length != 4) {
            return -1;
        }
        return (bytes[0] & 0xFF) << 24 | (bytes[1] & 0xFF) << 16 | (bytes[2] & 0xFF) << 8 | (bytes[3] & 0xFF);
    }

    /** One HTTP exchange used by the WebDAV detection; null means "no answer at all". */
    private static final class Answer {
        final int status;
        final String davHeader;

        Answer(int status, String davHeader) {
            this.status = status;
            this.davHeader = davHeader;
        }

        boolean advertisesDav() {
            return davHeader != null && !davHeader.trim().isEmpty();
        }
    }

    /**
     * Sends {@code method} against {@code url} over the shared raw-socket client: Android's
     * HttpURLConnection refuses PROPFIND outright, and HTTPS has to be probed with a trust-all
     * factory (this only decides what to offer the user; the real connection still validates).
     */
    private static Answer exchange(String url, String method, String body) {
        try {
            Map<String, String> headers = new LinkedHashMap<>();
            headers.put("Depth", "1");
            headers.put("Accept", "application/xml");
            Http.Response response = Http.request(url, method, headers,
                    body == null ? null : body.getBytes(StandardCharsets.UTF_8),
                    PROPFIND_TIMEOUT_MS);
            return new Answer(response.status, response.header("dav"));
        } catch (Exception error) {
            return null;
        } finally {
            // One connection per probe, closed inside Http.request.
        }
    }

}
