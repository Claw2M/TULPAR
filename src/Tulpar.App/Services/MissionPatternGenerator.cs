using Tulpar.Core.Mavlink;

namespace Tulpar.App.Services;

public enum MissionPatternKind
{
    ExpandingSpiral,
    ShrinkingSpiral,
    CircularOrbit,
    PolygonGridScan
}

public sealed record MissionPatternRequest(
    MissionPatternKind Kind,
    double CenterLat,
    double CenterLon,
    float AltitudeMeters,
    MavFrame Frame,
    double StartRadiusMeters = 25.0,
    double EndRadiusMeters = 120.0,
    double Turns = 3.0,
    int PointsPerTurn = 12,
    double AreaWidthMeters = 180.0,
    double AreaHeightMeters = 140.0,
    double GridSpacingMeters = 35.0,
    double GridAngleDeg = 0.0,
    bool Clockwise = true,
    bool IncludeTakeoff = false,
    bool CloseLoopWithJump = false,
    IReadOnlyList<(double Lat, double Lon)>? PolygonVertices = null);

/// <summary>
/// Generates structured autonomous flight patterns (spirals, orbits, and polygon interior zig-zag surveys)
/// using local flat-Earth WGS84 meter projections accurate at regional UAV scales.
/// </summary>
public static class MissionPatternGenerator
{
    private const double MetersPerDegLat = 111_320.0;

    public static IReadOnlyList<EditableMissionItem> Generate(MissionPatternRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var items = new List<EditableMissionItem>();
        float alt = Math.Clamp(request.AltitudeMeters, 1f, 5000f);

        if (request.IncludeTakeoff)
        {
            items.Add(new EditableMissionItem(
                seq: 0,
                frame: request.Frame,
                command: MavCmd.NavTakeoff,
                param1: 15f,
                param2: 0f,
                param3: 0f,
                param4: 0f,
                latitude: request.CenterLat,
                longitude: request.CenterLon,
                altitude: alt,
                autocontinue: true));
        }

        int firstPatternSeq = items.Count;

        switch (request.Kind)
        {
            case MissionPatternKind.ExpandingSpiral:
                AppendSpiral(
                    items,
                    request.CenterLat,
                    request.CenterLon,
                    Math.Min(request.StartRadiusMeters, request.EndRadiusMeters),
                    Math.Max(request.StartRadiusMeters, request.EndRadiusMeters),
                    request.Turns,
                    request.PointsPerTurn,
                    request.Clockwise,
                    request.Frame,
                    alt);
                break;

            case MissionPatternKind.ShrinkingSpiral:
                AppendSpiral(
                    items,
                    request.CenterLat,
                    request.CenterLon,
                    Math.Max(request.StartRadiusMeters, request.EndRadiusMeters),
                    Math.Min(request.StartRadiusMeters, request.EndRadiusMeters),
                    request.Turns,
                    request.PointsPerTurn,
                    request.Clockwise,
                    request.Frame,
                    alt);
                break;

            case MissionPatternKind.CircularOrbit:
                AppendCircle(
                    items,
                    request.CenterLat,
                    request.CenterLon,
                    Math.Max(5.0, request.StartRadiusMeters),
                    Math.Clamp(request.PointsPerTurn, 6, 72),
                    request.Clockwise,
                    request.Frame,
                    alt);
                break;

            case MissionPatternKind.PolygonGridScan:
                AppendPolygonScan(items, request, alt);
                break;
        }

        if (request.CloseLoopWithJump && items.Count > firstPatternSeq + 1)
        {
            items.Add(new EditableMissionItem(
                seq: items.Count,
                frame: MavFrame.Mission,
                command: MavCmd.DoJump,
                param1: firstPatternSeq,
                param2: -1f,
                param3: 0f,
                param4: 0f,
                latitude: 0.0,
                longitude: 0.0,
                altitude: 0f,
                autocontinue: true));
        }

        for (int i = 0; i < items.Count; i++)
        {
            items[i].Seq = i;
        }

        return items;
    }

    public static (double Lat, double Lon) OffsetMeters(double originLat, double originLon, double eastMeters, double northMeters)
    {
        double metersPerDegLon = MetersPerDegLat * Math.Max(0.1, Math.Cos(originLat * Math.PI / 180.0));
        return (originLat + (northMeters / MetersPerDegLat), originLon + (eastMeters / metersPerDegLon));
    }

    private static (double EastMeters, double NorthMeters) ToLocalMeters(double originLat, double originLon, double lat, double lon)
    {
        double metersPerDegLon = MetersPerDegLat * Math.Max(0.1, Math.Cos(originLat * Math.PI / 180.0));
        return ((lon - originLon) * metersPerDegLon, (lat - originLat) * MetersPerDegLat);
    }

