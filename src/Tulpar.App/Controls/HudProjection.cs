using System.Globalization;
using Tulpar.Core.Mavlink;
using Tulpar.Core.Session;
using Tulpar.Core.Vehicles;

namespace Tulpar.App.Controls;

/// <summary>
/// Projected, sanitized HUD instrument state derived from <see cref="TelemetrySnapshot"/> and <see cref="IVehicleProfile"/>.
/// </summary>
public sealed record HudRenderState(
    bool ShowHorizon,
    bool ShowAirspeed,
    bool ShowAltitudeTape,
    bool ShowVtolTransition,
    bool SurfaceOnlyMode,
    double RollDeg,
    double PitchDeg,
    double HeadingDeg,
    double AirspeedMps,
    double GroundspeedMps,
    double AltitudeRelMeters,
    double VerticalSpeedMps,
    string ModeName,
    bool IsArmed,
    bool LinkLost,
    string VehicleLabel,
    double BatteryVoltageVolts = 0.0,
    int BatteryRemainingPercent = -1,
    GpsFixType GpsFixType = GpsFixType.NoGps,
    int SatellitesVisible = 0,
    int ThrottlePercent = 0,
    int ActiveWaypointSeq = -1);

/// <summary>
/// Pure-math projection and angular normalization helpers for the adaptive vector <see cref="HudControl"/>.
/// </summary>
public static class HudProjection
{
    private const double MinPitchDeg = -45.0;
    private const double MaxPitchDeg = 45.0;
    private const double FullCircleDeg = 360.0;
    private const double HalfCircleDeg = 180.0;

    /// <summary>
    /// Projects a raw <see cref="TelemetrySnapshot"/> and optional <see cref="IVehicleProfile"/> into a safe <see cref="HudRenderState"/>.
    /// </summary>
    public static HudRenderState FromSnapshot(TelemetrySnapshot? snapshot, IVehicleProfile? profile)
    {
        TelemetrySnapshot snap = snapshot ?? TelemetrySnapshot.Empty;

        (bool showHorizon, bool showAirspeed, bool showAltitudeTape, bool showVtolTransition, bool surfaceOnlyMode, string defaultLabel) =
            ResolveProfileLayout(snap.VehicleCategory, profile);

        string vehicleLabel = ResolveVehicleLabel(snap.VehicleDisplayLabel, defaultLabel);
        string modeName = string.IsNullOrWhiteSpace(snap.ModeName) ? "UNKNOWN" : snap.ModeName.Trim();

        return new HudRenderState(
            ShowHorizon: showHorizon,
            ShowAirspeed: showAirspeed,
            ShowAltitudeTape: showAltitudeTape,
            ShowVtolTransition: showVtolTransition,
            SurfaceOnlyMode: surfaceOnlyMode,
            RollDeg: FiniteOrZero(snap.RollDeg),
            PitchDeg: ClampPitch(snap.PitchDeg),
            HeadingDeg: NormalizeHeading(snap.HeadingDeg),
            AirspeedMps: FiniteOrZero(snap.AirspeedMps),
            GroundspeedMps: FiniteOrZero(snap.GroundspeedMps),
            AltitudeRelMeters: FiniteOrZero(snap.AltitudeRelativeMeters),
            VerticalSpeedMps: FiniteOrZero(snap.VerticalSpeedMps),
            ModeName: modeName,
            IsArmed: snap.IsArmed,
            LinkLost: snap.LinkLost,
            VehicleLabel: vehicleLabel,
            BatteryVoltageVolts: double.IsFinite(snap.BatteryVoltageVolts) && snap.BatteryVoltageVolts > 0.0 ? snap.BatteryVoltageVolts : 0.0,
            BatteryRemainingPercent: snap.BatteryRemainingPercent >= 0 ? Math.Clamp(snap.BatteryRemainingPercent, 0, 100) : -1,
            GpsFixType: snap.GpsFixType,
            SatellitesVisible: Math.Max(0, snap.SatellitesVisible),
            ThrottlePercent: Math.Clamp(snap.ThrottlePercent, 0, 100),
            ActiveWaypointSeq: snap.ActiveWaypointSeq);
    }

    /// <summary>
    /// Formats compact battery voltage and remaining capacity for HUD corner readout.
    /// </summary>
    public static string FormatBatteryReadout(double voltageVolts, int remainingPercent)
    {
        if (voltageVolts <= 0.0 && remainingPercent < 0)
        {
            return "--V / --%";
        }

        string voltPart = voltageVolts > 0.0
            ? voltageVolts.ToString("0.0", CultureInfo.InvariantCulture)
            : "--";
        string pctPart = remainingPercent >= 0
            ? Math.Clamp(remainingPercent, 0, 100).ToString(CultureInfo.InvariantCulture)
            : "--";
        return $"{voltPart}V / {pctPart}%";
    }

