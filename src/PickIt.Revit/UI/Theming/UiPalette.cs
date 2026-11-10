using System.Globalization;
using System.Windows.Media;

namespace PickIt.Revit.UI.Theming;

/// <summary>The family colours as frozen brushes, one set per theme.</summary>
public sealed class UiPalette
{
    public static readonly UiPalette Light = new(
        window: "#F4F5F7", surface: "#FFFFFF", surfaceAlt: "#F9FAFB", border: "#D3D7DE", text: "#1F2328",
        mutedText: "#5E6672", accent: "#2F6AD0", accentText: "#2F6AD0", hover: "#EEF0F3", pressed: "#E2E5EA",
        chipBorder: "#40000000", chipHoverBorder: "#A0000000", divider: "#E6E8EC", error: "#C42B1C",
        toolTip: "#F1F2F7", toolTipText: "#575757", toolTipBorder: "#767676");

    public static readonly UiPalette Dark = new(
        window: "#1F2329", surface: "#2A2F37", surfaceAlt: "#252A31", border: "#525B67", text: "#E6E9ED",
        mutedText: "#AEB6C1", accent: "#2F6AD0", accentText: "#79AEFF", hover: "#353B44", pressed: "#3E454F",
        chipBorder: "#70FFFFFF", chipHoverBorder: "#E0FFFFFF", divider: "#3A414B", error: "#FF8F85",
        toolTip: "#2F353E", toolTipText: "#E6E9ED", toolTipBorder: "#5F6874");

    private UiPalette(
        string window, string surface, string surfaceAlt, string border, string text, string mutedText,
        string accent, string accentText, string hover, string pressed, string chipBorder, string chipHoverBorder,
        string divider, string error, string toolTip, string toolTipText, string toolTipBorder)
    {
        Window = Frozen(window);
        Surface = Frozen(surface);
        SurfaceAlt = Frozen(surfaceAlt);
        Border = Frozen(border);
        Text = Frozen(text);
        MutedText = Frozen(mutedText);
        Accent = Frozen(accent);
        AccentText = Frozen(accentText);
        Hover = Frozen(hover);
        Pressed = Frozen(pressed);
        ChipBorder = Frozen(chipBorder);
        ChipHoverBorder = Frozen(chipHoverBorder);
        Divider = Frozen(divider);
        Error = Frozen(error);
        ToolTip = Frozen(toolTip);
        ToolTipText = Frozen(toolTipText);
        ToolTipBorder = Frozen(toolTipBorder);
    }

    public Brush Window { get; }

    public Brush Surface { get; }

    public Brush SurfaceAlt { get; }

    public Brush Border { get; }

    public Brush Text { get; }

    public Brush MutedText { get; }

    /// <summary>Accent as a fill.</summary>
    public Brush Accent { get; }

    /// <summary>Accent as text or a glyph on a surface.</summary>
    public Brush AccentText { get; }

    public Brush Hover { get; }

    public Brush Pressed { get; }

    /// <summary>The hairline around a colour swatch, so a swatch the colour of its surface still shows.</summary>
    public Brush ChipBorder { get; }

    /// <summary>The swatch outline under the pointer or with the keyboard focus.</summary>
    public Brush ChipHoverBorder { get; }

    /// <summary>A one-pixel line between groups of a menu.</summary>
    public Brush Divider { get; }

    /// <summary>Error text on a surface.</summary>
    public Brush Error { get; }

    public Brush ToolTip { get; }

    public Brush ToolTipText { get; }

    public Brush ToolTipBorder { get; }

    /// <summary><c>#RRGGBB</c> or <c>#AARRGGBB</c> as a frozen brush.</summary>
    private static Brush Frozen(string hex)
    {
        var value = uint.Parse(hex.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        var alpha = hex.Length == 9 ? (byte)(value >> 24) : (byte)255;
        var brush = new SolidColorBrush(Color.FromArgb(alpha, (byte)(value >> 16), (byte)(value >> 8), (byte)value));
        brush.Freeze();
        return brush;
    }
}
