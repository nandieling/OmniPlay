using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace OmniPlay.Player.Mpv;

internal sealed record RemoteIsoProxyRegistration(
    IReadOnlyList<string> Urls,
    IReadOnlyList<string> RouteIds);

internal sealed class RemoteIsoStreamProxy
{
    private const int SectorSize = 2048;
    private const int ReadChunkSize = 512 * 1024;
    private const int MaximumDirectoryBytes = 64 * 1024 * 1024;

    public static RemoteIsoStreamProxy Shared { get; } = new();

    private readonly object stateGate = new();
    private readonly HttpClient httpClient;
    private readonly Dictionary<string, RemoteIsoRoute> routes = new(StringComparer.Ordinal);
    private TcpListener? listener;
    private CancellationTokenSource? listenerCancellation;
    private Task? listenerTask;

    private RemoteIsoStreamProxy()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None
        };
        httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("OmniPlay-Windows/1.0");
    }

    public async Task<RemoteIsoProxyRegistration> PrepareAsync(
        string sourceUrl,
        bool includeExtras = false,
        CancellationToken cancellationToken = default)
    {
        var reader = new RemoteIsoHttpByteReader(httpClient, sourceUrl);
        var isoSize = await reader.ContentLengthAsync(cancellationToken).ConfigureAwait(false);
        var streamFiles = await new RemoteIsoImageParser(reader, isoSize)
            .BluRayStreamFilesAsync(cancellationToken)
            .ConfigureAwait(false);
        if (streamFiles.Count == 0)
        {
            throw new InvalidDataException("远程 ISO 内未找到 BDMV/STREAM 视频流。");
        }

        var candidates = streamFiles
            .Select(static file => new BluRayStreamCandidate(file.FileName, file.Size))
            .ToList();
        var selectedIndexes = SelectBluRayStreamIndices(candidates, includeExtras);
        var selectedFiles = selectedIndexes
            .Select(index => streamFiles[index])
            .OrderBy(static file => BluRayStreamSortKey(file.FileName).Number)
            .ThenBy(static file => BluRayStreamSortKey(file.FileName).Name, StringComparer.Ordinal)
            .ToList();
        if (selectedFiles.Count == 0)
        {
            throw new InvalidDataException("远程 ISO 内没有可播放的视频流。");
        }

        return Register(selectedFiles, reader);
    }

    public void Unregister(IEnumerable<string> routeIds)
    {
        lock (stateGate)
        {
            foreach (var routeId in routeIds)
            {
                routes.Remove(routeId);
            }
        }
    }

    public void Clear()
    {
        lock (stateGate)
        {
            routes.Clear();
        }
    }

    private RemoteIsoProxyRegistration Register(
        IReadOnlyList<RemoteIsoFile> files,
        RemoteIsoHttpByteReader reader)
    {
        var port = EnsureListener();
        var routeIds = new List<string>(files.Count);
        var urls = new List<string>(files.Count);

        lock (stateGate)
        {
            foreach (var file in files)
            {
                var routeId = Guid.NewGuid().ToString("N");
                routes[routeId] = new RemoteIsoRoute(reader, file);
                routeIds.Add(routeId);
                urls.Add($"http://127.0.0.1:{port}/remoteiso/{routeId}/{Uri.EscapeDataString(file.FileName)}");
            }
        }

        return new RemoteIsoProxyRegistration(urls, routeIds);
    }

    private int EnsureListener()
    {
        lock (stateGate)
        {
            if (listener is not null)
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }

            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var cancellation = new CancellationTokenSource();
            listenerCancellation = cancellation;
            listenerTask = AcceptLoopAsync(listener, cancellation.Token);
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private async Task AcceptLoopAsync(TcpListener tcpListener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await tcpListener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleClientAsync(client);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 30_000;
                client.SendTimeout = 60_000;
                await using var stream = client.GetStream();
                using var requestReader = new StreamReader(
                    stream,
                    Encoding.ASCII,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 4096,
                    leaveOpen: true);

                var requestLine = await requestReader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return;
                }

                var requestParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (requestParts.Length < 2)
                {
                    await SendErrorAsync(stream, 400, "Bad Request").ConfigureAwait(false);
                    return;
                }

                var method = requestParts[0].ToUpperInvariant();
                if (method is not ("GET" or "HEAD"))
                {
                    await SendErrorAsync(stream, 405, "Method Not Allowed").ConfigureAwait(false);
                    return;
                }

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (true)
                {
                    var line = await requestReader.ReadLineAsync().ConfigureAwait(false);
                    if (line is null || line.Length == 0)
                    {
                        break;
                    }

                    var separator = line.IndexOf(':');
                    if (separator > 0)
                    {
                        headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                    }
                }

                var requestPath = requestParts[1].Split('?', 2)[0];
                var pathParts = requestPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (pathParts.Length < 2 ||
                    !string.Equals(pathParts[0], "remoteiso", StringComparison.OrdinalIgnoreCase))
                {
                    await SendErrorAsync(stream, 404, "Not Found").ConfigureAwait(false);
                    return;
                }

                RemoteIsoRoute? route;
                lock (stateGate)
                {
                    routes.TryGetValue(pathParts[1], out route);
                }

                if (route is null)
                {
                    await SendErrorAsync(stream, 404, "Not Found").ConfigureAwait(false);
                    return;
                }

                if (!TryParseByteRange(
                        headers.GetValueOrDefault("Range"),
                        route.File.Size,
                        out var range,
                        out var hasRange))
                {
                    await SendErrorAsync(stream, 416, "Range Not Satisfiable").ConfigureAwait(false);
                    return;
                }

                await SendHeadersAsync(stream, hasRange ? 206 : 200, route.File.Size, range)
                    .ConfigureAwait(false);
                if (method == "HEAD")
                {
                    return;
                }

                long sent = 0;
                while (sent < range.Length)
                {
                    var chunkLength = (int)Math.Min(ReadChunkSize, range.Length - sent);
                    var data = await route.Reader
                        .ReadFileAsync(route.File, range.Offset + sent, chunkLength, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (data.Length == 0)
                    {
                        break;
                    }

                    await stream.WriteAsync(data).ConfigureAwait(false);
                    sent += data.Length;
                    if (data.Length < chunkLength)
                    {
                        break;
                    }
                }

                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static bool TryParseByteRange(
        string? header,
        long fileSize,
        out RemoteIsoByteRange range,
        out bool hasRange)
    {
        range = default;
        hasRange = !string.IsNullOrWhiteSpace(header);
        if (fileSize <= 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(header))
        {
            range = new RemoteIsoByteRange(0, fileSize);
            return true;
        }

        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var firstRange = header[6..].Split(',', 2)[0].Trim();
        var bounds = firstRange.Split('-', 2);
        if (bounds.Length != 2)
        {
            return false;
        }

        if (bounds[0].Length == 0)
        {
            if (!long.TryParse(bounds[1], out var suffixLength) || suffixLength <= 0)
            {
                return false;
            }

            var length = Math.Min(suffixLength, fileSize);
            range = new RemoteIsoByteRange(fileSize - length, length);
            return true;
        }

        if (!long.TryParse(bounds[0], out var start) || start < 0 || start >= fileSize)
        {
            return false;
        }

        long end;
        if (bounds[1].Length == 0)
        {
            end = fileSize - 1;
        }
        else if (!long.TryParse(bounds[1], out end))
        {
            return false;
        }

        end = Math.Min(end, fileSize - 1);
        if (end < start)
        {
            return false;
        }

        range = new RemoteIsoByteRange(start, end - start + 1);
        return true;
    }

    private static async Task SendHeadersAsync(
        NetworkStream stream,
        int status,
        long fileSize,
        RemoteIsoByteRange range)
    {
        var statusText = status == 206 ? "Partial Content" : "OK";
        var builder = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(statusText).Append("\r\n")
            .Append("Content-Type: video/MP2T\r\n")
            .Append("Accept-Ranges: bytes\r\n")
            .Append("Content-Length: ").Append(range.Length).Append("\r\n");
        if (status == 206)
        {
            builder.Append("Content-Range: bytes ")
                .Append(range.Offset)
                .Append('-')
                .Append(range.Offset + range.Length - 1)
                .Append('/')
                .Append(fileSize)
                .Append("\r\n");
        }

        builder.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString())).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static async Task SendErrorAsync(NetworkStream stream, int status, string message)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var headers = $"HTTP/1.1 {status} {message}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers)).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static IReadOnlyList<int> SelectBluRayStreamIndices(
        IReadOnlyList<BluRayStreamCandidate> candidates,
        bool includeExtras)
    {
        var ordered = Enumerable.Range(0, candidates.Count)
            .OrderBy(index => BluRayStreamSortKey(candidates[index].FileName).Number)
            .ThenBy(index => BluRayStreamSortKey(candidates[index].FileName).Name, StringComparer.Ordinal)
            .ToList();
        if (includeExtras || ordered.Count == 0)
        {
            return ordered;
        }

        var known = ordered
            .Where(index => candidates[index].FileSize > 0)
            .ToList();
        if (known.Count == 0)
        {
            return [ordered[0]];
        }

        var bySize = known
            .OrderByDescending(index => candidates[index].FileSize)
            .ThenBy(index => BluRayStreamSortKey(candidates[index].FileName).Number)
            .ThenBy(index => BluRayStreamSortKey(candidates[index].FileName).Name, StringComparer.Ordinal)
            .ToList();
        var largest = bySize[0];
        if (bySize.Count == 1)
        {
            return [largest];
        }

        var secondSize = candidates[bySize[1]].FileSize;
        if (secondSize <= 0 || candidates[largest].FileSize >= secondSize * 1.55)
        {
            return [largest];
        }

        var threshold = candidates[largest].FileSize * 0.72;
        var cluster = ordered
            .Where(index => candidates[index].FileSize >= threshold)
            .ToList();
        return cluster.Count == 0 ? [largest] : cluster;
    }

    private static (int Number, string Name) BluRayStreamSortKey(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).Trim();
        var number = int.TryParse(stem, out var parsed) ? parsed : int.MaxValue;
        return (number, fileName.ToLowerInvariant());
    }

    private readonly record struct RemoteIsoByteRange(long Offset, long Length);

    private readonly record struct BluRayStreamCandidate(string FileName, long FileSize);

    private sealed record RemoteIsoRoute(
        RemoteIsoHttpByteReader Reader,
        RemoteIsoFile File);

    private sealed class RemoteIsoHttpByteReader
    {
        private readonly HttpClient client;
        private readonly Uri sourceUri;
        private readonly string? authorizationHeader;

        public RemoteIsoHttpByteReader(HttpClient client, string sourceUrl)
        {
            this.client = client;
            if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var resolvedUri) ||
                (resolvedUri.Scheme != Uri.UriSchemeHttp && resolvedUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidDataException("远程 ISO 播放地址不是有效的 HTTP URL。");
            }

            var userInfo = resolvedUri.UserInfo;
            if (!string.IsNullOrWhiteSpace(userInfo))
            {
                var separator = userInfo.IndexOf(':');
                var rawUsername = separator >= 0 ? userInfo[..separator] : userInfo;
                var rawPassword = separator >= 0 ? userInfo[(separator + 1)..] : string.Empty;
                var username = Uri.UnescapeDataString(rawUsername);
                var password = Uri.UnescapeDataString(rawPassword);
                authorizationHeader = "Basic " + Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{username}:{password}"));

                var withoutUserInfo = new UriBuilder(resolvedUri)
                {
                    UserName = string.Empty,
                    Password = string.Empty
                };
                sourceUri = withoutUserInfo.Uri;
            }
            else
            {
                authorizationHeader = null;
                sourceUri = resolvedUri;
            }
        }

        public async Task<long> ContentLengthAsync(CancellationToken cancellationToken)
        {
            using (var head = new HttpRequestMessage(HttpMethod.Head, sourceUri))
            {
                ApplyAuthorization(head);
                using var response = await client.SendAsync(
                    head,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 200 and < 300 &&
                    response.Content.Headers.ContentLength is > 0 and var length)
                {
                    return length;
                }
            }

            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, sourceUri);
            rangeRequest.Headers.Range = new RangeHeaderValue(0, 0);
            ApplyAuthorization(rangeRequest);
            using var rangeResponse = await client.SendAsync(
                rangeRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            var total = rangeResponse.Content.Headers.ContentRange?.Length;
            if (total is > 0)
            {
                return total.Value;
            }

            if (rangeResponse.Content.Headers.ContentLength is > 1 and var fallbackLength)
            {
                return fallbackLength;
            }

            throw new InvalidDataException("无法读取远程 ISO 文件大小；服务端必须提供 Content-Length 或 Content-Range。");
        }

        public async Task<byte[]> ReadAsync(long offset, int length, CancellationToken cancellationToken)
        {
            if (length <= 0)
            {
                return [];
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
            request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1L);
            ApplyAuthorization(request);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.PartialContent &&
                (response.StatusCode != HttpStatusCode.OK || offset != 0))
            {
                throw new InvalidDataException($"远程 ISO Range 请求失败：{(int)response.StatusCode}。");
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await ReadAtMostAsync(input, length, cancellationToken).ConfigureAwait(false);
        }

        private void ApplyAuthorization(HttpRequestMessage request)
        {
            if (!string.IsNullOrWhiteSpace(authorizationHeader))
            {
                request.Headers.TryAddWithoutValidation("Authorization", authorizationHeader);
            }
        }

        public async Task<byte[]> ReadFileAsync(
            RemoteIsoFile file,
            long offset,
            int length,
            CancellationToken cancellationToken)
        {
            if (length <= 0 || offset >= file.Size)
            {
                return [];
            }

            var remaining = Math.Min((long)length, file.Size - offset);
            var skip = offset;
            using var output = new MemoryStream((int)Math.Min(remaining, length));
            foreach (var extent in file.Extents)
            {
                if (skip >= extent.Length)
                {
                    skip -= extent.Length;
                    continue;
                }

                var extentOffset = extent.Offset + skip;
                var extentRemaining = extent.Length - skip;
                skip = 0;
                while (remaining > 0 && extentRemaining > 0)
                {
                    var chunkLength = (int)Math.Min(Math.Min(remaining, extentRemaining), ReadChunkSize);
                    var data = await ReadAsync(extentOffset, chunkLength, cancellationToken).ConfigureAwait(false);
                    if (data.Length == 0)
                    {
                        return output.ToArray();
                    }

                    await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                    extentOffset += data.Length;
                    extentRemaining -= data.Length;
                    remaining -= data.Length;
                    if (data.Length < chunkLength)
                    {
                        return output.ToArray();
                    }
                }

                if (remaining <= 0)
                {
                    break;
                }
            }

            return output.ToArray();
        }

        private static async Task<byte[]> ReadAtMostAsync(
            Stream input,
            int maxLength,
            CancellationToken cancellationToken)
        {
            using var output = new MemoryStream(maxLength);
            var buffer = new byte[Math.Min(maxLength, 64 * 1024)];
            while (output.Length < maxLength)
            {
                var requested = (int)Math.Min(buffer.Length, maxLength - output.Length);
                var read = await input.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return output.ToArray();
        }
    }

    private sealed record RemoteIsoExtent(long Offset, long Length);

    private sealed record RemoteIsoFile(
        string Path,
        string FileName,
        long Size,
        IReadOnlyList<RemoteIsoExtent> Extents);

    private sealed class RemoteIsoImageParser
    {
        private readonly RemoteIsoHttpByteReader reader;
        private readonly long isoSize;
        private long logicalBlockSize = SectorSize;
        private readonly Dictionary<int, UdfPartition> partitionsByNumber = new();
        private readonly Dictionary<int, UdfPartition> partitionsByReference = new();
        private readonly Dictionary<int, UdfMetadataPartitionMap> metadataPartitionMapsByReference = new();
        private readonly Dictionary<int, IReadOnlyList<RemoteIsoExtent>> metadataExtentsByReference = new();

        public RemoteIsoImageParser(RemoteIsoHttpByteReader reader, long isoSize)
        {
            this.reader = reader;
            this.isoSize = isoSize;
        }

        public async Task<IReadOnlyList<RemoteIsoFile>> BluRayStreamFilesAsync(CancellationToken cancellationToken)
        {
            try
            {
                var udfFiles = await ParseUdfBluRayStreamFilesAsync(cancellationToken).ConfigureAwait(false);
                if (udfFiles.Count > 0)
                {
                    return udfFiles;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
            }

            var isoFiles = await ParseIso9660BluRayStreamFilesAsync(cancellationToken).ConfigureAwait(false);
            if (isoFiles.Count == 0)
            {
                throw new InvalidDataException("远程 ISO 内未找到 BDMV/STREAM 视频流。");
            }

            return isoFiles;
        }

        private async Task<IReadOnlyList<RemoteIsoFile>> ParseUdfBluRayStreamFilesAsync(
            CancellationToken cancellationToken)
        {
            partitionsByNumber.Clear();
            partitionsByReference.Clear();
            metadataPartitionMapsByReference.Clear();
            metadataExtentsByReference.Clear();

            var anchor = await FindUdfAnchorAsync(cancellationToken).ConfigureAwait(false);
            var mainLength = ReadUInt32LE(anchor, 16);
            var mainLocation = ReadUInt32LE(anchor, 20);
            if (mainLength <= 0 || mainLocation <= 0)
            {
                throw new InvalidDataException("UDF 主描述区无效。");
            }

            UdfLongAd? fileSetAd = null;
            var partitionMap = new Dictionary<int, int>();
            var descriptorSectors = Math.Min(
                ((ulong)mainLength + SectorSize - 1) / SectorSize,
                4096UL);
            for (ulong sectorOffset = 0; sectorOffset < descriptorSectors; sectorOffset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var descriptor = await ReadSectorAsync(
                        checked((long)mainLocation + checked((long)sectorOffset)),
                        cancellationToken)
                    .ConfigureAwait(false);
                var tag = ReadUInt16LE(descriptor, 0);
                if (tag == 8)
                {
                    break;
                }

                if (tag == 5)
                {
                    var number = ReadUInt16LE(descriptor, 22);
                    var start = ReadUInt32LE(descriptor, 188);
                    var length = ReadUInt32LE(descriptor, 192);
                    partitionsByNumber[number] = new UdfPartition(number, start, length);
                }
                else if (tag == 6)
                {
                    var blockSize = ReadUInt32LE(descriptor, 212);
                    if (blockSize > 0)
                    {
                        logicalBlockSize = checked((long)blockSize);
                    }

                    fileSetAd = ParseLongAd(descriptor, 248);
                    partitionMap = ParsePartitionMaps(descriptor);
                }
            }

            foreach (var entry in partitionMap)
            {
                if (partitionsByNumber.TryGetValue(entry.Value, out var partition))
                {
                    partitionsByReference[entry.Key] = partition;
                }
            }

            if (partitionsByReference.Count == 0)
            {
                foreach (var partition in partitionsByNumber.Values.OrderBy(static value => value.Number).Select((value, index) => (value, index)))
                {
                    partitionsByReference[partition.index] = partition.value;
                }
            }

            await ResolveMetadataPartitionsAsync(cancellationToken).ConfigureAwait(false);
            if (fileSetAd is null)
            {
                throw new InvalidDataException("UDF 文件集描述符无效。");
            }

            var fileSetDescriptor = await ReadDescriptorAsync(fileSetAd, cancellationToken).ConfigureAwait(false);
            if (ReadUInt16LE(fileSetDescriptor, 0) != 256)
            {
                throw new InvalidDataException("UDF 文件集描述符无效。");
            }

            var rootIcb = ParseLongAd(fileSetDescriptor, 400);
            var streamIcb = await LocateUdfBluRayStreamDirectoryAsync(rootIcb, cancellationToken)
                .ConfigureAwait(false);
            var entries = await ReadUdfDirectoryAsync(streamIcb, cancellationToken).ConfigureAwait(false);
            var files = new List<RemoteIsoFile>();
            foreach (var entry in entries)
            {
                var lower = entry.Name.ToLowerInvariant();
                if (entry.IsDirectory ||
                    !(lower.EndsWith(".m2ts", StringComparison.Ordinal) ||
                      lower.EndsWith(".m2t", StringComparison.Ordinal) ||
                      lower.EndsWith(".ts", StringComparison.Ordinal)))
                {
                    continue;
                }

                var fileEntry = await ReadUdfFileEntryAsync(entry.Icb, cancellationToken).ConfigureAwait(false);
                var extents = LimitExtents(fileEntry.Extents, fileEntry.InformationLength);
                if (fileEntry.InformationLength > 0 && extents.Count > 0)
                {
                    files.Add(new RemoteIsoFile(
                        $"BDMV/STREAM/{entry.Name}",
                        entry.Name,
                        fileEntry.InformationLength,
                        extents));
                }
            }

            return files;
        }

        private async Task<byte[]> FindUdfAnchorAsync(CancellationToken cancellationToken)
        {
            var sectors = Math.Max(0, isoSize / SectorSize);
            var candidates = new[] { 256L, sectors - 256, sectors - 1 }
                .Where(static value => value >= 0)
                .Distinct();
            foreach (var sector in candidates)
            {
                var data = await ReadSectorAsync(sector, cancellationToken).ConfigureAwait(false);
                if (ReadUInt16LE(data, 0) == 2)
                {
                    return data;
                }
            }

            throw new InvalidDataException("未找到 UDF Anchor Volume Descriptor。");
        }

        private Dictionary<int, int> ParsePartitionMaps(byte[] descriptor)
        {
            var mapTableLength = checked((int)ReadUInt32LE(descriptor, 264));
            var mapCount = checked((int)ReadUInt32LE(descriptor, 268));
            var result = new Dictionary<int, int>();
            var offset = 440;
            for (var reference = 0; reference < mapCount; reference++)
            {
                if (offset + 2 > descriptor.Length || offset >= 440 + mapTableLength)
                {
                    break;
                }

                var type = descriptor[offset];
                var length = descriptor[offset + 1];
                if (length <= 0 || offset + length > descriptor.Length)
                {
                    break;
                }

                if (type == 1 && length >= 6)
                {
                    result[reference] = ReadUInt16LE(descriptor, offset + 4);
                }
                else if (type == 2 && length >= 59)
                {
                    var identifier = Encoding.ASCII.GetString(Slice(descriptor, offset + 5, 23));
                    if (identifier.Contains("UDF Metadata", StringComparison.OrdinalIgnoreCase))
                    {
                        metadataPartitionMapsByReference[reference] = new UdfMetadataPartitionMap(
                            ReadUInt16LE(descriptor, offset + 38),
                            ReadUInt32LE(descriptor, offset + 40),
                            ReadUInt32LE(descriptor, offset + 44));
                    }
                }

                offset += length;
            }

            return result;
        }

        private async Task ResolveMetadataPartitionsAsync(CancellationToken cancellationToken)
        {
            foreach (var entry in metadataPartitionMapsByReference)
            {
                var locations = new[]
                {
                    entry.Value.MetadataFileLocation,
                    entry.Value.MetadataMirrorFileLocation
                };
                foreach (var location in locations.Where(static value => value > 0))
                {
                    try
                    {
                        var metadataIcb = new UdfLongAd(logicalBlockSize, location, entry.Value.PhysicalPartitionNumber);
                        var fileEntry = await ReadUdfFileEntryAsync(metadataIcb, cancellationToken).ConfigureAwait(false);
                        var extents = LimitExtents(fileEntry.Extents, fileEntry.InformationLength);
                        if (extents.Count > 0)
                        {
                            metadataExtentsByReference[entry.Key] = extents;
                            break;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException)
                    {
                    }
                }
            }
        }

        private async Task<UdfLongAd> LocateUdfBluRayStreamDirectoryAsync(
            UdfLongAd rootIcb,
            CancellationToken cancellationToken)
        {
            try
            {
                return await TraverseUdfPathAsync(
                        rootIcb,
                        ["BDMV", "STREAM"],
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
            }

            var visitCounter = new VisitCounter();
            var found = await SearchUdfBluRayStreamDirectoryAsync(
                    rootIcb,
                    depth: 0,
                    visitCounter,
                    cancellationToken)
                .ConfigureAwait(false);
            return found ?? throw new InvalidDataException("UDF 内未找到 BDMV/STREAM。");
        }

        private async Task<UdfLongAd?> SearchUdfBluRayStreamDirectoryAsync(
            UdfLongAd directoryIcb,
            int depth,
            VisitCounter visited,
            CancellationToken cancellationToken)
        {
            if (depth > 4 || visited.Count >= 256)
            {
                return null;
            }

            visited.Count++;
            var entries = await ReadUdfDirectoryAsync(directoryIcb, cancellationToken).ConfigureAwait(false);
            var bdmv = entries.FirstOrDefault(entry =>
                entry.IsDirectory && string.Equals(entry.Name, "BDMV", StringComparison.OrdinalIgnoreCase));
            if (bdmv is not null)
            {
                var bdmvEntries = await ReadUdfDirectoryAsync(bdmv.Icb, cancellationToken).ConfigureAwait(false);
                var stream = bdmvEntries.FirstOrDefault(entry =>
                    entry.IsDirectory && string.Equals(entry.Name, "STREAM", StringComparison.OrdinalIgnoreCase));
                if (stream is not null)
                {
                    return stream.Icb;
                }
            }

            foreach (var entry in entries.Where(static entry => entry.IsDirectory))
            {
                var lower = entry.Name.ToLowerInvariant();
                if (lower is "certificate" or "any!")
                {
                    continue;
                }

                var found = await SearchUdfBluRayStreamDirectoryAsync(
                        entry.Icb,
                        depth + 1,
                        visited,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }

        private async Task<UdfLongAd> TraverseUdfPathAsync(
            UdfLongAd rootIcb,
            IReadOnlyList<string> components,
            CancellationToken cancellationToken)
        {
            var current = rootIcb;
            foreach (var component in components)
            {
                var entries = await ReadUdfDirectoryAsync(current, cancellationToken).ConfigureAwait(false);
                var next = entries.FirstOrDefault(entry =>
                    entry.IsDirectory && string.Equals(entry.Name, component, StringComparison.OrdinalIgnoreCase));
                if (next is null)
                {
                    throw new InvalidDataException($"UDF 路径不存在：{component}。");
                }

                current = next.Icb;
            }

            return current;
        }

        private async Task<IReadOnlyList<UdfDirectoryEntry>> ReadUdfDirectoryAsync(
            UdfLongAd icb,
            CancellationToken cancellationToken)
        {
            var fileEntry = await ReadUdfFileEntryAsync(icb, cancellationToken).ConfigureAwait(false);
            byte[] data;
            if (fileEntry.InlineData is not null)
            {
                data = fileEntry.InlineData;
            }
            else
            {
                var length = checked((int)Math.Min(fileEntry.InformationLength, MaximumDirectoryBytes));
                data = await ReadAbsoluteExtentsAsync(fileEntry.Extents, length, cancellationToken)
                    .ConfigureAwait(false);
            }

            var entries = new List<UdfDirectoryEntry>();
            var offset = 0;
            while (offset + 38 <= data.Length)
            {
                var tag = ReadUInt16LE(data, offset);
                if (tag != 257)
                {
                    offset += 4;
                    continue;
                }

                var characteristics = data[offset + 18];
                var nameLength = data[offset + 19];
                var entryIcb = ParseLongAd(data, offset + 20);
                var implementationUseLength = ReadUInt16LE(data, offset + 36);
                var nameOffset = offset + 38 + implementationUseLength;
                var entryLength = Align4(38 + implementationUseLength + nameLength);
                if (entryLength <= 0 || offset + entryLength > data.Length)
                {
                    break;
                }

                var name = DecodeOstaCompressedUnicode(Slice(data, nameOffset, nameLength));
                var isParent = (characteristics & 0x08) != 0;
                if (!string.IsNullOrWhiteSpace(name) && !isParent)
                {
                    entries.Add(new UdfDirectoryEntry(
                        name,
                        (characteristics & 0x02) != 0,
                        entryIcb));
                }

                offset += entryLength;
            }

            return entries;
        }

        private async Task<UdfFileEntry> ReadUdfFileEntryAsync(
            UdfLongAd icb,
            CancellationToken cancellationToken)
        {
            var descriptor = await ReadDescriptorAsync(icb, cancellationToken).ConfigureAwait(false);
            var tag = ReadUInt16LE(descriptor, 0);
            if (tag is not (261 or 266))
            {
                throw new InvalidDataException("UDF 文件条目无效。");
            }

            var informationLength = checked((long)ReadUInt64LE(descriptor, 56));
            var flags = ReadUInt16LE(descriptor, 34) & 0x0007;
            var lengthEaOffset = tag == 261 ? 168 : 208;
            var lengthAdOffset = tag == 261 ? 172 : 212;
            var allocationOffset = tag == 261 ? 176 : 216;
            var lengthEa = checked((int)ReadUInt32LE(descriptor, lengthEaOffset));
            var lengthAd = checked((int)ReadUInt32LE(descriptor, lengthAdOffset));
            var adData = Slice(descriptor, allocationOffset + lengthEa, lengthAd);
            if (flags == 3)
            {
                return new UdfFileEntry(adData.LongLength, [], adData);
            }

            var descriptorLength = flags switch
            {
                0 => 8,
                1 => 16,
                2 => 20,
                _ => 0
            };
            var extents = new List<RemoteIsoExtent>();
            for (var offset = 0; descriptorLength > 0 && offset + descriptorLength <= adData.Length; offset += descriptorLength)
            {
                var rawLength = ReadUInt32LE(adData, offset);
                var extentType = rawLength >> 30;
                var length = (long)(rawLength & 0x3fffffff);
                if (length <= 0 || extentType == 3)
                {
                    continue;
                }

                ulong block;
                int partitionRef;
                if (flags == 0)
                {
                    block = ReadUInt32LE(adData, offset + 4);
                    partitionRef = icb.PartitionRef;
                }
                else if (flags == 1)
                {
                    block = ReadUInt32LE(adData, offset + 4);
                    partitionRef = ReadUInt16LE(adData, offset + 8);
                }
                else
                {
                    block = ReadUInt32LE(adData, offset + 12);
                    partitionRef = ReadUInt16LE(adData, offset + 16);
                }

                if (TryGetAbsoluteOffset(checked((long)block), partitionRef, out var absoluteOffset))
                {
                    extents.Add(new RemoteIsoExtent(absoluteOffset, length));
                }
            }

            return new UdfFileEntry(informationLength, extents, null);
        }

        private UdfLongAd ParseLongAd(byte[] data, int offset)
        {
            var rawLength = ReadUInt32LE(data, offset);
            return new UdfLongAd(
                rawLength & 0x3fffffff,
                checked((long)ReadUInt32LE(data, offset + 4)),
                ReadUInt16LE(data, offset + 8));
        }

        private async Task<byte[]> ReadDescriptorAsync(UdfLongAd ad, CancellationToken cancellationToken)
        {
            if (!TryGetAbsoluteOffset(ad.Block, ad.PartitionRef, out var offset) ||
                logicalBlockSize <= 0 || logicalBlockSize > int.MaxValue)
            {
                throw new InvalidDataException("UDF 描述符位置无效。");
            }

            return await reader.ReadAsync((long)offset, (int)logicalBlockSize, cancellationToken)
                .ConfigureAwait(false);
        }

        private bool TryGetAbsoluteOffset(long block, int partitionRef, out long offset)
        {
            if (metadataExtentsByReference.TryGetValue(partitionRef, out var metadataExtents) &&
                TryGetOffsetInExtents(metadataExtents, checked(block * logicalBlockSize), out offset))
            {
                return true;
            }

            if (!partitionsByReference.TryGetValue(partitionRef, out var partition) &&
                !partitionsByNumber.TryGetValue(partitionRef, out partition))
            {
                partition = partitionsByReference.Values.FirstOrDefault();
            }

            if (partition is null)
            {
                offset = 0;
                return false;
            }

            offset = checked((partition.StartBlock + block) * logicalBlockSize);
            return offset >= 0 && offset < isoSize;
        }

        private static bool TryGetOffsetInExtents(
            IReadOnlyList<RemoteIsoExtent> extents,
            long fileOffset,
            out long offset)
        {
            var remaining = fileOffset;
            foreach (var extent in extents)
            {
                if (remaining < extent.Length)
                {
                    offset = checked(extent.Offset + remaining);
                    return true;
                }

                remaining -= extent.Length;
            }

            offset = 0;
            return false;
        }

        private async Task<byte[]> ReadAbsoluteExtentsAsync(
            IReadOnlyList<RemoteIsoExtent> extents,
            int maxLength,
            CancellationToken cancellationToken)
        {
            using var output = new MemoryStream(maxLength);
            var remaining = maxLength;
            foreach (var extent in extents)
            {
                if (remaining <= 0)
                {
                    break;
                }

                var extentOffset = extent.Offset;
                var extentRemaining = (int)Math.Min(remaining, extent.Length);
                while (extentRemaining > 0)
                {
                    var chunkLength = Math.Min(extentRemaining, ReadChunkSize);
                    var data = await reader.ReadAsync(extentOffset, chunkLength, cancellationToken)
                        .ConfigureAwait(false);
                    if (data.Length == 0)
                    {
                        return output.ToArray();
                    }

                    await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                    extentOffset += data.Length;
                    extentRemaining -= data.Length;
                    remaining -= data.Length;
                    if (data.Length < chunkLength)
                    {
                        return output.ToArray();
                    }
                }
            }

            return output.ToArray();
        }

        private static IReadOnlyList<RemoteIsoExtent> LimitExtents(
            IReadOnlyList<RemoteIsoExtent> extents,
            long length)
        {
            var remaining = length;
            var result = new List<RemoteIsoExtent>();
            foreach (var extent in extents)
            {
                if (remaining <= 0)
                {
                    break;
                }

                var resolvedLength = Math.Min(extent.Length, remaining);
                result.Add(new RemoteIsoExtent(extent.Offset, resolvedLength));
                remaining -= resolvedLength;
            }

            return result;
        }

        private async Task<IReadOnlyList<RemoteIsoFile>> ParseIso9660BluRayStreamFilesAsync(
            CancellationToken cancellationToken)
        {
            var pvd = await ReadSectorAsync(16, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(Encoding.ASCII.GetString(Slice(pvd, 1, 5)), "CD001", StringComparison.Ordinal))
            {
                return [];
            }

            var root = ParseIso9660DirectoryRecord(pvd, 156);
            if (root is null)
            {
                return [];
            }

            var stream = await LocateIso9660BluRayStreamDirectoryAsync(root, cancellationToken)
                .ConfigureAwait(false);
            var entries = await ReadIso9660DirectoryAsync(stream, cancellationToken).ConfigureAwait(false);
            return entries
                .Where(static entry =>
                    !entry.IsDirectory &&
                    (entry.Name.EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase) ||
                     entry.Name.EndsWith(".m2t", StringComparison.OrdinalIgnoreCase) ||
                     entry.Name.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)))
                .Select(static entry => new RemoteIsoFile(
                    $"BDMV/STREAM/{entry.Name}",
                    entry.Name,
                    entry.Size,
                    [new RemoteIsoExtent(checked(entry.Extent * SectorSize), entry.Size)]))
                .ToList();
        }

        private async Task<Iso9660Entry> LocateIso9660BluRayStreamDirectoryAsync(
            Iso9660Entry root,
            CancellationToken cancellationToken)
        {
            try
            {
                var bdmv = await FindIso9660EntryAsync(root, "BDMV", true, cancellationToken).ConfigureAwait(false);
                return await FindIso9660EntryAsync(bdmv, "STREAM", true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
            }

            var visitCounter = new VisitCounter();
            var found = await SearchIso9660BluRayStreamDirectoryAsync(root, 0, visitCounter, cancellationToken)
                .ConfigureAwait(false);
            return found ?? throw new InvalidDataException("ISO9660 内未找到 BDMV/STREAM。");
        }

        private async Task<Iso9660Entry?> SearchIso9660BluRayStreamDirectoryAsync(
            Iso9660Entry directory,
            int depth,
            VisitCounter visited,
            CancellationToken cancellationToken)
        {
            if (depth > 4 || visited.Count >= 256)
            {
                return null;
            }

            visited.Count++;
            var entries = await ReadIso9660DirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
            var bdmv = entries.FirstOrDefault(entry =>
                entry.IsDirectory && string.Equals(entry.Name, "BDMV", StringComparison.OrdinalIgnoreCase));
            if (bdmv is not null)
            {
                var bdmvEntries = await ReadIso9660DirectoryAsync(bdmv, cancellationToken).ConfigureAwait(false);
                var stream = bdmvEntries.FirstOrDefault(entry =>
                    entry.IsDirectory && string.Equals(entry.Name, "STREAM", StringComparison.OrdinalIgnoreCase));
                if (stream is not null)
                {
                    return stream;
                }
            }

            foreach (var entry in entries.Where(static entry => entry.IsDirectory))
            {
                var lower = entry.Name.ToLowerInvariant();
                if (lower is "certificate" or "any!")
                {
                    continue;
                }

                var found = await SearchIso9660BluRayStreamDirectoryAsync(
                        entry,
                        depth + 1,
                        visited,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }

        private async Task<Iso9660Entry> FindIso9660EntryAsync(
            Iso9660Entry directory,
            string name,
            bool directoryExpected,
            CancellationToken cancellationToken)
        {
            var entries = await ReadIso9660DirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
            return entries.FirstOrDefault(entry =>
                       entry.IsDirectory == directoryExpected &&
                       string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidDataException($"ISO9660 路径不存在：{name}。");
        }

        private async Task<IReadOnlyList<Iso9660Entry>> ReadIso9660DirectoryAsync(
            Iso9660Entry directory,
            CancellationToken cancellationToken)
        {
            if (directory.Size > MaximumDirectoryBytes || directory.Size > int.MaxValue)
            {
                throw new InvalidDataException("ISO9660 目录过大。");
            }

            var data = await reader.ReadAsync(
                    checked(directory.Extent * SectorSize),
                    (int)directory.Size,
                    cancellationToken)
                .ConfigureAwait(false);
            var entries = new List<Iso9660Entry>();
            var cursor = 0;
            while (cursor < data.Length)
            {
                var recordLength = data[cursor];
                if (recordLength == 0)
                {
                    cursor = ((cursor / SectorSize) + 1) * SectorSize;
                    continue;
                }

                var entry = ParseIso9660DirectoryRecord(data, cursor);
                if (entry is not null && entry.Name is not ("." or ".."))
                {
                    entries.Add(entry);
                }

                cursor += recordLength;
            }

            return entries;
        }

        private static Iso9660Entry? ParseIso9660DirectoryRecord(byte[] data, int offset)
        {
            if (offset < 0 || offset + 34 > data.Length)
            {
                return null;
            }

            var recordLength = data[offset];
            if (recordLength < 34 || offset + recordLength > data.Length)
            {
                return null;
            }

            var extent = ReadUInt32LE(data, offset + 2);
            var size = ReadUInt32LE(data, offset + 10);
            var flags = data[offset + 25];
            var nameLength = data[offset + 32];
            var rawName = Slice(data, offset + 33, nameLength);
            string name;
            if (rawName.Length == 1 && rawName[0] == 0)
            {
                name = ".";
            }
            else if (rawName.Length == 1 && rawName[0] == 1)
            {
                name = "..";
            }
            else
            {
                name = Encoding.ASCII.GetString(rawName)
                    .Replace(";1", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Trim('.')
                    .Trim();
            }

            return new Iso9660Entry(
                name,
                extent,
                size,
                (flags & 0x02) != 0);
        }

        private async Task<byte[]> ReadSectorAsync(long sector, CancellationToken cancellationToken)
        {
            return await reader.ReadAsync(checked(sector * SectorSize), SectorSize, cancellationToken)
                .ConfigureAwait(false);
        }

        private static int Align4(int value) => (value + 3) & ~3;

        private static string DecodeOstaCompressedUnicode(byte[] data)
        {
            if (data.Length == 0)
            {
                return string.Empty;
            }

            return data[0] switch
            {
                8 => Encoding.UTF8.GetString(data, 1, data.Length - 1).TrimEnd('\0'),
                16 => DecodeOstaUtf16(data),
                _ => Encoding.UTF8.GetString(data).TrimEnd('\0')
            };
        }

        private static string DecodeOstaUtf16(byte[] data)
        {
            var builder = new StringBuilder();
            for (var offset = 1; offset + 1 < data.Length; offset += 2)
            {
                var value = (data[offset] << 8) | data[offset + 1];
                if (value > 0)
                {
                    builder.Append((char)value);
                }
            }

            return builder.ToString();
        }

        private static byte[] Slice(byte[] data, int offset, int length)
        {
            if (length <= 0 || offset < 0 || offset >= data.Length)
            {
                return [];
            }

            var available = Math.Min(length, data.Length - offset);
            return data[offset..(offset + available)];
        }

        private static ushort ReadUInt16LE(byte[] data, int offset)
        {
            if (offset < 0 || offset + 1 >= data.Length)
            {
                return 0;
            }

            return (ushort)(data[offset] | (data[offset + 1] << 8));
        }

        private static uint ReadUInt32LE(byte[] data, int offset)
        {
            return (uint)(ReadUInt16LE(data, offset) | (ReadUInt16LE(data, offset + 2) << 16));
        }

        private static ulong ReadUInt64LE(byte[] data, int offset)
        {
            return ReadUInt32LE(data, offset) | ((ulong)ReadUInt32LE(data, offset + 4) << 32);
        }

        private sealed record UdfPartition(int Number, long StartBlock, long BlockCount);

        private sealed record UdfMetadataPartitionMap(
            int PhysicalPartitionNumber,
            long MetadataFileLocation,
            long MetadataMirrorFileLocation);

        private sealed record UdfLongAd(long Length, long Block, int PartitionRef);

        private sealed record UdfFileEntry(
            long InformationLength,
            IReadOnlyList<RemoteIsoExtent> Extents,
            byte[]? InlineData);

        private sealed record UdfDirectoryEntry(
            string Name,
            bool IsDirectory,
            UdfLongAd Icb);

        private sealed record Iso9660Entry(
            string Name,
            long Extent,
            long Size,
            bool IsDirectory);

        private sealed class VisitCounter
        {
            public int Count { get; set; }
        }
    }
}
