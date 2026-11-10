using System.Windows;
using Autodesk.Revit.UI;
using Microsoft.Win32;

namespace PickIt.Revit.UI.Theming;

/// <summary>Light or dark; follows Revit's UI theme.</summary>
public enum UiTheme
{
    Light,
    Dark,
}

/// <summary>Reads Revit's UI theme and holds the family palette (docs/DESIGN.md, "Core palette").</summary>
public static class PickItTheme
{
    /// <summary>
    /// The theme of the window being opened: <see cref="ThemePalette"/> (merged by Theme.xaml)
    /// reads it while the window loads its XAML. Light until the first <see cref="Refresh"/>.
    /// </summary>
    public static UiTheme Current { get; private set; } = UiTheme.Light;

    /// <summary>Reads Revit's theme again; call before creating a themed window.</summary>
    public static void Refresh() => Current = Detect();

    /// <summary>
    /// Revit's effective UI theme. With Revit's "Use system setting" option on
    /// (<see cref="UIThemeManager.FollowSystemColorTheme"/>) the Windows app theme is
    /// read directly, so the result is right whether or not
    /// <see cref="UIThemeManager.CurrentTheme"/> resolves the system setting itself.
    /// Anything unreadable, and Windows high contrast, falls back to light.
    /// </summary>
    public static UiTheme Detect()
    {
#if DEBUG
        // Debug builds only: PICKIT_THEME=dark|light forces a theme for screenshots.
        switch (Environment.GetEnvironmentVariable("PICKIT_THEME")?.Trim().ToLowerInvariant())
        {
            case "dark": return UiTheme.Dark;
            case "light": return UiTheme.Light;
        }
#endif
        if (SystemParameters.HighContrast) return UiTheme.Light;
        try
        {
            if (UIThemeManager.FollowSystemColorTheme && WindowsAppTheme() is { } system) return system;
            return UIThemeManager.CurrentTheme == UITheme.Dark ? UiTheme.Dark : UiTheme.Light;
        }
        catch (Exception)
        {
            return UiTheme.Light;
        }
    }

    /// <summary>The palette of <paramref name="theme"/>.</summary>
    public static UiPalette Palette(UiTheme theme) => theme == UiTheme.Dark ? UiPalette.Dark : UiPalette.Light;

    /// <summary>The Windows "app mode" (Settings › Personalization › Colors), or null when it can't be read.</summary>
    private static UiTheme? WindowsAppTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light ? (light == 0 ? UiTheme.Dark : UiTheme.Light) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