    /// <summary>
    /// Formats compact GPS fix type and visible satellite count for HUD corner readout.
    /// </summary>
    public static string FormatGpsReadout(GpsFixType fixType, int satellitesVisible)
    {
        string fixShort = fixType switch
        {
            GpsFixType.NoGps => "NO GPS",
            GpsFixType.NoFix => "NO FIX",
            GpsFixType.Fix2D => "2D",
            GpsFixType.Fix3D => "3D",
            GpsFixType.Dgps => "DGPS",
            GpsFixType.RtkFloat => "RTK FLT",
            GpsFixType.RtkFixed => "RTK FIX",
            GpsFixType.Static => "STATIC",
            GpsFixType.Ppp => "PPP",
            _ => fixType.ToString().ToUpperInvariant()
        };

        return $"{fixShort} / {Math.Max(0, satellitesVisible).ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Formats compact throttle percentage for HUD corner readout.
    /// </summary>
    public static string FormatThrottleReadout(int throttlePercent) =>
        $"THR %{Math.Clamp(throttlePercent, 0, 100).ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Formats active waypoint sequence badge when <paramref name="activeWaypointSeq"/> is non-negative.
    /// </summary>
    public static string FormatActiveWaypointReadout(int activeWaypointSeq) =>
        activeWaypointSeq >= 0
            ? $"WP #{activeWaypointSeq.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;

    /// <summary>
    /// Clamps pitch angle into [-45, 45] degrees for artificial horizon ladder rendering.
    /// </summary>
    public static double ClampPitch(double pitchDeg)
    {
        if (double.IsNaN(pitchDeg))
        {
            return 0.0;
        }

        if (double.IsPositiveInfinity(pitchDeg))
        {
            return MaxPitchDeg;
        }

        if (double.IsNegativeInfinity(pitchDeg))
        {
            return MinPitchDeg;
        }

        return Math.Clamp(pitchDeg, MinPitchDeg, MaxPitchDeg);
    }

    /// <summary>
    /// Wraps any heading angle cleanly into [0, 360) degrees.
    /// </summary>
    public static double NormalizeHeading(double headingDeg)
    {
        if (!double.IsFinite(headingDeg))
        {
            return 0.0;
        }

        double wrapped = headingDeg % FullCircleDeg;
        if (wrapped < 0.0)
        {
            wrapped += FullCircleDeg;
        }

        if (wrapped >= FullCircleDeg)
        {
            wrapped -= FullCircleDeg;
        }

        return wrapped == 0.0 ? 0.0 : wrapped;
    }

    /// <summary>
    /// Computes the shortest signed angular difference (targetDeg - currentDeg) wrapped into [-180, 180).
    /// </summary>
    public static double ShortestAngleDiffDeg(double targetDeg, double currentDeg)
    {
        if (!double.IsFinite(targetDeg) || !double.IsFinite(currentDeg))
        {
            return 0.0;
        }

        double diff = NormalizeHeading(targetDeg) - NormalizeHeading(currentDeg);
        if (diff >= HalfCircleDeg)
        {
            diff -= FullCircleDeg;
        }
        else if (diff < -HalfCircleDeg)
        {
            diff += FullCircleDeg;
        }

        if (diff >= HalfCircleDeg)
        {
            diff -= FullCircleDeg;
        }

        return diff == 0.0 ? 0.0 : diff;
    }

    private static (bool ShowHorizon, bool ShowAirspeed, bool ShowAltitudeTape, bool ShowVtolTransition, bool SurfaceOnlyMode, string DefaultLabel)
        ResolveProfileLayout(VehicleCategory snapshotCategory, IVehicleProfile? profile)
    {
        if (profile is not null)
        {
            return (
                profile.ShowHorizon,
                profile.ShowAirspeed,
                profile.ShowAltitudeTape,
                profile.ShowVtolTransition,
                profile.SurfaceOnlyMode,
                profile.DisplayLabel);
        }

        return snapshotCategory switch
        {
            VehicleCategory.FixedWing => (true, true, true, false, false, "SİHA / SABİT KANAT"),
            VehicleCategory.Vtol => (true, true, true, true, false, "VTOL"),
            VehicleCategory.Multirotor => (true, false, true, false, false, "DRONE / MULTIROTOR"),
            VehicleCategory.RoverIka => (false, false, false, false, true, "İKA / ROVER"),
            VehicleCategory.BoatIda => (false, false, false, false, true, "İDA / YÜZEY"),
            _ => (true, false, true, false, false, TelemetrySnapshot.Empty.VehicleDisplayLabel)
        };
    }

    private static string ResolveVehicleLabel(string snapshotLabel, string defaultLabel)
    {
        if (string.IsNullOrWhiteSpace(snapshotLabel) ||
            string.Equals(snapshotLabel, TelemetrySnapshot.Empty.VehicleDisplayLabel, StringComparison.OrdinalIgnoreCase))
        {
            return defaultLabel;
        }

        return snapshotLabel.Trim();
    }

    private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0.0;
}
