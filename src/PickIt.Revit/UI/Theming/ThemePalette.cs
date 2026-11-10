using System.Windows;

namespace PickIt.Revit.UI.Theming;

/// <summary>
/// The palette brushes of <see cref="PickItTheme.Current"/> under the family's resource
/// keys (<c>SurfaceBrush</c>, <c>TextBrush</c>, …), merged first into Theme.xaml so its
/// styles and a window's <c>{StaticResource …Brush}</c> resolve to the light or dark
/// colour. A new instance is built each time a window loads Theme.xaml, which is how the
/// theme is applied per window open.
/// </summary>
public sealed class ThemePalette : ResourceDictionary
{
    public ThemePalette()
    {
        var palette = PickItTheme.Palette(PickItTheme.Current);
        Add("WindowBrush", palette.Window);
        Add("SurfaceBrush", palette.Surface);
        Add("SurfaceAltBrush", palette.SurfaceAlt);
        Add("BorderBrush", palette.Border);
        Add("DividerBrush", palette.Divider);
        Add("TextBrush", palette.Text);
        Add("MutedTextBrush", palette.MutedText);
        Add("AccentBrush", palette.Accent);
        Add("AccentForegroundBrush", palette.AccentText);
        Add("ErrorBrush", palette.Error);
        Add("HoverBrush", palette.Hover);
        Add("PressedBrush", palette.Pressed);
        Add("ChipBorderBrush", palette.ChipBorder);
        Add("ChipHoverBorderBrush", palette.ChipHoverBorder);
        Add("ToolTipBrush", palette.ToolTip);
        Add("ToolTipTextBrush", palette.ToolTipText);
        Add("ToolTipBorderBrush", palette.ToolTipBorder);
    }
}
