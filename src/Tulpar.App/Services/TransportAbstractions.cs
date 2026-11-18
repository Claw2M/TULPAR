using System.Windows.Threading;
using Tulpar.Core.Mavlink;
using Tulpar.Core.Transport;

namespace Tulpar.App.Services;

public enum ConnectionKind
{
    Udp,
    Serial,
    Tcp
}

public enum MapEditMode
{
    None,
    Mission,
    Fence,
    Rally,
    Bounds,
    Measure
}

public enum CockpitFormFactor
{
    Desktop,
    Tablet,
    PhoneLandscape
}

public readonly record struct MapBounds(double MinLat, double MinLon, double MaxLat, double MaxLon);

public sealed record LogEntry(
    DateTimeOffset TimestampUtc,
    MavSeverity Severity,
    string Source,
    string Message,
    int RepeatCount = 1)
{
    public string DisplayLine =>
        RepeatCount > 1
            ? $"[{TimestampUtc.ToLocalTime():HH:mm:ss}] [{Severity.ToString().ToUpperInvariant()}] {Source}: {Message} (×{RepeatCount})"
            : $"[{TimestampUtc.ToLocalTime():HH:mm:ss}] [{Severity.ToString().ToUpperInvariant()}] {Source}: {Message}";
}

public interface ITransportFactory
{
    ITransport CreateSerial(string portName, int baudRate);
    ITransport CreateUdp(int listenPort, string? remoteHost = null, int remotePort = 0);
    ITransport CreateTcp(string host, int port);
}

public sealed class DefaultTransportFactory : ITransportFactory
{
    public ITransport CreateSerial(string portName, int baudRate) => new SerialTransport(portName, baudRate);
    public ITransport CreateUdp(int listenPort, string? remoteHost = null, int remotePort = 0) => new UdpTransport(listenPort, remoteHost, remotePort);
    public ITransport CreateTcp(string host, int port) => new TcpTransport(host, port);
}

public interface IUiDispatcher
{
    void InvokeOrBeginInvoke(Action action);
}

public sealed class InlineUiDispatcher : IUiDispatcher
{
    public void InvokeOrBeginInvoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}

public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void InvokeOrBeginInvoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.DataBind, action);
        }
    }
}

public sealed class MainViewModelOptions
{
    public string? TelemetryLogDirectory { get; init; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Tulpar",
        "Logs");

    public bool EnableUdpForwarder { get; init; } = true;
    public string UdpForwarderHost { get; init; } = "127.0.0.1";
    public int UdpForwarderPort { get; init; } = 14551;
    public TimeSpan HeartbeatCheckInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan HudThrottleInterval { get; init; } = TimeSpan.FromMilliseconds(16);
    public TimeSpan PanelThrottleInterval { get; init; } = TimeSpan.FromMilliseconds(33);
    public TimeProvider? TimeProvider { get; init; }
    public Func<IReadOnlyList<string>>? SerialPortEnumerator { get; init; }
    public Func<IReadOnlyList<string>>? VideoDeviceEnumerator { get; init; }
}
