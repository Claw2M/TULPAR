using Tulpar.Core.Mavlink;

namespace Tulpar.Core.Vehicles;

/// <summary>
/// Resolves an <see cref="IVehicleProfile"/> from MAVLink HEARTBEAT type and autopilot metadata.
/// </summary>
public static class VehicleProfileFactory
{
    /// <summary>
    /// Creates the matching adaptive vehicle profile for <paramref name="type"/> and <paramref name="autopilot"/>.
    /// Unrecognized <see cref="MavType"/> values fall back safely without throwing.
    /// </summary>
    public static IVehicleProfile Create(MavType type, MavAutopilot autopilot)
    {
        _ = autopilot;

        return type switch
        {
            MavType.FixedWing => new FixedWingProfile(),

            MavType.Quadrotor or
            MavType.Coaxial or
            MavType.Helicopter or
            MavType.Hexarotor or
            MavType.Octorotor or
            MavType.Tricopter or
            MavType.Dodecarotor or
            MavType.Decarotor => new MultirotorProfile(type),

            MavType.VtolTailsitterDuorotor or
            MavType.VtolTailsitterQuadrotor or
            MavType.VtolTiltrotor or
            MavType.VtolFixedrotor or
            MavType.VtolTailsitter or
            MavType.VtolTilthot or
            MavType.VtolReserved5 => new VtolProfile(),

            MavType.GroundRover => new RoverIkaProfile(),
            MavType.SurfaceBoat => new BoatIdaProfile(),

            _ => new MultirotorProfile(VehicleCategory.Unknown, BuildUnknownLabel(type))
        };
    }

    private static string BuildUnknownLabel(MavType type) =>
        Enum.IsDefined(type)
            ? $"UNKNOWN ({type.ToString().ToUpperInvariant()})"
            : $"UNKNOWN ({(byte)type})";
}
