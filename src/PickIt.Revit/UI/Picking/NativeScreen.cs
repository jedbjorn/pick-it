using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PickIt.Revit.UI.Picking;

/// <summary>
/// The user32/gdi32/dwmapi calls behind the eyedropper. Every coordinate here is
/// in physical pixels: callers wrap screen reads in <see cref="PerMonitorDpi"/>, so
/// monitor rectangles, the capture, the pointer and window positions share one space
/// even on mixed-DPI layouts and with monitors left of or above the primary.
/// </summary>
internal static class NativeScreen
{
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);
    private static readonly IntPtr PerMonitorAware = new(-3);
    private static readonly IntPtr HwndTopmost = new(-1);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint MonitorDefaultToNearest = 2;
    private const int SrcCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;
    private const int HorzRes = 8;
    private const int DesktopHorzRes = 118;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpDoNotRound = 1;
    private const uint QdcOnlyActivePaths = 2;
    private const int DisplayConfigGetSourceName = 1;
    private const int DisplayConfigGetAdvancedColorInfo = 9;
    private const int PathInfoSize = 72;
    private const int ModeInfoSize = 64;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>
    /// Runs the calling thread per-monitor-DPI-aware until disposed (v2, or v1 where
    /// Windows refuses v2), so screen coordinates are physical pixels and windows created
    /// meanwhile get their own monitor's DPI. Where Windows doesn't support it (before
    /// 10 1607) or refuses both, the scope does nothing and <see cref="DpiScope.Active"/>
    /// is false: coordinates may then be virtualised, which the picker reports.
    /// </summary>
    public static DpiScope PerMonitorDpi() => new();

    public sealed class DpiScope : IDisposable
    {
        private IntPtr _previous;

        public DpiScope()
        {
            Level = "off";
            try
            {
                // Returns the context it replaced; zero means the call failed and nothing changed.
                _previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
                if (_previous != IntPtr.Zero)
                {
                    Level = "v2";
                }
                else
                {
                    _previous = SetThreadDpiAwarenessContext(PerMonitorAware);
                    if (_previous != IntPtr.Zero) Level = "v1";
                }
            }
            catch (EntryPointNotFoundException)
            {
                _previous = IntPtr.Zero;
            }
            Active = _previous != IntPtr.Zero;
        }

        /// <summary>True when the thread really was switched (or already was) per-monitor aware.</summary>
        public bool Active { get; }

        /// <summary><c>v2</c>, <c>v1</c> or <c>off</c>, for the diagnostic line.</summary>
        public string Level { get; }

        public void Dispose()
        {
            if (_previous == IntPtr.Zero) return;
            try
            {
                SetThreadDpiAwarenessContext(_previous);
            }
            catch (EntryPointNotFoundException)
            {
                // Not reachable: the constructor's call succeeded.
            }
            _previous = IntPtr.Zero;
        }
    }

    /// <summary>
    /// What Windows says about the calling thread's view of the screen, for the diagnostic
    /// line; call it inside <see cref="PerMonitorDpi"/>. Never throws: what can't be read is
    /// "unknown" or 0.
    /// </summary>
    public static ScreenDiagnostics Diagnostics()
    {
        var awareness = "unknown";
        var systemDpi = 0;
        var horizontal = 0;
        var desktopHorizontal = 0;
        try
        {
            var context = GetThreadDpiAwarenessContext();
            awareness = GetAwarenessFromDpiAwarenessContext(context) switch
            {
                0 => "unaware",
                1 => "system",
                2 => AreDpiAwarenessContextsEqual(context, PerMonitorAwareV2) ? "permonitor-v2" : "permonitor",
                _ => "invalid",
            };
            systemDpi = (int)GetDpiForSystem();
        }
        catch (Exception)
        {
            // Windows before 10 1607: stays "unknown".
        }
        try
        {
            var screen = GetDC(IntPtr.Zero);
            if (screen != IntPtr.Zero)
            {
                try
                {
                    horizontal = GetDeviceCaps(screen, HorzRes);
                    desktopHorizontal = GetDeviceCaps(screen, DesktopHorzRes);
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, screen);
                }
            }
        }
        catch (Exception)
        {
            // Stays 0: not counted as virtualised.
        }
        return new ScreenDiagnostics(awareness, systemDpi, horizontal, desktopHorizontal);
    }

    /// <summary>
    /// Every monitor's full rectangle (not just its work area) and GDI device name, in
    /// enumeration order. Monitors Windows reports with an empty rectangle are left out.
    /// </summary>
    public static List<ScreenMonitor> Monitors()
    {
        var monitors = new List<ScreenMonitor>();
        MonitorEnumProc proc = (IntPtr monitor, IntPtr _, ref NativeRect rect, IntPtr _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>(), Device = "" };
            var known = GetMonitorInfoW(monitor, ref info);
            var r = known ? info.Monitor : rect;
            var bounds = PixelRect.FromEdges(r.Left, r.Top, r.Right, r.Bottom);
            if (!bounds.IsEmpty) monitors.Add(new ScreenMonitor(bounds, known ? info.Device ?? "" : ""));
            return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows did not list the monitors.");
        GC.KeepAlive(proc);
        return monitors;
    }

    /// <summary>
    /// Copies each of <paramref name="areas"/> (one per monitor) of the screen as it is now,
    /// into a capture of its own, in the same order. The bitmaps are all made first, so the
    /// copies themselves follow each other as closely as they can.
    /// </summary>
    public static List<ScreenSnapshot> Capture(IReadOnlyList<PixelRect> areas)
    {
        foreach (var area in areas)
        {
            if (area.IsEmpty || (long)area.Width * 4 * area.Height > int.MaxValue)
                throw new InvalidOperationException($"The screen area {area.Width} × {area.Height} can't be captured.");
        }

        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new Win32Exception("Windows did not give access to the screen.");
        var surfaces = new List<Surface>(areas.Count);
        try
        {
            foreach (var area in areas)
            {
                var surface = new Surface { Area = area };
                surfaces.Add(surface); // before anything is allocated, so the finally frees it
                surface.Memory = CreateCompatibleDC(screen);
                if (surface.Memory == IntPtr.Zero) throw new Win32Exception("CreateCompatibleDC failed.");

                var header = new BitmapInfoHeader
                {
                    Size = Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = area.Width,
                    Height = -area.Height, // top-down rows
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0, // BI_RGB
                };
                surface.Bitmap = CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
                if (surface.Bitmap == IntPtr.Zero || bits == IntPtr.Zero) throw new Win32Exception("CreateDIBSection failed.");
                surface.Bits = bits;
                surface.Old = SelectObject(surface.Memory, surface.Bitmap);
            }

            foreach (var surface in surfaces)
            {
                var area = surface.Area;
                if (!BitBlt(surface.Memory, 0, 0, area.Width, area.Height, screen, area.Left, area.Top, SrcCopy | CaptureBlt))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows refused to copy the screen.");
            }
            GdiFlush();

            var snapshots = new List<ScreenSnapshot>(surfaces.Count);
            foreach (var surface in surfaces)
            {
                var stride = surface.Area.Width * 4;
                var pixels = new byte[stride * surface.Area.Height];
                Marshal.Copy(surface.Bits, pixels, 0, pixels.Length);
                snapshots.Add(new ScreenSnapshot(surface.Area, pixels, stride));
            }
            return snapshots;
        }
        finally
        {
            foreach (var surface in surfaces)
            {
                if (surface.Old != IntPtr.Zero) SelectObject(surface.Memory, surface.Old);
                if (surface.Bitmap != IntPtr.Zero) DeleteObject(surface.Bitmap);
                if (surface.Memory != IntPtr.Zero) DeleteDC(surface.Memory);
            }
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>One monitor's memory bitmap while <see cref="Capture"/> runs.</summary>
    private sealed class Surface
    {
        public PixelRect Area;
        public IntPtr Memory;
        public IntPtr Bitmap;
        public IntPtr Bits;
        public IntPtr Old;
    }

    /// <summary>
    /// Whether advanced colour (HDR, or wide colour gamut) is switched on, per GDI device
    /// name as in <see cref="ScreenMonitor.Device"/>. A screen copy of such a monitor is
    /// tone-mapped by Windows, so picked colours can be off. Never throws: a monitor that
    /// isn't in the result is "unknown", and any failure gives an empty result.
    /// </summary>
    public static Dictionary<string, bool> AdvancedColor()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            byte[] paths;
            uint pathCount;
            for (var attempt = 0; ; attempt++)
            {
                if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out pathCount, out var modeCount) != 0) return result;
                if (pathCount == 0 || pathCount > 1024 || modeCount > 4096) return result;
                paths = new byte[pathCount * PathInfoSize];
                var modes = new byte[Math.Max(1, modeCount) * ModeInfoSize];
                var status = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (status == 0) break;
                // The display set changed between the two calls: once more, then give up.
                if (status != ErrorInsufficientBuffer || attempt >= 2) return result;
            }

            for (var i = 0; i < pathCount && (i + 1) * PathInfoSize <= paths.Length; i++)
            {
                var path = i * PathInfoSize;
                // DISPLAYCONFIG_PATH_INFO: the source's adapter LUID and id at 0 and 8, the target's at 20 and 28.
                var name = new byte[84]; // DISPLAYCONFIG_SOURCE_DEVICE_NAME: 20-byte header, WCHAR[32]
                WriteHeader(name, DisplayConfigGetSourceName, paths, path, path + 8);
                if (DisplayConfigGetDeviceInfo(name) != 0) continue;
                var device = System.Text.Encoding.Unicode.GetString(name, 20, 64);
                var end = device.IndexOf('\0');
                if (end >= 0) device = device[..end];
                if (device.Length == 0) continue;

                var color = new byte[32]; // DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO: header, flags, encoding, bits
                WriteHeader(color, DisplayConfigGetAdvancedColorInfo, paths, path + 20, path + 28);
                if (DisplayConfigGetDeviceInfo(color) != 0) continue;
                var enabled = (BitConverter.ToUInt32(color, 20) & 0x2) != 0; // advancedColorEnabled
                // Duplicated displays share a source: on when any of them is.
                result[device] = enabled || (result.TryGetValue(device, out var before) && before);
            }
        }
        catch (Exception)
        {
            result.Clear(); // unknown: no hint
        }
        return result;
    }

    /// <summary>Fills a DISPLAYCONFIG_DEVICE_INFO_HEADER: type, size, adapter LUID (8 bytes), id.</summary>
    private static void WriteHeader(byte[] packet, int type, byte[] paths, int adapterAt, int idAt)
    {
        BitConverter.GetBytes(type).CopyTo(packet, 0);
        BitConverter.GetBytes(packet.Length).CopyTo(packet, 4);
        Array.Copy(paths, adapterAt, packet, 8, 8);
        Array.Copy(paths, idAt, packet, 16, 4);
    }

    public static (int X, int Y) CursorPosition()
    {
        if (!GetCursorPos(out var p)) throw new Win32Exception(Marshal.GetLastWin32Error(), "The pointer position could not be read.");
        return (p.X, p.Y);
    }

    public static void SetCursorPosition(int x, int y) => SetCursorPos(x, y);

    /// <summary>Places a window topmost over <paramref name="rect"/> without activating it.</summary>
    public static void PlaceTopmost(IntPtr hwnd, PixelRect rect) =>
        SetWindowPos(hwnd, HwndTopmost, rect.Left, rect.Top, rect.Width, rect.Height, SwpNoActivate | SwpShowWindow);

    public static (int Left, int Top, int Right, int Bottom) WindowRect(IntPtr hwnd) =>
        GetWindowRect(hwnd, out var r) ? (r.Left, r.Top, r.Right, r.Bottom) : default;

    /// <summary>Moves a window's top-left corner, keeping its size, z-order and activation.</summary>
    public static void MoveTo(IntPtr hwnd, int x, int y) =>
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);

    /// <summary>
    /// The work area (the monitor without the taskbar) of the monitor nearest to a screen
    /// point, in the same coordinates as the point; empty when Windows doesn't say.
    /// </summary>
    public static PixelRect WorkAreaAt(int x, int y)
    {
        var monitor = MonitorFromPoint(new NativePoint { X = x, Y = y }, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return default;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info)) return default;
        return PixelRect.FromEdges(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
    }

    /// <summary>The window the user is working in right now (any process); zero when there is none.</summary>
    public static IntPtr ForegroundWindow() => GetForegroundWindow();

    /// <summary>Gives a window the foreground; only works while this process holds it.</summary>
    public static void Activate(IntPtr hwnd) => SetForegroundWindow(hwnd);

    /// <summary>
    /// The DPI of the monitor a window is on (96 = 100 %); 96 when it can't be read
    /// (no window, or Windows before 10 1607).
    /// </summary>
    public static int WindowDpi(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 96;
        try
        {
            var dpi = (int)GetDpiForWindow(hwnd);
            return dpi > 0 ? dpi : 96;
        }
        catch (EntryPointNotFoundException)
        {
            return 96;
        }
    }

    /// <summary>The size of a window's client area in its own pixels; (0, 0) when it can't be read.</summary>
    public static (int Width, int Height) ClientSize(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && GetClientRect(hwnd, out var r) ? (r.Right - r.Left, r.Bottom - r.Top) : default;

    /// <summary>
    /// Asks Windows 11 not to round a window's corners (a full-screen overlay would show
    /// the desktop behind them). Ignored where it isn't supported.
    /// </summary>
    public static void DisableRoundedCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var preference = DwmwcpDoNotRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (Exception)
        {
            // No DWM, or Windows 10: the corners are square anyway.
        }
    }

    /// <summary>Waits for the desktop compositor to present a frame (so hidden windows are off the screen).</summary>
    public static void FlushCompositor()
    {
        try
        {
            DwmFlush();
        }
        catch (DllNotFoundException)
        {
            // No DWM.
        }
    }

    // ---- interop -----------------------------------------------------------------------------

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref NativeRect rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags, ref uint pathCount, [In, Out] byte[] paths, ref uint modeCount, [In, Out] byte[] modes, IntPtr topology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo([In, Out] byte[] packet);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int rop);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}

/// <summary>A monitor: its full rectangle in physical pixels and its GDI device name (<c>\\.\DISPLAY1</c>; empty when unknown).</summary>
internal readonly record struct ScreenMonitor(PixelRect Bounds, string Device);

/// <summary>
/// The calling thread's view of the screen: its DPI awareness, the system DPI, and the
/// width of the primary monitor as GDI reports it to this thread and as it really is.
/// </summary>
internal readonly record struct ScreenDiagnostics(string ThreadAwareness, int SystemDpi, int HorzRes, int DesktopHorzRes)
{
    /// <summary>True when Windows is scaling this thread's screen coordinates: they are not physical pixels.</summary>
    public bool Virtualised => HorzRes > 0 && DesktopHorzRes > 0 && HorzRes != DesktopHorzRes;
}
