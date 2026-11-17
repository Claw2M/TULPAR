using Tulpar.Core.Mavlink;

namespace Tulpar.Core.Vehicles;

/// <summary>
/// Vehicle profile for ground unmanned vehicles (İKA / ArduRover / PX4 Rover).
/// </summary>
public sealed class RoverIkaProfile : VehicleProfileBase
{
    private static readonly IReadOnlyList<FlightModeOption> RoverModes =
    [
        Apm(0, "MANUAL"),
        Apm(1, "ACRO"),
        Apm(3, "STEERING"),
        Apm(4, "HOLD"),
        Apm(5, "LOITER"),
        Apm(10, "AUTO"),
        Apm(11, "RTL"),
        Apm(12, "SMART_RTL"),
        Apm(15, "GUIDED")
    ];

    private static readonly IReadOnlyList<MavCmd> Commands =
    [
        MavCmd.NavWaypoint,
        MavCmd.NavLoiterUnlim,
        MavCmd.NavLoiterTime,
        MavCmd.NavReturnToLaunch,
        MavCmd.NavDelay,
        MavCmd.DoJump,
        MavCmd.DoChangeSpeed,
        MavCmd.DoSetReverse,
        MavCmd.DoSetServo,
        MavCmd.DoSetRelay
    ];

    public override VehicleCategory Category => VehicleCategory.RoverIka;
    public override string DisplayLabel => "İKA / ROVER";
    public override bool ShowAirspeed => false;
    public override bool ShowHorizon => false;
    public override bool ShowAltitudeTape => false;
    public override bool ShowVtolTransition => false;
    public override bool SurfaceOnlyMode => true;
    public override IReadOnlyList<MavCmd> SupportedMissionCommands => Commands;
    protected override IReadOnlyList<FlightModeOption> ArduPilotModes => RoverModes;
}
