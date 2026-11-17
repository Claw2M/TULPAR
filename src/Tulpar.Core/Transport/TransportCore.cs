using System.Net.Sockets;

namespace Tulpar.Core.Transport;

/// <summary>
/// Shared connection lifecycle, receive loop, send serialization, and clean async disposal
/// coordinator for <see cref="ITransport"/> implementations.
/// </summary>
internal sealed class TransportCore
{
    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();

    private CancellationTokenSource? _loopCts;
    private TaskCompletionSource? _loopExited;
    private Action? _closeResource;
    private volatile bool _isConnected;
    private int _disposed;
    private int _disconnectRaised;
    private int _activeConnects;

    public bool IsConnected => _isConnected && Volatile.Read(ref _disposed) == 0;

    public bool IsDisposingOrDisposed => Volatile.Read(ref _disposed) != 0;

    public void ThrowIfDisposed(object owner)
    {
        ObjectDisposedException.ThrowIf(IsDisposingOrDisposed, owner);
    }

    public async Task ConnectAsync(
        object owner,
        Func<CancellationToken, Task<Action>> openAsync,
        Func<CancellationToken, Task<string?>> receiveBodyAsync,
        Action<string> raiseDisconnected,
        CancellationToken ct)
    {
        ThrowIfDisposed(owner);
        ct.ThrowIfCancellationRequested();

        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        bool trackedConnect = false;
        try
        {
            CancellationToken disposeToken;
            lock (_stateLock)
            {
                ThrowIfDisposed(owner);
                if (_isConnected)
                {
                    throw new InvalidOperationException("Transport is already connected.");
                }

                _activeConnects++;
                trackedConnect = true;
                disposeToken = _disposeCts.Token;
            }

            TaskCompletionSource? previousLoopExited;
            lock (_stateLock)
            {
                previousLoopExited = _loopExited;
            }

            if (previousLoopExited is not null)
            {
                await previousLoopExited.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, disposeToken);
            Action closeResource;
            try
            {
                closeResource = await openAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (IsDisposingOrDisposed && !ct.IsCancellationRequested)
            {
                throw new ObjectDisposedException(owner.GetType().FullName);
            }

            var loopCts = new CancellationTokenSource();
            var loopExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            CancellationTokenSource? staleLoopCts = null;
            bool abortedByDispose = false;

            lock (_stateLock)
            {
                if (_disposed != 0)
                {
                    abortedByDispose = true;
                }
                else
                {
                    staleLoopCts = _loopCts;
                    _loopCts = loopCts;
                    _loopExited = loopExited;
                    _closeResource = closeResource;
                    Interlocked.Exchange(ref _disconnectRaised, 0);
                    _isConnected = true;
                }
            }

            staleLoopCts?.Dispose();

            if (abortedByDispose)
            {
                SafeInvokeClose(closeResource);
                loopCts.Dispose();
                throw new ObjectDisposedException(owner.GetType().FullName);
            }

            _ = Task.Run(() => RunLoopAsync(loopCts.Token, loopExited, receiveBodyAsync, raiseDisconnected), CancellationToken.None);
        }
        finally
        {
            if (trackedConnect)
            {
                bool shouldDisposeCts = false;
                lock (_stateLock)
                {
                    _activeConnects--;
                    if (_disposed != 0 && _activeConnects == 0)
                    {
                        shouldDisposeCts = true;
                    }
                }

                if (shouldDisposeCts)
                {
                    _disposeCts.Dispose();
                }
            }

            _connectLock.Release();
        }
    }

    public async Task ExecuteSendAsync(
        object owner,
        Func<CancellationToken, Task> sendAction,
        Action<string> raiseDisconnected,
        CancellationToken ct)
    {
        ThrowIfDisposed(owner);
        if (!IsConnected)
        {
            throw new InvalidOperationException("Transport is not connected.");
        }

        ct.ThrowIfCancellationRequested();

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        string? unexpectedDisconnectReason = null;
        try
        {
            ThrowIfDisposed(owner);
            if (!IsConnected)
            {
                throw new InvalidOperationException("Transport is not connected.");
            }

            try
            {
                await sendAction(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || IsDisposingOrDisposed)
            {
                if (IsDisposingOrDisposed)
                {
                    throw new ObjectDisposedException(owner.GetType().FullName);
                }

                throw;
            }
            catch (Exception ex) when (IsDisconnectException(ex))
            {
                if (!IsDisposingOrDisposed)
                {
                    unexpectedDisconnectReason = FormatDisconnectReason(ex);
                    CloseActiveResourceOnFailure();
                }

                throw;
            }
        }
        finally
        {
            _sendLock.Release();
            if (unexpectedDisconnectReason is not null && !IsDisposingOrDisposed)
            {
                RaiseDisconnectedOnce(unexpectedDisconnectReason, raiseDisconnected);
            }
        }
    }

    public static async Task<string?> RunStreamReadLoopAsync(
        Stream stream,
        int bufferSize,
        Action<ReadOnlyMemory<byte>> onData,
        CancellationToken ct)
    {
        byte[] buffer = new byte[bufferSize];
        while (!ct.IsCancellationRequested)
        {
            int bytesRead = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return "Transport stream closed by remote endpoint.";
            }

            byte[] copy = buffer.AsSpan(0, bytesRead).ToArray();
            onData(copy);
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? loopCts;
        TaskCompletionSource? loopExited;
        Action? closeResource;
        bool canDisposeConnectCts;

        lock (_stateLock)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            _isConnected = false;
            loopCts = _loopCts;
            loopExited = _loopExited;
            closeResource = _closeResource;
            _closeResource = null;
            canDisposeConnectCts = _activeConnects == 0;
        }

        try
        {
            _disposeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (loopCts is not null)
        {
            try
            {
                loopCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (closeResource is not null)
        {
            SafeInvokeClose(closeResource);
        }

        if (loopExited is not null)
        {
            await loopExited.Task.ConfigureAwait(false);
        }

        loopCts?.Dispose();
        if (canDisposeConnectCts)
        {
            _disposeCts.Dispose();
        }
    }

    private async Task RunLoopAsync(
        CancellationToken ct,
        TaskCompletionSource loopExited,
        Func<CancellationToken, Task<string?>> receiveBodyAsync,
        Action<string> raiseDisconnected)
    {
        string? disconnectReason = null;
        try
        {
            disconnectReason = await receiveBodyAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || IsDisposingOrDisposed)
        {
            // Intentional cancellation / disposal.
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested && !IsDisposingOrDisposed)
            {
                disconnectReason = FormatDisconnectReason(ex);
            }
        }
        finally
        {
            Action? closeOnExit;
            lock (_stateLock)
            {
                _isConnected = false;
                closeOnExit = _closeResource;
                _closeResource = null;
            }

            if (closeOnExit is not null)
            {
                SafeInvokeClose(closeOnExit);
            }

            loopExited.TrySetResult();
        }

        if (disconnectReason is not null && !ct.IsCancellationRequested && !IsDisposingOrDisposed)
        {
            RaiseDisconnectedOnce(disconnectReason, raiseDisconnected);
        }
    }

    private void CloseActiveResourceOnFailure()
    {
        Action? closeResource;
        CancellationTokenSource? loopCts;
        lock (_stateLock)
        {
            _isConnected = false;
            closeResource = _closeResource;
            _closeResource = null;
            loopCts = _loopCts;
        }

        if (loopCts is not null && !IsDisposingOrDisposed)
        {
            try
            {
                loopCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (closeResource is not null)
        {
            SafeInvokeClose(closeResource);
        }
    }

    private void RaiseDisconnectedOnce(string reason, Action<string> raiseDisconnected)
    {
        if (Interlocked.Exchange(ref _disconnectRaised, 1) == 0)
        {
            string safeReason = string.IsNullOrWhiteSpace(reason) ? "Transport disconnected." : reason;
            raiseDisconnected(safeReason);
        }
    }

    private static bool IsDisconnectException(Exception ex) =>
        ex is IOException or SocketException or UnauthorizedAccessException or InvalidOperationException;

    private static string FormatDisconnectReason(Exception ex) =>
        string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;

    private static void SafeInvokeClose(Action closeResource)
    {
        try
        {
            closeResource();
        }
        catch
        {
            // Swallow teardown exceptions during disconnect/disposal.
        }
    }
}
