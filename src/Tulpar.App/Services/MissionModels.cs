using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Tulpar.Core.Mavlink;
using Tulpar.Core.Missions;
using Tulpar.Core.Session;

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
    private double _legDistanceMeters;
    private double? _legBearingDeg;
    private double _cumulativeDistanceMeters;

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
        set
        {
            if (SetField(ref _command, value))
            {
                NotifyCommandMetadataChanged();
            }
        }
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
                OnPropertyChanged(nameof(AltitudeDisplay));
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
        set
        {
            if (SetField(ref _latitude, value))
            {
                OnPropertyChanged(nameof(CoordsShortDisplay));
            }
        }
    }

    public double Longitude
    {
        get => _longitude;
        set
        {
            if (SetField(ref _longitude, value))
            {
                OnPropertyChanged(nameof(CoordsShortDisplay));
            }
        }
    }

    public float Altitude
    {
        get => _altitude;
        set
        {
            if (SetField(ref _altitude, value))
            {
                OnPropertyChanged(nameof(AltitudeDisplay));
            }
        }
    }

    public float Param1
    {
        get => _param1;
        set
        {
            if (SetField(ref _param1, value))
            {
                OnPropertyChanged(nameof(ParamSummaryDisplay));
            }
        }
    }

    public float Param2
    {
        get => _param2;
        set
        {
            if (SetField(ref _param2, value))
            {
                OnPropertyChanged(nameof(ParamSummaryDisplay));
            }
        }
    }

    public float Param3
    {
        get => _param3;
        set
        {
            if (SetField(ref _param3, value))
            {
                OnPropertyChanged(nameof(ParamSummaryDisplay));
            }
        }
    }

    public float Param4
    {
        get => _param4;
        set
        {
            if (SetField(ref _param4, value))
            {
                OnPropertyChanged(nameof(ParamSummaryDisplay));
            }
        }
    }

    public bool Autocontinue
    {
        get => _autocontinue;
        set => SetField(ref _autocontinue, value);
    }

    public double LegDistanceMeters
    {
        get => _legDistanceMeters;
        set
        {
            if (SetField(ref _legDistanceMeters, value))
            {
                OnPropertyChanged(nameof(LegDistanceDisplay));
                OnPropertyChanged(nameof(HasLegDistance));
            }
        }
    }

    public double? LegBearingDeg
    {
        get => _legBearingDeg;
        set
        {
            if (SetField(ref _legBearingDeg, value))
            {
                OnPropertyChanged(nameof(LegBearingDisplay));
            }
        }
    }

    public double CumulativeDistanceMeters
    {
        get => _cumulativeDistanceMeters;
        set => SetField(ref _cumulativeDistanceMeters, value);
    }

    public string LegDistanceDisplay =>
        _legDistanceMeters >= 1000.0
            ? string.Create(CultureInfo.InvariantCulture, $"{_legDistanceMeters / 1000.0:0.00} KM")
            : _legDistanceMeters >= 1.0
                ? string.Create(CultureInfo.InvariantCulture, $"{_legDistanceMeters:0} M")
                : string.Empty;

    public bool HasLegDistance => _legDistanceMeters >= 1.0;

    public string LegBearingDisplay =>
        _legBearingDeg is { } deg
            ? string.Create(CultureInfo.InvariantCulture, $"{deg:0}°")
            : string.Empty;

    public string CommandShortLabel => _command switch
    {
        MavCmd.NavWaypoint => "WAYPOINT",
        MavCmd.NavTakeoff or MavCmd.NavTakeoffLocal => "TAKEOFF",
        MavCmd.NavVtolTakeoff => "VTOL T/O",
        MavCmd.NavLand or MavCmd.NavLandLocal => "İNİŞ (LAND)",
        MavCmd.NavVtolLand => "VTOL LAND",
        MavCmd.NavReturnToLaunch => "EVE DÖN (RTL)",
        MavCmd.NavLoiterTurns => "LOITER TUR",
        MavCmd.NavLoiterTime => "LOITER SÜRE",
        MavCmd.NavLoiterUnlim => "LOITER ∞",
        MavCmd.NavLoiterToAlt => "LOITER İRTİFA",
        MavCmd.NavSplineWaypoint => "SPLINE WP",
        MavCmd.NavDelay => "GECİKME",
        MavCmd.NavContinueAndChangeAlt => "İRTİFA DEĞİŞ",
        MavCmd.DoChangeSpeed => "HIZ AYARLA",
        MavCmd.DoSetRoi or MavCmd.NavRoi => "HEDEF (ROI)",
        MavCmd.DoJump => "ADIMA ATLA",
        MavCmd.DoSetHome => "HOME AYARLA",
        MavCmd.DoSetServo => "SERVO",
        MavCmd.DoSetRelay => "RÖLE",
        _ => _command.ToString().ToUpperInvariant()
    };

    public string CommandBadgeGlyph => _command switch
    {
        MavCmd.NavTakeoff or MavCmd.NavTakeoffLocal or MavCmd.NavVtolTakeoff => "↑",
        MavCmd.NavLand or MavCmd.NavLandLocal or MavCmd.NavVtolLand => "↓",
        MavCmd.NavReturnToLaunch => "⌂",
        MavCmd.NavLoiterTurns or MavCmd.NavLoiterUnlim or MavCmd.NavLoiterToAlt => "↻",
        MavCmd.NavLoiterTime or MavCmd.NavDelay => "⏱",
        MavCmd.NavSplineWaypoint => "〰",
        MavCmd.DoChangeSpeed => "⚡",
        MavCmd.DoSetRoi or MavCmd.NavRoi => "⌖",
        MavCmd.DoJump => "↪",
        _ => "◎"
    };

    public string CommandAccentHex => _command switch
    {
        MavCmd.NavTakeoff or MavCmd.NavTakeoffLocal or MavCmd.NavVtolTakeoff => "#34C759",
        MavCmd.NavLand or MavCmd.NavLandLocal or MavCmd.NavVtolLand or MavCmd.NavReturnToLaunch => "#FFCC00",
        MavCmd.NavLoiterTurns or MavCmd.NavLoiterTime or MavCmd.NavLoiterUnlim or MavCmd.NavLoiterToAlt => "#38BDF8",
        MavCmd.DoChangeSpeed or MavCmd.DoSetRoi or MavCmd.NavRoi or MavCmd.DoJump or MavCmd.DoSetHome or MavCmd.DoSetServo or MavCmd.DoSetRelay or MavCmd.NavDelay => "#A78BFA",
        _ => "#FFFFFF"
    };

    public string AltitudeDisplay => _command switch
    {
        MavCmd.DoChangeSpeed when _param2 > 0f => $"{_param2:0.#} M/S",
        MavCmd.DoJump => $"ADIM #{_param1:0}",
        MavCmd.NavDelay => $"{_param1:0.#} SN",
        _ => $"{_altitude:0.#} M {ReferenceFrameLabel}"
    };

    public string CoordsShortDisplay =>
        _latitude == 0.0 && _longitude == 0.0
            ? "KOORDİNAT YOK"
            : $"{_latitude.ToString("0.00000", CultureInfo.InvariantCulture)}, {_longitude.ToString("0.00000", CultureInfo.InvariantCulture)}";

    public string ParamSummaryDisplay => _command switch
    {
        MavCmd.NavWaypoint => _param1 > 0f
            ? $"BEKLE: {_param1:0.#} SN"
            : _param2 > 0f ? $"YARIÇAP: {_param2:0.#} M" : "DİREKT GEÇİŞ",
        MavCmd.NavLoiterTurns => $"TUR: {_param1:0.#}" + (_param3 != 0f ? $" · R: {_param3:0.#}M" : string.Empty),
        MavCmd.NavLoiterTime => $"SÜRE: {_param1:0.#} SN" + (_param3 != 0f ? $" · R: {_param3:0.#}M" : string.Empty),
        MavCmd.NavLoiterUnlim or MavCmd.NavLoiterToAlt => _param3 != 0f ? $"YARIÇAP: {_param3:0.#} M" : "SINIRSIZ TUR",
        MavCmd.NavTakeoff or MavCmd.NavVtolTakeoff => _param1 > 0f ? $"PİTCH: {_param1:0.#}°" : "KALKIŞ TIRMANIŞ",
        MavCmd.DoChangeSpeed => $"TİP: {_param1:0} · HIZ: {_param2:0.#} M/S",
        MavCmd.DoJump => $"TEKRAR: {_param2:0}x",
        MavCmd.NavDelay => "BEKLEME KOMUTU",
        _ => "STANDART NOKTA"
    };

    public string Param1Label => _command switch
    {
        MavCmd.NavWaypoint or MavCmd.NavSplineWaypoint => "BEKLEME (SN)",
        MavCmd.NavLoiterTurns => "TUR SAYISI",
        MavCmd.NavLoiterTime or MavCmd.NavDelay => "SÜRE (SN)",
        MavCmd.NavTakeoff or MavCmd.NavVtolTakeoff => "MİN PİTCH (°)",
        MavCmd.DoChangeSpeed => "HIZ TİPİ (0/1)",
        MavCmd.DoJump => "HEDEF ADIM (#)",
        _ => "PARAM 1"
    };

    public string Param2Label => _command switch
    {
        MavCmd.NavWaypoint => "KABUL YARIÇAPI (M)",
        MavCmd.DoChangeSpeed => "HEDEF HIZ (M/S)",
        MavCmd.DoJump => "TEKRAR SAYISI",
        _ => "PARAM 2"
    };

    public string Param3Label => _command switch
    {
        MavCmd.NavWaypoint => "GEÇİŞ MESAFESİ (M)",
        MavCmd.NavLoiterTurns or MavCmd.NavLoiterTime or MavCmd.NavLoiterUnlim or MavCmd.NavLoiterToAlt => "YÖNÜNCE YARIÇAP (M)",
        MavCmd.DoChangeSpeed => "GAZ SINIRI (%)",
        _ => "PARAM 3"
    };

    public string Param4Label => _command switch
    {
        MavCmd.NavWaypoint or MavCmd.NavTakeoff or MavCmd.NavLand or MavCmd.NavLoiterTurns or MavCmd.NavLoiterTime => "YÖNELİM / YAW (°)",
        _ => "PARAM 4"
    };

    private void NotifyCommandMetadataChanged()
    {
        OnPropertyChanged(nameof(CommandShortLabel));
        OnPropertyChanged(nameof(CommandBadgeGlyph));
        OnPropertyChanged(nameof(CommandAccentHex));
        OnPropertyChanged(nameof(AltitudeDisplay));
        OnPropertyChanged(nameof(ParamSummaryDisplay));
        OnPropertyChanged(nameof(Param1Label));
        OnPropertyChanged(nameof(Param2Label));
        OnPropertyChanged(nameof(Param3Label));
        OnPropertyChanged(nameof(Param4Label));
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

public sealed class EditableRallyPoint : INotifyPropertyChanged
{
    private int _index;
    private double _latitude;
    private double _longitude;
    private float _altitudeMeters;
    private float _breakAltitudeMeters;
    private ushort _landDirectionDeg;
    private byte _flags;

    public EditableRallyPoint(
        int index,
        double latitude,
        double longitude,
        float altitudeMeters = 50f,
        float breakAltitudeMeters = 0f,
        ushort landDirectionDeg = 0,
        byte flags = 0)
    {
        _index = index;
        _latitude = latitude;
        _longitude = longitude;
        _altitudeMeters = altitudeMeters;
        _breakAltitudeMeters = breakAltitudeMeters;
        _landDirectionDeg = landDirectionDeg;
        _flags = flags;
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

    public float AltitudeMeters
    {
        get => _altitudeMeters;
        set => SetField(ref _altitudeMeters, value);
    }

    public float BreakAltitudeMeters
    {
        get => _breakAltitudeMeters;
        set => SetField(ref _breakAltitudeMeters, value);
    }

    public ushort LandDirectionDeg
    {
        get => _landDirectionDeg;
        set => SetField(ref _landDirectionDeg, value);
    }

    public byte Flags
    {
        get => _flags;
        set => SetField(ref _flags, value);
    }

    public MissionItem ToMissionItem(int seq) =>
        new(
            seq,
            MavFrame.GlobalRelativeAltInt,
            MavCmd.NavRallyPoint,
            BreakAltitudeMeters,
            LandDirectionDeg,
            Flags,
            0f,
            Latitude,
            Longitude,
            AltitudeMeters,
            false);

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

public sealed class VehicleParameterRow : INotifyPropertyChanged
{
    private string _displayValue;
    private string _editValue;
    private MavParamType _paramType;
    private ushort _index;
    private ushort _count;
    private float _numericValue;
    private bool _isDirty;

    public VehicleParameterRow(VehicleParameter parameter)
        : this(
            parameter?.Name ?? throw new ArgumentNullException(nameof(parameter)),
            parameter.Value,
            parameter.Type,
            parameter.Index,
            parameter.TotalCount)
    {
    }

    public VehicleParameterRow(
        string name,
        float value,
        MavParamType paramType,
        ushort index,
        ushort count)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _numericValue = value;
        _paramType = paramType;
        _index = index;
        _count = count;
        _displayValue = FormatParamValue(value, paramType);
        _editValue = _displayValue;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    public float NumericValue
    {
        get => _numericValue;
        private set => SetField(ref _numericValue, value);
    }

    public string DisplayValue
    {
        get => _displayValue;
        private set => SetField(ref _displayValue, value);
    }

    public string EditValue
    {
        get => _editValue;
        set
        {
            string normalized = value ?? string.Empty;
            if (SetField(ref _editValue, normalized))
            {
                IsDirty = !string.Equals(normalized.Trim(), _displayValue, StringComparison.Ordinal);
            }
        }
    }

    public MavParamType ParamType
    {
        get => _paramType;
        private set => SetField(ref _paramType, value);
    }

    public ushort Index
    {
        get => _index;
        private set => SetField(ref _index, value);
    }

    public ushort Count
    {
        get => _count;
        private set => SetField(ref _count, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetField(ref _isDirty, value);
    }

    public void UpdateFrom(VehicleParameter parameter, bool markApplied = false)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        NumericValue = parameter.Value;
        ParamType = parameter.Type;
        Index = parameter.Index;
        Count = parameter.TotalCount;
        string formatted = FormatParamValue(parameter.Value, parameter.Type);
        DisplayValue = formatted;

        if (markApplied || !IsDirty)
        {
            if (SetField(ref _editValue, formatted, nameof(EditValue)))
            {
                // EditValue notified
            }
            IsDirty = false;
        }
        else
        {
            IsDirty = !string.Equals(_editValue.Trim(), formatted, StringComparison.Ordinal);
        }
    }

    public static string FormatParamValue(float value, MavParamType paramType)
    {
        if (!float.IsFinite(value))
        {
            return "0";
        }

        if (paramType is MavParamType.Uint8 or MavParamType.Int8 or MavParamType.Uint16 or MavParamType.Int16
            or MavParamType.Uint32 or MavParamType.Int32 or MavParamType.Uint64 or MavParamType.Int64)
        {
            float rounded = MathF.Round(value);
            if (MathF.Abs(value - rounded) < 1e-5f && MathF.Abs(rounded) <= long.MaxValue / 2f)
            {
                return ((long)rounded).ToString(CultureInfo.InvariantCulture);
            }
        }

        if (MathF.Abs(value) >= 1e7f || (value != 0f && MathF.Abs(value) < 1e-4f))
        {
            return value.ToString("G7", CultureInfo.InvariantCulture);
        }

        return value.ToString("0.######", CultureInfo.InvariantCulture);
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

public sealed record PreFlightCheckItem(
    string Title,
    string Detail,
    bool IsPassed,
    string SeverityLabel);

public enum SystemHealthState
{
    Ok,
    Warning,
    Critical,
    Unknown
}

public sealed record SystemHealthItem(
    string Key,
    string Title,
    string StatusText,
    string Detail,
    SystemHealthState State);
