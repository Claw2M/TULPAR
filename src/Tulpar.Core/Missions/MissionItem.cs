using Tulpar.Core.Mavlink;

namespace Tulpar.Core.Missions;

/// <summary>
/// Canonical mission or geofence item used by Tulpar planners, codecs, and protocol transfers.
/// </summary>
public sealed record MissionItem(
    int Seq,
    MavFrame Frame,
    MavCmd Command,
    float Param1,
    float Param2,
    float Param3,
    float Param4,
    double Latitude,
    double Longitude,
    float Altitude,
    bool Autocontinue = true);

/// <summary>
/// Progress report emitted during MAVLink mission or geofence upload/download operations.
/// </summary>
public sealed record MissionTransferProgress(
    MavMissionType MissionType,
    int CompletedCount,
    int TotalCount,
    string Stage);

/// <summary>
/// Serializable mission plan document containing both autonomous waypoints and geofence items.
/// </summary>
public sealed record MissionDocument(
    MavMissionType MissionType,
    IReadOnlyList<MissionItem> Items,
    IReadOnlyList<MissionItem> FenceItems);
