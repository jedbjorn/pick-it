using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PickIt.Revit.UI.Theming;

namespace PickIt.Revit.UI.Picking;

/// <summary>
/// What an overlay looked like once it covered its monitor, for the diagnostic line: the
/// monitor, the window's client size in its own pixels, the DPI Windows gives the window,
/// WPF's device pixels per DIP for it, and whether the monitor has HDR on (null: unknown).
/// </summary>
internal readonly record struct OverlayInfo(PixelRect Monitor, int ClientWidth, int ClientHeight, int Dpi, double Scale, bool? Hdr)
{
    /// <summary>True when the window's client area isn't exactly the monitor: its pixels aren't screen pixels.</summary>
    public bool Mismatch => ClientWidth != Monitor.Width || ClientHeight != Monitor.Height;
}

/// <summary>
/// A topmost, borderless window covering one monitor while the eyedropper runs: the
/// frozen screenshot of that monitor and the magnifier. Input is forwarded to
/// <see cref="ScreenColorPicker"/>, which owns the pick.
/// </summary>
public partial class ScreenPickOverlay : Window
{
    /// <summary>Pixels shown either side of the centre pixel in the magnifier.</summary>
    public const int Radius = 7;

    private const int Span = Radius * 2 + 1;
    private const double LoupeOffset = 24;
    private const int WmWindowPosChanging = 0x0046;
    private const int WmDisplayChange = 0x007E;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;

    private readonly WriteableBitmap _cells = new(Span, Span, 96, 96, PixelFormats.Bgra32, null);
    private readonly byte[] _cellBuffer = new byte[Span * Span * 4];
    private readonly bool? _hdr;
    private PixelRect? _pinned;
    private double _scale = 1;

