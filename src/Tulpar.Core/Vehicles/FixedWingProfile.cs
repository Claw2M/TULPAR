using Tulpar.Core.Mavlink;

namespace Tulpar.Core.Vehicles;

/// <summary>
/// Vehicle profile for fixed-wing aircraft (ArduPlane / PX4 Fixed-Wing).
/// </summary>
public sealed class FixedWingProfile : VehicleProfileBase
{
    private static readonly IReadOnlyList<FlightModeOption> PlaneModes =
    [
        Apm(0, "MANUAL"),
        Apm(1, "CIRCLE"),
        Apm(2, "STABILIZE"),
        Apm(3, "TRAINING"),
        Apm(4, "ACRO"),
        Apm(5, "FBWA"),
        Apm(6, "FBWB"),
        Apm(7, "CRUISE"),
        Apm(8, "AUTOTUNE"),
        Apm(10, "AUTO"),
        Apm(11, "RTL"),
        Apm(12, "LOITER"),
        Apm(13, "TAKEOFF"),
        Apm(15, "GUIDED"),
        Apm(17, "QSTABILIZE"),
        Apm(18, "QHOVER"),
        Apm(19, "QLOITER"),
        Apm(20, "QLAND"),
        Apm(21, "QRTL")
    ];

    private static readonly IReadOnlyList<MavCmd> Commands =
    [
        MavCmd.NavWaypoint,
        MavCmd.NavLoiterUnlim,
        MavCmd.NavLoiterTurns,
        MavCmd.NavLoiterTime,
        MavCmd.NavLoiterToAlt,
        MavCmd.NavTakeoff,
        MavCmd.NavLand,
        MavCmd.NavReturnToLaunch,
        MavCmd.NavContinueAndChangeAlt,
        MavCmd.DoJump,
        MavCmd.DoChangeSpeed,
        MavCmd.DoSetServo,
        MavCmd.DoSetRelay,
        MavCmd.DoSetRoi,
        MavCmd.DoSetCamTriggDist
    ];

    public override VehicleCategory Category => VehicleCategory.FixedWing;
    public override string DisplayLabel => "SİHA / SABİT KANAT";
    public override bool ShowAirspeed => true;
    public override bool ShowHorizon => true;
    public override bool ShowAltitudeTape => true;
    public override bool ShowVtolTransition => false;
    public override bool SurfaceOnlyMode => false;
    public override IReadOnlyList<MavCmd> SupportedMissionCommands => Commands;
    protected override IReadOnlyList<FlightModeOption> ArduPilotModes => PlaneModes;
    protected override bool PreferMissionItemGuided => true;
}
