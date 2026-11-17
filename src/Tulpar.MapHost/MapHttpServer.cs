using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Tulpar.MapHost;

/// <summary>
/// Embedded localhost HTTP/1.1 server serving self-contained MapLibre GL JS assets,
/// cache-first online/offline tiles, and local .mbtiles layers on an ephemeral loopback port.
/// </summary>
public sealed class MapHttpServer : IAsyncDisposable
{
    private static readonly TimeSpan DefaultUpstreamTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan KeepAliveIdleTimeout = TimeSpan.FromSeconds(15);
    private const int MaxHeaderBytes = 16 * 1024;

    private static readonly Dictionary<string, (string FileName, string ContentType)> StaticAssets =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["/"] = ("index.html", "text/html; charset=utf-8"),
            ["/index.html"] = ("index.html", "text/html; charset=utf-8"),
            ["/map.js"] = ("map.js", "text/javascript; charset=utf-8"),
            ["/maplibre-gl.js"] = ("maplibre-gl.js", "text/javascript; charset=utf-8"),
            ["/maplibre-gl.css"] = ("maplibre-gl.css", "text/css; charset=utf-8")
        };

    private static readonly JsonSerializerOptions LayerJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly TileCacheStore _cache;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _assetsDirectory;
    private readonly TimeSpan _upstreamTimeout;

    private readonly ConcurrentDictionary<string, TileProviderConfig> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RegisteredMbtilesLayer> _mbtilesLayers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _mbtilesPathToId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, (TcpClient Client, Task Task)> _activeConnections = new();
    private readonly Lock _lifecycleLock = new();

    private long _connectionSeq;
    private int _mbtilesCounter;
    private TcpListener? _listener;
    private CancellationTokenSource? _serverCts;
    private Task? _acceptLoopTask;
    private Uri? _baseUri;
    private int _disposed;

    public MapHttpServer(
        TileCacheStore cache,
        HttpClient? httpClient = null,
        string? assetsDirectory = null,
        TimeSpan? upstreamTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(cache);

        _cache = cache;
        if (httpClient is null)
        {
            _httpClient = new HttpClient();
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }

        _assetsDirectory = Path.TrimEndingDirectorySeparator(
            string.IsNullOrWhiteSpace(assetsDirectory)
                ? Path.Combine(AppContext.BaseDirectory, "Assets", "Map")
                : Path.GetFullPath(assetsDirectory));

        _upstreamTimeout = upstreamTimeout is { TotalMilliseconds: > 0 } customTimeout
            ? customTimeout
            : DefaultUpstreamTimeout;

        foreach (TileProviderConfig provider in BuiltInTileProviders.All)
        {
            _providers[provider.Id] = provider;
        }
    }

    public Uri BaseUri => _baseUri
        ?? throw new InvalidOperationException("MapHttpServer has not been started yet. Call StartAsync() first.");

    public Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_listener is not null)
            {
                return Task.CompletedTask;
            }

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            _listener = listener;
            _baseUri = new Uri($"http://127.0.0.1:{port}/");
            _serverCts = new CancellationTokenSource();
            _acceptLoopTask = Task.Run(() => RunAcceptLoopAsync(listener, _serverCts.Token));
        }

        return Task.CompletedTask;
    }

    public void RegisterProvider(TileProviderConfig provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider.UrlTemplate);
        ArgumentOutOfRangeException.ThrowIfNegative(provider.MinZoom);
        ArgumentOutOfRangeException.ThrowIfLessThan(provider.MaxZoom, provider.MinZoom);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        _providers[provider.Id.Trim()] = provider with { Id = provider.Id.Trim() };
    }

    public string RegisterMbtiles(string mbtilesPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mbtilesPath);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        string fullPath = Path.GetFullPath(mbtilesPath);

        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);

            if (_mbtilesPathToId.TryGetValue(fullPath, out string? existingId))
            {
                return existingId;
            }
        }

        var reader = new MbtilesReader(fullPath);

        lock (_lifecycleLock)
        {
            if (_disposed != 0)
            {
                reader.Dispose();
                throw new ObjectDisposedException(nameof(MapHttpServer));
            }

            if (_mbtilesPathToId.TryGetValue(fullPath, out string? existingId))
            {
                reader.Dispose();
                return existingId;
            }

            int seq = Interlocked.Increment(ref _mbtilesCounter);
            string layerId = $"mbtiles-{seq}";
            _mbtilesPathToId[fullPath] = layerId;
            _mbtilesLayers[layerId] = new RegisteredMbtilesLayer(layerId, reader);
            return layerId;
        }
    }

    public IReadOnlyList<TileProviderConfig> GetProviders() =>
        _providers.Values
            .OrderByDescending(p => p.IsBuiltIn)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async ValueTask DisposeAsync()
    {
        TcpListener? listener;
        CancellationTokenSource? cts;
        Task? acceptTask;
        List<MbtilesReader> readersToDispose;

        lock (_lifecycleLock)
        {
            if (_disposed != 0)
            {
                return;
            }

            Volatile.Write(ref _disposed, 1);
            listener = _listener;
            cts = _serverCts;
            acceptTask = _acceptLoopTask;

            _listener = null;
            _serverCts = null;
            _acceptLoopTask = null;

            readersToDispose = _mbtilesLayers.Values.Select(l => l.Reader).ToList();
            _mbtilesLayers.Clear();
            _mbtilesPathToId.Clear();
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            listener?.Stop();
        }
        catch (SocketException)
        {
        }

        if (acceptTask is not null)
        {
            try
            {
                await acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
        }

        // Wait for all active HTTP request handlers to unwind before disposing readers or HttpClient.
        KeyValuePair<long, (TcpClient Client, Task Task)>[] connections = _activeConnections.ToArray();
        foreach (KeyValuePair<long, (TcpClient Client, Task Task)> conn in connections)
        {
            try
            {
                conn.Value.Client.Close();
            }
            catch
            {
            }
        }

        if (connections.Length > 0)
        {
            try
            {
                await Task.WhenAll(connections.Select(c => c.Value.Task)).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        foreach (MbtilesReader reader in readersToDispose)
        {
            reader.Dispose();
        }

        cts?.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task RunAcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            long connId = Interlocked.Increment(ref _connectionSeq);
            var doneTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeConnections[connId] = (client, doneTcs.Task);

            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleClientConnectionAsync(client, ct).ConfigureAwait(false);
                }
                finally
                {
                    _activeConnections.TryRemove(connId, out _);
                    doneTcs.TrySetResult();
                }
            }, CancellationToken.None);
        }
    }

    private async Task HandleClientConnectionAsync(TcpClient client, CancellationToken serverCt)
    {
        using (client)
        {
            NetworkStream stream;
            try
            {
                client.NoDelay = true;
                stream = client.GetStream();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            await using (stream)
            {
                while (!serverCt.IsCancellationRequested)
                {
                    HttpRequestHeaderInfo? request;
                    try
                    {
                        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
                        idleCts.CancelAfter(KeepAliveIdleTimeout);
                        request = await ReadHttpRequestAsync(stream, idleCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (IOException)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                        break;
                    }

                    if (request is null)
                    {
                        break;
                    }

                    bool closeAfterResponse = request.CloseConnection;
                    try
                    {
                        await RouteAndWriteResponseAsync(stream, request, serverCt).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (serverCt.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (IOException)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                        break;
                    }

                    if (closeAfterResponse)
                    {
                        break;
                    }
                }
            }
        }
    }

    private async Task RouteAndWriteResponseAsync(
        NetworkStream stream,
        HttpRequestHeaderInfo request,
        CancellationToken ct)
    {
        if (request.Malformed)
        {
            await WriteResponseAsync(
                stream,
                HttpStatusCode.BadRequest,
                "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes("Bad Request"),
                headOnly: false,
                closeConnection: true,
                ct).ConfigureAwait(false);
            return;
        }

        if (request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            await WriteEmptyResponseAsync(stream, HttpStatusCode.NoContent, request.CloseConnection, ct)
                .ConfigureAwait(false);
            return;
        }

        bool isHead = request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
        if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && !isHead)
        {
            await WriteResponseAsync(
                stream,
                HttpStatusCode.MethodNotAllowed,
                "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes("Method Not Allowed"),
                headOnly: isHead,
                closeConnection: request.CloseConnection,
                ct).ConfigureAwait(false);
            return;
        }

        string path = request.Path;

        if (path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
        {
            await WriteEmptyResponseAsync(stream, HttpStatusCode.NoContent, request.CloseConnection, ct)
                .ConfigureAwait(false);
            return;
        }

        if (path.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            await WriteResponseAsync(
                stream,
                HttpStatusCode.OK,
                "text/plain; charset=utf-8",
                "OK"u8.ToArray(),
                headOnly: isHead,
                closeConnection: request.CloseConnection,
                ct).ConfigureAwait(false);
            return;
        }

        if (path.Equals("/api/layers", StringComparison.OrdinalIgnoreCase))
        {
            var layers = new List<object>();
            foreach (TileProviderConfig provider in GetProviders())
            {
                layers.Add(new
                {
                    id = provider.Id,
                    name = provider.Name,
                    kind = "xyz",
                    minZoom = provider.MinZoom,
                    maxZoom = provider.MaxZoom,
                    isBuiltIn = provider.IsBuiltIn,
                    tileUrl = $"/tiles/{Uri.EscapeDataString(provider.Id)}/{{z}}/{{x}}/{{y}}"
                });
            }

            foreach (RegisteredMbtilesLayer mb in _mbtilesLayers.Values.OrderBy(m => m.LayerId, StringComparer.OrdinalIgnoreCase))
            {
                layers.Add(new
                {
                    id = mb.LayerId,
                    name = mb.Reader.Name,
                    kind = "mbtiles",
                    minZoom = 0,
                    maxZoom = 22,
                    isBuiltIn = false,
                    tileUrl = $"/tiles/{Uri.EscapeDataString(mb.LayerId)}/{{z}}/{{x}}/{{y}}"
                });
            }

            byte[] json = JsonSerializer.SerializeToUtf8Bytes(layers, LayerJsonOptions);
            await WriteResponseAsync(
                stream,
                HttpStatusCode.OK,
                "application/json; charset=utf-8",
                json,
                headOnly: isHead,
                closeConnection: request.CloseConnection,
                ct).ConfigureAwait(false);
            return;
        }

        if (path.StartsWith("/tiles/", StringComparison.OrdinalIgnoreCase))
        {
            await HandleTileRequestAsync(stream, path, isHead, request.CloseConnection, ct).ConfigureAwait(false);
            return;
        }

        if (StaticAssets.TryGetValue(path, out (string FileName, string ContentType) asset))
        {
            await ServeStaticAssetAsync(stream, asset.FileName, asset.ContentType, isHead, request.CloseConnection, ct)
                .ConfigureAwait(false);
            return;
        }

        await WriteResponseAsync(
            stream,
            HttpStatusCode.NotFound,
            "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes("Not Found"),
            headOnly: isHead,
            closeConnection: request.CloseConnection,
            ct).ConfigureAwait(false);
    }

    private async Task ServeStaticAssetAsync(
        NetworkStream stream,
        string fileName,
        string contentType,
        bool headOnly,
        bool closeConnection,
        CancellationToken ct)
    {
        string baseDirFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_assetsDirectory));
        string basePrefix = baseDirFull.EndsWith(Path.DirectorySeparatorChar)
            ? baseDirFull
            : baseDirFull + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(baseDirFull, fileName));
        if (!fullPath.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath))
        {
            await WriteResponseAsync(
                stream,
                HttpStatusCode.NotFound,
                "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes("Asset Not Found"),
                headOnly: headOnly,
                closeConnection: closeConnection,
                ct).ConfigureAwait(false);
            return;
        }

        byte[] bytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
        await WriteResponseAsync(
            stream,
            HttpStatusCode.OK,
            contentType,
            bytes,
            headOnly: headOnly,
            closeConnection: closeConnection,
            ct).ConfigureAwait(false);
    }

    private async Task HandleTileRequestAsync(
        NetworkStream stream,
        string path,
        bool headOnly,
        bool closeConnection,
        CancellationToken ct)
    {
        if (!TryParseTileRoute(path, out string layerId, out int z, out int x, out int y))
        {
            await WriteResponseAsync(
                stream,
                HttpStatusCode.BadRequest,
                "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes("Invalid tile coordinates"),
                headOnly: headOnly,
                closeConnection: closeConnection,
                ct).ConfigureAwait(false);
            return;
        }

        // 1. Registered local .mbtiles archive
        if (_mbtilesLayers.TryGetValue(layerId, out RegisteredMbtilesLayer? mbtilesLayer))
        {
            byte[]? mbTile = mbtilesLayer.Reader.TryGetTileXyz(z, x, y);
            if (mbTile is null)
            {
                await WriteEmptyResponseAsync(stream, HttpStatusCode.NoContent, closeConnection, ct)
                    .ConfigureAwait(false);
                return;
            }

            string mbContentType = TileFormatHelper.DetectImageContentType(mbTile, mbtilesLayer.Reader.Format);
            await WriteResponseAsync(
                stream,
                HttpStatusCode.OK,
                mbContentType,
                mbTile,
                headOnly: headOnly,
                closeConnection: closeConnection,
                ct).ConfigureAwait(false);
            return;
        }

        // 2. Registered XYZ tile provider (Cache-First -> Upstream -> 204 fallback)
        if (!_providers.TryGetValue(layerId, out TileProviderConfig? provider))
        {
            await WriteResponseAsync(
                stream,
                HttpStatusCode.NotFound,
                "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes("Unknown layer"),
                headOnly: headOnly,
                closeConnection: closeConnection,
                ct).ConfigureAwait(false);
            return;
        }

        byte[]? cached = await _cache.TryGetTileAsync(provider.Id, z, x, y, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            string cachedContentType = TileFormatHelper.DetectImageContentType(cached, null);
            await WriteResponseAsync(
                stream,
                HttpStatusCode.OK,
                cachedContentType,
                cached,
                headOnly: headOnly,
                closeConnection: closeConnection,
                ct).ConfigureAwait(false);
            return;
        }

        if (z < provider.MinZoom || z > provider.MaxZoom)
        {
            await WriteEmptyResponseAsync(stream, HttpStatusCode.NoContent, closeConnection, ct)
                .ConfigureAwait(false);
            return;
        }

        byte[]? fetched = await TryFetchUpstreamTileAsync(provider, z, x, y, ct).ConfigureAwait(false);
        if (fetched is null || fetched.Length == 0)
        {
            await WriteEmptyResponseAsync(stream, HttpStatusCode.NoContent, closeConnection, ct)
                .ConfigureAwait(false);
            return;
        }

        await _cache.PutTileAsync(provider.Id, z, x, y, fetched, ct).ConfigureAwait(false);

        string fetchedContentType = TileFormatHelper.DetectImageContentType(fetched, null);
        await WriteResponseAsync(
            stream,
            HttpStatusCode.OK,
            fetchedContentType,
            fetched,
            headOnly: headOnly,
            closeConnection: closeConnection,
            ct).ConfigureAwait(false);
    }

    private async Task<byte[]?> TryFetchUpstreamTileAsync(
        TileProviderConfig provider,
        int z,
        int x,
        int y,
        CancellationToken serverCt)
    {
        string url = TileFormatHelper.FormatTileUrl(provider.UrlTemplate, z, x, y);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        timeoutCts.CancelAfter(_upstreamTimeout);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            {
                req.Headers.UserAgent.Add(new ProductInfoHeaderValue("TulparMissionPlanner", "1.0"));
            }

            using HttpResponseMessage resp = await _httpClient
                .SendAsync(req, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            byte[] bytes = await resp.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
            return bytes.Length > 0 ? bytes : null;
        }
        catch (OperationCanceledException) when (serverCt.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Upstream timed out; return 204 cleanly so WebView2 never stalls.
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryParseTileRoute(string path, out string layerId, out int z, out int x, out int y)
    {
        layerId = string.Empty;
        z = 0;
        x = 0;
        y = 0;

        string trimmed = path.Substring("/tiles/".Length);
        string[] parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            return false;
        }

        layerId = Uri.UnescapeDataString(parts[0]).Trim();
        if (string.IsNullOrWhiteSpace(layerId))
        {
            return false;
        }

        string yPart = parts[3];
        int dotIndex = yPart.IndexOf('.');
        if (dotIndex > 0)
        {
            yPart = yPart[..dotIndex];
        }

        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out z) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out x) ||
            !int.TryParse(yPart, NumberStyles.None, CultureInfo.InvariantCulture, out y))
        {
            return false;
        }

        if (z is < 0 or > 22)
        {
            return false;
        }

        int gridSize = 1 << z;
        return x >= 0 && x < gridSize && y >= 0 && y < gridSize;
    }

    private static async Task<HttpRequestHeaderInfo?> ReadHttpRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[MaxHeaderBytes];
        int totalRead = 0;
        int headerEnd = -1;

        while (totalRead < MaxHeaderBytes)
        {
            int bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead, MaxHeaderBytes - totalRead), ct)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return null;
            }

            totalRead += bytesRead;
            headerEnd = FindHeaderTerminator(buffer.AsSpan(0, totalRead));
            if (headerEnd >= 0)
            {
                break;
            }
        }

        if (headerEnd < 0)
        {
            return HttpRequestHeaderInfo.CreateMalformed();
        }

        string headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        string[] lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0)
        {
            return HttpRequestHeaderInfo.CreateMalformed();
        }

        string[] requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2)
        {
            return HttpRequestHeaderInfo.CreateMalformed();
        }

        string method = requestLine[0];
        string rawTarget = requestLine[1];
        string version = requestLine.Length >= 3 ? requestLine[2] : "HTTP/1.1";

        int queryIdx = rawTarget.IndexOf('?');
        string rawPath = queryIdx >= 0 ? rawTarget[..queryIdx] : rawTarget;
        if (rawPath.Contains('\\') || rawPath.Contains("..", StringComparison.Ordinal))
        {
            return HttpRequestHeaderInfo.CreateMalformed();
        }

        string decodedPath;
        try
        {
            decodedPath = Uri.UnescapeDataString(rawPath);
        }
        catch (UriFormatException)
        {
            return HttpRequestHeaderInfo.CreateMalformed();
        }

        if (decodedPath.Contains('\\') || decodedPath.Contains("..", StringComparison.Ordinal) || !decodedPath.StartsWith('/'))
        {
            return HttpRequestHeaderInfo.CreateMalformed();
        }

        bool closeConnection = version.Equals("HTTP/1.0", StringComparison.OrdinalIgnoreCase);
        int contentLength = 0;

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();

            if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Contains("close", StringComparison.OrdinalIgnoreCase))
                {
                    closeConnection = true;
                }
                else if (value.Contains("keep-alive", StringComparison.OrdinalIgnoreCase))
                {
                    closeConnection = false;
                }
            }
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength);
            }
        }

        // Drain small request bodies if any were sent on keep-alive connections; close on large unexpected payloads.
        int bodyAlreadyBuffered = totalRead - (headerEnd + 4);
        if (contentLength > 0)
        {
            if (contentLength > 64 * 1024)
            {
                closeConnection = true;
            }
            else
            {
                int remaining = contentLength - bodyAlreadyBuffered;
                byte[] discard = new byte[Math.Min(4096, Math.Max(0, remaining))];
                while (remaining > 0)
                {
                    int read = await stream.ReadAsync(discard.AsMemory(0, Math.Min(discard.Length, remaining)), ct)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        closeConnection = true;
                        break;
                    }

                    remaining -= read;
                }
            }
        }

        return new HttpRequestHeaderInfo(method, decodedPath, closeConnection, Malformed: false);
    }

    private static int FindHeaderTerminator(ReadOnlySpan<byte> span)
    {
        for (int i = 0; i <= span.Length - 4; i++)
        {
            if (span[i] == '\r' && span[i + 1] == '\n' && span[i + 2] == '\r' && span[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static Task WriteEmptyResponseAsync(
        NetworkStream stream,
        HttpStatusCode statusCode,
        bool closeConnection,
        CancellationToken ct)
    {
        string reason = GetReasonPhrase(statusCode);
        string connHeader = closeConnection ? "close" : "keep-alive";
        string header =
            $"HTTP/1.1 {(int)statusCode} {reason}\r\n" +
            $"Access-Control-Allow-Origin: *\r\n" +
            $"Cache-Control: no-store\r\n" +
            $"Connection: {connHeader}\r\n\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        return stream.WriteAsync(headerBytes, ct).AsTask();
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        HttpStatusCode statusCode,
        string contentType,
        byte[] body,
        bool headOnly,
        bool closeConnection,
        CancellationToken ct)
    {
        string reason = GetReasonPhrase(statusCode);
        string connHeader = closeConnection ? "close" : "keep-alive";
        string header =
            $"HTTP/1.1 {(int)statusCode} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n" +
            $"Access-Control-Allow-Origin: *\r\n" +
            $"Cache-Control: no-cache\r\n" +
            $"Connection: {connHeader}\r\n\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        if (!headOnly && body.Length > 0)
        {
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
        }
    }

    private static string GetReasonPhrase(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.OK => "OK",
        HttpStatusCode.NoContent => "No Content",
        HttpStatusCode.BadRequest => "Bad Request",
        HttpStatusCode.NotFound => "Not Found",
        HttpStatusCode.MethodNotAllowed => "Method Not Allowed",
        _ => statusCode.ToString()
    };

    private sealed record RegisteredMbtilesLayer(string LayerId, MbtilesReader Reader);

    private sealed record HttpRequestHeaderInfo(
        string Method,
        string Path,
        bool CloseConnection,
        bool Malformed)
    {
        public static HttpRequestHeaderInfo CreateMalformed() =>
            new(string.Empty, string.Empty, CloseConnection: true, Malformed: true);
    }
}
