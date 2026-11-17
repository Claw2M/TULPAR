using System.Net.Http.Headers;

namespace Tulpar.MapHost;

/// <summary>
/// Progress report emitted during offline bounding-box tile downloads.
/// </summary>
public sealed record OfflineDownloadProgress(int CompletedTiles, int FailedTiles, int TotalTiles);

/// <summary>
/// Summary of a completed offline region download operation.
/// </summary>
public sealed record OfflineDownloadResult(
    int DownloadedTiles,
    int SkippedCachedTiles,
    int FailedTiles,
    int TotalTiles,
    bool Capped);

/// <summary>
/// Rate-limited, cap-enforced bounding-box tile downloader writing tiles into <see cref="TileCacheStore"/>.
/// </summary>
public sealed class OfflineRegionDownloader
{
    private const double MinMercatorLat = -85.05112878;
    private const double MaxMercatorLat = 85.05112878;
    private const int MaxSupportedZoom = 22;

    private readonly TileCacheStore _cache;
    private readonly HttpClient _httpClient;
    private readonly int _delayMsPerRequest;

    public OfflineRegionDownloader(TileCacheStore cache, HttpClient httpClient, int delayMsPerRequest = 75)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentOutOfRangeException.ThrowIfNegative(delayMsPerRequest);

        _cache = cache;
        _httpClient = httpClient;
        _delayMsPerRequest = delayMsPerRequest;
    }

    public static IReadOnlyList<(int Z, int X, int Y)> EnumerateTiles(
        double minLat,
        double minLon,
        double maxLat,
        double maxLon,
        int minZoom,
        int maxZoom,
        int maxTilesLimit)
    {
        return EnumerateTilesCore(minLat, minLon, maxLat, maxLon, minZoom, maxZoom, maxTilesLimit, out _);
    }

    public async Task<OfflineDownloadResult> DownloadBoundsAsync(
        TileProviderConfig provider,
        double minLat,
        double minLon,
        double maxLat,
        double maxLon,
        int minZoom,
        int maxZoom,
        int maxTilesLimit,
        IProgress<OfflineDownloadProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider.UrlTemplate);

        int effectiveMinZoom = Math.Max(minZoom, provider.MinZoom);
        int effectiveMaxZoom = Math.Min(maxZoom, provider.MaxZoom);
        if (effectiveMaxZoom < effectiveMinZoom)
        {
            ValidateBoundsAndLimit(minLat, minLon, maxLat, maxLon, minZoom, maxZoom, maxTilesLimit);
            return new OfflineDownloadResult(0, 0, 0, 0, Capped: false);
        }

        IReadOnlyList<(int Z, int X, int Y)> tiles = EnumerateTilesCore(
            minLat,
            minLon,
            maxLat,
            maxLon,
            effectiveMinZoom,
            effectiveMaxZoom,
            maxTilesLimit,
            out bool capped);

        int total = tiles.Count;
        int downloaded = 0;
        int skipped = 0;
        int failed = 0;
        int networkRequestsAttempted = 0;

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            (int z, int x, int y) = tiles[i];

            byte[]? existing = await _cache.TryGetTileAsync(provider.Id, z, x, y, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                skipped++;
                progress?.Report(new OfflineDownloadProgress(downloaded + skipped, failed, total));
                continue;
            }

            if (networkRequestsAttempted > 0 && _delayMsPerRequest > 0)
            {
                await Task.Delay(_delayMsPerRequest, ct).ConfigureAwait(false);
            }

            networkRequestsAttempted++;
            bool success = await TryDownloadAndStoreTileAsync(provider, z, x, y, ct).ConfigureAwait(false);
            if (success)
            {
                downloaded++;
            }
            else
            {
                failed++;
            }

            progress?.Report(new OfflineDownloadProgress(downloaded + skipped, failed, total));
        }

        return new OfflineDownloadResult(downloaded, skipped, failed, total, capped);
    }

    private async Task<bool> TryDownloadAndStoreTileAsync(
        TileProviderConfig provider,
        int z,
        int x,
        int y,
        CancellationToken ct)
    {
        string url = TileFormatHelper.FormatTileUrl(provider.UrlTemplate, z, x, y);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            {
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("TulparMissionPlanner", "1.0"));
            }

            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return false;
            }

            await _cache.PutTileAsync(provider.Id, z, x, y, bytes, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static IReadOnlyList<(int Z, int X, int Y)> EnumerateTilesCore(
        double minLat,
        double minLon,
        double maxLat,
        double maxLon,
        int minZoom,
        int maxZoom,
        int maxTilesLimit,
        out bool capped)
    {
        ValidateBoundsAndLimit(minLat, minLon, maxLat, maxLon, minZoom, maxZoom, maxTilesLimit);

        double latLow = Math.Clamp(Math.Min(minLat, maxLat), MinMercatorLat, MaxMercatorLat);
        double latHigh = Math.Clamp(Math.Max(minLat, maxLat), MinMercatorLat, MaxMercatorLat);
        double lonLow = Math.Clamp(Math.Min(minLon, maxLon), -180.0, 180.0);
        double lonHigh = Math.Clamp(Math.Max(minLon, maxLon), -180.0, 180.0);

        var tiles = new List<(int Z, int X, int Y)>();
        capped = false;

        for (int z = minZoom; z <= maxZoom; z++)
        {
            int xStart = LonToTileX(lonLow, z);
            int xEnd = LonToTileX(lonHigh, z);
            // In Slippy Map XYZ, northern latitudes have smaller Y values.
            int yStart = LatToTileY(latHigh, z);
            int yEnd = LatToTileY(latLow, z);

            for (int x = xStart; x <= xEnd; x++)
            {
                for (int y = yStart; y <= yEnd; y++)
                {
                    if (tiles.Count >= maxTilesLimit)
                    {
                        capped = true;
                        return tiles;
                    }

                    tiles.Add((z, x, y));
                }
            }
        }

        return tiles;
    }

    private static void ValidateBoundsAndLimit(
        double minLat,
        double minLon,
        double maxLat,
        double maxLon,
        int minZoom,
        int maxZoom,
        int maxTilesLimit)
    {
        if (!double.IsFinite(minLat) || !double.IsFinite(maxLat) ||
            !double.IsFinite(minLon) || !double.IsFinite(maxLon))
        {
            throw new ArgumentOutOfRangeException(nameof(minLat), "Coordinates must be finite numbers.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(minZoom);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxZoom, MaxSupportedZoom);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxZoom, minZoom);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxTilesLimit, 0);
    }

    private static int LonToTileX(double lonDeg, int z)
    {
        int n = 1 << z;
        int x = (int)Math.Floor((lonDeg + 180.0) / 360.0 * n);
        return Math.Clamp(x, 0, n - 1);
    }

    private static int LatToTileY(double latDeg, int z)
    {
        int n = 1 << z;
        double latRad = latDeg * Math.PI / 180.0;
        double mercator = Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad));
        int y = (int)Math.Floor((1.0 - mercator / Math.PI) / 2.0 * n);
        return Math.Clamp(y, 0, n - 1);
    }
}
