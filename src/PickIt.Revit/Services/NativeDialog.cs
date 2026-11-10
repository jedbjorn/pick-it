using System.Runtime.InteropServices;
using System.Text;

namespace PickIt.Revit.Services;

/// <summary>
/// The user32 calls <see cref="PaletteWriter"/> drives Revit's Color dialog with. All
/// coordinates are in the calling thread's DPI space, and only ever compared with each
/// other.
/// </summary>
internal static class NativeDialog
{
    public const int WmSetText = 0x000C;
    public const int WmClose = 0x0010;
    public const int WmShowWindow = 0x0018;
    public const int WmWindowPosChanging = 0x0046;
    public const int WmInitDialog = 0x0110;
    public const int WmCommand = 0x0111;
    public const int WmLButtonDown = 0x0201;
    public const int WmLButtonUp = 0x0202;
    public const int EnKillFocus = 0x0200;
    public const int EnChange = 0x0300;

    /// <summary>WH_CALLWNDPROC: sees every message sent to a window of the thread, before the window does.</summary>
    public const int WhCallWndProc = 4;

    /// <summary>Where a concealed dialog is parked: far outside any monitor.</summary>
    public const int OffScreen = -32000;

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsChild = 0x40000000;
    private const long WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x2;
    private const byte ConcealAlpha = 1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    public static uint CurrentThreadId() => GetCurrentThreadId();

    /// <summary>Installs a hook for one thread of this process; zero when Windows refuses.</summary>
    public static IntPtr Hook(int kind, HookProc proc, uint threadId) => SetWindowsHookExW(kind, proc, IntPtr.Zero, threadId);

    public static void Unhook(IntPtr hook) => UnhookWindowsHookEx(hook);

    public static IntPtr NextHook(int code, IntPtr wParam, IntPtr lParam) => CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

    /// <summary>The top-level windows of a thread.</summary>
    public static List<IntPtr> ThreadWindows(uint threadId)
    {
        var windows = new List<IntPtr>();
        EnumWindowsProc collect = (hwnd, _) =>
        {
            windows.Add(hwnd);
            return true;
        };
        EnumThreadWindows(threadId, collect, IntPtr.Zero);
        GC.KeepAlive(collect);
        return windows;
    }

    public static string ClassOf(IntPtr hwnd)
    {
        var text = new StringBuilder(256);
        return GetClassNameW(hwnd, text, text.Capacity) > 0 ? text.ToString() : "";
    }

    public static string TextOf(IntPtr hwnd)
    {
        var text = new StringBuilder(256);
        return GetWindowTextW(hwnd, text, text.Capacity) > 0 ? text.ToString() : "";
    }

    /// <summary>A direct child control of a dialog by its id; zero when there is none.</summary>
    public static IntPtr Control(IntPtr dialog, int id) => GetDlgItem(dialog, id);

    public static bool Exists(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    public static bool IsVisible(IntPtr hwnd) => IsWindowVisible(hwnd);

    /// <summary>True for a window that isn't a child control (a dialog, a frame).</summary>
    public static bool IsTopLevel(IntPtr hwnd) => (GetWindowLongPtrW(hwnd, GwlStyle).ToInt64() & WsChild) == 0;

    public static (int Left, int Top, int Right, int Bottom) RectOf(IntPtr hwnd) =>
        GetWindowRect(hwnd, out var r) ? (r.Left, r.Top, r.Right, r.Bottom) : default;

    /// <summary>A screen point in a window's client coordinates.</summary>
    public static (int X, int Y) ToClient(IntPtr hwnd, int x, int y)
    {
        var point = new NativePoint { X = x, Y = y };
        ScreenToClient(hwnd, ref point);
        return (point.X, point.Y);
    }

    public static IntPtr Send(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam) => SendMessageW(hwnd, message, wParam, lParam);

    public static void SetText(IntPtr hwnd, string text) => SendMessageText(hwnd, WmSetText, IntPtr.Zero, text);

    public static void Post(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam) => PostMessageW(hwnd, message, wParam, lParam);

    public static void Focus(IntPtr hwnd) => SetFocus(hwnd);

    /// <summary>The wParam of a WM_COMMAND: control id in the low word, notification code in the high word.</summary>
    public static IntPtr CommandParam(int id, int code) => new((long)(((uint)code << 16) | ((uint)id & 0xFFFF)));

    /// <summary>The lParam of a mouse message: client x in the low word, y in the high word.</summary>
    public static IntPtr PointParam(int x, int y) => new((long)((((uint)y & 0xFFFF) << 16) | ((uint)x & 0xFFFF)));

    /// <summary>
    /// Makes a top-level window all but invisible (layered, alpha 1 of 255: nothing of it
    /// reaches the screen). Not alpha 0: Revit's Color dialog then ignores the sent mouse
    /// clicks that select a custom-colour slot. False when Windows refuses.
    /// </summary>
    public static bool MakeTransparent(IntPtr hwnd)
    {
        var style = GetWindowLongPtrW(hwnd, GwlExStyle).ToInt64();
        if ((style & WsExLayered) == 0) SetWindowLongPtrW(hwnd, GwlExStyle, new IntPtr(style | WsExLayered));
        return SetLayeredWindowAttributes(hwnd, 0, ConcealAlpha, LwaAlpha);
    }

    /// <summary>Parks a window outside every monitor, keeping its size, z-order and activation.</summary>
    public static void MoveOffScreen(IntPtr hwnd) =>
        SetWindowPos(hwnd, IntPtr.Zero, OffScreen, OffScreen, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);

    /// <summary>Undoes <see cref="MakeTransparent"/> and <see cref="MoveOffScreen"/> so the user can deal with the window.</summary>
    public static void Reveal(IntPtr hwnd)
    {
        if ((GetWindowLongPtrW(hwnd, GwlExStyle).ToInt64() & WsExLayered) != 0)
            SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);
        if (GetWindowRect(hwnd, out var r) && r.Left <= OffScreen + 1000)
            SetWindowPos(hwnd, IntPtr.Zero, 120, 120, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    // ---- interop -----------------------------------------------------------------------------

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

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

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int kind, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDlgItem(IntPtr dialog, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr hwnd, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageText(IntPtr hwnd, int message, IntPtr wParam, string lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);

    // The add-in is x64 only (PlatformTarget), where these two are real exports.
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);
}
