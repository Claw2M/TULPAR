using Tulpar.Core.Mavlink;

namespace Tulpar.Core.Session;

/// <summary>
/// Reactive in-process event bus broadcasting telemetry snapshots, status text alerts, and MAVLink frames.
/// </summary>
public sealed class TelemetryBus
{
    public event Action<TelemetrySnapshot>? SnapshotUpdated;
    public event Action<StatusTextMessage>? StatusTextReceived;
    public event Action<MavlinkFrame>? RawFrameReceived;
    public event Action<MavlinkFrame>? CommandAckReceived;

    public void PublishSnapshot(TelemetrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        SnapshotUpdated?.Invoke(snapshot);
    }

    public void PublishStatusText(StatusTextMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        StatusTextReceived?.Invoke(message);
    }

    public void PublishRawFrame(MavlinkFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        RawFrameReceived?.Invoke(frame);
    }

    public void PublishCommandAck(MavlinkFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        CommandAckReceived?.Invoke(frame);
    }
}
