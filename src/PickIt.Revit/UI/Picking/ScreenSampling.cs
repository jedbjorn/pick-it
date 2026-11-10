using PickIt.Revit.Model;

namespace PickIt.Revit.UI.Picking;

// Pure geometry and pixel lookup behind the eyedropper (ported from Gantt-It).
// Everything here is in physical screen pixels: the picker captures each
// monitor, reads the pointer and positions the overlays in the same
// per-monitor-DPI-aware coordinate space, so a pointer position maps to a
// snapshot pixel by a plain offset whatever the monitors' scale factors are.
// No WPF or Win32 in this file: tests/PickIt.Tests compiles it as it is.

/// <summary>A rectangle in physical screen pixels. Left/Top inclusive, Right/Bottom exclusive.</summary>
internal readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public static PixelRect FromEdges(int left, int top, int right, int bottom) => new(left, top, right - left, bottom - top);

    /// <summary>The smallest rectangle holding every rectangle (the virtual screen of a set of monitors).</summary>
    public static PixelRect Union(IEnumerable<PixelRect> rects)
    {
        int? l = null, t = null, r = null, b = null;
        foreach (var rect in rects)
        {
            if (rect.IsEmpty) continue;
            l = Math.Min(l ?? rect.Left, rect.Left);
            t = Math.Min(t ?? rect.Top, rect.Top);
            r = Math.Max(r ?? rect.Right, rect.Right);
            b = Math.Max(b ?? rect.Bottom, rect.Bottom);
        }
        return l is null ? default : FromEdges(l.Value, t!.Value, r!.Value, b!.Value);
    }

    /// <summary>
    /// Index of the rectangle containing the point, or of the nearest one when the
    /// point falls in a gap between monitors; -1 for an empty list.
    /// </summary>
    public static int IndexAt(IReadOnlyList<PixelRect> rects, int x, int y)
    {
        var best = -1;
        long bestDistance = long.MaxValue;
        for (var i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            if (r.Contains(x, y)) return i;
            long dx = x < r.Left ? r.Left - x : x >= r.Right ? x - r.Right + 1 : 0;
            long dy = y < r.Top ? r.Top - y : y >= r.Bottom ? y - r.Bottom + 1 : 0;
            var d = dx * dx + dy * dy;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>The point moved inside the rectangle (for keyboard nudges at an edge).</summary>
    public (int X, int Y) Clamp(int x, int y) =>
        (Math.Clamp(x, Left, Math.Max(Left, Right - 1)), Math.Clamp(y, Top, Math.Max(Top, Bottom - 1)));
}

/// <summary>
/// A frozen capture of one screen area (a monitor) as 32-bit BGRX rows, top-down, the
/// layout of a top-down 32 bpp DIB. The fourth byte is ignored (screen captures
/// leave it undefined).
/// </summary>
internal sealed class ScreenSnapshot
{
    public ScreenSnapshot(PixelRect bounds, byte[] pixels, int stride)
    {
        if (bounds.IsEmpty) throw new ArgumentException("The captured area is empty.", nameof(bounds));
        if (stride < bounds.Width * 4) throw new ArgumentException("The stride is shorter than a row.", nameof(stride));
        if (pixels.Length < (long)stride * bounds.Height) throw new ArgumentException("The pixel buffer is too small.", nameof(pixels));
        Bounds = bounds;
        Pixels = pixels;
        Stride = stride;
    }

    /// <summary>The captured area in physical screen pixels (its Left/Top may be negative).</summary>
    public PixelRect Bounds { get; }

    public byte[] Pixels { get; }

    public int Stride { get; }

    /// <summary>The colour of the screen pixel at (<paramref name="x"/>, <paramref name="y"/>); null outside the capture.</summary>
    public Rgb? ColorAt(int x, int y)
    {
        if (!Bounds.Contains(x, y)) return null;
        var i = (y - Bounds.Top) * Stride + (x - Bounds.Left) * 4;
        return new Rgb(Pixels[i + 2], Pixels[i + 1], Pixels[i]);
    }

    /// <summary>
    /// The (2·<paramref name="radius"/>+1)² pixels centred on (x, y), row by row,
    /// for the magnifier. Pixels outside the capture are null.
    /// </summary>
    public Rgb?[] Neighbourhood(int x, int y, int radius)
    {
        var size = radius * 2 + 1;
        var cells = new Rgb?[size * size];
        for (var row = 0; row < size; row++)
            for (var col = 0; col < size; col++)
                cells[row * size + col] = ColorAt(x - radius + col, y - radius + row);
        return cells;
    }

    /// <summary>
    /// How many pixels differ from <paramref name="other"/>, a capture of the same area
    /// (the fourth byte is ignored); <paramref name="firstX"/>/<paramref name="firstY"/> are
    /// the screen position of the first one, row by row.
    /// </summary>
    public int DifferenceCount(ScreenSnapshot other, out int firstX, out int firstY)
    {
        if (other.Bounds != Bounds) throw new ArgumentException("The captures cover different areas.", nameof(other));
        firstX = 0;
        firstY = 0;
        var count = 0;
        for (var row = 0; row < Bounds.Height; row++)
        {
            var a = row * Stride;
            var b = row * other.Stride;
            for (var col = 0; col < Bounds.Width; col++, a += 4, b += 4)
            {
                if (Pixels[a] == other.Pixels[b] && Pixels[a + 1] == other.Pixels[b + 1] && Pixels[a + 2] == other.Pixels[b + 2])
                    continue;
                if (count++ == 0)
                {
                    firstX = Bounds.Left + col;
                    firstY = Bounds.Top + row;
                }
            }
        }
        return count;
    }
}

/// <summary>
/// The captures of one pick, one per monitor. A lookup goes to the capture containing the
/// point, so the magnifier reads across a seam into the neighbouring monitor, and a point
/// in a gap between monitors has no colour.
/// </summary>
internal sealed class ScreenSnapshotSet
{
    private readonly ScreenSnapshot[] _snapshots;
    private ScreenSnapshot _last;

    public ScreenSnapshotSet(IEnumerable<ScreenSnapshot> snapshots)
    {
        _snapshots = snapshots.ToArray();
        if (_snapshots.Length == 0) throw new ArgumentException("There is no capture.", nameof(snapshots));
        _last = _snapshots[0];
        Bounds = PixelRect.Union(_snapshots.Select(s => s.Bounds));
    }

    /// <summary>The captures, in the order given (the picker's overlay order).</summary>
    public IReadOnlyList<ScreenSnapshot> Snapshots => _snapshots;

    /// <summary>The smallest rectangle holding every capture: the virtual screen. It can contain gaps.</summary>
    public PixelRect Bounds { get; }

    /// <summary>The size of all the pixel buffers together.</summary>
    public long ByteCount => _snapshots.Sum(s => (long)s.Pixels.Length);

    /// <summary>The capture containing the point; null in a gap or outside every monitor.</summary>
    public ScreenSnapshot? Find(int x, int y)
    {
        // Neighbouring lookups mostly land in the same capture.
        if (_last.Bounds.Contains(x, y)) return _last;
        foreach (var snapshot in _snapshots)
        {
            if (!snapshot.Bounds.Contains(x, y)) continue;
            _last = snapshot;
            return snapshot;
        }
        return null;
    }

    /// <summary>The colour of the screen pixel at (<paramref name="x"/>, <paramref name="y"/>); null where no monitor is.</summary>
    public Rgb? ColorAt(int x, int y) => Find(x, y)?.ColorAt(x, y);

    /// <summary>
    /// The (2·<paramref name="radius"/>+1)² pixels centred on (x, y), row by row, for the
    /// magnifier, each from the capture it lies in. Pixels on no monitor are null.
    /// </summary>
    public Rgb?[] Neighbourhood(int x, int y, int radius)
    {
        var size = radius * 2 + 1;
        var cells = new Rgb?[size * size];
        for (var row = 0; row < size; row++)
            for (var col = 0; col < size; col++)
                cells[row * size + col] = ColorAt(x - radius + col, y - radius + row);
        return cells;
    }
}

/// <summary>Placement math for the overlay windows, the magnifier, the menu popup and the ribbon icon.</summary>
internal static class PickerGeometry
{
    /// <summary>A usable scale factor (device pixels per WPF DIP): 1 for anything that isn't a positive number.</summary>
    public static double SaneScale(double scale) => scale > 0 && !double.IsInfinity(scale) ? scale : 1;

    /// <summary>
    /// The size in WPF DIPs at which something <paramref name="monitor"/>-sized is drawn
    /// one to one, in a window whose scale factor is <paramref name="scale"/>.
    /// </summary>
    public static (double Width, double Height) OverlaySize(PixelRect monitor, double scale)
    {
        scale = SaneScale(scale);
        return (monitor.Width / scale, monitor.Height / scale);
    }

    /// <summary>
    /// The side of one magnified pixel in DIPs: about 10, adjusted so it is a whole number
    /// of device pixels (at 125 % or 175 % ten DIPs would be 12.5 or 17.5 pixels).
    /// </summary>
    public static double LoupeCell(double scale)
    {
        scale = SaneScale(scale);
        return Math.Max(1, Math.Round(10 * scale)) / scale;
    }

    /// <summary>The thickness in DIPs of a line about one DIP thick that is a whole number of device pixels.</summary>
    public static double HairLine(double scale)
    {
        scale = SaneScale(scale);
        return Math.Max(1, Math.Round(scale)) / scale;
    }

    /// <summary>
    /// Top-left, in DIPs, of the magnifier (<paramref name="boxWidth"/> ×
    /// <paramref name="boxHeight"/> DIPs) for the pointer at physical pixel (x, y) in an
    /// overlay covering <paramref name="monitor"/> at <paramref name="scale"/>. Derived from
    /// the scale factor, not from the overlay's laid-out size, which lags behind a DPI change.
    /// </summary>
    public static (double X, double Y) PlaceLoupe(
        PixelRect monitor, double scale, int x, int y, double boxWidth, double boxHeight, double offset)
    {
        var (width, height) = OverlaySize(monitor, scale);
        var (px, py) = ToOverlay(monitor, width, height, x, y);
        return PlaceBeside(px, py, boxWidth, boxHeight, width, height, offset);
    }

    /// <summary>The pointer moved by a keyboard nudge, kept inside <paramref name="bounds"/> (the virtual screen).</summary>
    public static (int X, int Y) Nudge(PixelRect bounds, int x, int y, int dx, int dy) => bounds.Clamp(x + dx, y + dy);

    /// <summary>
    /// Bitmap pixels per icon pixel for the drawn ribbon icons, from a monitor's DPI
    /// (96 = 100 %): 1 at 100 %, 2 up to 200 %, 3 above. Whole numbers only, so the
    /// one-pixel edge and the digits stay sharp when the ribbon scales the image.
    /// </summary>
    public static int IconScaleFor(int dpi) => dpi switch
    {
        <= 96 => 1,
        <= 192 => 2,
        _ => 3,
    };

    /// <summary>
    /// Where a <paramref name="width"/> × <paramref name="height"/> window asked for at (x, y)
    /// goes so that its card (the window less <paramref name="margin"/> on every side) lies
    /// inside <paramref name="work"/>. A card larger than the work area keeps its top-left
    /// corner in it; an empty work area changes nothing.
    /// </summary>
    public static (int X, int Y) MoveInside(PixelRect work, int x, int y, int width, int height, int margin)
    {
        if (work.IsEmpty) return (x, y);
        return (
            Math.Max(work.Left - margin, Math.Min(x, work.Right - width + margin)),
            Math.Max(work.Top - margin, Math.Min(y, work.Bottom - height + margin)));
    }

    /// <summary>
    /// Where the centre of physical pixel (x, y) falls in an overlay that covers
    /// <paramref name="monitor"/> with a client area of <paramref name="width"/> ×
    /// <paramref name="height"/> in the overlay's own units (WPF DIPs). Proportional,
    /// so it holds whatever scale factor WPF applies to the overlay.
    /// </summary>
    public static (double X, double Y) ToOverlay(PixelRect monitor, double width, double height, int x, int y) =>
        ((x - monitor.Left + 0.5) * width / monitor.Width, (y - monitor.Top + 0.5) * height / monitor.Height);

    /// <summary>
    /// Top-left of a <paramref name="boxWidth"/> × <paramref name="boxHeight"/> box shown
    /// <paramref name="offset"/> below-right of the pointer, flipped to the left and/or
    /// above when it would leave the area, and kept inside it.
    /// </summary>
    public static (double X, double Y) PlaceBeside(
        double pointerX, double pointerY, double boxWidth, double boxHeight, double areaWidth, double areaHeight, double offset)
    {
        var x = pointerX + offset;
        if (x + boxWidth > areaWidth) x = pointerX - offset - boxWidth;
        var y = pointerY + offset;
        if (y + boxHeight > areaHeight) y = pointerY - offset - boxHeight;
        x = Math.Max(0, Math.Min(x, areaWidth - boxWidth));
        y = Math.Max(0, Math.Min(y, areaHeight - boxHeight));
        return (x, y);
    }
}
