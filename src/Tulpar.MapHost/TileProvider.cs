namespace Tulpar.MapHost;

/// <summary>
/// Configuration for a slippy-map XYZ tile provider.
/// </summary>
public sealed record TileProviderConfig(
    string Id,
    string Name,
    string UrlTemplate,
    int MaxZoom,
    int MinZoom = 0,
    bool IsBuiltIn = true);

/// <summary>
/// Built-in open raster tile layers supported out of the box by Tulpar GCS.
/// </summary>
public static class BuiltInTileProviders
{
    public static TileProviderConfig GoogleSatelliteHd { get; } = new(
        Id: "google-satellite-hd",
        Name: "UYDU HD (GOOGLE 2X)",
        UrlTemplate: "https://mt1.google.com/vt/lyrs=s&scale=2&x={x}&y={y}&z={z}",
        MaxZoom: 21,
        MinZoom: 0,
        IsBuiltIn: true);

    public static TileProviderConfig GoogleHybridHd { get; } = new(
        Id: "google-hybrid-hd",
        Name: "HİBRİT HD (GOOGLE 2X)",
        UrlTemplate: "https://mt1.google.com/vt/lyrs=y&scale=2&x={x}&y={y}&z={z}",
        MaxZoom: 21,
        MinZoom: 0,
        IsBuiltIn: true);

    public static TileProviderConfig EsriClarity { get; } = new(
        Id: "esri-clarity",
        Name: "UYDU NETLİK (ESRI CLARITY)",
        UrlTemplate: "https://clarity.maptiles.arcgis.com/arcgis/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}",
        MaxZoom: 19,
        MinZoom: 0,
        IsBuiltIn: true);

    public static TileProviderConfig Satellite { get; } = new(
        Id: "satellite",
        Name: "UYDU (ESRI)",
        UrlTemplate: "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}",
        MaxZoom: 19,
        MinZoom: 0,
        IsBuiltIn: true);

    public static TileProviderConfig GoogleStreetHd { get; } = new(
        Id: "google-street-hd",
        Name: "HARİTA HD (GOOGLE 2X)",
        UrlTemplate: "https://mt1.google.com/vt/lyrs=m&scale=2&x={x}&y={y}&z={z}",
        MaxZoom: 21,
        MinZoom: 0,
        IsBuiltIn: true);

    public static TileProviderConfig Street { get; } = new(
        Id: "street",
        Name: "SOKAK (OSM)",
        UrlTemplate: "https://tile.openstreetmap.org/{z}/{x}/{y}.png",
        MaxZoom: 19,
        MinZoom: 0,
        IsBuiltIn: true);

    public static TileProviderConfig Topo { get; } = new(
        Id: "topo",
        Name: "TOPOGRAFİK (OPENTOPOMAP)",
        UrlTemplate: "https://tile.opentopomap.org/{z}/{x}/{y}.png",
        MaxZoom: 17,
        MinZoom: 0,
        IsBuiltIn: true);

    public static IReadOnlyList<TileProviderConfig> All { get; } =
    [
        GoogleSatelliteHd,
        GoogleHybridHd,
        EsriClarity,
        Satellite,
        GoogleStreetHd,
        Street,
        Topo
    ];
}

internal static class TileFormatHelper
{
    public static string FormatTileUrl(string urlTemplate, int z, int x, int y)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlTemplate);

        string url = urlTemplate
            .Replace("{z}", z.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{x}", x.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{y}", y.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{s}", "a", StringComparison.OrdinalIgnoreCase);

        if (url.Contains("{-y}", StringComparison.OrdinalIgnoreCase) && z is >= 0 and <= 30)
        {
            long tmsY = (1L << z) - 1 - y;
            url = url.Replace("{-y}", tmsY.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        }

        return url;
    }

    public static string DetectImageContentType(ReadOnlySpan<byte> data, string? formatHint)
    {
        if (data.Length >= 8 &&
            data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
            data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            return "image/png";
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (data.Length >= 12 &&
            data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
            data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
        {
            return "image/webp";
        }

        if (data.Length >= 4 &&
            data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'8')
        {
            return "image/gif";
        }

        return formatHint?.Trim().ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "webp" => "image/webp",
            "gif" => "image/gif",
            "pbf" or "mvt" => "application/x-protobuf",
            _ => "image/png"
        };
    }
}
