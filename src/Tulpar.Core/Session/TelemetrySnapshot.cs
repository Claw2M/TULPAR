using Tulpar.Core.Mavlink;
using Tulpar.Core.Vehicles;

namespace Tulpar.Core.Session;

/// <summary>
/// Immutable point-in-time vehicle telemetry state exposed by <see cref="VehicleSession"/> and <see cref="TelemetryBus"/>.
/// </summary>
public sealed record TelemetrySnapshot(
    bool IsConnected = false,
    bool LinkLost = false,
    byte SystemId = 0,
    byte ComponentId = 0,
    MavType VehicleType = MavType.Generic,
    MavAutopilot Autopilot = MavAutopilot.Generic,
    string VehicleDisplayLabel = "NO VEHICLE",
    VehicleCategory VehicleCategory = VehicleCategory.Unknown,
    string ModeName = "UNKNOWN",
    bool IsArmed = false,
    double Latitude = 0.0,
    double Longitude = 0.0,
    double AltitudeRelativeMeters = 0.0,
    double AltitudeAmslMeters = 0.0,
    double AirspeedMps = 0.0,
    double GroundspeedMps = 0.0,
    double VerticalSpeedMps = 0.0,
    double HeadingDeg = 0.0,
    double RollDeg = 0.0,
    double PitchDeg = 0.0,
    double YawDeg = 0.0,
    double BatteryVoltageVolts = 0.0,
    double BatteryCurrentAmps = 0.0,
    int BatteryRemainingPercent = -1,
    GpsFixType GpsFixType = GpsFixType.NoGps,
    int SatellitesVisible = 0,
    double Hdop = 0.0,
    int RadioRssi = 0,
    int RemoteRssi = 0,
    int ThrottlePercent = 0,
    double HomeLatitude = 0.0,
    double HomeLongitude = 0.0,
    double HomeAltitudeMeters = 0.0,
    bool HasHome = false,
    int ActiveWaypointSeq = 0,
    DateTimeOffset LastHeartbeatUtc = default,
    DateTimeOffset UpdatedUtc = default)
{
    public static TelemetrySnapshot Empty { get; } = new();
}
