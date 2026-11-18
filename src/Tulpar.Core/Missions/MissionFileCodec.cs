using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tulpar.Core.Mavlink;

namespace Tulpar.Core.Missions;

/// <summary>
/// Serializes and deserializes mission/geofence items in QGC WPL 110 (.waypoints) and Tulpar JSON formats.
/// </summary>
public static class MissionFileCodec
{
    private const string QgcHeader = "QGC WPL 110";
    private const int QgcColumnCount = 12;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Exports mission items to QGC WPL 110 tab-separated format using <see cref="CultureInfo.InvariantCulture"/>.
    /// </summary>
    public static string ExportQgcWpl110(IReadOnlyList<MissionItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        ValidateItemsForExport(items, nameof(items));

        var sb = new StringBuilder();
        sb.Append(QgcHeader).Append('\n');

        for (int i = 0; i < items.Count; i++)
        {
            MissionItem item = items[i];
            int currentWp = i == 0 ? 1 : 0;
            MavFrame wplFrame = NormalizeFrameForWplExport(item.Frame);

            sb.Append(item.Seq.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(currentWp.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(((byte)wplFrame).ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(((ushort)item.Command).ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(item.Param1.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(item.Param2.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(item.Param3.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(item.Param4.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(item.Latitude.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(item.Longitude.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(item.Altitude.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(item.Autocontinue ? "1" : "0").Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Imports mission items from QGC WPL 110 tab-separated text using <see cref="CultureInfo.InvariantCulture"/>.
    /// </summary>
    public static IReadOnlyList<MissionItem> ImportQgcWpl110(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        string[] rawLines = content.Split('\n');
        var items = new List<MissionItem>();
        bool headerFound = false;

        for (int i = 0; i < rawLines.Length; i++)
        {
            string line = rawLines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!headerFound)
            {
                if (!string.Equals(line.Trim(), QgcHeader, StringComparison.Ordinal))
                {
                    throw new FormatException($"Invalid QGC WPL header '{line.Trim()}'; expected '{QgcHeader}'.");
                }

                headerFound = true;
                continue;
            }

            if (line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            items.Add(ParseWplLine(line, i + 1));
        }

        if (!headerFound)
        {
            throw new FormatException($"Empty QGC WPL content; expected '{QgcHeader}' header.");
        }

        return items;
    }

    /// <summary>
    /// Exports a <see cref="MissionDocument"/> (mission + geofence items) as indented JSON.
    /// </summary>
    public static string ExportJson(MissionDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (doc.Items is not null)
        {
            ValidateItemsForExport(doc.Items, nameof(doc));
        }

        if (doc.FenceItems is not null)
        {
            ValidateItemsForExport(doc.FenceItems, nameof(doc));
        }

        return JsonSerializer.Serialize(doc, JsonOptions);
    }

    /// <summary>
    /// Imports a <see cref="MissionDocument"/> from JSON.
    /// </summary>
    public static MissionDocument ImportJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        MissionDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<MissionDocument>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Invalid mission JSON.", ex);
        }

        if (doc is null)
        {
            throw new FormatException("JSON content did not deserialize into a MissionDocument.");
        }

        IReadOnlyList<MissionItem> items = doc.Items ?? Array.Empty<MissionItem>();
        IReadOnlyList<MissionItem> fenceItems = doc.FenceItems ?? Array.Empty<MissionItem>();

        ValidateItemsForImport(items, "items");
        ValidateItemsForImport(fenceItems, "fenceItems");

        return doc with
        {
            Items = items,
            FenceItems = fenceItems
        };
    }

    private static MissionItem ParseWplLine(string line, int lineNumber)
    {
        string[] cols = line.Split('\t');
        if (cols.Length != QgcColumnCount)
        {
            throw new FormatException(
                $"Line {lineNumber}: expected {QgcColumnCount} tab-separated columns in QGC WPL 110 row, found {cols.Length}.");
        }

        int seq = ParseInt(cols[0], lineNumber, "seq");
        _ = ParseInt(cols[1], lineNumber, "current");
        byte frameByte = ParseByte(cols[2], lineNumber, "frame");
        ushort commandUshort = ParseUShort(cols[3], lineNumber, "command");
        float param1 = ParseFloat(cols[4], lineNumber, "param1");
        float param2 = ParseFloat(cols[5], lineNumber, "param2");
        float param3 = ParseFloat(cols[6], lineNumber, "param3");
        float param4 = ParseFloat(cols[7], lineNumber, "param4");
        double lat = ParseDouble(cols[8], lineNumber, "latitude");
        double lon = ParseDouble(cols[9], lineNumber, "longitude");
        float alt = ParseFloat(cols[10], lineNumber, "altitude");
        int autocontinueInt = ParseInt(cols[11], lineNumber, "autocontinue");

        MavFrame normalizedFrame = NormalizeFrameFromWplImport((MavFrame)frameByte);

        return new MissionItem(
            Seq: seq,
            Frame: normalizedFrame,
            Command: (MavCmd)commandUshort,
            Param1: param1,
            Param2: param2,
            Param3: param3,
            Param4: param4,
            Latitude: lat,
            Longitude: lon,
            Altitude: alt,
            Autocontinue: autocontinueInt != 0);
    }

    private static MavFrame NormalizeFrameForWplExport(MavFrame frame) =>
        frame switch
        {
            MavFrame.GlobalRelativeAltInt => MavFrame.GlobalRelativeAlt,
            MavFrame.GlobalInt => MavFrame.Global,
            _ => frame
        };

    private static MavFrame NormalizeFrameFromWplImport(MavFrame frame) =>
        frame switch
        {
            MavFrame.GlobalRelativeAlt => MavFrame.GlobalRelativeAltInt,
            MavFrame.Global => MavFrame.GlobalInt,
            _ => frame
        };

    private static int ParseInt(string value, int lineNumber, string field) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : throw new FormatException($"Line {lineNumber}: invalid integer for '{field}': '{value}'.");

    private static byte ParseByte(string value, int lineNumber, string field) =>
        byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte result)
            ? result
            : throw new FormatException($"Line {lineNumber}: invalid byte for '{field}': '{value}'.");

    private static ushort ParseUShort(string value, int lineNumber, string field) =>
        ushort.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort result)
            ? result
            : throw new FormatException($"Line {lineNumber}: invalid command id for '{field}': '{value}'.");

    private static void ValidateItemsForExport(IReadOnlyList<MissionItem> items, string paramName)
    {
        for (int i = 0; i < items.Count; i++)
        {
            MissionItem item = items[i] ?? throw new ArgumentException($"Mission item at index {i} is null.", paramName);
            if (!IsFiniteItem(item, out string? invalidField))
            {
                throw new ArgumentException(
                    $"Mission item at index {i} has non-finite value in '{invalidField}'.",
                    paramName);
            }
        }
    }

    private static void ValidateItemsForImport(IReadOnlyList<MissionItem> items, string section)
    {
        for (int i = 0; i < items.Count; i++)
        {
            MissionItem item = items[i] ?? throw new FormatException($"Mission item in '{section}' at index {i} is null.");
            if (!IsFiniteItem(item, out string? invalidField))
            {
                throw new FormatException(
                    $"Mission item in '{section}' at index {i} has non-finite value in '{invalidField}'.");
            }
        }
    }

    private static bool IsFiniteItem(MissionItem item, out string? invalidField)
    {
        if (!double.IsFinite(item.Latitude)) { invalidField = nameof(item.Latitude); return false; }
        if (!double.IsFinite(item.Longitude)) { invalidField = nameof(item.Longitude); return false; }
        if (!float.IsFinite(item.Altitude)) { invalidField = nameof(item.Altitude); return false; }
        if (!float.IsFinite(item.Param1)) { invalidField = nameof(item.Param1); return false; }
        if (!float.IsFinite(item.Param2)) { invalidField = nameof(item.Param2); return false; }
        if (!float.IsFinite(item.Param3)) { invalidField = nameof(item.Param3); return false; }
        if (!float.IsFinite(item.Param4)) { invalidField = nameof(item.Param4); return false; }
        invalidField = null;
        return true;
    }

    private static float ParseFloat(string value, int lineNumber, string field) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result) && float.IsFinite(result)
            ? result
            : throw new FormatException($"Line {lineNumber}: invalid or non-finite float for '{field}': '{value}'.");

    private static double ParseDouble(string value, int lineNumber, string field) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) && double.IsFinite(result)
            ? result
            : throw new FormatException($"Line {lineNumber}: invalid or non-finite double for '{field}': '{value}'.");
}
