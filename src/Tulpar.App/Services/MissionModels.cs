using System.ComponentModel;
using System.Runtime.CompilerServices;
using Tulpar.Core.Mavlink;
using Tulpar.Core.Missions;

namespace Tulpar.App.Services;

public sealed class EditableMissionItem : INotifyPropertyChanged
{
    public static IReadOnlyList<string> ReferenceFrameOptions { get; } = ["REL", "AMSL"];

    private int _seq;
    private MavCmd _command;
    private MavFrame _frame;
    private double _latitude;
    private double _longitude;
    private float _altitude;
    private float _param1;
    private float _param2;
    private float _param3;
    private float _param4;
    private bool _autocontinue = true;

    public EditableMissionItem()
        : this(0, MavFrame.GlobalRelativeAltInt, MavCmd.NavWaypoint, 0f, 0f, 0f, 0f, 0.0, 0.0, 50f, true)
    {
    }

    public EditableMissionItem(
        int seq,
        MavFrame frame,
        MavCmd command,
        float param1,
        float param2,
        float param3,
        float param4,
        double latitude,
        double longitude,
        float altitude,
        bool autocontinue = true)
    {
        _seq = seq;
        _frame = NormalizeFrame(frame);
        _command = command;
        _param1 = param1;
        _param2 = param2;
        _param3 = param3;
        _param4 = param4;
        _latitude = latitude;
        _longitude = longitude;
        _altitude = altitude;
        _autocontinue = autocontinue;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Seq
    {
        get => _seq;
        set => SetField(ref _seq, value);
    }

    public MavCmd Command
    {
        get => _command;
        set => SetField(ref _command, value);
    }

    public MavFrame Frame
    {
        get => _frame;
        set
        {
            MavFrame normalized = NormalizeFrame(value);
            if (SetField(ref _frame, normalized))
            {
                OnPropertyChanged(nameof(ReferenceFrameLabel));
            }
        }
    }

    public string ReferenceFrameLabel
    {
        get => _frame is MavFrame.GlobalInt or MavFrame.Global ? "AMSL" : "REL";
        set
        {
            MavFrame target = string.Equals(value?.Trim(), "AMSL", StringComparison.OrdinalIgnoreCase)
                ? MavFrame.GlobalInt
                : MavFrame.GlobalRelativeAltInt;
            Frame = target;
        }
    }

    public double Latitude
    {
        get => _latitude;
        set => SetField(ref _latitude, value);
    }

    public double Longitude
    {
        get => _longitude;
        set => SetField(ref _longitude, value);
    }

    public float Altitude
    {
        get => _altitude;
        set => SetField(ref _altitude, value);
    }

    public float Param1
    {
        get => _param1;
        set => SetField(ref _param1, value);
    }

    public float Param2
    {
        get => _param2;
        set => SetField(ref _param2, value);
    }

    public float Param3
    {
        get => _param3;
        set => SetField(ref _param3, value);
    }

    public float Param4
    {
        get => _param4;
        set => SetField(ref _param4, value);
    }

    public bool Autocontinue
    {
        get => _autocontinue;
        set => SetField(ref _autocontinue, value);
    }

    public MissionItem ToMissionItem() =>
        new(Seq, Frame, Command, Param1, Param2, Param3, Param4, Latitude, Longitude, Altitude, Autocontinue);

    public static EditableMissionItem FromMissionItem(MissionItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new EditableMissionItem(
            item.Seq,
            item.Frame,
            item.Command,
            item.Param1,
            item.Param2,
            item.Param3,
            item.Param4,
            item.Latitude,
            item.Longitude,
            item.Altitude,
            item.Autocontinue);
    }

    private static MavFrame NormalizeFrame(MavFrame frame) =>
        frame switch
        {
            MavFrame.Global => MavFrame.GlobalInt,
            MavFrame.GlobalRelativeAlt => MavFrame.GlobalRelativeAltInt,
            _ => frame
        };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class EditableFencePoint : INotifyPropertyChanged
{
    private int _index;
    private double _latitude;
    private double _longitude;

    public EditableFencePoint(int index, double latitude, double longitude)
    {
        _index = index;
        _latitude = latitude;
        _longitude = longitude;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index
    {
        get => _index;
        set => SetField(ref _index, value);
    }

    public double Latitude
    {
        get => _latitude;
        set => SetField(ref _latitude, value);
    }

    public double Longitude
    {
        get => _longitude;
        set => SetField(ref _longitude, value);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
