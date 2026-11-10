using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using PickIt.Revit.Commands;
using PickIt.Revit.Model;
using PickIt.Revit.Services;
using PickIt.Revit.UI;
using PickIt.Revit.UI.Picking;
using PickIt.Revit.UI.Theming;

namespace PickIt.Revit;

/// <summary>
/// Revit entry point: adds the "Pick-It" panel with one button to the built-in Add-Ins
/// tab and keeps the button's icon in step with the picked colour and Revit's theme.
/// Also the one place the picked colour, the recent colours and the copy of Revit's custom
/// colours live, for the button and its Shift-click menu.
/// </summary>
public sealed class App : IExternalApplication
{
    public const string PanelName = "Pick-It";

    private const string IdleToolTip = "Pick a colour from anywhere on screen.";

    private static PushButton? _button;
    private static IntPtr _mainWindow;

    /// <summary>The current colour and the recent colours, restored at startup.</summary>
    internal static PickStore Store { get; } = new(PickStore.DefaultPath);

    /// <summary>What is known of Revit's 16 custom colours: Revit.ini at startup, then our own writes.</summary>
    internal static RevitPalette Palette { get; } = new();

    /// <summary>Revit's main window: owner of the Shift-click menu, and what gets the foreground back.</summary>
    internal static IntPtr MainWindow => _mainWindow;

    public Result OnStartup(UIControlledApplication application)
    {
        Store.Load();
        _mainWindow = application.MainWindowHandle;
        LoadPalette(application);

        // CreateRibbonPanel without a tab name targets the built-in Add-Ins tab.
        var panel = application.CreateRibbonPanel(PanelName);
        var button = new PushButtonData(
            "PickIt.Pick",
            "Pick-It",
            Assembly.GetExecutingAssembly().Location,
            typeof(PickCommand).FullName)
        {
            ToolTip = IdleToolTip,
            // Without this Revit disables the button while no document is open.
            AvailabilityClassName = typeof(AlwaysAvailable).FullName,
        };
        _button = panel.AddItem(button) as PushButton;
        RefreshButton();
        application.ThemeChanged += OnThemeChanged;
#if DEBUG
        application.Idling += DebugCommands.OnIdling;
#endif
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        application.ThemeChanged -= OnThemeChanged;
#if DEBUG
        application.Idling -= DebugCommands.OnIdling;
#endif
        _button = null;
        return Result.Succeeded;
    }

    /// <summary>The eyedropper picked a colour: it becomes current and joins the recent colours.</summary>
    internal static void Picked(Rgb color)
    {
        Store.Pick(color);
        RefreshButton();
    }

    /// <summary>Makes a colour current without reordering the recent colours (a recent swatch was chosen).</summary>
    internal static void SetCurrent(Rgb color)
    {
        Store.SetCurrent(color);
        RefreshButton();
    }

    /// <summary>Back to the idle icon; the recent colours stay.</summary>
    internal static void Clear()
    {
        Store.Clear();
        RefreshButton();
    }

    /// <summary>Empties the recent colours; the button keeps its colour.</summary>
    internal static void ClearHistory() => Store.ClearHistory();

    /// <summary>
    /// Writes a colour into a custom-colour slot (0 to 15) of Revit's Color dialog and, when
    /// that worked, remembers it as the slot's colour. Needs a valid Revit API context and
    /// no window of ours in the way; never throws.
    /// </summary>
    internal static PaletteWriteResult AddToPalette(int slot, Rgb color)
    {
        var result = PaletteWriter.Write(slot, color);
        if (result.Ok) Palette.Set(slot, color);
        PickLog.Write($"palette slot={slot} rgb={color.R},{color.G},{color.B} {(result.Ok ? "ok" : "fail")} {result.Trace}");
        return result;
    }

    /// <summary>Reads the custom colours Revit saved on its last clean exit; what isn't there stays unknown.</summary>
    private static void LoadPalette(UIControlledApplication application)
    {
        try
        {
            var path = RevitPalette.IniPath(application.ControlledApplication.VersionNumber);
            var known = Palette.Load(path);
            PickLog.Write($"palette read known={known}/{RevitPalette.SlotCount} from {path}");
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Pick-It: Revit's custom colours not read: {ex.Message}");
        }
    }

    private static void OnThemeChanged(object? sender, ThemeChangedEventArgs e)
    {
        // Only the idle icon depends on the theme; redrawing a colour icon is harmless.
        if (e.ThemeChangedType == ThemeType.UITheme) RefreshButton();
    }

    /// <summary>Shows the current colour on the button, or the idle icon of Revit's theme when there is none.</summary>
    private static void RefreshButton()
    {
        if (_button is null) return;
        try
        {
            if (Store.Current is { } color)
            {
                var scale = IconScale();
                _button.LargeImage = ColorIcon.Large(color, scale);
                _button.Image = ColorIcon.Small(color, scale);
                _button.ToolTip = color.ToDisplay();
            }
            else
            {
                var suffix = PickItTheme.Detect() == UiTheme.Dark ? "_dark" : "";
                // A missing dark icon falls back to the light one; with neither, the button has no icon.
                _button.LargeImage = LoadIcon($"pick_32{suffix}.png") ?? LoadIcon("pick_32.png");
                _button.Image = LoadIcon($"pick_16{suffix}.png") ?? LoadIcon("pick_16.png");
                _button.ToolTip = IdleToolTip;
            }
        }
        catch (Exception ex)
        {
            // An icon must never take the add-in (or a pick) down with it.
            Trace.TraceWarning($"Pick-It: ribbon icon not updated: {ex.Message}");
        }
    }

    /// <summary>
    /// Bitmap pixels per icon pixel for the drawn icons, from the display scaling of the
    /// monitor Revit is on (see <see cref="PickerGeometry.IconScaleFor"/>).
    /// </summary>
    private static int IconScale() => PickerGeometry.IconScaleFor(NativeScreen.WindowDpi(_mainWindow));

    /// <summary>Loads an icon shipped next to the DLL; null when the file is missing.</summary>
    private static BitmapImage? LoadIcon(string fileName)
    {
        var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
        var path = Path.Combine(directory, "Resources", "icons", fileName);
        if (!File.Exists(path)) return null;

        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path);
        image.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
        image.EndInit();
        image.Freeze();
        return image;
    }
}
