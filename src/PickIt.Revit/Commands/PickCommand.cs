using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using PickIt.Revit.Services;
using PickIt.Revit.UI.MenuPopup;
using PickIt.Revit.UI.Picking;
using PickIt.Revit.UI.Theming;

namespace PickIt.Revit.Commands;

/// <summary>
/// The ribbon button. A plain click runs the eyedropper and puts the picked colour on
/// the button; a cancelled pick keeps the previous colour. Shift-click opens the menu
/// at the pointer (recent colours, Clear, Clear history, Add to palette…).
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class PickCommand : IExternalCommand
{
    /// <summary>
    /// True while a pick or the menu is running. Both pump messages inside Execute, so a
    /// double click on the button delivers its second click as a nested Execute: that one
    /// must not start another pick (or a second menu) on top of the first.
    /// </summary>
    private static bool _busy;

    public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
    {
        if (_busy) return Result.Cancelled;
        _busy = true;
        try
        {
            return Run(commandData, ref message);
        }
        finally
        {
            _busy = false;
        }
    }

    private static Result Run(ExternalCommandData commandData, ref string message)
    {
        // Read first: the key state at the click, before anything else can change it.
        var modifiers = System.Windows.Input.Keyboard.Modifiers;
        if ((modifiers & System.Windows.Input.ModifierKeys.Shift) != 0)
        {
            try
            {
                // Returns when the menu has closed; "Add to palette…" runs in here, in this API context.
                PickMenuWindow.Run(commandData.Application.MainWindowHandle, new MenuRequest());
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                PickLog.Write($"error menu {ex.GetType().Name}: {ex.Message}");
                message = $"Couldn't show the Pick-It menu: {ex.Message}";
                return Result.Failed;
            }
        }

        try
        {
            var palette = PickItTheme.Palette(PickItTheme.Detect());
            var picked = ScreenColorPicker.Pick(commandData.Application.MainWindowHandle, palette);
            if (picked is not { } color)
            {
                PickLog.Write("cancel");
                return Result.Cancelled;
            }

            App.Picked(color);
            PickLog.Write($"pick rgb={color.R},{color.G},{color.B} hex={color.ToHex()}");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            PickLog.Write($"error {ex.GetType().Name}: {ex.Message}");
            message = $"Couldn't pick from the screen: {ex.Message}";
            return Result.Failed;
        }
    }
}

/// <summary>Keeps the button enabled with no document open (and in every view and selection).</summary>
public sealed class AlwaysAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, Autodesk.Revit.DB.CategorySet selectedCategories) => true;
}
