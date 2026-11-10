using System.Globalization;

namespace PickIt.Revit.Model;

/// <summary>An opaque 8-bit RGB colour. Stored as <c>#RRGGBB</c>.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>Upper-case <c>#RRGGBB</c>.</summary>
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>The ribbon tooltip and readout form: <c>RGB r, g, b · #HEX</c>.</summary>
    public string ToDisplay() => $"RGB {R}, {G}, {B} · {ToHex()}";

    public override string ToString() => ToHex();

    /// <summary>Parses <c>#RRGGBB</c> (the leading <c>#</c> is optional, case-insensitive).</summary>
    public static bool TryParse(string? text, out Rgb color)
    {
        color = default;
        if (text is null) return false;
        var s = text.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v))
            return false;
        color = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    /// <summary>WCAG 2.x relative luminance, 0 to 1.</summary>
    public double RelativeLuminance()
    {
        static double Channel(byte c)
        {
            var s = c / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);
    }

    /// <summary>
    /// True when black text contrasts better with this colour than white text does
    /// (WCAG contrast ratio; the two are equal at a luminance of about 0.179).
    /// </summary>
    public bool PrefersBlackText()
    {
        var l = RelativeLuminance();
        return (l + 0.05) / 0.05 >= 1.05 / (l + 0.05);
    }
}
