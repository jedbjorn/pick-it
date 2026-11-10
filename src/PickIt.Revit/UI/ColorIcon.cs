using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PickIt.Revit.Model;

namespace PickIt.Revit.UI;

/// <summary>
/// Draws the ribbon button's images for a picked colour: a rounded tile filled with the
/// colour, the shape of the idle icon's tile. The 32 px image carries the R, G and B
/// values stacked, in black or white, whichever contrasts better; the 16 px image is the
/// colour only.
/// <para>
/// A one-pixel edge in the same black or white at 40 % keeps the tile visible when the
/// colour is close to the ribbon's own, in either Revit theme, without needing to know
/// the theme.
/// </para>
/// </summary>
internal static class ColorIcon
{
    private const double FontSize = 11;

    /// <summary>Top of one row of digits to the top of the next, in px of the 32 px icon.</summary>
    private const double RowPitch = 10;

    private const byte EdgeAlpha = 0x66;

    private static readonly Typeface Face = new(
        new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    /// <summary>The 32 px image: the colour with its R, G and B values.</summary>
    public static BitmapSource Large(Rgb color, int scale) => Render(32, 3, color, scale, withValues: true);

    /// <summary>The 16 px image: the colour only.</summary>
    public static BitmapSource Small(Rgb color, int scale) => Render(16, 1.5, color, scale, withValues: false);

    /// <summary>
    /// <paramref name="scale"/> is the number of bitmap pixels per icon pixel (1 at 100 %
    /// display scaling); the bitmap's DPI is raised to match, so it still measures
    /// <paramref name="size"/> units in the ribbon.
    /// </summary>
    private static BitmapSource Render(int size, double radius, Rgb color, int scale, bool withValues)
    {
        var black = color.PrefersBlackText();
        var ink = black ? Brushes.Black : Brushes.White;
        var fill = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        var edge = new Pen(new SolidColorBrush(black ? Color.FromArgb(EdgeAlpha, 0, 0, 0) : Color.FromArgb(EdgeAlpha, 255, 255, 255)), 1);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // The pen is centred on the outline, so the edge is the tile's outermost pixel.
            dc.DrawRoundedRectangle(fill, edge, new Rect(0.5, 0.5, size - 1, size - 1), radius, radius);
            if (withValues)
            {
                // Digits have no descenders, so the rows are spaced by cap height, not line height.
                var capHeight = Face.CapsHeight * FontSize;
                var top = (size - (2 * RowPitch + capHeight)) / 2;
                var values = new[] { color.R, color.G, color.B };
                for (var i = 0; i < values.Length; i++)
                {
                    var text = new FormattedText(
                        values[i].ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, Face, FontSize, ink, scale);
                    // Whole bitmap pixels, so the digits aren't smeared across two.
                    var x = Math.Round((size - text.Width) / 2 * scale) / scale;
                    var y = Math.Round((top + i * RowPitch + capHeight - text.Baseline) * scale) / scale;
                    dc.DrawText(text, new Point(x, y));
                }
            }
        }

        var bitmap = new RenderTargetBitmap(size * scale, size * scale, 96.0 * scale, 96.0 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
