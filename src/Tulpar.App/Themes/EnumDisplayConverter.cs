using System.Globalization;
using System.Text;
using System.Windows.Data;
using Tulpar.App.Services;
using Tulpar.Core.Mavlink;

namespace Tulpar.App.Themes;

/// <summary>
/// Converts Tulpar enum values (<see cref="ConnectionKind"/>, <see cref="VideoSourceKind"/>, <see cref="MavCmd"/>)
/// into uppercase aerospace display labels matching DESIGN-spacex.md typography.
/// </summary>
public sealed class EnumDisplayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        FormatEnum(value);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    public static string FormatEnum(object? value) =>
        value switch
        {
            ConnectionKind.Udp => "UDP SITL",
            ConnectionKind.Serial => "USB / COM",
            ConnectionKind.Tcp => "TCP SITL",
            VideoSourceKind.UdpRtp => "UDP / RTP",
            VideoSourceKind.Rtsp => "RTSP",
            VideoSourceKind.UsbDirectShow => "USB KAMERA",
            MavCmd.NavWaypoint => "NAV WAYPOINT",
            MavCmd.NavSplineWaypoint => "SPLINE WP",
            MavCmd.NavLoiterUnlim => "LOITER UNLIM",
            MavCmd.NavLoiterTurns => "LOITER TURNS",
            MavCmd.NavLoiterTime => "LOITER TIME",
            MavCmd.NavLoiterToAlt => "LOITER TO ALT",
            MavCmd.NavTakeoff => "NAV TAKEOFF",
            MavCmd.NavLand => "NAV LAND",
            MavCmd.NavReturnToLaunch => "NAV RTL",
            MavCmd.NavVtolTakeoff => "VTOL TAKEOFF",
            MavCmd.NavVtolLand => "VTOL LAND",
            MavCmd.DoVtolTransition => "VTOL TRANSITION",
            MavCmd.NavContinueAndChangeAlt => "CHANGE ALT",
            MavCmd.NavDelay => "NAV DELAY",
            MavCmd.NavRoi => "NAV ROI",
            MavCmd.DoJump => "DO JUMP",
            MavCmd.DoChangeSpeed => "CHANGE SPEED",
            MavCmd.DoSetServo => "DO SET SERVO",
            MavCmd.DoSetRelay => "DO SET RELAY",
            MavCmd.DoSetRoi => "SET ROI",
            MavCmd.DoSetReverse => "SET REVERSE",
            MavCmd.DoSetCamTriggDist => "CAM TRIGG DIST",
            Enum enumValue => SplitPascalToUpper(enumValue.ToString()),
            string s => s.ToUpperInvariant(),
            _ => value?.ToString()?.ToUpperInvariant() ?? string.Empty
        };

    private static string SplitPascalToUpper(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(raw.Length + 6);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(raw[i - 1]))
            {
                sb.Append(' ');
            }
            sb.Append(char.ToUpperInvariant(c));
        }

        return sb.ToString();
    }
}
