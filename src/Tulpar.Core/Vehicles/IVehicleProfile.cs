using Tulpar.Core.Mavlink;

namespace Tulpar.Core.Vehicles;

/// <summary>
/// High-level adaptive platform categories supported by Tulpar GCS.
/// </summary>
public enum VehicleCategory
{
    Unknown,
    FixedWing,
    Multirotor,
    Vtol,
    RoverIka,
    BoatIda
}

/// <summary>
/// Selectable autopilot flight mode entry for UI mode selectors and SET_MODE commands.
/// </summary>
public sealed record FlightModeOption(string Name, uint CustomMode, byte BaseMode);

/// <summary>
/// Platform-specific capability, flight mode, HUD layout, and guided target contract.
/// </summary>
public interface IVehicleProfile
{
    VehicleCategory Category { get; }
    string DisplayLabel { get; }
    bool ShowAirspeed { get; }
    bool ShowHorizon { get; }
    bool ShowAltitudeTape { get; }
    bool ShowVtolTransition { get; }
    bool SurfaceOnlyMode { get; }
    IReadOnlyList<FlightModeOption> GetFlightModes(MavAutopilot autopilot);
    string ResolveModeName(MavAutopilot autopilot, uint customMode, byte baseMode);
    IReadOnlyList<MavCmd> SupportedMissionCommands { get; }

    MavlinkFrame BuildGuidedTargetCommand(
        byte targetSysId,
        byte targetCompId,
        byte gcsSysId,
        byte gcsCompId,
        byte seq,
        MavAutopilot autopilot,
        double lat,
        double lon,
        float altMeters,
        float? loiterRadius = null);
}

/// <summary>
/// Shared implementation base for Tulpar vehicle profiles providing autopilot mode tables and guided target construction.
/// </summary>
public abstract class VehicleProfileBase : IVehicleProfile
{
    private protected VehicleProfileBase()
    {
    }
    private const byte CustomModeFlag = (byte)MavModeFlag.CustomModeEnabled;

    private static readonly IReadOnlyList<FlightModeOption> AerialPx4Modes =
    [
        new("MANUAL", PackPx4Mode(1), CustomModeFlag),
        new("STABILIZED", PackPx4Mode(7), CustomModeFlag),
        new("ALTCTL", PackPx4Mode(2), CustomModeFlag),
        new("POSCTL", PackPx4Mode(3), CustomModeFlag),
        new("ACRO", PackPx4Mode(5), CustomModeFlag),
        new("OFFBOARD", PackPx4Mode(6), CustomModeFlag),
        new("AUTO_READY", PackPx4Mode(4, 1), CustomModeFlag),
        new("AUTO_TAKEOFF", PackPx4Mode(4, 2), CustomModeFlag),
        new("AUTO_LOITER", PackPx4Mode(4, 3), CustomModeFlag),
        new("AUTO_MISSION", PackPx4Mode(4, 4), CustomModeFlag),
        new("AUTO_RTL", PackPx4Mode(4, 5), CustomModeFlag),
        new("AUTO_LAND", PackPx4Mode(4, 6), CustomModeFlag)
    ];

    private static readonly IReadOnlyList<FlightModeOption> SurfacePx4Modes =
    [
        new("MANUAL", PackPx4Mode(1), CustomModeFlag),
        new("STABILIZED", PackPx4Mode(7), CustomModeFlag),
        new("POSCTL", PackPx4Mode(3), CustomModeFlag),
        new("ACRO", PackPx4Mode(5), CustomModeFlag),
        new("OFFBOARD", PackPx4Mode(6), CustomModeFlag),
        new("AUTO_LOITER", PackPx4Mode(4, 3), CustomModeFlag),
        new("AUTO_MISSION", PackPx4Mode(4, 4), CustomModeFlag),
        new("AUTO_RTL", PackPx4Mode(4, 5), CustomModeFlag)
    ];

