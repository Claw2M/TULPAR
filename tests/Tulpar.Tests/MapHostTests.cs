using System.Diagnostics;
using System.Net;
using System.Net.Http;
using Microsoft.Data.Sqlite;
using Tulpar.MapHost;

namespace Tulpar.Tests;

public sealed class MapHostTests : IDisposable
{
    private static readonly byte[] SamplePngTile =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52
    ];

    private static readonly byte[] SampleJpegTile =
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46
    ];

    private readonly string _tempDir;

    public MapHostTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tulpar-maphost-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore transient test cleanup issues if OS holds a handle briefly.
        }
    }

    [Fact]
    public async Task TileCacheStore_PutAndGet_PersistsAcrossReopen()
    {
        string dbPath = Path.Combine(_tempDir, "nested", "tiles.db");
        byte[] updatedTile = [.. SamplePngTile, 0xAA, 0xBB];

        using (var store = new TileCacheStore(dbPath))
        {
            Assert.Null(await store.TryGetTileAsync("satellite", 14, 9684, 6215));

            await store.PutTileAsync("satellite", 14, 9684, 6215, SamplePngTile);
            byte[]? firstRead = await store.TryGetTileAsync("satellite", 14, 9684, 6215);
            Assert.Equal(SamplePngTile, firstRead);

            // Upsert updates existing tile cleanly.
            await store.PutTileAsync("satellite", 14, 9684, 6215, updatedTile);
            byte[]? secondRead = await store.TryGetTileAsync("satellite", 14, 9684, 6215);
            Assert.Equal(updatedTile, secondRead);
        }

        // Reopen on a brand-new TileCacheStore instance to verify durable SQLite persistence.
        using (var reopened = new TileCacheStore(dbPath))
        {
            byte[]? persisted = await reopened.TryGetTileAsync("satellite", 14, 9684, 6215);
            Assert.Equal(updatedTile, persisted);
            Assert.Null(await reopened.TryGetTileAsync("street", 14, 9684, 6215));
        }

        // Verify Dispose released SQLite file locks on Windows.
        File.Delete(dbPath);
        Assert.False(File.Exists(dbPath));
    }

    [Fact]
    public void MbtilesReader_ConvertsXyzToTmsYCoordinateCorrectly()
    {
        string mbtilesPath = Path.Combine(_tempDir, "ankara.mbtiles");
        const int z = 12;
        const int x = 2421;
        const int xyzY = 1554;
        const int tmsY = (1 << z) - 1 - xyzY; // 4095 - 1554 = 2541

        byte[] expectedTmsPayload = [.. SampleJpegTile, 0x11, 0x22];
        byte[] decoyUnflippedPayload = [.. SampleJpegTile, 0x99, 0x88];
        byte[] zoom0Payload = [.. SampleJpegTile, 0x00];

        CreateSampleMbtiles(
            mbtilesPath,
            name: "Ankara Offline",
            format: "jpg",
            tiles:
            [
                (z, x, tmsY, expectedTmsPayload),
                (z, x, xyzY, decoyUnflippedPayload),
                (0, 0, 0, zoom0Payload)
            ]);

        using (var reader = new MbtilesReader(mbtilesPath))
        {
            Assert.Equal("Ankara Offline", reader.Name);
            Assert.Equal("jpg", reader.Format);

            byte[]? tile = reader.TryGetTileXyz(z, x, xyzY);
            Assert.Equal(expectedTmsPayload, tile);

            byte[]? z0 = reader.TryGetTileXyz(0, 0, 0);
            Assert.Equal(zoom0Payload, z0);

            Assert.Null(reader.TryGetTileXyz(z, x, xyzY + 1));
            Assert.Null(reader.TryGetTileXyz(-1, 0, 0));
            Assert.Null(reader.TryGetTileXyz(31, 0, 0));
            Assert.Null(reader.TryGetTileXyz(2, 4, 0));
        }

        File.Delete(mbtilesPath);
        Assert.False(File.Exists(mbtilesPath));
    }

    [Fact]
    public async Task MapHttpServer_OfflineUpstreamFailure_ServesCachedTileOrReturns204WithoutHanging()
    {
        string dbPath = Path.Combine(_tempDir, "server-cache.db");
        using var cache = new TileCacheStore(dbPath);
        await cache.PutTileAsync(BuiltInTileProviders.Street.Id, 10, 600, 380, SamplePngTile);

        int upstreamCallCount = 0;
        using var mockHandler = new DelegateHttpMessageHandler(async (req, ct) =>
        {
            Interlocked.Increment(ref upstreamCallCount);
            string url = req.RequestUri!.ToString();

            if (url.Contains("/10/600/381", StringComparison.Ordinal))
            {
                throw new HttpRequestException("Simulated offline DNS failure.");
            }

            if (url.Contains("/10/600/382", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            }

            if (url.Contains("/10/600/383", StringComparison.Ordinal))
            {
                // Simulate hung upstream connection; server must abort via short timeout and return 204.
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (url.Contains("/10/600/384", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(SampleJpegTile)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var upstreamHttp = new HttpClient(mockHandler);
        await using var server = new MapHttpServer(
            cache,
            upstreamHttp,
            upstreamTimeout: TimeSpan.FromMilliseconds(250));
        await server.StartAsync();

        Assert.Equal(IPAddress.Loopback.ToString(), server.BaseUri.Host);
        Assert.True(server.BaseUri.Port > 0);

        using var client = new HttpClient { BaseAddress = server.BaseUri };

        // 1. Cached tile served immediately with 200 OK without touching upstream.
        HttpResponseMessage cachedResp = await client.GetAsync("/tiles/street/10/600/380");
        Assert.Equal(HttpStatusCode.OK, cachedResp.StatusCode);
        Assert.Equal("image/png", cachedResp.Content.Headers.ContentType?.MediaType);
        Assert.Equal(SamplePngTile, await cachedResp.Content.ReadAsByteArrayAsync());
        Assert.Equal(0, upstreamCallCount);

        // 2. Offline network exception -> 204 No Content immediately.
        HttpResponseMessage offlineResp = await client.GetAsync("/tiles/street/10/600/381");
        Assert.Equal(HttpStatusCode.NoContent, offlineResp.StatusCode);

        // 3. Upstream HTTP 429 -> 204 No Content.
        HttpResponseMessage rateLimitedResp = await client.GetAsync("/tiles/street/10/600/382");
        Assert.Equal(HttpStatusCode.NoContent, rateLimitedResp.StatusCode);

        // 4. Hung upstream -> timed out quickly and returns 204 No Content without hanging.
        var sw = Stopwatch.StartNew();
        HttpResponseMessage hungResp = await client.GetAsync("/tiles/street/10/600/383");
        sw.Stop();
        Assert.Equal(HttpStatusCode.NoContent, hungResp.StatusCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Expected fast 204 timeout, elapsed={sw.Elapsed}");

        // 5. Upstream 200 JPEG -> returned with image/jpeg and persisted to cache.
        HttpResponseMessage onlineResp = await client.GetAsync("/tiles/street/10/600/384");
        Assert.Equal(HttpStatusCode.OK, onlineResp.StatusCode);
        Assert.Equal("image/jpeg", onlineResp.Content.Headers.ContentType?.MediaType);
        Assert.Equal(SampleJpegTile, await onlineResp.Content.ReadAsByteArrayAsync());

        int callsAfterOnline = upstreamCallCount;
        HttpResponseMessage secondRead = await client.GetAsync("/tiles/street/10/600/384");
        Assert.Equal(HttpStatusCode.OK, secondRead.StatusCode);
        Assert.Equal(SampleJpegTile, await secondRead.Content.ReadAsByteArrayAsync());
        Assert.Equal(callsAfterOnline, upstreamCallCount);
    }

    [Fact]
    public async Task MapHttpServer_ServesMbtilesLayerAndLocalAssetsWithoutExternalCdn()
    {
        string dbPath = Path.Combine(_tempDir, "assets-test.db");
        string mbtilesPath = Path.Combine(_tempDir, "field-site.mbtiles");

        const int z = 11;
        const int x = 1210;
        const int xyzY = 777;
        const int tmsY = (1 << z) - 1 - xyzY;
        byte[] mbtilesTile = [.. SamplePngTile, 0x42];

        CreateSampleMbtiles(
            mbtilesPath,
            name: "Field Site",
            format: "png",
            tiles: [(z, x, tmsY, mbtilesTile)]);

        using var cache = new TileCacheStore(dbPath);
        using var mockHandler = new DelegateHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("Upstream should never be called for mbtiles or static assets."));
        using var upstreamHttp = new HttpClient(mockHandler);

        await using var server = new MapHttpServer(cache, upstreamHttp);
        string layerId = server.RegisterMbtiles(mbtilesPath);
        Assert.False(string.IsNullOrWhiteSpace(layerId));

        server.RegisterProvider(new TileProviderConfig(
            "custom-ortophoto",
            "Custom Ortho",
            "https://example.invalid/tiles/{z}/{x}/{y}.png",
            MaxZoom: 20,
            MinZoom: 1,
            IsBuiltIn: false));

        Assert.Contains(server.GetProviders(), p => p.Id == BuiltInTileProviders.GoogleSatelliteHd.Id && p.MaxZoom == 21);
        Assert.Contains(server.GetProviders(), p => p.Id == BuiltInTileProviders.GoogleHybridHd.Id && p.MaxZoom == 21);
        Assert.Contains(server.GetProviders(), p => p.Id == BuiltInTileProviders.EsriClarity.Id);
        Assert.Contains(server.GetProviders(), p => p.Id == BuiltInTileProviders.GoogleStreetHd.Id && p.MaxZoom == 21);
        Assert.Contains(server.GetProviders(), p => p.Id == BuiltInTileProviders.Satellite.Id);
        Assert.Contains(server.GetProviders(), p => p.Id == BuiltInTileProviders.Street.Id);
        Assert.Contains(server.GetProviders(), p => p.Id == BuiltInTileProviders.Topo.Id);
        Assert.Contains(server.GetProviders(), p => p.Id == "custom-ortophoto" && !p.IsBuiltIn);

        await server.StartAsync();
        using var client = new HttpClient { BaseAddress = server.BaseUri };

        // Registered .mbtiles hit and miss
        HttpResponseMessage mbHit = await client.GetAsync($"/tiles/{layerId}/{z}/{x}/{xyzY}");
        Assert.Equal(HttpStatusCode.OK, mbHit.StatusCode);
        Assert.Equal("image/png", mbHit.Content.Headers.ContentType?.MediaType);
        Assert.Equal(mbtilesTile, await mbHit.Content.ReadAsByteArrayAsync());

        HttpResponseMessage mbMiss = await client.GetAsync($"/tiles/{layerId}/{z}/{x}/{xyzY + 1}");
        Assert.Equal(HttpStatusCode.NoContent, mbMiss.StatusCode);

        // Local self-contained MapLibre GL JS assets
        HttpResponseMessage indexResp = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, indexResp.StatusCode);
        Assert.Equal("text/html", indexResp.Content.Headers.ContentType?.MediaType);
        string html = await indexResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("maplibre-gl.js", html, StringComparison.Ordinal);
        Assert.Contains("maplibre-gl.css", html, StringComparison.Ordinal);
        Assert.Contains("map.js", html, StringComparison.Ordinal);

        HttpResponseMessage mapJsResp = await client.GetAsync("/map.js");
        Assert.Equal(HttpStatusCode.OK, mapJsResp.StatusCode);
        string mapJs = await mapJsResp.Content.ReadAsStringAsync();
        Assert.Contains("tulparMap", mapJs, StringComparison.Ordinal);
        Assert.Contains("guidedTarget", mapJs, StringComparison.Ordinal);

        HttpResponseMessage glJsResp = await client.GetAsync("/maplibre-gl.js");
        Assert.Equal(HttpStatusCode.OK, glJsResp.StatusCode);
        string glJs = await glJsResp.Content.ReadAsStringAsync();
        Assert.True(glJs.Length > 100_000, "Expected full official MapLibre GL JS bundle.");

        HttpResponseMessage glCssResp = await client.GetAsync("/maplibre-gl.css");
        Assert.Equal(HttpStatusCode.OK, glCssResp.StatusCode);
        string glCss = await glCssResp.Content.ReadAsStringAsync();
        Assert.Contains("maplibregl", glCss, StringComparison.Ordinal);

        // Path traversal must be blocked
        HttpResponseMessage traversalResp = await client.GetAsync("/../Tulpar.MapHost.dll");
        Assert.NotEqual(HttpStatusCode.OK, traversalResp.StatusCode);

        // Favicon returns 204 cleanly without noisy 404 errors
        HttpResponseMessage faviconResp = await client.GetAsync("/favicon.ico");
        Assert.Equal(HttpStatusCode.NoContent, faviconResp.StatusCode);

        // HEAD on tile returns headers without body
        using var headReq = new HttpRequestMessage(HttpMethod.Head, $"/tiles/{layerId}/{z}/{x}/{xyzY}");
        HttpResponseMessage headResp = await client.SendAsync(headReq);
        Assert.Equal(HttpStatusCode.OK, headResp.StatusCode);
        Assert.Equal(mbtilesTile.Length, headResp.Content.Headers.ContentLength);
        Assert.Empty(await headResp.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task MapHttpServer_HdBuiltInProviders_ExposeMaxZoom21AndServeZoom21Tiles()
    {
        string dbPath = Path.Combine(_tempDir, "hd-layers.db");
        using var cache = new TileCacheStore(dbPath);
        string? requestedUpstreamUrl = null;
        using var mockHandler = new DelegateHttpMessageHandler((req, _) =>
        {
            requestedUpstreamUrl = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(SampleJpegTile)
            });
        });

        using var upstreamHttp = new HttpClient(mockHandler);
        await using var server = new MapHttpServer(cache, upstreamHttp);
        await server.StartAsync();

        using var client = new HttpClient { BaseAddress = server.BaseUri };
        string layersJson = await client.GetStringAsync("/api/layers");
        using var doc = System.Text.Json.JsonDocument.Parse(layersJson);
        var byId = doc.RootElement.EnumerateArray()
            .ToDictionary(e => e.GetProperty("id").GetString()!, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(21, byId[BuiltInTileProviders.GoogleSatelliteHd.Id].GetProperty("maxZoom").GetInt32());
        Assert.Equal(21, byId[BuiltInTileProviders.GoogleHybridHd.Id].GetProperty("maxZoom").GetInt32());
        Assert.Equal(21, byId[BuiltInTileProviders.GoogleStreetHd.Id].GetProperty("maxZoom").GetInt32());
        Assert.Equal(19, byId[BuiltInTileProviders.EsriClarity.Id].GetProperty("maxZoom").GetInt32());

        HttpResponseMessage z21Resp = await client.GetAsync("/tiles/google-satellite-hd/21/1239997/794444");
        Assert.Equal(HttpStatusCode.OK, z21Resp.StatusCode);
        Assert.Equal("image/jpeg", z21Resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal(SampleJpegTile, await z21Resp.Content.ReadAsByteArrayAsync());
        Assert.Equal("https://mt1.google.com/vt/lyrs=s&scale=2&x=1239997&y=794444&z=21", requestedUpstreamUrl);
    }

    [Fact]
    public async Task OfflineRegionDownloader_EnforcesMaxTileLimitAndRateDelay()
    {
        // Enumerate tiles over Ankara across z10..z12 (well over 5 tiles total)
        IReadOnlyList<(int Z, int X, int Y)> cappedList = OfflineRegionDownloader.EnumerateTiles(
            minLat: 39.85,
            minLon: 32.75,
            maxLat: 40.00,
            maxLon: 32.95,
            minZoom: 10,
            maxZoom: 12,
            maxTilesLimit: 5);

        Assert.Equal(5, cappedList.Count);
        foreach ((int z, int x, int y) in cappedList)
        {
            int n = 1 << z;
            Assert.InRange(z, 10, 12);
            Assert.InRange(x, 0, n - 1);
            Assert.InRange(y, 0, n - 1);
        }

        string dbPath = Path.Combine(_tempDir, "downloader.db");
        using var cache = new TileCacheStore(dbPath);
        var provider = new TileProviderConfig(
            "test-sat",
            "Test Sat",
            "https://tiles.local/{z}/{x}/{y}.jpg",
            MaxZoom: 18);

        // Pre-cache the 1st tile so downloader skips it without network hit.
        (int Z, int X, int Y) firstTile = cappedList[0];
        (int Z, int X, int Y) failTile = cappedList[2];
        await cache.PutTileAsync(provider.Id, firstTile.Z, firstTile.X, firstTile.Y, SampleJpegTile);

        int requestedCount = 0;
        using var handler = new DelegateHttpMessageHandler((req, _) =>
        {
            Interlocked.Increment(ref requestedCount);
            string url = req.RequestUri!.ToString();
            if (url.EndsWith($"/{failTile.Z}/{failTile.X}/{failTile.Y}.jpg", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(SampleJpegTile)
            });
        });

        using var http = new HttpClient(handler);
        var downloader = new OfflineRegionDownloader(cache, http, delayMsPerRequest: 40);

        var progressReports = new List<OfflineDownloadProgress>();
        IProgress<OfflineDownloadProgress> progress = new Progress<OfflineDownloadProgress>(p =>
        {
            lock (progressReports)
            {
                progressReports.Add(p);
            }
        });

        var sw = Stopwatch.StartNew();
        OfflineDownloadResult result = await downloader.DownloadBoundsAsync(
            provider,
            minLat: 39.85,
            minLon: 32.75,
            maxLat: 40.00,
            maxLon: 32.95,
            minZoom: 10,
            maxZoom: 12,
            maxTilesLimit: 5,
            progress: progress,
            ct: CancellationToken.None);
        sw.Stop();

        Assert.Equal(5, result.TotalTiles);
        Assert.True(result.Capped);
        Assert.Equal(1, result.SkippedCachedTiles);
        Assert.Equal(3, result.DownloadedTiles);
        Assert.Equal(1, result.FailedTiles);
        Assert.Equal(4, requestedCount);

        // 4 uncached HTTP requests with >= 40ms rate spacing between requests => at least 3 * 40 = 120ms (allow 90ms timer margin).
        Assert.True(sw.ElapsedMilliseconds >= 90, $"Expected rate delay >= 90ms, got {sw.ElapsedMilliseconds}ms");

        // Downloaded tiles are now persisted in cache; failed tile is not.
        (int Z, int X, int Y) downloadedTile = cappedList[1];
        Assert.Equal(SampleJpegTile, await cache.TryGetTileAsync(provider.Id, downloadedTile.Z, downloadedTile.X, downloadedTile.Y));
        Assert.Null(await cache.TryGetTileAsync(provider.Id, failTile.Z, failTile.X, failTile.Y));

        // Small single-tile area under cap reports Capped == false.
        OfflineDownloadResult uncapped = await downloader.DownloadBoundsAsync(
            provider,
            minLat: 39.92,
            minLon: 32.85,
            maxLat: 39.93,
            maxLon: 32.86,
            minZoom: 10,
            maxZoom: 10,
            maxTilesLimit: 50,
            progress: null,
            ct: CancellationToken.None);

        Assert.False(uncapped.Capped);
        Assert.Equal(1, uncapped.TotalTiles);

        // Exact boundary: totalAvailable == maxTilesLimit must report Capped == false
        OfflineDownloadResult exactLimit = await downloader.DownloadBoundsAsync(
            provider,
            minLat: 39.92,
            minLon: 32.85,
            maxLat: 39.93,
            maxLon: 32.86,
            minZoom: 10,
            maxZoom: 10,
            maxTilesLimit: 1,
            progress: null,
            ct: CancellationToken.None);

        Assert.False(exactLimit.Capped);
        Assert.Equal(1, exactLimit.TotalTiles);

        // Cancellation propagates cleanly
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            downloader.DownloadBoundsAsync(
                provider,
                minLat: 39.85,
                minLon: 32.75,
                maxLat: 40.00,
                maxLon: 32.95,
                minZoom: 10,
                maxZoom: 12,
                maxTilesLimit: 5,
                progress: null,
                ct: cts.Token));
    }

    [Fact]
    public async Task MapHttpServer_MultipleInstancesUseDistinctEphemeralPorts_AndDisposeCleanly()
    {
        using var cache1 = new TileCacheStore(Path.Combine(_tempDir, "multi1.db"));
        using var cache2 = new TileCacheStore(Path.Combine(_tempDir, "multi2.db"));

        await using var server1 = new MapHttpServer(cache1);
        await using var server2 = new MapHttpServer(cache2);

        await server1.StartAsync();
        await server2.StartAsync();

        Assert.NotEqual(server1.BaseUri.Port, server2.BaseUri.Port);

        await server1.DisposeAsync();
        await server1.DisposeAsync();
    }

    [Fact]
    public async Task MapHttpServer_DisposeWhileUpstreamInFlight_ShutsDownCleanlyWithoutDeadlock()
    {
        using var cache = new TileCacheStore(Path.Combine(_tempDir, "inflight.db"));
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new DelegateHttpMessageHandler(async (_, ct) =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var upstream = new HttpClient(handler);
        var server = new MapHttpServer(cache, upstream, upstreamTimeout: TimeSpan.FromSeconds(10));
        await server.StartAsync();

        using var client = new HttpClient { BaseAddress = server.BaseUri };
        Task<HttpResponseMessage> inflightReq = client.GetAsync("/tiles/satellite/12/2421/1554");
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await server.DisposeAsync();

        // Client either observes connection close or 204, without hanging for 10-30s.
        try
        {
            await inflightReq.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (HttpRequestException)
        {
        }
    }

    [Fact]
    public async Task MapHttpServer_AssetsDirectoryWithTrailingSeparator_ServesStaticAssets()
    {
        string dbPath = Path.Combine(_tempDir, "trailing-slash.db");
        using var cache = new TileCacheStore(dbPath);
        string assetsDirWithTrailingSlash = Path.Combine(AppContext.BaseDirectory, "Assets", "Map")
            + Path.DirectorySeparatorChar;

        await using var server = new MapHttpServer(cache, assetsDirectory: assetsDirWithTrailingSlash);
        await server.StartAsync();

        using var client = new HttpClient { BaseAddress = server.BaseUri };
        HttpResponseMessage rootResp = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, rootResp.StatusCode);
        Assert.Contains("Tulpar Mission Map", await rootResp.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpResponseMessage indexResp = await client.GetAsync("/index.html");
        Assert.Equal(HttpStatusCode.OK, indexResp.StatusCode);

        string assetsDirWithForwardSlash = Path.Combine(AppContext.BaseDirectory, "Assets", "Map") + '/';
        await using var serverForward = new MapHttpServer(cache, assetsDirectory: assetsDirWithForwardSlash);
        await serverForward.StartAsync();
        using var clientForward = new HttpClient { BaseAddress = serverForward.BaseUri };
        Assert.Equal(HttpStatusCode.OK, (await clientForward.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task MapHttpServer_ReregisterMbtilesDuringConcurrentReads_AndDisposeActiveKeepAlive_SucceedsWithoutUnobservedExceptions()
    {
        string dbPath = Path.Combine(_tempDir, "reregister.db");
        string mbtilesPath = Path.Combine(_tempDir, "concurrent.mbtiles");
        const int z = 11;
        const int x = 1210;
        const int xyzY = 777;
        const int tmsY = (1 << z) - 1 - xyzY;
        byte[] payload = [.. SamplePngTile, 0x77];

        CreateSampleMbtiles(mbtilesPath, "Concurrent", "png", [(z, x, tmsY, payload)]);

        var unobserved = new List<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, args) =>
        {
            lock (unobserved)
            {
                unobserved.Add(args.Exception);
            }
            args.SetObserved();
        };
        TaskScheduler.UnobservedTaskException += onUnobserved;

        try
        {
            using var cache = new TileCacheStore(dbPath);
            var server = new MapHttpServer(cache);
            string firstLayerId = server.RegisterMbtiles(mbtilesPath);
            await server.StartAsync();

            using var client = new HttpClient { BaseAddress = server.BaseUri };

            // Exercise concurrent tile reads while re-registering the same .mbtiles path
            Task[] readTasks = Enumerable.Range(0, 16).Select(async _ =>
            {
                for (int i = 0; i < 10; i++)
                {
                    HttpResponseMessage resp = await client.GetAsync($"/tiles/{firstLayerId}/{z}/{x}/{xyzY}");
                    Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                    Assert.Equal(payload, await resp.Content.ReadAsByteArrayAsync());
                }
            }).ToArray();

            Task reregisterTask = Task.Run(() =>
            {
                for (int i = 0; i < 20; i++)
                {
                    string id = server.RegisterMbtiles(mbtilesPath);
                    Assert.Equal(firstLayerId, id);
                }
            });

            await Task.WhenAll([.. readTasks, reregisterTask]);

            // Leave client keep-alive connection open & idle, then dispose server to force Client.Close()
            // while HandleClientConnectionAsync is awaiting ReadHttpRequestAsync.
            await server.DisposeAsync();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            lock (unobserved)
            {
                Assert.Empty(unobserved);
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= onUnobserved;
        }
    }

    [Fact]
    public void MbtilesReader_MalformedTilesSchema_ThrowsInvalidDataException_AndQueryFaultReturnsNull()
    {
        // 1. Missing required columns in 'tiles' table
        string missingColPath = Path.Combine(_tempDir, "missing-column.mbtiles");
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = missingColPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ConnectionString))
        {
            conn.Open();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE tiles (zoom_level INTEGER, tile_column INTEGER, tile_data BLOB);";
            cmd.ExecuteNonQuery();
        }

        InvalidDataException colEx = Assert.Throws<InvalidDataException>(() => new MbtilesReader(missingColPath));
        Assert.Contains("tile_row", colEx.Message, StringComparison.Ordinal);

        // 2. Missing 'tiles' table completely
        string missingTablePath = Path.Combine(_tempDir, "missing-table.mbtiles");
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = missingTablePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ConnectionString))
        {
            conn.Open();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE metadata (name TEXT, value TEXT);";
            cmd.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() => new MbtilesReader(missingTablePath));

        // 3. View backed by a table dropped after open -> TryGetTileXyz catches SqliteException cleanly
        string brokenViewPath = Path.Combine(_tempDir, "broken-view.mbtiles");
        string brokenViewCs = new SqliteConnectionStringBuilder
        {
            DataSource = brokenViewPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ConnectionString;

        using (var conn = new SqliteConnection(brokenViewCs))
        {
            conn.Open();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE backing_tiles (zoom_level INTEGER, tile_column INTEGER, tile_row INTEGER, tile_data BLOB);
                CREATE VIEW tiles AS SELECT zoom_level, tile_column, tile_row, tile_data FROM backing_tiles;
                """;
            cmd.ExecuteNonQuery();
        }

        using var reader = new MbtilesReader(brokenViewPath);
        using (var dropConn = new SqliteConnection(brokenViewCs))
        {
            dropConn.Open();
            using SqliteCommand dropCmd = dropConn.CreateCommand();
            dropCmd.CommandText = "DROP TABLE backing_tiles;";
            dropCmd.ExecuteNonQuery();
        }

        Assert.Null(reader.TryGetTileXyz(0, 0, 0));
    }

    [Fact]
    public async Task MapHttpServer_TileFaultIsolation_ReturnsCleanHttpErrorWithoutCrashing()
    {
        string dbPath = Path.Combine(_tempDir, "fault-isolation.db");
        string mbtilesPath = Path.Combine(_tempDir, "healthy.mbtiles");
        CreateSampleMbtiles(mbtilesPath, "Healthy", "png", [(0, 0, 0, SamplePngTile)]);

        var unobserved = new List<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, args) =>
        {
            lock (unobserved)
            {
                unobserved.Add(args.Exception);
            }
            args.SetObserved();
        };
        TaskScheduler.UnobservedTaskException += onUnobserved;

        try
        {
            using var cache = new TileCacheStore(dbPath);
            await using var server = new MapHttpServer(cache);
            string mbLayerId = server.RegisterMbtiles(mbtilesPath);
            await server.StartAsync();

            // Drop the cache table externally to force SqliteException on XYZ tile lookup
            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ConnectionString))
            {
                conn.Open();
                using SqliteCommand drop = conn.CreateCommand();
                drop.CommandText = "DROP TABLE tiles;";
                drop.ExecuteNonQuery();
            }

            using var client = new HttpClient { BaseAddress = server.BaseUri };

            HttpResponseMessage faultResp = await client.GetAsync("/tiles/street/10/600/380");
            Assert.Equal(HttpStatusCode.InternalServerError, faultResp.StatusCode);

            // Server and unaffected routes remain healthy after faulted tile request
            HttpResponseMessage healthResp = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, healthResp.StatusCode);

            HttpResponseMessage mbResp = await client.GetAsync($"/tiles/{mbLayerId}/0/0/0");
            Assert.Equal(HttpStatusCode.OK, mbResp.StatusCode);
            Assert.Equal(SamplePngTile, await mbResp.Content.ReadAsByteArrayAsync());

            await server.DisposeAsync();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            lock (unobserved)
            {
                Assert.Empty(unobserved);
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= onUnobserved;
        }
    }

    [Fact]
    public async Task OfflineRegionDownloader_InvalidZoomAndBounds_ThrowsAndRespectsRequestTimeout()
    {
        string dbPath = Path.Combine(_tempDir, "invalid-bounds.db");
        using var cache = new TileCacheStore(dbPath);
        var provider = new TileProviderConfig(
            "test-topo",
            "Test Topo",
            "https://tiles.local/{z}/{x}/{y}.png",
            MaxZoom: 14,
            MinZoom: 2);

        // 1. Invalid bounds / zoom / tile limit throw ArgumentOutOfRangeException
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OfflineRegionDownloader.EnumerateTiles(double.NaN, 32.0, 40.0, 33.0, 5, 10, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OfflineRegionDownloader.EnumerateTiles(39.0, double.PositiveInfinity, 40.0, 33.0, 5, 10, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OfflineRegionDownloader.EnumerateTiles(39.0, 32.0, 40.0, 33.0, -1, 10, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OfflineRegionDownloader.EnumerateTiles(39.0, 32.0, 40.0, 33.0, 5, 23, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OfflineRegionDownloader.EnumerateTiles(39.0, 32.0, 40.0, 33.0, 10, 8, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OfflineRegionDownloader.EnumerateTiles(39.0, 32.0, 40.0, 33.0, 5, 10, 0));

        int calls = 0;
        using var slowHandler = new DelegateHttpMessageHandler(async (_, ct) =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new HttpClient(slowHandler);
        var downloader = new OfflineRegionDownloader(
            cache,
            http,
            delayMsPerRequest: 0,
            requestTimeout: TimeSpan.FromMilliseconds(120));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            downloader.DownloadBoundsAsync(provider, 39.0, 32.0, 40.0, 33.0, 12, 10, 100, null, CancellationToken.None));

        // 2. Requested zoom outside provider's supported range returns empty result without HTTP calls
        OfflineDownloadResult outOfProviderRange = await downloader.DownloadBoundsAsync(
            provider,
            minLat: 39.9,
            minLon: 32.8,
            maxLat: 39.95,
            maxLon: 32.85,
            minZoom: 16,
            maxZoom: 18,
            maxTilesLimit: 50,
            progress: null,
            ct: CancellationToken.None);

        Assert.Equal(0, outOfProviderRange.TotalTiles);
        Assert.Equal(0, calls);

        // 3. Per-request timeout aborts hung tile request cleanly as a failed tile instead of hanging
        var sw = Stopwatch.StartNew();
        OfflineDownloadResult timeoutResult = await downloader.DownloadBoundsAsync(
            provider,
            minLat: 39.92,
            minLon: 32.85,
            maxLat: 39.93,
            maxLon: 32.86,
            minZoom: 10,
            maxZoom: 10,
            maxTilesLimit: 10,
            progress: null,
            ct: CancellationToken.None);
        sw.Stop();

        Assert.Equal(1, timeoutResult.TotalTiles);
        Assert.Equal(1, timeoutResult.FailedTiles);
        Assert.Equal(0, timeoutResult.DownloadedTiles);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Expected quick per-request timeout, elapsed={sw.Elapsed}");
    }

    [Fact]
    public async Task MapHttpServer_StaticAssetsIncludeInteractiveRouteAndMeasureTools()
    {
        string dbPath = Path.Combine(_tempDir, "interactive-assets.db");
        using var cache = new TileCacheStore(dbPath);
        await using var server = new MapHttpServer(cache);
        await server.StartAsync();

        using var client = new HttpClient { BaseAddress = server.BaseUri };
        string html = await client.GetStringAsync("/");
        string mapJs = await client.GetStringAsync("/map.js");

        Assert.Contains("MESAFE ÖLÇ / BİTİR", html, StringComparison.Ordinal);
        Assert.Contains("ÖLÇÜMÜ TEMİZLE", html, StringComparison.Ordinal);
        Assert.Contains("tulpar-wp-midpoint", html, StringComparison.Ordinal);
        Assert.Contains("is-active", html, StringComparison.Ordinal);

        Assert.Contains("missionMovePreview", mapJs, StringComparison.Ordinal);
        Assert.Contains("missionInsertAfter", mapJs, StringComparison.Ordinal);
        Assert.Contains("setActiveWaypoint", mapJs, StringComparison.Ordinal);
        Assert.Contains("tulpar-measure-source", mapJs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapHttpServer_StaticAssetsIncludeSelectedWaypointAndLayerToggles()
    {
        string dbPath = Path.Combine(_tempDir, "layer-selection-assets.db");
        using var cache = new TileCacheStore(dbPath);
        await using var server = new MapHttpServer(cache);
        await server.StartAsync();

        using var client = new HttpClient { BaseAddress = server.BaseUri };
        string html = await client.GetStringAsync("/");
        string mapJs = await client.GetStringAsync("/map.js");

        Assert.Contains(".tulpar-wp-marker.is-selected", html, StringComparison.Ordinal);
        Assert.Contains("#38BDF8", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("KATMANLAR", html, StringComparison.Ordinal);
        Assert.Contains("layers-toggle-btn", html, StringComparison.Ordinal);
        Assert.Contains("layers-panel", html, StringComparison.Ordinal);
        Assert.Contains("data-layer-group=\"trail\"", html, StringComparison.Ordinal);
        Assert.Contains("data-layer-group=\"mission\"", html, StringComparison.Ordinal);
        Assert.Contains("data-layer-group=\"fence\"", html, StringComparison.Ordinal);
        Assert.Contains("data-layer-group=\"markers\"", html, StringComparison.Ordinal);
        Assert.Contains("Uçuş İzi (Trail)", html, StringComparison.Ordinal);
        Assert.Contains("Görev Rotası", html, StringComparison.Ordinal);
        Assert.Contains("Geofence", html, StringComparison.Ordinal);
        Assert.Contains("WP İşaretleri", html, StringComparison.Ordinal);

        Assert.Contains("setSelectedWaypoint", mapJs, StringComparison.Ordinal);
        Assert.Contains("selectedSeq", mapJs, StringComparison.Ordinal);
        Assert.Contains("is-selected", mapJs, StringComparison.Ordinal);
        Assert.Contains("setLayoutProperty", mapJs, StringComparison.Ordinal);
        Assert.Contains("data-layer-group", mapJs, StringComparison.Ordinal);
        Assert.Contains("tulpar-trail-line", mapJs, StringComparison.Ordinal);
        Assert.Contains("tulpar-mission-line", mapJs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapHostAssets_IncludeRallyPointControlsAndMarkers()
    {
        string dbPath = Path.Combine(_tempDir, "rally-map.db");
        using var cache = new TileCacheStore(dbPath);
        await using var server = new MapHttpServer(cache);
        await server.StartAsync();

        using var client = new HttpClient { BaseAddress = server.BaseUri };
        string html = await client.GetStringAsync("/");
        string mapJs = await client.GetStringAsync("/map.js");

        Assert.Contains(".tulpar-rally-marker", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"rally-add\"", html, StringComparison.Ordinal);
        Assert.Contains("data-layer-group=\"rally\"", html, StringComparison.Ordinal);
        Assert.Contains("setRally", mapJs, StringComparison.Ordinal);
        Assert.Contains("rallyAdd", mapJs, StringComparison.Ordinal);
        Assert.Contains("rallyMove", mapJs, StringComparison.Ordinal);
        Assert.Contains("setFollowVehicle", mapJs, StringComparison.Ordinal);
        Assert.Contains("followVehicle", mapJs, StringComparison.Ordinal);
    }

    private static void CreateSampleMbtiles(
        string path,
        string name,
        string format,
        IReadOnlyList<(int Z, int X, int TmsY, byte[] Data)> tiles)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };

        using var conn = new SqliteConnection(csb.ConnectionString);
        conn.Open();

        using (SqliteCommand ddl = conn.CreateCommand())
        {
            ddl.CommandText = """
                CREATE TABLE metadata (name TEXT, value TEXT);
                CREATE TABLE tiles (zoom_level INTEGER, tile_column INTEGER, tile_row INTEGER, tile_data BLOB);
                """;
            ddl.ExecuteNonQuery();
        }

        using (SqliteCommand meta = conn.CreateCommand())
        {
            meta.CommandText = "INSERT INTO metadata (name, value) VALUES ('name', $name), ('format', $fmt);";
            meta.Parameters.AddWithValue("$name", name);
            meta.Parameters.AddWithValue("$fmt", format);
            meta.ExecuteNonQuery();
        }

        foreach ((int z, int x, int tmsY, byte[] data) in tiles)
        {
            using SqliteCommand ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO tiles (zoom_level, tile_column, tile_row, tile_data) VALUES ($z, $x, $y, $data);";
            ins.Parameters.AddWithValue("$z", z);
            ins.Parameters.AddWithValue("$x", x);
            ins.Parameters.AddWithValue("$y", tmsY);
            ins.Parameters.AddWithValue("$data", data);
            ins.ExecuteNonQuery();
        }
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
