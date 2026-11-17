using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Tulpar.App.Services;

public enum VideoSourceKind
{
    UdpRtp,
    Rtsp,
    UsbDirectShow
}

/// <summary>
/// Shared media resource locator (MRL) and LibVLC low-latency options builder.
/// </summary>
public static class VideoMrlBuilder
{
    public const string DefaultUdpMrl = "udp://@:5600";
    public const string DefaultRtspMrl = "rtsp://127.0.0.1:8554/stream";
    public const string DefaultDirectShowMrl = "dshow://";

    public static string BuildEffectiveMrl(VideoSourceKind kind, string? input)
    {
        string trimmed = input?.Trim() ?? string.Empty;
        return kind switch
        {
            VideoSourceKind.UdpRtp => string.IsNullOrWhiteSpace(trimmed) ? DefaultUdpMrl : trimmed,
            VideoSourceKind.Rtsp => string.IsNullOrWhiteSpace(trimmed) ? DefaultRtspMrl : trimmed,
            VideoSourceKind.UsbDirectShow => DefaultDirectShowMrl,
            _ => string.IsNullOrWhiteSpace(trimmed) ? DefaultUdpMrl : trimmed
        };
    }

    public static IReadOnlyList<string> BuildMediaOptions(VideoSourceKind kind, string? input)
    {
        var options = new List<string>
        {
            ":clock-jitter=0",
            ":clock-synchro=0"
        };

        switch (kind)
        {
            case VideoSourceKind.UdpRtp:
                options.Add(":network-caching=80");
                options.Add(":live-caching=80");
                break;
            case VideoSourceKind.Rtsp:
                options.Add(":network-caching=120");
                options.Add(":live-caching=80");
                break;
            case VideoSourceKind.UsbDirectShow:
                options.Add(":live-caching=50");
                options.Add(":dshow-adev=none");
                string device = NormalizeDirectShowDevice(input);
                if (!string.IsNullOrWhiteSpace(device))
                {
                    options.Add($":dshow-vdev={device}");
                }
                break;
        }

        return options;
    }

    private static string NormalizeDirectShowDevice(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        string trimmed = input.Trim();
        if (trimmed.Equals(DefaultDirectShowMrl, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        const string vdevPrefix = "video=";
        if (trimmed.StartsWith(vdevPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[vdevPrefix.Length..].Trim();
        }

        const string optPrefix = ":dshow-vdev=";
        if (trimmed.StartsWith(optPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[optPrefix.Length..].Trim();
        }

        return trimmed;
    }
}

/// <summary>
/// Safe Windows DirectShow video input device enumerator falling back to empty list in headless environments.
/// </summary>
internal static class DirectShowDeviceEnumerator
{
    private static readonly Guid ClsidSystemDeviceEnum = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
    private static readonly Guid ClsidVideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");
    private static readonly Guid IidIPropertyBag = new("55272A00-42CB-11CE-8135-00AA004BB851");

    public static IReadOnlyList<string> GetVideoCaptureDevices()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<string>();
        }

        var devices = new List<string>();
        ICreateDevEnum? devEnum = null;
        IEnumMoniker? enumMoniker = null;

        try
        {
            Type? comType = Type.GetTypeFromCLSID(ClsidSystemDeviceEnum, throwOnError: false);
            if (comType is null)
            {
                return Array.Empty<string>();
            }

            devEnum = Activator.CreateInstance(comType) as ICreateDevEnum;
            if (devEnum is null)
            {
                return Array.Empty<string>();
            }

            Guid cat = ClsidVideoInputDeviceCategory;
            int hr = devEnum.CreateClassEnumerator(ref cat, out enumMoniker, 0);
            if (hr != 0 || enumMoniker is null)
            {
                return Array.Empty<string>();
            }

            var monikers = new IMoniker[1];
            while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0 && monikers[0] is not null)
            {
                IMoniker moniker = monikers[0];
                try
                {
                    Guid bagId = IidIPropertyBag;
                    moniker.BindToStorage(null!, null!, ref bagId, out object bagObj);
                    if (bagObj is IPropertyBag bag)
                    {
                        try
                        {
                            if (bag.Read("FriendlyName", out object? nameObj, IntPtr.Zero) == 0 &&
                                nameObj is string name &&
                                !string.IsNullOrWhiteSpace(name))
                            {
                                devices.Add(name.Trim());
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(bag);
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    Marshal.ReleaseComObject(moniker);
                    monikers[0] = null!;
                }
            }
        }
        catch
        {
            return Array.Empty<string>();
        }
        finally
        {
            if (enumMoniker is not null)
            {
                Marshal.ReleaseComObject(enumMoniker);
            }

            if (devEnum is not null)
            {
                Marshal.ReleaseComObject(devEnum);
            }
        }

        return devices.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator([In] ref Guid pType, [Out] out IEnumMoniker? ppEnumMoniker, [In] int dwFlags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig]
        int Read([In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName, [Out] out object? pVar, [In] IntPtr pErrorLog);

        [PreserveSig]
        int Write([In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName, [In] ref object pVar);
    }
}
