using System.Threading.Channels;
using Tulpar.Core.Mavlink;

namespace Tulpar.Core.Missions;

/// <summary>
/// Exception thrown when a vehicle rejects or violates the MAVLink mission protocol.
/// </summary>
public sealed class MissionProtocolException : Exception
{
    public MissionProtocolException(string message, MavMissionResult? result = null)
        : base(message)
    {
        Result = result;
    }

    public MissionProtocolException(string message, Exception innerException, MavMissionResult? result = null)
        : base(message, innerException)
    {
        Result = result;
    }

    public MavMissionResult? Result { get; }
}

/// <summary>
/// Thread-safe async state machine implementing the MAVLink v2 mission and geofence transfer protocol
/// with step timeouts, retries, out-of-order/duplicate seq handling, and legacy MISSION_REQUEST/MISSION_ITEM fallback.
/// </summary>
public sealed class MissionProtocolManager
{
    private readonly byte _gcsSystemId;
    private readonly byte _gcsComponentId;
    private readonly int _stepTimeoutMs;
    private readonly int _maxRetries;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly Lock _stateLock = new();

    private ActiveTransfer? _activeTransfer;
    private int _txSequence;

    public MissionProtocolManager(
        byte gcsSystemId = 255,
        byte gcsComponentId = 190,
        int stepTimeoutMs = 1500,
        int maxRetries = 5)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stepTimeoutMs, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);

        _gcsSystemId = gcsSystemId;
        _gcsComponentId = gcsComponentId;
        _stepTimeoutMs = stepTimeoutMs;
        _maxRetries = maxRetries;
    }

    /// <summary>
    /// Uploads mission or geofence items to the target vehicle using MISSION_COUNT / MISSION_REQUEST(_INT) / MISSION_ITEM_INT / MISSION_ACK.
    /// </summary>
    public Task UploadAsync(
        byte targetSysId,
        byte targetCompId,
        MavMissionType missionType,
        IReadOnlyList<MissionItem> items,
        Func<byte[], CancellationToken, Task> sendAsync,
        IProgress<MissionTransferProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(sendAsync);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(items.Count, ushort.MaxValue);

        for (int i = 0; i < items.Count; i++)
        {
            MissionItem item = items[i] ?? throw new ArgumentException($"Mission item at index {i} is null.", nameof(items));
            ValidateMissionItemCoordinates(item, nameof(items));
        }

        return RunTransferAsync(
            targetSysId,
            targetCompId,
            missionType,
            async transfer =>
            {
                int total = items.Count;
                progress?.Report(new MissionTransferProgress(missionType, 0, total, "Starting"));

                IMavlinkMessage currentOutgoing = new MissionCountMessage(
                    TargetSystem: targetSysId,
                    TargetComponent: targetCompId,
                    Count: (ushort)total,
                    MissionType: missionType);

                var sentSeqs = new HashSet<int>();

                while (true)
                {
                    MavlinkFrame response = await SendAndWaitForNextFrameAsync(
                        transfer,
                        currentOutgoing,
                        sendAsync,
                        frame => IsUploadResponseFrame(frame, allowRequests: total > 0),
                        $"{missionType} upload",
                        ct).ConfigureAwait(false);

                    if (response.MessageId == MissionAckMessage.MsgId)
                    {
                        MissionAckMessage ack = MissionAckMessage.Decode(response);
                        EnsureAckAccepted(ack, $"{missionType} upload");
                        if (sentSeqs.Count < total)
                        {
                            throw new MissionProtocolException(
                                $"Received premature {missionType} MISSION_ACK(Accepted) after sending {sentSeqs.Count} of {total} items.",
                                ack.Type);
                        }

                        progress?.Report(new MissionTransferProgress(missionType, total, total, "Completed"));
                        return true;
                    }

                    ushort requestedSeq = DecodeRequestedSeq(response);
                    if (requestedSeq >= total)
                    {
                        throw new MissionProtocolException(
                            $"Vehicle requested out-of-range sequence {requestedSeq} for {total} {missionType} items.");
                    }

                    MissionItem item = items[requestedSeq];
                    currentOutgoing = response.MessageId == MissionRequestMessage.MsgId
                        ? BuildMissionItem(targetSysId, targetCompId, missionType, requestedSeq, item)
                        : BuildMissionItemInt(targetSysId, targetCompId, missionType, requestedSeq, item);

                    sentSeqs.Add(requestedSeq);
                    progress?.Report(new MissionTransferProgress(missionType, sentSeqs.Count, total, "Uploading"));
                }
            },
            ct);
    }

    /// <summary>
    /// Downloads all mission or geofence items from the target vehicle in sequence order.
    /// </summary>
    public Task<IReadOnlyList<MissionItem>> DownloadAsync(
        byte targetSysId,
        byte targetCompId,
        MavMissionType missionType,
        Func<byte[], CancellationToken, Task> sendAsync,
        IProgress<MissionTransferProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sendAsync);

        return RunTransferAsync<IReadOnlyList<MissionItem>>(
            targetSysId,
            targetCompId,
            missionType,
            async transfer =>
            {
                progress?.Report(new MissionTransferProgress(missionType, 0, 0, "RequestingList"));

                var requestList = new MissionRequestListMessage(targetSysId, targetCompId, missionType);
                MavlinkFrame countFrame = await SendAndWaitForNextFrameAsync(
                    transfer,
                    requestList,
                    sendAsync,
                    frame => IsCountOrErrorAck(frame, $"{missionType} download list"),
                    $"{missionType} download list",
                    ct).ConfigureAwait(false);

                ushort count = MissionCountMessage.Decode(countFrame).Count;
                if (count == 0)
                {
                    await SendAckAsync(targetSysId, targetCompId, missionType, sendAsync, ct).ConfigureAwait(false);
                    progress?.Report(new MissionTransferProgress(missionType, 0, 0, "Completed"));
                    return Array.Empty<MissionItem>();
                }

                var items = new List<MissionItem>(count);
                for (ushort seq = 0; seq < count; seq++)
                {
                    var requestInt = new MissionRequestIntMessage(targetSysId, targetCompId, seq, missionType);
                    ushort expectedSeq = seq;

                    MavlinkFrame itemFrame = await SendAndWaitForNextFrameAsync(
                        transfer,
                        requestInt,
                        sendAsync,
                        frame => IsMatchingDownloadedItemOrErrorAck(frame, expectedSeq, $"{missionType} download item {expectedSeq}"),
                        $"{missionType} download item {seq}",
                        ct).ConfigureAwait(false);

                    items.Add(DecodeMissionItem(itemFrame));
                    progress?.Report(new MissionTransferProgress(missionType, items.Count, count, "Downloading"));
                }

                await SendAckAsync(targetSysId, targetCompId, missionType, sendAsync, ct).ConfigureAwait(false);
                progress?.Report(new MissionTransferProgress(missionType, count, count, "Completed"));
                return items;
            },
            ct);
    }

    /// <summary>
    /// Clears all items of the specified mission type on the target vehicle and waits for MISSION_ACK.
    /// </summary>
    public Task ClearAllAsync(
        byte targetSysId,
        byte targetCompId,
        MavMissionType missionType,
        Func<byte[], CancellationToken, Task> sendAsync,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sendAsync);

        return RunTransferAsync(
            targetSysId,
            targetCompId,
            missionType,
            async transfer =>
            {
                var clearMsg = new MissionClearAllMessage(targetSysId, targetCompId, missionType);
                MavlinkFrame ackFrame = await SendAndWaitForNextFrameAsync(
                    transfer,
                    clearMsg,
                    sendAsync,
                    frame => frame.MessageId == MissionAckMessage.MsgId,
                    $"{missionType} clear",
                    ct).ConfigureAwait(false);

                MissionAckMessage ack = MissionAckMessage.Decode(ackFrame);
                EnsureAckAccepted(ack, $"{missionType} clear");
                return true;
            },
            ct);
    }

    /// <summary>
    /// Feeds a received MAVLink frame into any currently active mission protocol transfer.
    /// </summary>
    public void HandleIncomingFrame(MavlinkFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (!IsMissionProtocolMessageId(frame.MessageId))
        {
            return;
        }

        lock (_stateLock)
        {
            ActiveTransfer? transfer = _activeTransfer;
            if (transfer is null)
            {
                return;
            }

            if (frame.SystemId != transfer.TargetSysId)
            {
                return;
            }

            if (transfer.TargetCompId != 0 && frame.ComponentId != transfer.TargetCompId)
            {
                return;
            }

            if (!MatchesTransferMissionType(frame, transfer.MissionType))
            {
                return;
            }

            transfer.Channel.Writer.TryWrite(frame);
        }
    }

    private async Task<T> RunTransferAsync<T>(
        byte targetSysId,
        byte targetCompId,
        MavMissionType missionType,
        Func<ActiveTransfer, Task<T>> action,
        CancellationToken ct)
    {
        await _operationLock.WaitAsync(ct).ConfigureAwait(false);
        var transfer = new ActiveTransfer(targetSysId, targetCompId, missionType);

        try
        {
            lock (_stateLock)
            {
                _activeTransfer = transfer;
            }

            return await action(transfer).ConfigureAwait(false);
        }
        finally
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_activeTransfer, transfer))
                {
                    _activeTransfer = null;
                }
            }

            transfer.Channel.Writer.TryComplete();
            _operationLock.Release();
        }
    }

    private async Task<MavlinkFrame> SendAndWaitForNextFrameAsync(
        ActiveTransfer transfer,
        IMavlinkMessage message,
        Func<byte[], CancellationToken, Task> sendAsync,
        Func<MavlinkFrame, bool> isExpectedFrame,
        string stepDescription,
        CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            byte[] packet = EncodePacket(message);
            await sendAsync(packet, ct).ConfigureAwait(false);

            long deadlineTick = Environment.TickCount64 + _stepTimeoutMs;
            while (true)
            {
                while (transfer.Channel.Reader.TryRead(out MavlinkFrame? queuedFrame))
                {
                    if (isExpectedFrame(queuedFrame))
                    {
                        return queuedFrame;
                    }
                }

                long remainingMs = deadlineTick - Environment.TickCount64;
                if (remainingMs <= 0)
                {
                    break;
                }

                try
                {
                    using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stepCts.CancelAfter((int)remainingMs);
                    if (!await transfer.Channel.Reader.WaitToReadAsync(stepCts.Token).ConfigureAwait(false))
                    {
                        throw new MissionProtocolException($"Transfer channel closed unexpectedly during {stepDescription}.");
                    }
                }
                catch (OperationCanceledException)
                {
                    ct.ThrowIfCancellationRequested();
                    break;
                }
            }

            if (attempt >= _maxRetries)
            {
                throw new TimeoutException(
                    $"Timed out waiting for vehicle response during {stepDescription} after {_maxRetries} retries.");
            }
        }
    }

    private Task SendAckAsync(
        byte targetSysId,
        byte targetCompId,
        MavMissionType missionType,
        Func<byte[], CancellationToken, Task> sendAsync,
        CancellationToken ct)
    {
        var ack = new MissionAckMessage(targetSysId, targetCompId, MavMissionResult.Accepted, missionType);
        return sendAsync(EncodePacket(ack), ct);
    }

    private byte[] EncodePacket(IMavlinkMessage message)
    {
        byte seq = unchecked((byte)(Interlocked.Increment(ref _txSequence) - 1));
        return MavlinkEncoder.EncodeV2(_gcsSystemId, _gcsComponentId, seq, message);
    }

    private static bool IsUploadResponseFrame(MavlinkFrame frame, bool allowRequests)
    {
        if (frame.MessageId == MissionAckMessage.MsgId)
        {
            return true;
        }

        return allowRequests &&
               (frame.MessageId == MissionRequestIntMessage.MsgId ||
                frame.MessageId == MissionRequestMessage.MsgId);
    }

    private static bool IsCountOrErrorAck(MavlinkFrame frame, string operation)
    {
        if (frame.MessageId == MissionCountMessage.MsgId)
        {
            return true;
        }

        ThrowIfErrorAck(frame, operation);
        return false;
    }

    private static bool IsMatchingDownloadedItemOrErrorAck(MavlinkFrame frame, ushort expectedSeq, string operation)
    {
        if (frame.MessageId == MissionItemIntMessage.MsgId)
        {
            return MissionItemIntMessage.Decode(frame).Seq == expectedSeq;
        }

        if (frame.MessageId == MissionItemMessage.MsgId)
        {
            return MissionItemMessage.Decode(frame).Seq == expectedSeq;
        }

        ThrowIfErrorAck(frame, operation);
        return false;
    }

    private static void ThrowIfErrorAck(MavlinkFrame frame, string operation)
    {
        if (frame.MessageId != MissionAckMessage.MsgId)
        {
            return;
        }

        MissionAckMessage ack = MissionAckMessage.Decode(frame);
        if (ack.Type != MavMissionResult.Accepted)
        {
            throw new MissionProtocolException($"{operation} failed with vehicle ack {ack.Type}.", ack.Type);
        }
    }

    private static void EnsureAckAccepted(MissionAckMessage ack, string operation)
    {
        if (ack.Type != MavMissionResult.Accepted)
        {
            throw new MissionProtocolException(
                $"{operation} rejected by vehicle with {ack.Type}.",
                ack.Type);
        }
    }

    private static ushort DecodeRequestedSeq(MavlinkFrame frame) =>
        frame.MessageId == MissionRequestIntMessage.MsgId
            ? MissionRequestIntMessage.Decode(frame).Seq
            : MissionRequestMessage.Decode(frame).Seq;

    private static void ValidateMissionItemCoordinates(MissionItem item, string paramName)
    {
        if (!double.IsFinite(item.Latitude) || item.Latitude is < -90.0 or > 90.0)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                item.Latitude,
                $"Mission item seq {item.Seq} latitude must be finite and within [-90, 90].");
        }

        if (!double.IsFinite(item.Longitude) || item.Longitude is < -180.0 or > 180.0)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                item.Longitude,
                $"Mission item seq {item.Seq} longitude must be finite and within [-180, 180].");
        }

        if (!float.IsFinite(item.Altitude))
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                item.Altitude,
                $"Mission item seq {item.Seq} altitude must be finite.");
        }
    }

    private static MissionItemIntMessage BuildMissionItemInt(
        byte targetSysId,
        byte targetCompId,
        MavMissionType missionType,
        ushort seq,
        MissionItem item)
    {
        ValidateMissionItemCoordinates(item, nameof(item));
        return new MissionItemIntMessage(
            TargetSystem: targetSysId,
            TargetComponent: targetCompId,
            Seq: seq,
            Frame: item.Frame,
            Command: item.Command,
            Current: 0,
            Autocontinue: (byte)(item.Autocontinue ? 1 : 0),
            Param1: item.Param1,
            Param2: item.Param2,
            Param3: item.Param3,
            Param4: item.Param4,
            X: (int)Math.Round(item.Latitude * 1e7),
            Y: (int)Math.Round(item.Longitude * 1e7),
            Z: item.Altitude,
            MissionType: missionType);
    }

    private static MissionItemMessage BuildMissionItem(
        byte targetSysId,
        byte targetCompId,
        MavMissionType missionType,
        ushort seq,
        MissionItem item)
    {
        ValidateMissionItemCoordinates(item, nameof(item));
        return new MissionItemMessage(
            TargetSystem: targetSysId,
            TargetComponent: targetCompId,
            Seq: seq,
            Frame: item.Frame,
            Command: item.Command,
            Current: 0,
            Autocontinue: (byte)(item.Autocontinue ? 1 : 0),
            Param1: item.Param1,
            Param2: item.Param2,
            Param3: item.Param3,
            Param4: item.Param4,
            X: (float)item.Latitude,
            Y: (float)item.Longitude,
            Z: item.Altitude,
            MissionType: missionType);
    }

    private static MissionItem DecodeMissionItem(MavlinkFrame frame)
    {
        if (frame.MessageId == MissionItemIntMessage.MsgId)
        {
            MissionItemIntMessage msg = MissionItemIntMessage.Decode(frame);
            return new MissionItem(
                Seq: msg.Seq,
                Frame: msg.Frame,
                Command: msg.Command,
                Param1: msg.Param1,
                Param2: msg.Param2,
                Param3: msg.Param3,
                Param4: msg.Param4,
                Latitude: msg.X / 1e7,
                Longitude: msg.Y / 1e7,
                Altitude: msg.Z,
                Autocontinue: msg.Autocontinue != 0);
        }

        MissionItemMessage legacy = MissionItemMessage.Decode(frame);
        return new MissionItem(
            Seq: legacy.Seq,
            Frame: legacy.Frame,
            Command: legacy.Command,
            Param1: legacy.Param1,
            Param2: legacy.Param2,
            Param3: legacy.Param3,
            Param4: legacy.Param4,
            Latitude: legacy.X,
            Longitude: legacy.Y,
            Altitude: legacy.Z,
            Autocontinue: legacy.Autocontinue != 0);
    }

    private static bool IsMissionProtocolMessageId(uint messageId) =>
        messageId is MissionItemMessage.MsgId
            or MissionRequestMessage.MsgId
            or MissionCountMessage.MsgId
            or MissionAckMessage.MsgId
            or MissionRequestIntMessage.MsgId
            or MissionItemIntMessage.MsgId;

    private static bool MatchesTransferMissionType(MavlinkFrame frame, MavMissionType activeType) =>
        frame.MessageId switch
        {
            MissionRequestIntMessage.MsgId =>
                MissionRequestIntMessage.Decode(frame).MissionType == activeType,

            MissionRequestMessage.MsgId =>
                frame.Version == 1 || MissionRequestMessage.Decode(frame).MissionType == activeType,

            MissionItemIntMessage.MsgId =>
                MissionItemIntMessage.Decode(frame).MissionType == activeType,

            MissionItemMessage.MsgId =>
                frame.Version == 1 || MissionItemMessage.Decode(frame).MissionType == activeType,

            MissionCountMessage.MsgId =>
                frame.Version == 1 || MissionCountMessage.Decode(frame).MissionType == activeType,

            MissionAckMessage.MsgId =>
                frame.Version == 1 || MatchesAckMissionType(MissionAckMessage.Decode(frame).MissionType, activeType),

            _ => false
        };

    private static bool MatchesAckMissionType(MavMissionType ackType, MavMissionType activeType) =>
        ackType == activeType || (activeType == MavMissionType.All && ackType == MavMissionType.Mission);

    private sealed class ActiveTransfer(byte targetSysId, byte targetCompId, MavMissionType missionType)
    {
        public byte TargetSysId { get; } = targetSysId;
        public byte TargetCompId { get; } = targetCompId;
        public MavMissionType MissionType { get; } = missionType;
        public Channel<MavlinkFrame> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<MavlinkFrame>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    }
}
