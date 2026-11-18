using System.Text;
using System.Threading.Channels;
using Tulpar.Core.Mavlink;

namespace Tulpar.Core.Session;

/// <summary>
/// Exception thrown when a vehicle rejects, mismatches, or violates the MAVLink parameter protocol.
/// </summary>
public sealed class ParameterProtocolException : Exception
{
    public ParameterProtocolException(string message)
        : base(message)
    {
    }

    public ParameterProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Strongly-typed vehicle onboard parameter entry reported via MAVLink PARAM_VALUE (#22).
/// </summary>
public sealed record VehicleParameter(
    string Name,
    float Value,
    MavParamType Type,
    ushort Index,
    ushort TotalCount);

/// <summary>
/// Progress report emitted during MAVLink parameter list download or write operations.
/// </summary>
public sealed record ParameterTransferProgress(
    int ReceivedCount,
    int TotalCount,
    string? LastParamName,
    string Stage);

/// <summary>
/// Thread-safe async state machine implementing the MAVLink parameter protocol
/// (<c>PARAM_REQUEST_LIST</c>, <c>PARAM_REQUEST_READ</c>, <c>PARAM_VALUE</c>, <c>PARAM_SET</c>)
/// with progress reporting, missing-index recovery, and echo verification.
/// </summary>
public sealed class ParameterProtocolManager
{
    private readonly byte _gcsSystemId;
    private readonly byte _gcsComponentId;
    private readonly int _stepTimeoutMs;
    private readonly int _maxRetries;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly Lock _stateLock = new();
    private readonly Dictionary<string, VehicleParameter> _parameters = new(StringComparer.Ordinal);

    private ActiveOperation? _activeOperation;
    private int _totalParameterCount;
    private int _txSequence;

    public ParameterProtocolManager(
        byte gcsSystemId = 255,
        byte gcsComponentId = 190,
        int stepTimeoutMs = 2000,
        int maxRetries = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stepTimeoutMs, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);

        _gcsSystemId = gcsSystemId;
        _gcsComponentId = gcsComponentId;
        _stepTimeoutMs = stepTimeoutMs;
        _maxRetries = maxRetries;
    }

    /// <summary>
    /// Raised whenever a valid <c>PARAM_VALUE</c> message updates the parameter cache.
    /// </summary>
    public event Action<VehicleParameter>? ParameterUpdated;

    /// <summary>
    /// Returns a thread-safe point-in-time snapshot dictionary of known parameters keyed by parameter ID.
    /// </summary>
    public IReadOnlyDictionary<string, VehicleParameter> Parameters
    {
        get
        {
            lock (_stateLock)
            {
                return new Dictionary<string, VehicleParameter>(_parameters, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// Returns a thread-safe point-in-time snapshot list of known parameters ordered by index then name.
    /// </summary>
    public IReadOnlyList<VehicleParameter> ParameterList
    {
        get
        {
            lock (_stateLock)
            {
                return _parameters.Values
                    .OrderBy(p => p.Index)
                    .ThenBy(p => p.Name, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    /// <summary>
    /// Total parameter count reported by the vehicle, or 0 if unknown.
    /// </summary>
    public int TotalParameterCount
    {
        get
        {
            lock (_stateLock)
            {
                return _totalParameterCount;
            }
        }
    }

    /// <summary>
    /// Clears all cached parameters (e.g., on transport disconnect or vehicle swap).
    /// </summary>
    public void Clear()
    {
        lock (_stateLock)
        {
            _parameters.Clear();
            _totalParameterCount = 0;
        }
    }

    /// <summary>
    /// Requests the complete parameter table from the vehicle using <c>PARAM_REQUEST_LIST</c> (#21),
    /// recovering any dropped indices with <c>PARAM_REQUEST_READ</c> (#20).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, VehicleParameter>> RequestAllParametersAsync(
        byte targetSysId,
        byte targetCompId,
        Func<byte[], CancellationToken, Task> sendAsync,
        IProgress<ParameterTransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sendAsync);

        await _operationLock.WaitAsync(ct).ConfigureAwait(false);
        var operation = new ActiveOperation(targetSysId, targetCompId);

        try
        {
            lock (_stateLock)
            {
                _parameters.Clear();
                _totalParameterCount = 0;
                _activeOperation = operation;
            }

            progress?.Report(new ParameterTransferProgress(0, 0, null, "RequestingList"));

            var receivedIndices = new HashSet<ushort>();
            int expectedTotal = 0;
            int attempt = 0;

            await sendAsync(EncodePacket(new ParamRequestListMessage(targetSysId, targetCompId)), ct)
                .ConfigureAwait(false);

            while (true)
            {
                long deadlineTick = Environment.TickCount64 + _stepTimeoutMs;
                bool receivedAnyInWindow = false;

                while (true)
                {
                    while (operation.Channel.Reader.TryRead(out ParamValueMessage? msg))
                    {
                        receivedAnyInWindow = true;
                        if (msg.ParamCount == 0)
                        {
                            progress?.Report(new ParameterTransferProgress(0, 0, null, "Completed"));
                            return Parameters;
                        }

                        if (msg.ParamCount > expectedTotal)
                        {
                            expectedTotal = msg.ParamCount;
                        }

                        if (msg.ParamIndex < expectedTotal)
                        {
                            receivedIndices.Add(msg.ParamIndex);
                        }

                        progress?.Report(new ParameterTransferProgress(
                            receivedIndices.Count,
                            expectedTotal,
                            msg.ParamId,
                            "Downloading"));

                        if (expectedTotal > 0 && receivedIndices.Count >= expectedTotal)
                        {
                            progress?.Report(new ParameterTransferProgress(
                                expectedTotal,
                                expectedTotal,
                                msg.ParamId,
                                "Completed"));
                            return Parameters;
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
                        if (!await operation.Channel.Reader.WaitToReadAsync(stepCts.Token).ConfigureAwait(false))
                        {
                            throw new ParameterProtocolException("Parameter transfer channel closed unexpectedly.");
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
                        $"Timed out waiting for vehicle parameters ({receivedIndices.Count}/{expectedTotal}) after {_maxRetries} retries.");
                }

                attempt++;

                if (expectedTotal == 0)
                {
                    await sendAsync(EncodePacket(new ParamRequestListMessage(targetSysId, targetCompId)), ct)
                        .ConfigureAwait(false);
                }
                else
                {
                    // Request only the missing parameter indices instead of restarting the full list stream.
                    for (ushort idx = 0; idx < expectedTotal; idx++)
                    {
                        if (!receivedIndices.Contains(idx))
                        {
                            var readReq = new ParamRequestReadMessage(
                                targetSysId,
                                targetCompId,
                                ParamId: string.Empty,
                                ParamIndex: checked((short)idx));
                            await sendAsync(EncodePacket(readReq), ct).ConfigureAwait(false);
                        }
                    }

                    if (!receivedAnyInWindow && attempt > 0)
                    {
                        progress?.Report(new ParameterTransferProgress(
                            receivedIndices.Count,
                            expectedTotal,
                            null,
                            "RecoveringMissing"));
                    }
                }
            }
        }
        finally
        {
            EndOperation(operation);
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Reads a single parameter by name using <c>PARAM_REQUEST_READ</c> (#20).
    /// </summary>
    public async Task<VehicleParameter> RequestParameterAsync(
        byte targetSysId,
        byte targetCompId,
        string paramId,
        Func<byte[], CancellationToken, Task> sendAsync,
        CancellationToken ct = default)
    {
        ValidateParamId(paramId);
        ArgumentNullException.ThrowIfNull(sendAsync);

        await _operationLock.WaitAsync(ct).ConfigureAwait(false);
        var operation = new ActiveOperation(targetSysId, targetCompId);

        try
        {
            lock (_stateLock)
            {
                _activeOperation = operation;
            }

            var req = new ParamRequestReadMessage(targetSysId, targetCompId, paramId, ParamIndex: -1);
            ParamValueMessage echoed = await SendAndWaitForParamValueAsync(
                operation,
                req,
                sendAsync,
                msg => string.Equals(msg.ParamId, paramId, StringComparison.Ordinal),
                $"read parameter '{paramId}'",
                ct).ConfigureAwait(false);

            return ToVehicleParameter(echoed);
        }
        finally
        {
            EndOperation(operation);
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Writes a single parameter using <c>PARAM_SET</c> (#23) and verifies the echoed <c>PARAM_VALUE</c> (#22).
    /// </summary>
    public async Task<VehicleParameter> SetParameterAsync(
        byte targetSysId,
        byte targetCompId,
        string paramId,
        float value,
        MavParamType paramType,
        Func<byte[], CancellationToken, Task> sendAsync,
        CancellationToken ct = default)
    {
        ValidateParamId(paramId);
        if (!float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Parameter value must be finite.");
        }

        ArgumentNullException.ThrowIfNull(sendAsync);

        await _operationLock.WaitAsync(ct).ConfigureAwait(false);
        var operation = new ActiveOperation(targetSysId, targetCompId);

        try
        {
            lock (_stateLock)
            {
                _activeOperation = operation;
            }

            var setMsg = new ParamSetMessage(targetSysId, targetCompId, paramId, value, paramType);
            ParamValueMessage echoed = await SendAndWaitForParamValueAsync(
                operation,
                setMsg,
                sendAsync,
                msg => string.Equals(msg.ParamId, paramId, StringComparison.Ordinal),
                $"set parameter '{paramId}'",
                ct).ConfigureAwait(false);

            float tolerance = 1e-4f * Math.Max(1f, Math.Abs(value));
            if (!float.IsFinite(echoed.ParamValue) || Math.Abs(echoed.ParamValue - value) > tolerance)
            {
                throw new ParameterProtocolException(
                    $"Parameter '{paramId}' echo verification failed: requested {value}, vehicle echoed {echoed.ParamValue}.");
            }

            return ToVehicleParameter(echoed);
        }
        finally
        {
            EndOperation(operation);
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Feeds a received MAVLink frame into the parameter cache and any active parameter transfer.
    /// </summary>
    public void HandleIncomingFrame(MavlinkFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.MessageId != ParamValueMessage.MsgId)
        {
            return;
        }

        ParamValueMessage msg = ParamValueMessage.Decode(frame);
        VehicleParameter param = ToVehicleParameter(msg);

        lock (_stateLock)
        {
            if (!string.IsNullOrEmpty(param.Name))
            {
                _parameters[param.Name] = param;
            }

            if (param.TotalCount > _totalParameterCount)
            {
                _totalParameterCount = param.TotalCount;
            }

            ActiveOperation? op = _activeOperation;
            if (op is not null &&
                frame.SystemId == op.TargetSysId &&
                (op.TargetCompId == 0 || frame.ComponentId == op.TargetCompId))
            {
                op.Channel.Writer.TryWrite(msg);
            }
        }

        ParameterUpdated?.Invoke(param);
    }

    private async Task<ParamValueMessage> SendAndWaitForParamValueAsync(
        ActiveOperation operation,
        IMavlinkMessage outgoing,
        Func<byte[], CancellationToken, Task> sendAsync,
        Func<ParamValueMessage, bool> isMatch,
        string stepDescription,
        CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            await sendAsync(EncodePacket(outgoing), ct).ConfigureAwait(false);

            long deadlineTick = Environment.TickCount64 + _stepTimeoutMs;
            while (true)
            {
                while (operation.Channel.Reader.TryRead(out ParamValueMessage? msg))
                {
                    if (isMatch(msg))
                    {
                        return msg;
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
                    if (!await operation.Channel.Reader.WaitToReadAsync(stepCts.Token).ConfigureAwait(false))
                    {
                        throw new ParameterProtocolException($"Parameter channel closed unexpectedly during {stepDescription}.");
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

    private void EndOperation(ActiveOperation operation)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(_activeOperation, operation))
            {
                _activeOperation = null;
            }
        }

        operation.Channel.Writer.TryComplete();
    }

    private byte[] EncodePacket(IMavlinkMessage message)
    {
        byte seq = unchecked((byte)(Interlocked.Increment(ref _txSequence) - 1));
        return MavlinkEncoder.EncodeV2(_gcsSystemId, _gcsComponentId, seq, message);
    }

    private static VehicleParameter ToVehicleParameter(ParamValueMessage msg) =>
        new(msg.ParamId, msg.ParamValue, msg.ParamType, msg.ParamIndex, msg.ParamCount);

    private static void ValidateParamId(string paramId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paramId);
        if (Encoding.UTF8.GetByteCount(paramId) > ParamSetMessage.ParamIdLength)
        {
            throw new ArgumentException(
                $"MAVLink parameter name '{paramId}' exceeds {ParamSetMessage.ParamIdLength} bytes.",
                nameof(paramId));
        }
    }

    private sealed class ActiveOperation(byte targetSysId, byte targetCompId)
    {
        public byte TargetSysId { get; } = targetSysId;
        public byte TargetCompId { get; } = targetCompId;
        public Channel<ParamValueMessage> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<ParamValueMessage>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    }
}
