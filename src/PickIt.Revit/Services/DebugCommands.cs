#if DEBUG
using System.Globalization;
using System.IO;
using Autodesk.Revit.UI.Events;
using PickIt.Revit.Model;
using PickIt.Revit.UI.MenuPopup;
using PickIt.Revit.UI.Picking;
using PickIt.Revit.UI.Theming;

namespace PickIt.Revit.Services;

/// <summary>
/// Debug builds only: lets a tester drive Pick-It from outside Revit, without synthesising
/// mouse or keyboard input. Revit's Idling event polls
/// <c>%LOCALAPPDATA%\Temp\PickIt\commands.txt</c>; each line is one command, run in order
/// in a valid API context, and logged to <c>pick.log</c> (<c>&gt; line</c>, then the
/// command's own lines). The file is deleted before its commands run and
/// <c>commands.done</c> is written after the last one.
/// <code>
/// set rgb=47,106,208             make a colour current (recent colours untouched)
/// pick rgb=47,106,208            as if the eyedropper had picked it (joins the recent colours)
/// pick at=-100,250               a real eyedropper session (overlays, capture, diagnostics) that
///                                picks that physical screen pixel by itself; logs the usual
///                                "pick rgb=… hex=…" line, or "cancel" when no monitor has the pixel
/// overlaycheck                   opens the eyedropper's overlays, hides the magnifiers, copies the
///                                screen again and compares it with the frozen captures; logs
///                                "overlaycheck monitor=i rect=… diff=N" per monitor (0 = the frozen
///                                image is drawn one to one), then cancels
/// clear                          back to the idle icon
/// clearhistory                   empty the recent colours
/// palette slot=5 rgb=250,250,10  write a colour into custom-colour slot 0..15
/// menu [view=palette] [close=3000]
///                                open the Shift-click menu at the centre of Revit's screen;
///                                close=ms closes it by itself (otherwise Esc or a click does)
/// # comment
/// </code>
/// A file that is still being written (locked, or modified a moment ago) is left for the
/// next tick. The poll asks Revit to keep raising Idling, which keeps one core busy: one
/// more reason this exists in Debug builds only.
/// </summary>
internal static class DebugCommands
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(250);

    private static bool _busy;

    private static string CommandsPath => Path.Combine(PickLog.Folder, "commands.txt");

    private static string DonePath => Path.Combine(PickLog.Folder, "commands.done");

    public static void OnIdling(object? sender, IdlingEventArgs e)
    {
        e.SetRaiseWithoutDelay();
        if (_busy) return; // a command (the menu) is pumping messages
        _busy = true;
        try
        {
            if (Read() is not { } lines) return;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                PickLog.Write($"> {line}");
                try
                {
                    Run(line);
                }
                catch (Exception ex)
                {
                    PickLog.Write($"command error {ex.GetType().Name}: {ex.Message}");
                }
            }
            File.WriteAllText(DonePath, DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            PickLog.Write($"commands error {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>The command file's lines, removing the file; null when there is none or it isn't ready.</summary>
    private static string[]? Read()
    {
        try
        {
            var path = CommandsPath;
            if (!File.Exists(path)) return null;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < Quiet) return null;
            var lines = File.ReadAllLines(path);
            // Deleted before anything runs: if that fails the commands would run again next tick.
            File.Delete(path);
            File.Delete(DonePath);
            return lines;
        }
        catch (IOException)
        {
            return null; // still being written: next tick
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Run(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts.Skip(1))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2) args[pair[0]] = pair[1];
        }

        switch (parts[0].ToLowerInvariant())
        {
            case "set":
                App.SetCurrent(Color(args));
                PickLog.Write("set done");
                break;
            case "pick":
                if (args.TryGetValue("at", out var at))
                {
                    PickAt(at);
                    break;
                }
                App.Picked(Color(args));
                PickLog.Write("pick done");
                break;
            case "overlaycheck":
                // Logs its own "overlaycheck monitor=… diff=…" lines.
                ScreenColorPicker.DebugOverlayCheck(App.MainWindow, PickItTheme.Palette(PickItTheme.Detect()));
                PickLog.Write("overlaycheck done");
                break;
            case "clear":
                App.Clear();
                PickLog.Write("clear done");
                break;
            case "clearhistory":
                App.ClearHistory();
                PickLog.Write("clearhistory done");
                break;
            case "palette":
                // Logs its own "palette slot=… ok|fail …" line.
                App.AddToPalette(int.Parse(Required(args, "slot"), CultureInfo.InvariantCulture), Color(args));
                break;
            case "menu":
                var palette = args.TryGetValue("view", out var view) && view.Equals("palette", StringComparison.OrdinalIgnoreCase);
                var close = args.TryGetValue("close", out var ms) ? int.Parse(ms, CultureInfo.InvariantCulture) : 0;
                PickMenuWindow.Run(App.MainWindow, new MenuRequest(AtCentre: true, PaletteView: palette, CloseAfterMs: close));
                break;
            default:
                PickLog.Write("unknown command");
                break;
        }
    }

    /// <summary>A real pick session that picks the physical pixel <c>x,y</c>; logged as the ribbon button logs a pick.</summary>
    private static void PickAt(string at)
    {
        var values = at.Split(',');
        if (values.Length != 2) throw new FormatException("at needs two values: at=x,y");
        var x = int.Parse(values[0], CultureInfo.InvariantCulture);
        var y = int.Parse(values[1], CultureInfo.InvariantCulture);
        var picked = ScreenColorPicker.DebugPickAt(App.MainWindow, PickItTheme.Palette(PickItTheme.Detect()), x, y);
        if (picked is not { } color)
        {
            PickLog.Write("cancel");
            return;
        }
        App.Picked(color);
        PickLog.Write($"pick rgb={color.R},{color.G},{color.B} hex={color.ToHex()}");
    }

    private static Rgb Color(Dictionary<string, string> args)
    {
        var values = Required(args, "rgb").Split(',');
        if (values.Length != 3) throw new FormatException("rgb needs three values: rgb=r,g,b");
        return new Rgb(
            byte.Parse(values[0], CultureInfo.InvariantCulture),
            byte.Parse(values[1], CultureInfo.InvariantCulture),
            byte.Parse(values[2], CultureInfo.InvariantCulture));
    }

    private static string Required(Dictionary<string, string> args, string name) =>
        args.TryGetValue(name, out var value) ? value : throw new FormatException($"{name}= is missing");
}
#endif
