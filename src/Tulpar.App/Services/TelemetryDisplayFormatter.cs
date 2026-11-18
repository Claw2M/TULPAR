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

    public static string FormatAltitude(bool hasLiveTelemetry, double meters) =>
        !hasLiveTelemetry || !double.IsFinite(meters)
            ? "-- M"
            : string.Create(CultureInfo.InvariantCulture, $"{meters:0.0} M");

    public static string FormatSpeed(bool hasLiveTelemetry, double speedMps) =>
        !hasLiveTelemetry || !double.IsFinite(speedMps)
            ? "-- M/S"
            : string.Create(CultureInfo.InvariantCulture, $"{speedMps:0.0} M/S");

    public static string FormatVerticalSpeed(bool hasLiveTelemetry, double verticalSpeedMps) =>
        !hasLiveTelemetry || !double.IsFinite(verticalSpeedMps)
            ? "-- M/S"
            : string.Create(CultureInfo.InvariantCulture, $"{verticalSpeedMps:+0.0;-0.0;0.0} M/S");

    public static string FormatCurrentThrottle(bool hasLiveTelemetry, double currentAmps, int throttlePercent) =>
        !hasLiveTelemetry || !double.IsFinite(currentAmps)
            ? "-- A · -- %"
            : string.Create(CultureInfo.InvariantCulture, $"{currentAmps:0.0}A · %{throttlePercent}");

    public static string FormatHeading(bool hasLiveTelemetry, double headingDeg)
    {
        if (!hasLiveTelemetry || !double.IsFinite(headingDeg))
        {
            return "---°";
        }

        int normalized = (((int)Math.Round(headingDeg) % 360) + 360) % 360;
        return string.Create(CultureInfo.InvariantCulture, $"{normalized:000}°");
    }

    public static string FormatRollPitch(bool hasLiveTelemetry, double rollDeg, double pitchDeg) =>
        !hasLiveTelemetry || !double.IsFinite(rollDeg) || !double.IsFinite(pitchDeg)
            ? "--° / --°"
            : string.Create(CultureInfo.InvariantCulture, $"{rollDeg:0}° / {pitchDeg:0}°");

    public static string FormatCoordinate(bool hasLiveTelemetry, double latitude, double longitude, bool isLatitude) =>
        !hasLiveTelemetry || !IsValidGeoCoordinate(latitude, longitude)
            ? "--"
            : (isLatitude ? latitude : longitude).ToString("0.000000", CultureInfo.InvariantCulture);

    public static (string Text, string State) EvaluateDataFreshness(
        bool hasLiveTelemetry,
        TelemetrySnapshot snapshot,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!hasLiveTelemetry)
        {
            return ("VERİ YOK", "Unknown");
        }

        if (snapshot.LinkLost)
        {
            return ("LİNK KAYBI", "Lost");
        }

        DateTimeOffset referenceUtc = snapshot.UpdatedUtc != default
            ? snapshot.UpdatedUtc
            : snapshot.LastHeartbeatUtc;
        if (referenceUtc == default)
        {
            return ("VERİ YOK", "Unknown");
        }

        double ageSeconds = Math.Max(0.0, (nowUtc - referenceUtc).TotalSeconds);
        if (ageSeconds >= 1.5)
        {
            return (string.Create(CultureInfo.InvariantCulture, $"{ageSeconds:0.0} SN · GECİKME"), "Stale");
        }

        return (string.Create(CultureInfo.InvariantCulture, $"{ageSeconds:0.0} SN"), "Fresh");
    }

    public static IReadOnlyList<SystemHealthItem> BuildSystemHealthItems(
        bool isTransportOrReplayActive,
        bool isReplayMode,
        TelemetrySnapshot snapshot,
        string freshnessState)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        bool hasLive = isTransportOrReplayActive && snapshot.IsConnected;

        return
        [
            BuildLinkHealthItem(isTransportOrReplayActive, isReplayMode, hasLive, snapshot, freshnessState),
            BuildEkfHealthItem(hasLive, snapshot),
            BuildVibrationHealthItem(hasLive, snapshot),
            BuildGpsHealthItem(hasLive, snapshot),
            BuildSensorHealthItem(hasLive, snapshot),
            BuildPowerHealthItem(hasLive, snapshot),
            BuildCpuHealthItem(hasLive, snapshot)
        ];
    }

    public static string EvaluateSystemHealthSummary(bool hasLiveTelemetry, IReadOnlyList<SystemHealthItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (!hasLiveTelemetry || items.Count == 0)
        {
            return "BEKLENİYOR";
        }

        bool hasCritical = false;
        bool hasWarning = false;
        bool hasOk = false;

        for (int i = 0; i < items.Count; i++)
        {
            switch (items[i].State)
            {
                case SystemHealthState.Critical:
                    hasCritical = true;
                    break;
                case SystemHealthState.Warning:
                    hasWarning = true;
                    break;
                case SystemHealthState.Ok:
                    hasOk = true;
                    break;
            }
        }

        if (hasCritical)
        {
            return "KRİTİK";
        }

        if (hasWarning)
        {
            return "DİKKAT";
        }

        return hasOk ? "SAĞLIKLI" : "BEKLENİYOR";
    }

    private static SystemHealthItem BuildLinkHealthItem(
        bool isTransportOrReplayActive,
        bool isReplayMode,
        bool hasLive,
        TelemetrySnapshot snapshot,
        string freshnessState)
    {
        if (!isTransportOrReplayActive)
        {
            return new SystemHealthItem("LINK", "BAĞLANTI & LİNK", "ÇEVRİMDIŞI", "Araç bağlantısı bekleniyor", SystemHealthState.Unknown);
        }

        if (!hasLive || snapshot.SystemId == 0)
        {
            return new SystemHealthItem("LINK", "BAĞLANTI & LİNK", "BEKLENİYOR", "Heartbeat bekleniyor", SystemHealthState.Unknown);
        }

        double drop = double.IsFinite(snapshot.DropRateCommPercent) ? Math.Max(0.0, snapshot.DropRateCommPercent) : 0.0;
        if (snapshot.LinkLost)
        {
            return new SystemHealthItem(
                "LINK",
                "BAĞLANTI & LİNK",
                "LİNK KAYBI",
                string.Create(CultureInfo.InvariantCulture, $"Heartbeat zaman aşımı · Kayıp %{drop:0.0}"),
                SystemHealthState.Critical);
        }

        string detail = string.Create(CultureInfo.InvariantCulture, $"SYS #{snapshot.SystemId} · Kayıp %{drop:0.0}");
        if (drop >= 15.0)
        {
            return new SystemHealthItem("LINK", "BAĞLANTI & LİNK", "YÜKSEK KAYIP", detail, SystemHealthState.Critical);
        }

        if (drop >= 5.0 || string.Equals(freshnessState, "Stale", StringComparison.Ordinal))
        {
            string status = string.Equals(freshnessState, "Stale", StringComparison.Ordinal) ? "GECİKMELİ" : "DİKKAT";
            return new SystemHealthItem("LINK", "BAĞLANTI & LİNK", status, detail, SystemHealthState.Warning);
        }

        return new SystemHealthItem(
            "LINK",
            "BAĞLANTI & LİNK",
            isReplayMode ? "TEKRAR (TLOG)" : "ÇEVRİMİÇİ",
            detail,
            SystemHealthState.Ok);
    }

    private static SystemHealthItem BuildEkfHealthItem(bool hasLive, TelemetrySnapshot snapshot)
    {
        if (!hasLive)
        {
            return new SystemHealthItem("EKF", "EKF NAVİGASYON", "VERİ YOK", "EKF telemetrisi bekleniyor", SystemHealthState.Unknown);
        }

        if (snapshot.EkfFlags == 0)
        {
            return new SystemHealthItem("EKF", "EKF NAVİGASYON", "BEKLENİYOR", "EKF durum raporu alınmadı", SystemHealthState.Unknown);
        }

        var flags = (EkfStatusFlags)snapshot.EkfFlags;
        bool uninitialized = flags.HasFlag(EkfStatusFlags.Uninitialized);
        bool attOk = flags.HasFlag(EkfStatusFlags.Attitude);
        bool velOk = flags.HasFlag(EkfStatusFlags.VelocityHoriz) || flags.HasFlag(EkfStatusFlags.VelocityVert);
        bool posOk = flags.HasFlag(EkfStatusFlags.PosHorizRel) || flags.HasFlag(EkfStatusFlags.PosHorizAbs);
        bool constPos = flags.HasFlag(EkfStatusFlags.ConstPosMode);

        if (uninitialized || !attOk)
        {
            return new SystemHealthItem(
                "EKF",
                "EKF NAVİGASYON",
                "SAĞLIKSIZ",
                uninitialized ? "EKF başlatılmadı" : "Yönelim (Attitude) kilidi yok",
                SystemHealthState.Critical);
        }

        if (!velOk || !posOk || constPos)
        {
            return new SystemHealthItem(
                "EKF",
                "EKF NAVİGASYON",
                "KISITLI",
                $"Att:OK · Vel:{(velOk ? "OK" : "NO")} · Pos:{(posOk ? "OK" : "NO")}",
                SystemHealthState.Warning);
        }

        return new SystemHealthItem("EKF", "EKF NAVİGASYON", "SAĞLIKLI", "Attitude + Velocity + Pos aktif", SystemHealthState.Ok);
    }

    private static SystemHealthItem BuildVibrationHealthItem(bool hasLive, TelemetrySnapshot snapshot)
    {
        if (!hasLive)
        {
            return new SystemHealthItem("VIBRATION", "TİTREŞİM (IMU)", "VERİ YOK", "0.0 m/s² · Bağlantı yok", SystemHealthState.Unknown);
        }

        float vx = float.IsFinite(snapshot.VibrationX) ? Math.Abs(snapshot.VibrationX) : 0f;
        float vy = float.IsFinite(snapshot.VibrationY) ? Math.Abs(snapshot.VibrationY) : 0f;
        float vz = float.IsFinite(snapshot.VibrationZ) ? Math.Abs(snapshot.VibrationZ) : 0f;
        float maxAxis = Math.Max(vx, Math.Max(vy, vz));

        if (maxAxis >= 60f)
        {
            return new SystemHealthItem(
                "VIBRATION",
                "TİTREŞİM (IMU)",
                "KRİTİK",
                string.Create(CultureInfo.InvariantCulture, $"Maks {maxAxis:0.0} m/s² (≥60)"),
                SystemHealthState.Critical);
        }

        if (maxAxis >= 30f)
        {
            return new SystemHealthItem(
                "VIBRATION",
                "TİTREŞİM (IMU)",
                "YÜKSEK",
                string.Create(CultureInfo.InvariantCulture, $"Maks {maxAxis:0.0} m/s² (Eşik ≥30)"),
                SystemHealthState.Warning);
        }

        return new SystemHealthItem(
            "VIBRATION",
            "TİTREŞİM (IMU)",
            "NORMAL",
            string.Create(CultureInfo.InvariantCulture, $"Maks {maxAxis:0.0} m/s² (<30 m/s²)"),
            SystemHealthState.Ok);
    }

    private static SystemHealthItem BuildGpsHealthItem(bool hasLive, TelemetrySnapshot snapshot)
    {
        if (!hasLive)
        {
            return new SystemHealthItem("GPS", "GPS ALICISI", "VERİ YOK", "NO GPS · 0 uydu · HDOP --", SystemHealthState.Unknown);
        }

        string fixText = FormatGpsFix(snapshot.GpsFixType);
        string hdopText = snapshot.Hdop > 0.0 && double.IsFinite(snapshot.Hdop)
            ? snapshot.Hdop.ToString("0.00", CultureInfo.InvariantCulture)
            : "--";
        string detail = $"{fixText} · {snapshot.SatellitesVisible} uydu · HDOP {hdopText}";

        if (snapshot.GpsFixType < GpsFixType.Fix2D)
        {
            return new SystemHealthItem("GPS", "GPS ALICISI", fixText, detail, SystemHealthState.Critical);
        }

        if (snapshot.GpsFixType < GpsFixType.Fix3D || snapshot.SatellitesVisible < 6 || snapshot.Hdop > 2.5)
        {
            return new SystemHealthItem("GPS", "GPS ALICISI", "ZAYIF", detail, SystemHealthState.Warning);
        }

        return new SystemHealthItem("GPS", "GPS ALICISI", fixText, detail, SystemHealthState.Ok);
    }

    private static SystemHealthItem BuildSensorHealthItem(bool hasLive, TelemetrySnapshot snapshot)
    {
        if (!hasLive)
        {
            return new SystemHealthItem("SENSORS", "SENSÖR DURUMU", "VERİ YOK", "SYS_STATUS bekleniyor", SystemHealthState.Unknown);
        }

        if (snapshot.SensorHealthBits == 0)
        {
            return new SystemHealthItem("SENSORS", "SENSÖR DURUMU", "BEKLENİYOR", "Sensör bitmaskı alınmadı", SystemHealthState.Unknown);
        }

        const uint CoreImuMask = 0b0011u;
        bool coreHealthy = (snapshot.SensorHealthBits & CoreImuMask) == CoreImuMask;
        string detail = $"Bitmask 0x{snapshot.SensorHealthBits:X}";
        return coreHealthy
            ? new SystemHealthItem("SENSORS", "SENSÖR DURUMU", "SAĞLIKLI", detail, SystemHealthState.Ok)
            : new SystemHealthItem("SENSORS", "SENSÖR DURUMU", "EKSİK SENSÖR", detail, SystemHealthState.Warning);
    }

    private static SystemHealthItem BuildPowerHealthItem(bool hasLive, TelemetrySnapshot snapshot)
    {
        if (!hasLive)
        {
            return new SystemHealthItem("POWER", "GÜÇ & BATARYA", "VERİ YOK", "-- V · --% · -- mAh", SystemHealthState.Unknown);
        }

        bool hasVoltage = snapshot.BatteryVoltageVolts > 0.1 && double.IsFinite(snapshot.BatteryVoltageVolts);
        bool hasPercent = snapshot.BatteryRemainingPercent >= 0;
        string mahText = snapshot.BatteryCurrentConsumedMah >= 0
            ? $"{snapshot.BatteryCurrentConsumedMah} mAh"
            : "-- mAh";
        string detail = $"{FormatBattery(snapshot)} · {mahText}";

        if (!hasVoltage && !hasPercent)
        {
            return new SystemHealthItem("POWER", "GÜÇ & BATARYA", "VERİ YOK", detail, SystemHealthState.Unknown);
        }

        if (hasPercent && snapshot.BatteryRemainingPercent <= 15)
        {
            return new SystemHealthItem("POWER", "GÜÇ & BATARYA", "KRİTİK", detail, SystemHealthState.Critical);
        }

        if (hasPercent && snapshot.BatteryRemainingPercent <= 25)
        {
            return new SystemHealthItem("POWER", "GÜÇ & BATARYA", "DÜŞÜK", detail, SystemHealthState.Warning);
        }

        return new SystemHealthItem("POWER", "GÜÇ & BATARYA", "NORMAL", detail, SystemHealthState.Ok);
    }

    private static SystemHealthItem BuildCpuHealthItem(bool hasLive, TelemetrySnapshot snapshot)
    {
        if (!hasLive)
        {
            return new SystemHealthItem("CPU", "OTOPİLOT CPU", "VERİ YOK", "-- % CPU yükü", SystemHealthState.Unknown);
        }

        double cpu = double.IsFinite(snapshot.CpuLoadPercent) ? Math.Max(0.0, snapshot.CpuLoadPercent) : 0.0;
        string detail = string.Create(CultureInfo.InvariantCulture, $"Yük: %{cpu:0.0}");

        if (cpu >= 85.0)
        {
            return new SystemHealthItem("CPU", "OTOPİLOT CPU", "AŞIRI YÜK", detail, SystemHealthState.Critical);
        }

        if (cpu >= 70.0)
        {
            return new SystemHealthItem("CPU", "OTOPİLOT CPU", "YÜKSEK", detail, SystemHealthState.Warning);
        }

        return new SystemHealthItem("CPU", "OTOPİLOT CPU", "NORMAL", detail, SystemHealthState.Ok);
    }
}
