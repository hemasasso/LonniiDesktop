using System.Runtime.InteropServices;

namespace Lonnii.Client.Features.CustomerDisplay;

/// <summary>
/// Lists the monitors Windows currently knows about, in physical pixels. Done through Win32
/// rather than WinForms' <c>Screen</c> so the client does not have to pull in a second UI
/// framework (and its clashing type names) for one call. A laptop's second screen, an HDMI
/// panel and a USB-C one all show up here the same way.
/// </summary>
public static class MonitorEnumerator
{
    public static IReadOnlyList<MonitorInfo> GetAll()
    {
        var monitors = new List<MonitorInfo>();

        bool Callback(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data)
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var area = info.Monitor;
                monitors.Add(new MonitorInfo(
                    info.DeviceName, area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top,
                    IsPrimary: (info.Flags & MonitorInfoPrimary) != 0));
            }
            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        return monitors.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.DeviceName).ToList();
    }

    /// <summary>The monitor to use: the one the user named, or - when auto-detecting - the first
    /// one that is not the main screen. Null when there is nothing to use.</summary>
    public static MonitorInfo? Pick(IReadOnlyList<MonitorInfo> monitors, bool auto, string? device) =>
        auto
            ? monitors.FirstOrDefault(m => !m.IsPrimary)
            : monitors.FirstOrDefault(m => m.DeviceName == device);

    private const int MonitorInfoPrimary = 1;

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public int Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
}
