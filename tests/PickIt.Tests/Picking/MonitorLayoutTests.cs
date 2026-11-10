using PickIt.Revit.Model;
using PickIt.Revit.UI.Picking;

namespace PickIt.Tests.Picking;

/// <summary>
/// The eyedropper on unusual monitor setups: one capture per monitor, monitors left of and
/// above the primary, portrait and very wide monitors, walls, gaps, and fractional scale
/// factors. Everything is in physical pixels unless a name says DIPs.
/// </summary>
public class MonitorLayoutTests
{
    // Monitors left of, right of and above a 1920 × 1080 primary: negative X and negative Y.
    private static readonly PixelRect Left = new(-2560, -200, 2560, 1440);
    private static readonly PixelRect Primary = new(0, 0, 1920, 1080);
    private static readonly PixelRect Right = new(1920, -300, 1920, 1080);
    private static readonly PixelRect Above = new(200, -768, 1366, 768);

    // A portrait monitor left of the primary, starting above it and ending below it.
    private static readonly PixelRect Portrait = new(-1440, -740, 1440, 2560);

    private static readonly PixelRect Wide = new(0, 0, 7680, 2160);

    // A 2 × 2 wall of 1920 × 1080 monitors around the origin.
    private static readonly PixelRect WallTopLeft = new(-1920, -1080, 1920, 1080);
    private static readonly PixelRect WallTopRight = new(0, -1080, 1920, 1080);
    private static readonly PixelRect WallBottomLeft = new(-1920, 0, 1920, 1080);
    private static readonly PixelRect WallBottomRight = new(0, 0, 1920, 1080);

    private static readonly double[] Scales = [1.25, 1.5, 1.75, 2.25, 2.5, 3.5];