    /// <param name="monitor">The monitor to cover, in physical pixels.</param>
    /// <param name="palette">Colours of the magnifier.</param>
    /// <param name="hdr">Whether the monitor has HDR (advanced colour) on; null when unknown.</param>
    internal ScreenPickOverlay(PixelRect monitor, UiPalette palette, bool? hdr)
    {
        InitializeComponent();
        Monitor = monitor;
        _hdr = hdr;

        Loupe.Background = palette.Surface;
        Loupe.BorderBrush = palette.Border;
        Swatch.BorderBrush = palette.ChipBorder;
        RgbText.Foreground = palette.Text;
        HexText.Foreground = palette.MutedText;
        HintText.Foreground = palette.MutedText;
        HdrText.Foreground = palette.MutedText;
        if (hdr == true) HdrText.Visibility = Visibility.Visible;

        Cells.Source = _cells;
        ApplyScale(1);

        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(Handle)?.AddHook(PinHook);
            NativeScreen.DisableRoundedCorners(Handle);
            ApplyScale(CurrentScale());
        };
        // WPF changes the window's scale factor at once and lays it out again later: the
        // geometry follows the scale factor, and the picker places the magnifier again.
        // The scale factor is read from the composition target, not from the event: inside a
        // system-DPI-aware host (Revit) the event's DpiScale stays 1 while the window renders
        // at the monitor's scale factor, which would draw the screenshot too large.
        DpiChanged += (_, _) =>
        {
            ApplyScale(CurrentScale());
            GeometryChanged?.Invoke();
        };
        SizeChanged += (_, _) => GeometryChanged?.Invoke();
    }

    /// <summary>The monitor this overlay covers, in physical pixels.</summary>
    internal PixelRect Monitor { get; }

    internal IntPtr Handle { get; private set; }

    /// <summary>The window's scale factor or size changed: the magnifier has to be placed again.</summary>
    internal event Action? GeometryChanged;

    /// <summary>Windows changed the display setup (resolution, monitors) while the overlay is up.</summary>
    internal event Action? DisplayChanged;

    internal void ShowScreenshot(BitmapSource image) => Screenshot.Source = image;

    /// <summary>
    /// Covers the monitor exactly and keeps it that way: every later move or resize
    /// (for example WPF answering WM_DPICHANGED when the window lands on a monitor with
    /// another scale factor) is pinned back to the monitor's rectangle. Returns what the
    /// window then looks like to Windows and to WPF.
    /// </summary>
    internal OverlayInfo Cover()
    {
        _pinned = Monitor;
        NativeScreen.PlaceTopmost(Handle, Monitor);
        // Belt and braces: if a DPI change still moved it, place it once more.
        if (NativeScreen.WindowRect(Handle) != (Monitor.Left, Monitor.Top, Monitor.Right, Monitor.Bottom))
            NativeScreen.PlaceTopmost(Handle, Monitor);
        // The window may have changed monitor, and with it its scale factor.
        ApplyScale(CurrentScale());
        UpdateLayout();
        var (width, height) = NativeScreen.ClientSize(Handle);
        return new OverlayInfo(Monitor, width, height, NativeScreen.WindowDpi(Handle), _scale, _hdr);
    }

    /// <summary>Shows the magnifier for physical pixel (x, y), which lies on this monitor.</summary>
    internal void ShowLoupe(ScreenSnapshotSet snapshots, int x, int y)
    {
        // Across a seam the cells come from the neighbouring monitor's capture; where no monitor is they stay empty.
        var colors = snapshots.Neighbourhood(x, y, Radius);
        for (var i = 0; i < colors.Length; i++)
        {
            var c = colors[i];
            var o = i * 4;
            _cellBuffer[o] = c?.B ?? 0;
            _cellBuffer[o + 1] = c?.G ?? 0;
            _cellBuffer[o + 2] = c?.R ?? 0;
            _cellBuffer[o + 3] = c is null ? (byte)0 : (byte)255;
        }
        _cells.WritePixels(new Int32Rect(0, 0, Span, Span), _cellBuffer, Span * 4, 0);

        var centre = colors[Radius * Span + Radius];
        if (centre is { } color)
        {
            Swatch.Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
            RgbText.Text = $"RGB {color.R}, {color.G}, {color.B}";
            HexText.Text = color.ToHex();
        }

        Loupe.Visibility = Visibility.Visible;
        // Its size now (the text and the cell size change it), without waiting for a layout pass.
        Loupe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = Loupe.DesiredSize;
        var (left, top) = PickerGeometry.PlaceLoupe(Monitor, _scale, x, y, size.Width, size.Height, LoupeOffset);
        // On whole device pixels, or the 1 px grid lines blur at 125 % and 175 %.
        Canvas.SetLeft(Loupe, Math.Round(left * _scale) / _scale);
        Canvas.SetTop(Loupe, Math.Round(top * _scale) / _scale);
    }

    internal void HideLoupe() => Loupe.Visibility = Visibility.Collapsed;

    /// <summary>WPF's device pixels per DIP for this window right now; the last known value before it has a handle.</summary>
    private double CurrentScale()
    {
        var target = PresentationSource.FromVisual(this)?.CompositionTarget;
        return target is null ? _scale : target.TransformToDevice.M11;
    }

    /// <summary>
    /// Sizes everything that must be whole device pixels from the window's scale factor
    /// <paramref name="scale"/> (device pixels per DIP): the screenshot is drawn one to one
    /// (monitor pixels / scale DIPs), and a magnified pixel is a whole number of pixels
    /// (ten DIPs would be 12.5 pixels at 125 %).
    /// </summary>
    private void ApplyScale(double scale)
    {
        _scale = PickerGeometry.SaneScale(scale);
        var (width, height) = PickerGeometry.OverlaySize(Monitor, _scale);
        Screenshot.Width = width;
        Screenshot.Height = height;

        var cell = PickerGeometry.LoupeCell(_scale);
        var line = PickerGeometry.HairLine(_scale);
        var zoom = Span * cell;
        Zoom.Width = zoom;
        Zoom.Height = zoom;
        Cells.Width = zoom;
        Cells.Height = zoom;
        GridLines.Width = zoom;
        GridLines.Height = zoom;
        GridLines.Fill = GridBrush(cell, line);
        CentreOuter.StrokeThickness = line;
        CentreInner.StrokeThickness = line;
        Place(CentreOuter, Radius * cell - line, cell + 2 * line);
        Place(CentreInner, Radius * cell, cell);
    }

    private IntPtr PinHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmWindowPosChanging && _pinned is { } r && lParam != IntPtr.Zero)
        {
            var pos = Marshal.PtrToStructure<WindowPos>(lParam);
            pos.X = r.Left;
            pos.Y = r.Top;
            pos.Width = r.Width;
            pos.Height = r.Height;
            pos.Flags &= ~(SwpNoMove | SwpNoSize);
            Marshal.StructureToPtr(pos, lParam, false);
        }
        else if (msg == WmDisplayChange)
        {
            // The monitors this overlay and its screenshot were made for are no longer the screen.
            DisplayChanged?.Invoke();
        }
        return IntPtr.Zero;
    }

    private static void Place(FrameworkElement element, double at, double size)
    {
        Canvas.SetLeft(element, at);
        Canvas.SetTop(element, at);
        element.Width = size;
        element.Height = size;
    }

    /// <summary>Faint lines between the magnified pixels: <paramref name="line"/> thick, one per <paramref name="cell"/>.</summary>
    private static Brush GridBrush(double cell, double line)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), line);
        var lines = new GeometryGroup();
        lines.Children.Add(new LineGeometry(new Point(cell - line / 2, 0), new Point(cell - line / 2, cell)));
        lines.Children.Add(new LineGeometry(new Point(0, cell - line / 2), new Point(cell, cell - line / 2)));
        var drawing = new GeometryDrawing(null, pen, lines);
        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, cell, cell),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, cell, cell),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Hwnd;
        public IntPtr InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }
}
