using Tulpar.Core.Mavlink;

namespace Tulpar.Core.Missions;

/// <summary>
/// Per-waypoint leg geometry and elevation metrics for mission route cards and elevation profiles.
/// </summary>
public sealed record MissionLegMetric(
    int Seq,
    double LegDistanceMeters,
    double LegBearingDeg,
    double CumulativeDistanceMeters,
    double AltitudeMeters);

/// <summary>
/// Aggregate distance, flight duration, elevation bounds, and per-leg metrics for a mission plan.
/// </summary>
public sealed record FlightMetricsSummary(
    double TotalDistanceMeters,
    TimeSpan EstimatedFlightDuration,
    IReadOnlyList<MissionLegMetric> Legs,
    double MinAltitudeMeters = 0.0,
    double MaxAltitudeMeters = 0.0,
    double TotalAscentMeters = 0.0,
    double TotalDescentMeters = 0.0);

/// <summary>
/// Computes route leg distances, forward bearings, cumulative path length, elevation profile metrics,
/// and estimated flight duration for a sequence of <see cref="MissionItem"/>s.
/// </summary>
public static class FlightMetricsEstimator
{
    private const double EarthRadiusMeters = 6371000.0;
    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    /// <summary>
    /// Computes aggregate and per-leg flight metrics for <paramref name="items"/> at <paramref name="cruiseSpeedMps"/>.
    /// </summary>
    public static FlightMetricsSummary Estimate(
        IReadOnlyList<MissionItem> items,
        double cruiseSpeedMps = 15.0,
        (double Latitude, double Longitude, double AltitudeMeters)? homePosition = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (!double.IsFinite(cruiseSpeedMps) || cruiseSpeedMps <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cruiseSpeedMps),
                cruiseSpeedMps,
                "Cruise speed must be finite and greater than 0 m/s.");
        }

        if (items.Count == 0)
        {
            return new FlightMetricsSummary(0.0, TimeSpan.Zero, Array.Empty<MissionLegMetric>());
        }

        var legs = new List<MissionLegMetric>(items.Count);
        double cumulativeDistance = 0.0;

        bool hasPrevPos = false;
        double prevLat = 0.0;
        double prevLon = 0.0;
        double prevAlt = 0.0;

        if (homePosition is { } home && IsValidGeoCoordinate(home.Latitude, home.Longitude))
        {
            hasPrevPos = true;
            prevLat = home.Latitude;
            prevLon = home.Longitude;
            prevAlt = double.IsFinite(home.AltitudeMeters) ? home.AltitudeMeters : 0.0;
        }

        bool hasElevationSample = false;
        double minAlt = 0.0;
        double maxAlt = 0.0;
        double totalAscent = 0.0;
        double totalDescent = 0.0;

        for (int i = 0; i < items.Count; i++)
        {
            MissionItem item = items[i] ?? throw new ArgumentException($"Mission item at index {i} is null.", nameof(items));

            if (!double.IsFinite(item.Latitude) || item.Latitude is < -90.0 or > 90.0)
            {
                throw new ArgumentOutOfRangeException(nameof(items), item.Latitude, $"Item seq {item.Seq} has invalid latitude.");
            }

            if (!double.IsFinite(item.Longitude) || item.Longitude is < -180.0 or > 180.0)
            {
                throw new ArgumentOutOfRangeException(nameof(items), item.Longitude, $"Item seq {item.Seq} has invalid longitude.");
            }

            if (!float.IsFinite(item.Altitude))
            {
                throw new ArgumentOutOfRangeException(nameof(items), item.Altitude, $"Item seq {item.Seq} has non-finite altitude.");
            }

            bool hasCoord = TryResolveNavCoordinate(item, homePosition, out double targetLat, out double targetLon);
            if (!hasCoord)
            {
                double fallbackAlt = hasElevationSample ? prevAlt : item.Altitude;
                legs.Add(new MissionLegMetric(
                    Seq: item.Seq,
                    LegDistanceMeters: 0.0,
                    LegBearingDeg: 0.0,
                    CumulativeDistanceMeters: cumulativeDistance,
                    AltitudeMeters: fallbackAlt));
                continue;
            }

            double legDistance = 0.0;
            double legBearing = 0.0;
            if (hasPrevPos)
            {
                legDistance = ComputeHaversineMeters(prevLat, prevLon, targetLat, targetLon);
                legBearing = legDistance > 0.0
                    ? ComputeBearingDeg(prevLat, prevLon, targetLat, targetLon)
                    : 0.0;
                cumulativeDistance += legDistance;
            }

            double itemAlt = item.Altitude;
            if (!hasElevationSample)
            {
                hasElevationSample = true;
                minAlt = itemAlt;
                maxAlt = itemAlt;
                if (hasPrevPos)
                {
                    AccumulateVerticalDelta(prevAlt, itemAlt, ref totalAscent, ref totalDescent);
                }
            }
            else
            {
                minAlt = Math.Min(minAlt, itemAlt);
                maxAlt = Math.Max(maxAlt, itemAlt);
                AccumulateVerticalDelta(prevAlt, itemAlt, ref totalAscent, ref totalDescent);
            }

            legs.Add(new MissionLegMetric(
                Seq: item.Seq,
                LegDistanceMeters: legDistance,
                LegBearingDeg: legBearing,
                CumulativeDistanceMeters: cumulativeDistance,
                AltitudeMeters: itemAlt));

            hasPrevPos = true;
            prevLat = targetLat;
            prevLon = targetLon;
            prevAlt = itemAlt;
        }

        TimeSpan duration = TimeSpan.FromSeconds(cumulativeDistance / cruiseSpeedMps);
        return new FlightMetricsSummary(
            TotalDistanceMeters: cumulativeDistance,
            EstimatedFlightDuration: duration,
            Legs: legs,
            MinAltitudeMeters: minAlt,
            MaxAltitudeMeters: maxAlt,
            TotalAscentMeters: totalAscent,
            TotalDescentMeters: totalDescent);
    }

    /// <summary>
    /// Computes great-circle horizontal distance in meters between two WGS84 coordinates.
    /// </summary>
    public static double ComputeHaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = (lat2 - lat1) * DegToRad;
        double dLon = (lon2 - lon1) * DegToRad;
        double phi1 = lat1 * DegToRad;
        double phi2 = lat2 * DegToRad;

        double sinLat = Math.Sin(dLat / 2.0);
        double sinLon = Math.Sin(dLon / 2.0);
        double a = (sinLat * sinLat) + (Math.Cos(phi1) * Math.Cos(phi2) * sinLon * sinLon);
        double c = 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0.0, 1.0 - a)));
        return EarthRadiusMeters * c;
    }

    /// <summary>
    /// Computes initial forward azimuth in degrees [0, 360) from point 1 to point 2.
    /// </summary>
    public static double ComputeBearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        if (lat1 == lat2 && lon1 == lon2)
        {
            return 0.0;
        }

        double phi1 = lat1 * DegToRad;
        double phi2 = lat2 * DegToRad;
        double dLon = (lon2 - lon1) * DegToRad;

        double y = Math.Sin(dLon) * Math.Cos(phi2);
        double x = (Math.Cos(phi1) * Math.Sin(phi2)) - (Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLon));
        double bearing = ((Math.Atan2(y, x) * RadToDeg) + 360.0) % 360.0;
        return bearing >= 360.0 ? 0.0 : bearing;
    }

    private static bool TryResolveNavCoordinate(
        MissionItem item,
        (double Latitude, double Longitude, double AltitudeMeters)? homePosition,
        out double lat,
        out double lon)
    {
        if (IsNonPositionalCommand(item.Command))
        {
            lat = 0.0;
            lon = 0.0;
            return false;
        }

        if (IsValidGeoCoordinate(item.Latitude, item.Longitude))
        {
            lat = item.Latitude;
            lon = item.Longitude;
            return true;
        }

        if (item.Command == MavCmd.NavReturnToLaunch &&
            homePosition is { } home &&
            IsValidGeoCoordinate(home.Latitude, home.Longitude))
        {
            lat = home.Latitude;
            lon = home.Longitude;
            return true;
        }

        lat = 0.0;
        lon = 0.0;
        return false;
    }

    private static bool IsNonPositionalCommand(MavCmd command) =>
        command is MavCmd.NavDelay
            or MavCmd.DoSetMode
            or MavCmd.DoJump
            or MavCmd.DoChangeSpeed
            or MavCmd.DoSetHome
            or MavCmd.DoSetRelay
            or MavCmd.DoRepeatRelay
            or MavCmd.DoSetServo
            or MavCmd.DoRepeatServo
            or MavCmd.DoPauseContinue
            or MavCmd.DoSetReverse
            or MavCmd.DoDigicamControl
            or MavCmd.DoMountControl
            or MavCmd.DoSetCamTriggDist
            or MavCmd.DoFenceEnable
            or MavCmd.DoParachute
            or MavCmd.DoInvertedFlight
            or MavCmd.DoGripper
            or MavCmd.DoAutotuneEnable
            or MavCmd.DoVtolTransition
            or MavCmd.ComponentArmDisarm
            or MavCmd.GetHomePosition
            or MavCmd.StartRxPair
            or MavCmd.SetMessageInterval
            or MavCmd.RequestMessage;

    private static bool IsValidGeoCoordinate(double lat, double lon) =>
        double.IsFinite(lat) &&
        double.IsFinite(lon) &&
        lat is >= -90.0 and <= 90.0 &&
        lon is >= -180.0 and <= 180.0 &&
        !(lat == 0.0 && lon == 0.0);

    private static void AccumulateVerticalDelta(double fromAlt, double toAlt, ref double totalAscent, ref double totalDescent)
    {
        double delta = toAlt - fromAlt;
        if (delta > 0.0)
        {
            totalAscent += delta;
        }
        else if (delta < 0.0)
        {
            totalDescent += -delta;
        }
    }
}
