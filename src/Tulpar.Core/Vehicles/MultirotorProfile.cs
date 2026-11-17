using Tulpar.Core.Mavlink;

namespace Tulpar.Core.Vehicles;

/// <summary>
/// Vehicle profile for multirotor and rotary-wing platforms (ArduCopter / PX4 Multirotor).
/// Also acts as the safe fallback profile for unrecognized MAV_TYPE values.
/// </summary>
public sealed class MultirotorProfile : VehicleProfileBase
{
    private static readonly IReadOnlyList<FlightModeOption> CopterModes =
    [
        Apm(0, "STABILIZE"),
        Apm(1, "ACRO"),
        Apm(2, "ALT_HOLD"),
        Apm(3, "AUTO"),
        Apm(4, "GUIDED"),
        Apm(5, "LOITER"),
        Apm(6, "RTL"),
        Apm(7, "CIRCLE"),
        Apm(9, "LAND"),
        Apm(11, "DRIFT"),
        Apm(16, "POSHOLD"),
        Apm(17, "BRAKE"),
        Apm(21, "SMART_RTL")
    ];

    private static readonly IReadOnlyList<MavCmd> Commands =
    [
        MavCmd.NavWaypoint,
        MavCmd.NavSplineWaypoint,
        MavCmd.NavLoiterUnlim,
        MavCmd.NavLoiterTime,
        MavCmd.NavLoiterTurns,
        MavCmd.NavTakeoff,
        MavCmd.NavLand,
        MavCmd.NavReturnToLaunch,
        MavCmd.NavDelay,
        MavCmd.NavRoi,
        MavCmd.DoSetRoi,
        MavCmd.DoJump,
        MavCmd.DoChangeSpeed,
        MavCmd.DoSetServo,
        MavCmd.DoSetRelay,
        MavCmd.DoSetCamTriggDist
    ];

    private readonly VehicleCategory _category;
    private readonly string _displayLabel;

    public MultirotorProfile()
        : this(VehicleCategory.Multirotor, "DRONE / MULTIROTOR")
    {
    }

    public MultirotorProfile(MavType vehicleType)
        : this(VehicleCategory.Multirotor, FormatDisplayLabel(vehicleType))
    {
    }

    internal MultirotorProfile(VehicleCategory category, string displayLabel)
    {
        _category = category;
        _displayLabel = displayLabel;
    }

    public override VehicleCategory Category => _category;
    public override string DisplayLabel => _displayLabel;
    public override bool ShowAirspeed => false;
    public override bool ShowHorizon => true;
    public override bool ShowAltitudeTape => true;
    public override bool ShowVtolTransition => false;
    public override bool SurfaceOnlyMode => false;
    public override IReadOnlyList<MavCmd> SupportedMissionCommands => Commands;
    protected override IReadOnlyList<FlightModeOption> ArduPilotModes => CopterModes;

    private static string FormatDisplayLabel(MavType vehicleType) =>
        vehicleType switch
        {
            MavType.Quadrotor => "DRONE / QUADROTOR",
            MavType.Hexarotor => "DRONE / HEXAROTOR",
            MavType.Octorotor => "DRONE / OCTOROTOR",
            MavType.Tricopter => "DRONE / TRICOPTER",
            MavType.Helicopter => "DRONE / HELICOPTER",
            MavType.Coaxial => "DRONE / COAXIAL",
            MavType.Dodecarotor => "DRONE / DODECAROTOR",
            MavType.Decarotor => "DRONE / DECAROTOR",
            _ => "DRONE / MULTIROTOR"
        };
}
