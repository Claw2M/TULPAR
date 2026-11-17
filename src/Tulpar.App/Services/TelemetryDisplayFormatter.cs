using System.Globalization;
using Tulpar.Core.Mavlink;
using Tulpar.Core.Session;
using Tulpar.Core.Vehicles;

namespace Tulpar.App.Services;

/// <summary>
/// Pure telemetry formatting and coordinate math helpers for the Tulpar cockpit UI.
/// </summary>
public static class TelemetryDisplayFormatter
{
    public static string FormatVehicleBadge(TelemetrySnapshot snapshot, IVehicleProfile? profile)
    {
        if (!snapshot.IsConnected || snapshot.SystemId == 0)
        {
            return "BAĞLI DEĞİL";
        }

        string label = profile?.DisplayLabel ?? snapshot.VehicleDisplayLabel;
        string ap = snapshot.Autopilot switch
        {
            MavAutopilot.ArduPilotMega => "ARDUPILOT",
            MavAutopilot.Px4 => "PX4",
            _ => snapshot.Autopilot.ToString().ToUpperInvariant()
        };
        return $"{label} · {ap}";
    }

    public static string FormatBattery(TelemetrySnapshot snapshot)
    {
        if (snapshot.BatteryVoltageVolts <= 0.0 && snapshot.BatteryRemainingPercent < 0)
        {
            return "-- V · --%";
        }

        string pct = snapshot.BatteryRemainingPercent >= 0 ? $"{snapshot.BatteryRemainingPercent}%" : "--";
        return string.Create(CultureInfo.InvariantCulture, $"{snapshot.BatteryVoltageVolts:0.0}V · {pct}");
    }

    public static string FormatGpsFix(GpsFixType fix) =>
        fix switch
        {
            GpsFixType.NoGps => "NO GPS",
            GpsFixType.NoFix => "NO FIX",
            GpsFixType.Fix2D => "2D FIX",
            GpsFixType.Fix3D => "3D FIX",
            GpsFixType.Dgps => "DGPS",
            GpsFixType.RtkFloat => "RTK FLT",
            GpsFixType.RtkFixed => "RTK FIX",
            _ => fix.ToString().ToUpperInvariant()
        };

    public static string FormatGpsDisplay(TelemetrySnapshot snapshot) =>
        snapshot.IsConnected
            ? $"{FormatGpsFix(snapshot.GpsFixType)} · {snapshot.SatellitesVisible} UYDU"
            : "NO GPS · 0 UYDU";

    public static string FormatRssiDisplay(TelemetrySnapshot snapshot) =>
        snapshot.IsConnected && (snapshot.RadioRssi > 0 || snapshot.RemoteRssi > 0)
            ? $"{Math.Clamp((int)Math.Round(snapshot.RadioRssi / 2.55), 0, 100)}%"
            : "--%";

    public static string FormatHomeDistance(TelemetrySnapshot snapshot, double distanceMeters) =>
        snapshot.HasHome
            ? string.Create(CultureInfo.InvariantCulture, $"{distanceMeters:0.0} M")
            : "-- M";

    public static bool IsValidGeoCoordinate(double lat, double lon) =>
        double.IsFinite(lat) &&
        double.IsFinite(lon) &&
        Math.Abs(lat) <= 90.0 &&
        Math.Abs(lon) <= 180.0 &&
        !(lat == 0.0 && lon == 0.0);

    public static double ComputeHaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double EarthRadiusMeters = 6371000.0;
        double dLat = (lat2 - lat1) * (Math.PI / 180.0);
        double dLon = (lon2 - lon1) * (Math.PI / 180.0);
        double a = Math.Sin(dLat / 2.0) * Math.Sin(dLat / 2.0) +
                   Math.Cos(lat1 * (Math.PI / 180.0)) * Math.Cos(lat2 * (Math.PI / 180.0)) *
                   Math.Sin(dLon / 2.0) * Math.Sin(dLon / 2.0);
        double c = 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));
        return EarthRadiusMeters * c;
    }
}