    private static readonly IReadOnlyList<FlightModeOption> GenericModes =
    [
        new("MANUAL", 0u, (byte)MavModeFlag.ManualInputEnabled),
        new("STABILIZE", 0u, (byte)MavModeFlag.StabilizeEnabled),
        new("GUIDED", 0u, (byte)MavModeFlag.GuidedEnabled),
        new("AUTO", 0u, (byte)MavModeFlag.AutoEnabled)
    ];

    public abstract VehicleCategory Category { get; }
    public abstract string DisplayLabel { get; }
    public abstract bool ShowAirspeed { get; }
    public abstract bool ShowHorizon { get; }
    public abstract bool ShowAltitudeTape { get; }
    public abstract bool ShowVtolTransition { get; }
    public abstract bool SurfaceOnlyMode { get; }
    public abstract IReadOnlyList<MavCmd> SupportedMissionCommands { get; }

    protected abstract IReadOnlyList<FlightModeOption> ArduPilotModes { get; }
    protected virtual bool PreferMissionItemGuided => false;

    public IReadOnlyList<FlightModeOption> GetFlightModes(MavAutopilot autopilot) =>
        autopilot switch
        {
            MavAutopilot.ArduPilotMega => ArduPilotModes,
            MavAutopilot.Px4 => SurfaceOnlyMode ? SurfacePx4Modes : AerialPx4Modes,
            _ => GenericModes
        };

    public string ResolveModeName(MavAutopilot autopilot, uint customMode, byte baseMode) =>
        autopilot switch
        {
            MavAutopilot.ArduPilotMega => ResolveFromOptions(ArduPilotModes, customMode)
                ?? ResolveUnknownCustomMode(customMode),
            MavAutopilot.Px4 => ResolvePx4ModeName(customMode, baseMode),
            _ => ResolveBaseModeFallback(customMode, baseMode)
        };

    public MavlinkFrame BuildGuidedTargetCommand(
        byte targetSysId,
        byte targetCompId,
        byte gcsSysId,
        byte gcsCompId,
        byte seq,
        MavAutopilot autopilot,
        double lat,
        double lon,
        float altMeters,
        float? loiterRadius = null)
    {
        if (!double.IsFinite(lat) || lat is < -90.0 or > 90.0)
        {
            throw new ArgumentOutOfRangeException(nameof(lat), "Latitude must be finite and within [-90, 90].");
        }

        if (!double.IsFinite(lon) || lon is < -180.0 or > 180.0)
        {
            throw new ArgumentOutOfRangeException(nameof(lon), "Longitude must be finite and within [-180, 180].");
        }

        if (!float.IsFinite(altMeters))
        {
            throw new ArgumentOutOfRangeException(nameof(altMeters), "Altitude must be finite.");
        }

        float effectiveAlt = SurfaceOnlyMode ? 0f : altMeters;

        // PX4 uses MAV_CMD_DO_REPOSITION (COMMAND_LONG #192) for click-to-fly / loiter without requiring Offboard mode.
        if (autopilot == MavAutopilot.Px4)
        {
            var reposition = new CommandLongMessage(
                TargetSystem: targetSysId,
                TargetComponent: targetCompId,
                Command: MavCmd.DoReposition,
                Confirmation: 0,
                Param1: -1f,
                Param2: 1f,
                Param3: loiterRadius ?? 0f,
                Param4: float.NaN,
                Param5: (float)lat,
                Param6: (float)lon,
                Param7: effectiveAlt);
            return reposition.ToFrame(gcsSysId, gcsCompId, seq);
        }

        int latInt = (int)Math.Round(lat * 1e7);
        int lonInt = (int)Math.Round(lon * 1e7);

        // ArduPlane and QuadPlane VTOL accept guided waypoints/loiters via MISSION_ITEM_INT with current = 2.
        if (PreferMissionItemGuided)
        {
            MavCmd cmd = loiterRadius is > 0f ? MavCmd.NavLoiterUnlim : MavCmd.NavWaypoint;
            var guidedItem = new MissionItemIntMessage(
                TargetSystem: targetSysId,
                TargetComponent: targetCompId,
                Seq: 0,
                Frame: MavFrame.GlobalRelativeAltInt,
                Command: cmd,
                Current: 2,
                Autocontinue: 1,
                Param1: 0f,
                Param2: 0f,
                Param3: loiterRadius ?? 0f,
                Param4: 0f,
                X: latInt,
                Y: lonInt,
                Z: effectiveAlt,
                MissionType: MavMissionType.Mission);
            return guidedItem.ToFrame(gcsSysId, gcsCompId, seq);
        }

        // Multirotor, Rover, and Boat use SET_POSITION_TARGET_GLOBAL_INT (#86) with position-only type_mask.
        const PositionTargetTypemask positionOnlyMask =
            PositionTargetTypemask.VxIgnore |
            PositionTargetTypemask.VyIgnore |
            PositionTargetTypemask.VzIgnore |
            PositionTargetTypemask.AxIgnore |
            PositionTargetTypemask.AyIgnore |
            PositionTargetTypemask.AzIgnore |
            PositionTargetTypemask.YawIgnore |
            PositionTargetTypemask.YawRateIgnore;

        var target = new SetPositionTargetGlobalIntMessage(
            TimeBootMs: 0,
            TargetSystem: targetSysId,
            TargetComponent: targetCompId,
            CoordinateFrame: MavFrame.GlobalRelativeAltInt,
            TypeMask: positionOnlyMask,
            LatInt: latInt,
            LonInt: lonInt,
            Alt: effectiveAlt);
        return target.ToFrame(gcsSysId, gcsCompId, seq);
    }

