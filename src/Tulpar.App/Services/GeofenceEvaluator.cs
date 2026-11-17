namespace Tulpar.App.Services;

public readonly record struct GeoPoint(double Latitude, double Longitude);

/// <summary>
/// Pure geometry evaluator for local polygon geofence containment.
/// </summary>
public static class GeofenceEvaluator
{
    /// <summary>
    /// Returns true if the point is inside (or on the boundary of) the polygon using ray-casting.
    /// Polygons with fewer than 3 vertices are treated as inactive (returns true).
    /// </summary>
    public static bool IsPointInsidePolygon(double latitude, double longitude, IReadOnlyList<GeoPoint> polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);

        if (!double.IsFinite(latitude) || !double.IsFinite(longitude))
        {
            return false;
        }

        if (polygon.Count < 3)
        {
            return true;
        }

        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            double xi = polygon[i].Longitude;
            double yi = polygon[i].Latitude;
            double xj = polygon[j].Longitude;
            double yj = polygon[j].Latitude;

            if (IsPointOnSegment(longitude, latitude, xi, yi, xj, yj))
            {
                return true;
            }

            bool crossesLatBand = (yi > latitude) != (yj > latitude);
            if (crossesLatBand)
            {
                double intersectLon = (xj - xi) * (latitude - yi) / (yj - yi) + xi;
                if (longitude < intersectLon)
                {
                    inside = !inside;
                }
            }
        }

        return inside;
    }

    private static bool IsPointOnSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        const double eps = 1e-9;
        double cross = (px - ax) * (by - ay) - (py - ay) * (bx - ax);
        if (Math.Abs(cross) > eps)
        {
            return false;
        }

        return px >= Math.Min(ax, bx) - eps &&
               px <= Math.Max(ax, bx) + eps &&
               py >= Math.Min(ay, by) - eps &&
               py <= Math.Max(ay, by) + eps;
    }
}
