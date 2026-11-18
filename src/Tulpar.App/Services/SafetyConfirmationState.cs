using Tulpar.Core.Mavlink;
using Tulpar.Core.Session;
using Tulpar.Core.Vehicles;

namespace Tulpar.App.Services;

public enum SafetyActionKind
{
    None,
    Arm,
    Disarm,
    Rtl,
    Hold,
    Takeoff,
    Land,
    GuidedFlyHere,
    StartMission
}

public sealed record SafetyActionRequest(
    SafetyActionKind Kind,
    string Title,
    string Description,
    double Latitude = 0.0,
    double Longitude = 0.0,
    float AltitudeMeters = 0f,
    float? LoiterRadius = null);

/// <summary>
/// Resolves platform-adaptive safety-guarded MAVLink commands and aerial Guided minimum altitudes.
/// </summary>
public static class SafetyCommandBuilder
{
    public const float MinFixedWingGuidedAltMeters = 50f;
    public const float MinMultirotorGuidedAltMeters = 20f;

    public static float ResolveSafeGuidedAltitude(
        IVehicleProfile profile,
        TelemetrySnapshot snapshot,
        float? requestedAltMeters,
        double? operatorDefaultAltMeters = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (profile.SurfaceOnlyMode)
        {
            return 0f;
        }

        if (requestedAltMeters is float explicitAlt && float.IsFinite(explicitAlt) && explicitAlt > 0f)
        {
            return explicitAlt;
        }

        float minFloor = profile.Category is VehicleCategory.FixedWing or VehicleCategory.Vtol
            ? MinFixedWingGuidedAltMeters
            : MinMultirotorGuidedAltMeters;

        float currentRel = double.IsFinite(snapshot.AltitudeRelativeMeters)
            ? (float)snapshot.AltitudeRelativeMeters
            : 0f;

        float safeFloor = Math.Max(minFloor, currentRel);
        if (requestedAltMeters is null &&
            operatorDefaultAltMeters is double opAlt &&
            double.IsFinite(opAlt) &&
            opAlt > safeFloor)
        {
            return (float)opAlt;
        }

        return safeFloor;
    }

    public static FlightModeOption? FindPreferredHoldMode(IVehicleProfile profile, MavAutopilot autopilot)
    {
        ArgumentNullException.ThrowIfNull(profile);

        IReadOnlyList<FlightModeOption> modes = profile.GetFlightModes(autopilot);
        string[] preference = profile.Category == VehicleCategory.Vtol
            ? ["QLOITER", "HOLD", "LOITER", "AUTO_LOITER", "BRAKE", "POSHOLD"]
            : ["HOLD", "LOITER", "AUTO_LOITER", "QLOITER", "BRAKE", "POSHOLD"];

        foreach (string preferred in preference)
        {
            FlightModeOption? match = modes.FirstOrDefault(m => string.Equals(m.Name, preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    public static FlightModeOption? FindPreferredAutoMissionMode(IVehicleProfile profile, MavAutopilot autopilot)
    {
        ArgumentNullException.ThrowIfNull(profile);

        IReadOnlyList<FlightModeOption> modes = profile.GetFlightModes(autopilot);
        string[] preference = ["AUTO", "AUTO_MISSION", "MISSION"];
        foreach (string preferred in preference)
        {
            FlightModeOption? match = modes.FirstOrDefault(m => string.Equals(m.Name, preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    public static IMavlinkMessage BuildHoldMessage(
        byte targetSys,
        byte targetComp,
        IVehicleProfile profile,
        MavAutopilot autopilot)
    {
        FlightModeOption? holdMode = FindPreferredHoldMode(profile, autopilot);
        return holdMode is not null
            ? new SetModeMessage(targetSys, (MavModeFlag)holdMode.BaseMode, holdMode.CustomMode)
            : new CommandLongMessage(targetSys, targetComp, MavCmd.DoPauseContinue, 0, Param1: 0f);
    }
}