    protected static FlightModeOption Apm(uint customMode, string name) =>
        new(name, customMode, CustomModeFlag);

    private static uint PackPx4Mode(byte mainMode, byte subMode = 0) =>
        ((uint)mainMode << 16) | ((uint)subMode << 24);

    private static string? ResolveFromOptions(IReadOnlyList<FlightModeOption> options, uint customMode)
    {
        for (int i = 0; i < options.Count; i++)
        {
            if (options[i].CustomMode == customMode)
            {
                return options[i].Name;
            }
        }

        return null;
    }

    private static string ResolvePx4ModeName(uint customMode, byte baseMode)
    {
        byte mainMode = (byte)((customMode >> 16) & 0xFF);
        byte subMode = (byte)((customMode >> 24) & 0xFF);

        return mainMode switch
        {
            1 => "MANUAL",
            2 => "ALTCTL",
            3 => "POSCTL",
            4 => subMode switch
            {
                0 => "AUTO",
                1 => "AUTO_READY",
                2 => "AUTO_TAKEOFF",
                3 => "AUTO_LOITER",
                4 => "AUTO_MISSION",
                5 => "AUTO_RTL",
                6 => "AUTO_LAND",
                _ => $"AUTO ({subMode})"
            },
            5 => "ACRO",
            6 => "OFFBOARD",
            7 => "STABILIZED",
            0 => ResolveBaseModeFallback(customMode, baseMode),
            _ => $"PX4 ({mainMode}.{subMode})"
        };
    }

    private static string ResolveBaseModeFallback(uint customMode, byte baseMode)
    {
        var flags = (MavModeFlag)baseMode;
        if (flags.HasFlag(MavModeFlag.AutoEnabled))
        {
            return "AUTO";
        }

        if (flags.HasFlag(MavModeFlag.GuidedEnabled))
        {
            return "GUIDED";
        }

        if (flags.HasFlag(MavModeFlag.StabilizeEnabled))
        {
            return "STABILIZE";
        }

        if (flags.HasFlag(MavModeFlag.ManualInputEnabled))
        {
            return "MANUAL";
        }

        return customMode == 0 ? "UNKNOWN" : ResolveUnknownCustomMode(customMode);
    }

    private static string ResolveUnknownCustomMode(uint customMode) => $"MODE {customMode}";
}