    /// <summary>
    /// The colour a test capture has at a screen position: the position's index in
    /// <paramref name="world"/> (the layout's bounding box), so every pixel of a layout has
    /// its own colour and any off-by-one or origin error reads a different one.
    /// </summary>
    private static Rgb Expected(PixelRect world, int x, int y)
    {
        Assert.True(world.Contains(x, y), $"({x}, {y}) is outside the test layout");
        Assert.True((long)world.Width * world.Height <= 1 << 24, "the layout has more pixels than colours");
        var v = (y - world.Top) * world.Width + (x - world.Left);
        return new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    /// <summary>A capture of <paramref name="bounds"/> alone, as the picker makes one per monitor.</summary>
    private static ScreenSnapshot Snapshot(PixelRect world, PixelRect bounds)
    {
        var stride = bounds.Width * 4 + 8; // padded rows must not shift pixels
        var pixels = new byte[stride * bounds.Height];
        for (var row = 0; row < bounds.Height; row++)
        {
            var v = (bounds.Top + row - world.Top) * world.Width + (bounds.Left - world.Left);
            var i = row * stride;
            for (var col = 0; col < bounds.Width; col++, v++, i += 4)
            {
                pixels[i] = (byte)v;
                pixels[i + 1] = (byte)(v >> 8);
                pixels[i + 2] = (byte)(v >> 16);
                pixels[i + 3] = 0x5A; // undefined in screen captures
            }
        }
        return new ScreenSnapshot(bounds, pixels, stride);
    }

    private static (PixelRect World, ScreenSnapshotSet Set) Layout(params PixelRect[] monitors)
    {
        var world = PixelRect.Union(monitors);
        return (world, new ScreenSnapshotSet(monitors.Select(m => Snapshot(world, m))));
    }

    // ---- seams ----------------------------------------------------------------------------

    [Fact]
    public void Seams_AreExact_OnALayoutWithNegativeXAndY()
    {
        var monitors = new[] { Primary, Left, Right, Above };
        var (world, set) = Layout(monitors);
        Assert.Equal(PixelRect.FromEdges(-2560, -768, 3840, 1240), world);
        Assert.Equal(world, set.Bounds);

        // (x, y, the monitor that pixel belongs to)
        (int X, int Y, PixelRect Owner)[] cases =
        [
            (-1, 500, Left), (0, 500, Primary),         // left seam
            (1919, 500, Primary), (1920, 500, Right),   // right seam
            (300, -1, Above), (300, 0, Primary),        // seam above the primary
            (-1, -200, Left), (-1, 1239, Left),         // the left monitor's last column, top and bottom
            (-2560, -200, Left), (3839, 779, Right),    // far corners
            (1920, -300, Right), (200, -768, Above), (1565, -1, Above),
        ];
        foreach (var (x, y, monitor) in cases)
        {
            var snapshot = set.Find(x, y);
            Assert.Equal(monitor, snapshot?.Bounds);
            Assert.Equal(Expected(world, x, y), set.ColorAt(x, y));
            Assert.Equal(Array.IndexOf(monitors, monitor), PixelRect.IndexAt(monitors, x, y));
        }
    }

    [Fact]
    public void Neighbourhood_ReadsAcrossASeam_IntoTheNextMonitorsCapture()
    {
        var (world, set) = Layout(Primary, Left, Right, Above);
        const int radius = 7;
        const int span = radius * 2 + 1;

        // Centred on the primary's first column: the left half comes from the left monitor.
        var cells = set.Neighbourhood(0, 500, radius);
        Assert.Equal(span * span, cells.Length);
        for (var row = 0; row < span; row++)
            for (var col = 0; col < span; col++)
                Assert.Equal(Expected(world, col - radius, 500 - radius + row), cells[row * span + col]);

        // The single-capture lookup stops at the seam.
        var alone = Snapshot(world, Primary).Neighbourhood(0, 500, radius);
        Assert.Null(alone[radius * span]);                               // (-7, 500)
        Assert.Equal(Expected(world, 0, 500), alone[radius * span + radius]);

        // Across the right seam, from the right monitor's side.
        var right = set.Neighbourhood(1920, 500, radius);
        Assert.Equal(Expected(world, 1913, 500), right[radius * span]);
        Assert.Equal(Expected(world, 1927, 500), right[radius * span + span - 1]);
    }

    [Fact]
    public void SnapshotSet_FindsTheCaptureContainingAPoint()
    {
        var (_, set) = Layout(Primary, Left);
        Assert.Equal(2, set.Snapshots.Count);
        Assert.Same(set.Snapshots[0], set.Find(0, 0));
        Assert.Same(set.Snapshots[1], set.Find(-1, 0));
        Assert.Same(set.Snapshots[0], set.Find(1919, 1079)); // back again: the last hit is only a shortcut
        Assert.Same(set.Snapshots[1], set.Find(-2560, 1239));
        Assert.Null(set.Find(1920, 0));
        Assert.Null(set.Find(-2561, 0));
        Assert.Null(set.Find(100, 1100)); // below the primary, inside the bounding box
        Assert.Equal(PixelRect.FromEdges(-2560, -200, 1920, 1240), set.Bounds);
        Assert.Equal(
            (long)(Primary.Width * 4 + 8) * Primary.Height + (long)(Left.Width * 4 + 8) * Left.Height,
            set.ByteCount);
        Assert.Throws<ArgumentException>(() => new ScreenSnapshotSet([]));
    }

    // ---- shapes ---------------------------------------------------------------------------

    [Fact]
    public void Portrait_LeftOfAndTallerThanThePrimary()
    {
        var monitors = new[] { Primary, Portrait };
        var (world, set) = Layout(monitors);
        Assert.Equal(PixelRect.FromEdges(-1440, -740, 1920, 1820), world);

        (int X, int Y)[] onPortrait = [(-1440, -740), (-1, -740), (-1440, 1819), (-1, 1819), (-720, 540), (-1, 500)];
        foreach (var (x, y) in onPortrait)
        {
            Assert.Equal(Expected(world, x, y), set.ColorAt(x, y));
            Assert.Equal(1, PixelRect.IndexAt(monitors, x, y));
        }
        Assert.Equal(Expected(world, 0, 500), set.ColorAt(0, 500));
        Assert.Equal(0, PixelRect.IndexAt(monitors, 0, 500));

        // Beside the portrait monitor, above and below the primary: no monitor, but inside the bounding box.
        Assert.True(world.Contains(100, -100));
        Assert.Null(set.ColorAt(100, -100));
        Assert.Null(set.ColorAt(0, -1));
        Assert.Null(set.ColorAt(0, 1080));
        Assert.Null(set.ColorAt(1919, 1819));
        Assert.Null(set.ColorAt(-1441, 0));
        Assert.Null(set.ColorAt(-1, 1820));
    }

    [Fact]
    public void AVeryWideMonitor_7680x2160()
    {
        var (world, set) = Layout(Wide);
        Assert.Equal(Wide, world);
        (int X, int Y)[] points = [(0, 0), (7679, 0), (0, 2159), (7679, 2159), (3840, 1080), (4096, 1), (7678, 2158)];
        foreach (var (x, y) in points)
            Assert.Equal(Expected(world, x, y), set.ColorAt(x, y));
        Assert.Null(set.ColorAt(7680, 0));
        Assert.Null(set.ColorAt(0, 2160));
        Assert.Null(set.ColorAt(-1, 0));
        Assert.Equal((long)(7680 * 4 + 8) * 2160, set.ByteCount);

        var cells = set.Neighbourhood(7679, 2159, 1);
        Assert.Equal(Expected(world, 7678, 2158), cells[0]);
        Assert.Equal(Expected(world, 7679, 2159), cells[4]);
        Assert.Null(cells[5]);
        Assert.Null(cells[8]);
    }

    [Fact]
    public void ATwoByTwoWall_MeetsExactlyAtItsCentre()
    {
        var monitors = new[] { WallBottomRight, WallTopLeft, WallTopRight, WallBottomLeft };
        var (world, set) = Layout(monitors);
        Assert.Equal(PixelRect.FromEdges(-1920, -1080, 1920, 1080), world);

        Assert.Equal(1, PixelRect.IndexAt(monitors, -1, -1));
        Assert.Equal(2, PixelRect.IndexAt(monitors, 0, -1));
        Assert.Equal(3, PixelRect.IndexAt(monitors, -1, 0));
        Assert.Equal(0, PixelRect.IndexAt(monitors, 0, 0));

        // The four pixels around the centre, and the magnifier over them: every cell from its own monitor.
        var cells = set.Neighbourhood(0, 0, 1);
        for (var row = 0; row < 3; row++)
            for (var col = 0; col < 3; col++)
                Assert.Equal(Expected(world, col - 1, row - 1), cells[row * 3 + col]);

        (int X, int Y)[] corners = [(-1920, -1080), (1919, -1080), (-1920, 1079), (1919, 1079)];
        foreach (var (x, y) in corners)
            Assert.Equal(Expected(world, x, y), set.ColorAt(x, y));
    }

    // ---- gaps -----------------------------------------------------------------------------

    [Fact]
    public void AGapInsideTheBoundingBox_HasNoColour_AndEmptyMagnifierCells()
    {
        // The wall without its top-right monitor.
        var (world, set) = Layout(WallBottomRight, WallTopLeft, WallBottomLeft);
        Assert.True(world.Contains(500, -500));
        Assert.Null(set.Find(500, -500));
        Assert.Null(set.ColorAt(500, -500)); // what the picker asks before it picks: no pick here

        // Deep in the gap every cell is empty.
        Assert.All(set.Neighbourhood(500, -500, 7), cell => Assert.Null(cell));

        // At the gap's corner only the cells over the missing monitor are empty.
        var cells = set.Neighbourhood(0, 0, 1);
        Assert.Equal(Expected(world, -1, -1), cells[0]);
        Assert.Null(cells[1]); // (0, -1)
        Assert.Null(cells[2]); // (1, -1)
        Assert.Equal(Expected(world, -1, 0), cells[3]);
        Assert.Equal(Expected(world, 0, 0), cells[4]);
        Assert.Equal(Expected(world, 1, 1), cells[8]);
    }

    [Fact]
    public void IndexAt_Ties_GoToTheFirstMonitorListed()
    {
        var a = new PixelRect(0, 0, 100, 100);
        var b = new PixelRect(103, 0, 100, 100); // a three-pixel gap: columns 100, 101, 102
        Assert.Equal(0, PixelRect.IndexAt([a, b], 100, 50));
        Assert.Equal(0, PixelRect.IndexAt([a, b], 101, 50)); // as far from a as from b
        Assert.Equal(0, PixelRect.IndexAt([b, a], 101, 50)); // the first listed, whichever it is
        Assert.Equal(1, PixelRect.IndexAt([a, b], 102, 50));
        Assert.Equal(0, PixelRect.IndexAt([b, a], 102, 50));
    }

    [Fact]
    public void IndexAt_InADiagonalGap_FindsTheNearerMonitor()
    {
        // Two monitors touching only at a corner: the other two quadrants are gaps.
        var a = new PixelRect(0, 0, 100, 100);
        var b = new PixelRect(100, 100, 100, 100);
        var monitors = new[] { a, b };
        Assert.Equal(1, PixelRect.IndexAt(monitors, 150, 50));  // 51 right of a, 50 above b
        Assert.Equal(0, PixelRect.IndexAt(monitors, 149, 50));  // 50 right of a, 50 above b: a tie, the first
        Assert.Equal(0, PixelRect.IndexAt(monitors, 105, 20));
        Assert.Equal(1, PixelRect.IndexAt(monitors, 50, 160));
        Assert.Equal(0, PixelRect.IndexAt(monitors, 100, 99));  // one pixel from each: the first
        Assert.Equal(0, PixelRect.IndexAt(monitors, 99, 99));
        Assert.Equal(1, PixelRect.IndexAt(monitors, 100, 100));

        var set = new ScreenSnapshotSet(monitors.Select(m => Snapshot(PixelRect.Union(monitors), m)));
        Assert.Null(set.ColorAt(150, 50));
        Assert.Null(set.ColorAt(50, 150));
    }

    // ---- pixels and DIPs --------------------------------------------------------------------

    [Fact]
    public void PixelsAndDips_RoundTrip_AtFractionalScales()
    {
        int[] widths = [1366, 1440, 1920, 2560, 3440, 3840, 5120, 7680];
        foreach (var scale in Scales)
            foreach (var width in widths)
            {
                var height = width * 9 / 16;
                var monitor = new PixelRect(-width, -300, width, height);
                var (dipWidth, dipHeight) = PickerGeometry.OverlaySize(monitor, scale);
                Assert.Equal(width, dipWidth * scale, 6);
                Assert.Equal(height, dipHeight * scale, 6);

                foreach (var column in new[] { 0, width / 2, width - 1 })
                    foreach (var row in new[] { 0, height / 2, height - 1 })
                    {
                        var (x, y) = PickerGeometry.ToOverlay(monitor, dipWidth, dipHeight, monitor.Left + column, monitor.Top + row);
                        // The DIP position is the centre of that device pixel, and of no other.
                        Assert.Equal(column + 0.5, x * scale, 6);
                        Assert.Equal(row + 0.5, y * scale, 6);
                        Assert.Equal(column, (int)Math.Floor(x * scale));
                        Assert.Equal(row, (int)Math.Floor(y * scale));
                        Assert.InRange(x, 0.0, dipWidth);
                        Assert.InRange(y, 0.0, dipHeight);
                    }
            }
    }

    [Fact]
    public void MagnifierCells_AreWholeDevicePixels_AtEveryScale()
    {
        foreach (var scale in new[] { 1, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 3, 3.5 })
        {
            var cell = PickerGeometry.LoupeCell(scale) * scale;
            Assert.Equal(Math.Round(cell), cell, 9);
            Assert.InRange(cell, 10 * scale - 0.5 - 1e-9, 10 * scale + 0.5 + 1e-9);

            var line = PickerGeometry.HairLine(scale) * scale;
            Assert.Equal(Math.Round(line), line, 9);
            Assert.InRange(line, 1 - 1e-9, scale + 0.5 + 1e-9);
        }
        Assert.Equal(10.0, PickerGeometry.LoupeCell(1));
        Assert.Equal(1.0, PickerGeometry.HairLine(1));
        Assert.Equal(12 / 1.25, PickerGeometry.LoupeCell(1.25), 12);
        Assert.Equal(18 / 1.75, PickerGeometry.LoupeCell(1.75), 12);
    }

    [Fact]
    public void AScaleThatIsNotAPositiveNumber_CountsAsOne()
    {
        Assert.Equal(1.0, PickerGeometry.SaneScale(0));
        Assert.Equal(1.0, PickerGeometry.SaneScale(-1.5));
        Assert.Equal(1.0, PickerGeometry.SaneScale(double.NaN));
        Assert.Equal(1.0, PickerGeometry.SaneScale(double.PositiveInfinity));
        Assert.Equal(1.75, PickerGeometry.SaneScale(1.75));
        Assert.Equal((1920.0, 1080.0), PickerGeometry.OverlaySize(Primary, 0));
    }

    // ---- the magnifier's place ---------------------------------------------------------------

    [Fact]
    public void TheMagnifier_StaysOnTheMonitor_AndOffThePointer_InAllFourCorners()
    {
        // About the magnifier's real size in DIPs, with the HDR hint showing.
        const double boxWidth = 166;
        const double boxHeight = 250;
        const double offset = 24;
        (PixelRect Area, double[] Factors)[] cases =
        [
            (new PixelRect(0, 0, 1366, 768), new[] { 1, 1.25, 1.5 }),
            (Portrait, new[] { 1, 1.25, 1.5, 1.75, 2.25 }),
            (Wide, new[] { 1, 1.5, 1.75, 2.25, 2.5, 3.5 }),
        ];
        foreach (var (monitor, factors) in cases)
            foreach (var scale in factors)
            {
                var (areaWidth, areaHeight) = PickerGeometry.OverlaySize(monitor, scale);
                (int X, int Y)[] corners =
                [
                    (monitor.Left, monitor.Top), (monitor.Right - 1, monitor.Top),
                    (monitor.Left, monitor.Bottom - 1), (monitor.Right - 1, monitor.Bottom - 1),
                ];
                foreach (var (x, y) in corners)
                {
                    var (left, top) = PickerGeometry.PlaceLoupe(monitor, scale, x, y, boxWidth, boxHeight, offset);
                    var where = $"{monitor.Width} × {monitor.Height} at {scale}, pointer ({x}, {y}): magnifier at ({left}, {top})";
                    Assert.True(left >= 0 && top >= 0, where);
                    Assert.True(left + boxWidth <= areaWidth + 1e-9 && top + boxHeight <= areaHeight + 1e-9, where);

                    var (px, py) = PickerGeometry.ToOverlay(monitor, areaWidth, areaHeight, x, y);
                    var covered = px >= left && px <= left + boxWidth && py >= top && py <= top + boxHeight;
                    Assert.False(covered, where);
                    // Beside the pointer, by the offset, on both axes.
                    Assert.True(px <= left - offset + 1e-9 || px >= left + boxWidth + offset - 1e-9, where);
                    Assert.True(py <= top - offset + 1e-9 || py >= top + boxHeight + offset - 1e-9, where);
                }
            }
    }

    [Fact]
    public void TheMagnifier_IsPlacedFromTheScale_NotFromALaidOutSize()
    {
        // 150 %: pixel (1500, 900) of the left monitor is at DIP (1000.33, 600.33); the box goes 24 DIPs below-right.
        var (left, top) = PickerGeometry.PlaceLoupe(Left, 1.5, -2560 + 1500, -200 + 900, 166, 250, 24);
        Assert.Equal(1500.5 / 1.5 + 24, left, 9);
        Assert.Equal(900.5 / 1.5 + 24, top, 9);
    }

    // ---- keyboard nudges ----------------------------------------------------------------------

    [Fact]
    public void Nudge_StopsAtTheEdgesOfTheVirtualScreen_AlsoTheNegativeOnes()
    {
        var bounds = PixelRect.Union([Primary, Left, Right, Above]); // -2560, -768 … 3839, 1239

        // Left edge (negative X).
        Assert.Equal((-2560, 0), PickerGeometry.Nudge(bounds, -2560, 0, -1, 0));
        Assert.Equal((-2560, 0), PickerGeometry.Nudge(bounds, -2560, 0, -10, 0));
        Assert.Equal((-2560, 0), PickerGeometry.Nudge(bounds, -2555, 0, -10, 0));
        Assert.Equal((-2560, 0), PickerGeometry.Nudge(bounds, -2559, 0, -1, 0));
        Assert.Equal((-2559, 0), PickerGeometry.Nudge(bounds, -2560, 0, 1, 0));
        Assert.Equal((-2550, 0), PickerGeometry.Nudge(bounds, -2560, 0, 10, 0));

        // Top edge (negative Y).
        Assert.Equal((300, -768), PickerGeometry.Nudge(bounds, 300, -768, 0, -1));
        Assert.Equal((300, -768), PickerGeometry.Nudge(bounds, 300, -768, 0, -10));
        Assert.Equal((300, -768), PickerGeometry.Nudge(bounds, 300, -765, 0, -10));
        Assert.Equal((300, -767), PickerGeometry.Nudge(bounds, 300, -768, 0, 1));
        Assert.Equal((300, -758), PickerGeometry.Nudge(bounds, 300, -768, 0, 10));

        // Right and bottom edges: the last pixel, not one past it.
        Assert.Equal((3839, 0), PickerGeometry.Nudge(bounds, 3839, 0, 1, 0));
        Assert.Equal((3839, 0), PickerGeometry.Nudge(bounds, 3835, 0, 10, 0));
        Assert.Equal((0, 1239), PickerGeometry.Nudge(bounds, 0, 1239, 0, 1));
        Assert.Equal((0, 1239), PickerGeometry.Nudge(bounds, 0, 1232, 0, 10));

        // Away from the edges it is a plain step, across the origin too.
        Assert.Equal((-101, -100), PickerGeometry.Nudge(bounds, -100, -100, -1, 0));
        Assert.Equal((5, -5), PickerGeometry.Nudge(bounds, -5, 5, 10, -10));
    }

    // ---- overlay check ------------------------------------------------------------------------

    [Fact]
    public void DifferenceCount_CountsPixels_IgnoringTheFourthByteAndTheStride()
    {
        var bounds = new PixelRect(-40, -30, 20, 10);
        var world = bounds;
        var frozen = Snapshot(world, bounds);

        // The same picture with unpadded rows and another fourth byte.
        var pixels = new byte[bounds.Width * 4 * bounds.Height];
        for (var row = 0; row < bounds.Height; row++)
            for (var col = 0; col < bounds.Width; col++)
            {
                var c = Expected(world, bounds.Left + col, bounds.Top + row);
                var i = (row * bounds.Width + col) * 4;
                pixels[i] = c.B;
                pixels[i + 1] = c.G;
                pixels[i + 2] = c.R;
                pixels[i + 3] = 0xFF;
            }
        var now = new ScreenSnapshot(bounds, pixels, bounds.Width * 4);
        Assert.Equal(0, frozen.DifferenceCount(now, out _, out _));

        pixels[(3 * bounds.Width + 7) * 4 + 1] ^= 0x01; // (7, 3): green differs by one
        pixels[(9 * bounds.Width + 19) * 4 + 2] ^= 0x80; // the last pixel: red
        Assert.Equal(2, frozen.DifferenceCount(now, out var x, out var y));
        Assert.Equal((-33, -27), (x, y));

        var other = Snapshot(world, new PixelRect(-40, -30, 10, 10));
        Assert.Throws<ArgumentException>(() => frozen.DifferenceCount(other, out _, out _));
    }
}
