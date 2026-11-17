using Tulpar.Core.Mavlink;

namespace Tulpar.Core.Vehicles;

/// <summary>
/// Vehicle profile for hybrid VTOL aircraft (ArduPlane QuadPlane / PX4 VTOL).
/// </summary>
public sealed class VtolProfile : VehicleProfileBase
{
    private static readonly IReadOnlyList<FlightModeOption> VtolQModes =
    [
        Apm(0, "MANUAL"),
        Apm(5, "FBWA"),
        Apm(10, "AUTO"),
        Apm(11, "RTL"),
        Apm(12, "LOITER"),
        Apm(15, "GUIDED"),
        Apm(17, "QSTABILIZE"),
        Apm(18, "QHOVER"),
        Apm(19, "QLOITER"),
        Apm(20, "QLAND"),
        Apm(21, "QRTL")
    ];

    private static readonly IReadOnlyList<MavCmd> Commands =
    [
        MavCmd.NavVtolTakeoff,
        MavCmd.NavWaypoint,
        MavCmd.NavLoiterUnlim,
        MavCmd.NavLoiterTurns,
        MavCmd.NavLoiterTime,
        MavCmd.NavLoiterToAlt,
        MavCmd.NavVtolLand,
        MavCmd.NavReturnToLaunch,
        MavCmd.DoVtolTransition,
        MavCmd.NavTakeoff,
        MavCmd.NavLand,
        MavCmd.DoJump,
        MavCmd.DoChangeSpeed,
        MavCmd.DoSetServo,
        MavCmd.DoSetRoi
    ];

    public override VehicleCategory Category => VehicleCategory.Vtol;
    public override string DisplayLabel => "VTOL";
    public override bool ShowAirspeed => true;
    public override bool ShowHorizon => true;
    public override bool ShowAltitudeTape => true;
    public override bool ShowVtolTransition => true;
    public override bool SurfaceOnlyMode => false;
    public override IReadOnlyList<MavCmd> SupportedMissionCommands => Commands;
    protected override IReadOnlyList<FlightModeOption> ArduPilotModes => VtolQModes;
    protected override bool PreferMissionItemGuided => true;
}