    private static void AppendSpiral(
        List<EditableMissionItem> items,
        double centerLat,
        double centerLon,
        double startRadiusM,
        double endRadiusM,
        double turns,
        int pointsPerTurn,
        bool clockwise,
        MavFrame frame,
        float alt)
    {
        double clampedTurns = Math.Clamp(turns, 0.5, 12.0);
        int clampedPtsPerTurn = Math.Clamp(pointsPerTurn, 6, 36);
        int totalSteps = Math.Max(4, (int)Math.Round(clampedTurns * clampedPtsPerTurn));
        double totalAngle = clampedTurns * Math.PI * 2.0;
        double dir = clockwise ? 1.0 : -1.0;

        for (int i = 0; i <= totalSteps; i++)
        {
            double t = (double)i / totalSteps;
            double radius = startRadiusM + ((endRadiusM - startRadiusM) * t);
            double theta = dir * totalAngle * t;
            double east = radius * Math.Sin(theta);
            double north = radius * Math.Cos(theta);
            (double lat, double lon) = OffsetMeters(centerLat, centerLon, east, north);
            items.Add(new EditableMissionItem(items.Count, frame, MavCmd.NavWaypoint, 0f, 0f, 0f, 0f, lat, lon, alt));
        }
    }

    private static void AppendCircle(
        List<EditableMissionItem> items,
        double centerLat,
        double centerLon,
        double radiusM,
        int pointCount,
        bool clockwise,
        MavFrame frame,
        float alt)
    {
        double dir = clockwise ? 1.0 : -1.0;
        for (int i = 0; i < pointCount; i++)
        {
            double theta = dir * (2.0 * Math.PI * i / pointCount);
            double east = radiusM * Math.Sin(theta);
            double north = radiusM * Math.Cos(theta);
            (double lat, double lon) = OffsetMeters(centerLat, centerLon, east, north);
            items.Add(new EditableMissionItem(items.Count, frame, MavCmd.NavWaypoint, 0f, 0f, 0f, 0f, lat, lon, alt));
        }
    }

    private static void AppendPolygonScan(List<EditableMissionItem> items, MissionPatternRequest req, float alt)
    {
        double originLat = req.CenterLat;
        double originLon = req.CenterLon;
        List<(double X, double Y)> localPoly;

        if (req.PolygonVertices is { Count: >= 3 })
        {
            originLat = req.PolygonVertices.Average(v => v.Lat);
            originLon = req.PolygonVertices.Average(v => v.Lon);
            localPoly = req.PolygonVertices
                .Select(v => ToLocalMeters(originLat, originLon, v.Lat, v.Lon))
                .ToList();
        }
        else
        {
            double halfW = Math.Max(20.0, req.AreaWidthMeters) / 2.0;
            double halfH = Math.Max(20.0, req.AreaHeightMeters) / 2.0;
            localPoly =
            [
                (-halfW, -halfH),
                (halfW, -halfH),
                (halfW, halfH),
                (-halfW, halfH)
            ];
        }

        double rad = req.GridAngleDeg * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);

        static (double Rx, double Ry) Rotate((double X, double Y) p, double c, double s) =>
            (p.X * c + p.Y * s, -p.X * s + p.Y * c);

        static (double X, double Y) Unrotate((double Rx, double Ry) p, double c, double s) =>
            (p.Rx * c - p.Ry * s, p.Rx * s + p.Ry * c);

        List<(double Rx, double Ry)> rotated = localPoly.Select(p => Rotate(p, cos, sin)).ToList();
        double minY = rotated.Min(p => p.Ry);
        double maxY = rotated.Max(p => p.Ry);
        double spacing = Math.Clamp(req.GridSpacingMeters, 5.0, 500.0);

        // Scan strips cleanly inside polygon bounds using edge intersections
        var intersections = new List<double>();
        bool leftToRight = true;
        double startY = minY + Math.Min(spacing * 0.35, (maxY - minY) * 0.25);

        for (double y = startY; y <= maxY - 1.0; y += spacing)
        {
            intersections.Clear();
            for (int i = 0; i < rotated.Count; i++)
            {
                (double x1, double y1) = rotated[i];
                (double x2, double y2) = rotated[(i + 1) % rotated.Count];

                if ((y1 <= y && y2 > y) || (y2 <= y && y1 > y))
                {
                    double t = (y - y1) / (y2 - y1);
                    intersections.Add(x1 + t * (x2 - x1));
                }
            }

            if (intersections.Count < 2)
            {
                continue;
            }

            intersections.Sort();
            for (int pair = 0; pair + 1 < intersections.Count; pair += 2)
            {
                double xa = intersections[pair];
                double xb = intersections[pair + 1];
                if (xb - xa < 2.0)
                {
                    continue;
                }

                (double rx1, double rx2) = leftToRight ? (xa, xb) : (xb, xa);
                (double e1, double n1) = Unrotate((rx1, y), cos, sin);
                (double e2, double n2) = Unrotate((rx2, y), cos, sin);
                (double lat1, double lon1) = OffsetMeters(originLat, originLon, e1, n1);
                (double lat2, double lon2) = OffsetMeters(originLat, originLon, e2, n2);

                items.Add(new EditableMissionItem(items.Count, req.Frame, MavCmd.NavWaypoint, 0f, 0f, 0f, 0f, lat1, lon1, alt));
                items.Add(new EditableMissionItem(items.Count, req.Frame, MavCmd.NavWaypoint, 0f, 0f, 0f, 0f, lat2, lon2, alt));
            }

            leftToRight = !leftToRight;
        }
    }
}
