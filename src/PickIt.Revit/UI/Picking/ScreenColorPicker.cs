using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PickIt.Revit.Model;
using PickIt.Revit.Services;
using PickIt.Revit.UI.Theming;

namespace PickIt.Revit.UI.Picking;

/// <summary>
/// The eyedropper: picks the colour of any pixel on any monitor. Ported from Gantt-It's
/// "Pick from screen"; there it runs from a popup that has to be hidden first, here it
/// runs straight from the ribbon click, so there is no window of ours to hide.
/// <list type="number">
/// <item>One overlay window per monitor is created off-screen; the first is activated so
/// the keyboard is ours before anything is shown, and Revit's own tooltips close.</item>
/// <item>After a short wait (the ribbon button's tooltip, pressed state and hover highlight
/// are gone: the first overlay holds the mouse meanwhile) every monitor is captured, in
/// physical pixels, into a bitmap of its own, and each overlay shows its monitor's capture
/// full screen: snapshot-then-pick, so the magnifier and the result both come from the
/// same frozen bitmaps.</item>
/// <item>Left click picks the pixel under the pointer's hotspot; Esc, right click, Alt+F4
/// or switching to another app cancels, also while the screen is still being captured.
/// Arrow keys move the pointer one pixel (Shift: ten), Enter or Space picks. A change of
/// the display setup (resolution, a monitor plugged in or out) cancels too.</item>
/// </list>
/// Each pick writes one diagnostic line (<see cref="PickLog.Diagnostic"/>): always in Debug
/// builds, and in Release builds when something about the screen looks wrong or the
/// <c>diagnostics.on</c> flag file exists.
/// Every path, exceptions included, closes the overlays and hands the foreground back to
/// Revit's main window (unless the user switched to another app). Clicks and keys that
/// trail the pick (a double click's second click, a held Enter or Esc) are swallowed so
/// they don't land on Revit's ribbon.
/// </summary>
internal sealed class ScreenColorPicker
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan TrailingInput = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan HeldKeyLimit = TimeSpan.FromSeconds(5);

    private readonly IntPtr _revitWindow;
    private readonly UiPalette _palette;
    private readonly List<ScreenPickOverlay> _overlays = [];
    private ScreenSnapshotSet? _snapshots;
    private DispatcherFrame? _frame;
    private bool _finished;
    private (int X, int Y)? _pressedAt;
    private Key? _finishKey;
    private Rgb? _result;
    private Exception? _error;
#if DEBUG
    private Action<ScreenColorPicker>? _whenReady;
    private bool _quiet;
#endif

    private ScreenColorPicker(IntPtr revitWindow, UiPalette palette)
    {
        _revitWindow = revitWindow;
        _palette = palette;
    }

    /// <summary>
    /// Runs a pick over everything on screen, on the calling (Revit UI) thread; returns
    /// when it is over. <paramref name="revitWindow"/> is Revit's main window, which gets
    /// the foreground back afterwards; <paramref name="palette"/> colours the magnifier.
    /// Null when cancelled; throws when the screen couldn't be read (the overlays are
    /// gone by then).
    /// </summary>
    public static Rgb? Pick(IntPtr revitWindow, UiPalette palette) => new ScreenColorPicker(revitWindow, palette).Run();

    private Rgb? Run()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.UnhandledExceptionFilter += OnUnhandledException;
        try
        {
            // Before anything is shown: which monitors have HDR on (empty when Windows doesn't say).
            var hdr = NativeScreen.AdvancedColor();
            bool scopeActive;
            using (var scope = NativeScreen.PerMonitorDpi())
            {
                scopeActive = scope.Active;
                var monitors = NativeScreen.Monitors();
                if (monitors.Count == 0) throw new InvalidOperationException("Windows reported no monitors.");
                foreach (var monitor in monitors)
                {
                    bool? hdrOn = hdr.TryGetValue(monitor.Device, out var enabled) ? enabled : null;
                    var overlay = new ScreenPickOverlay(monitor.Bounds, _palette, hdrOn);
                    overlay.KeyDown += OnKeyDown;
                    overlay.MouseMove += (_, _) => Guard(Track);
                    overlay.MouseLeftButtonDown += OnLeftDown;
                    overlay.MouseLeftButtonUp += OnLeftUp;
                    overlay.MouseRightButtonDown += (_, e) => e.Handled = true;
                    overlay.MouseRightButtonUp += (_, e) =>
                    {
                        e.Handled = true;
                        Finish(null);
                    };
                    overlay.Deactivated += (_, _) => dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(CancelIfInactive));
                    overlay.Closed += (_, _) => Finish(null); // closed from outside: cancel
                    // The overlay's scale factor or size changed under the pointer: place the magnifier again.
                    overlay.GeometryChanged += () => Guard(Track);
                    // The monitors the overlays were made for are gone or changed: nothing sensible to pick from.
                    overlay.DisplayChanged += () => Guard(CancelIfDisplaysChanged);
                    _overlays.Add(overlay);
                    overlay.Show(); // off-screen and tiny until Cover()
                }
            }
            _overlays[0].Activate();
            // The pointer is still over the ribbon button: while an overlay holds the mouse the
            // button is no longer "under" it, so its hover highlight is gone from the capture.
            HoldMouse();

            Wait(Settle);
            // Esc or Alt+Tab during the wait (the first overlay has the keyboard).
            if (_error is not null) throw new InvalidOperationException(_error.Message, _error);
            if (_finished) return _result;
            NativeScreen.FlushCompositor();
            NativeScreen.FlushCompositor();

            var infos = new List<OverlayInfo>();
            ScreenDiagnostics screen;
            string level;
            long captureMs;
            long bytes;
            using (var scope = NativeScreen.PerMonitorDpi())
            {
                scopeActive &= scope.Active;
                level = scopeActive ? scope.Level : "off";
                var watch = Stopwatch.StartNew();
                // One capture per monitor, back to back.
                var shots = NativeScreen.Capture(_overlays.Select(o => o.Monitor).ToList());
                captureMs = watch.ElapsedMilliseconds;
                ReleaseMouse(); // from here on as without it: the overlays get the mouse by covering the screen
                screen = NativeScreen.Diagnostics();
                for (var i = 0; i < _overlays.Count; i++)
                {
                    var shot = shots[i];
                    var image = BitmapSource.Create(shot.Bounds.Width, shot.Bounds.Height, 96, 96,
                        PixelFormats.Bgr32, null, shot.Pixels, shot.Stride);
                    image.Freeze();
                    _overlays[i].ShowScreenshot(image);
                    infos.Add(_overlays[i].Cover());
                }
                var set = new ScreenSnapshotSet(shots);
                bytes = set.ByteCount;
                _snapshots = set;
            }
            LogDiagnostics(level, screen, infos, captureMs, bytes);

            ActivateOverlayUnderPointer();
            Track();
#if DEBUG
            // A Debug command drives this pick instead of the user.
            if (!_finished && _whenReady is { } ready) Guard(() => ready(this));
#endif

            // Never leave topmost overlays up without the keyboard: if another app holds the
            // foreground now, Esc couldn't reach them and the next click would pick by accident.
            if (!_finished && !_overlays.Any(o => o.IsActive)) Finish(null);
            if (!_finished)
            {
                _frame = new DispatcherFrame();
                Dispatcher.PushFrame(_frame);
            }
            if (_error is not null) throw new InvalidOperationException(_error.Message, _error);

            // Revit's views are native windows, outside the WPF filter in SwallowTrailingInput:
            // keep the frozen screen up for a moment so a double click's second click or a
            // key's auto-repeat lands on the overlay instead of selecting something in a view.
            if (_overlays.Any(o => o.IsActive))
            {
                foreach (var overlay in _overlays) overlay.HideLoupe();
                Wait(TrailingInput);
            }
            return _result;
        }
        finally
        {
            dispatcher.UnhandledExceptionFilter -= OnUnhandledException;
            Restore();
            SwallowTrailingInput(_finishKey);
        }
    }

    // ---- input ---------------------------------------------------------------------------

    /// <summary>Moves the magnifier to the pointer, on whichever monitor it is.</summary>
    private void Track()
    {
        if (_snapshots is null || _finished) return;
#if DEBUG
        if (_quiet) return;
#endif
        var (x, y) = Pointer();
        var index = PixelRect.IndexAt(_overlays.Select(o => o.Monitor).ToList(), x, y);
        for (var i = 0; i < _overlays.Count; i++)
        {
            // In a gap between monitors no overlay shows the magnifier.
            if (i == index && _overlays[i].Monitor.Contains(x, y)) _overlays[i].ShowLoupe(_snapshots, x, y);
            else _overlays[i].HideLoupe();
        }
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Guard(() =>
        {
            if (_snapshots is null) return; // still capturing
            _pressedAt = Pointer();
            ((UIElement)sender).CaptureMouse();
        });
    }

    /// <summary>
    /// Windows announces display changes for more than layout changes (sleep and wake, driver
    /// events, remote sessions): only a different set of monitor rectangles ends the pick.
    /// Every overlay hears the message; the first one to act finishes it for all.
    /// </summary>
    private void CancelIfDisplaysChanged()
    {
        if (_finished) return;
        List<PixelRect> now;
        using (NativeScreen.PerMonitorDpi())
        {
            now = NativeScreen.Monitors().Select(m => m.Bounds).ToList();
        }
        var then = _overlays.Select(o => o.Monitor).ToList();
        if (now.Count == then.Count && now.All(then.Contains)) return;
        PickLog.Write("display changed: pick cancelled");
        Finish(null);
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Guard(() =>
        {
            // Still capturing: the first overlay is holding the mouse on purpose, leave it.
            if (_snapshots is null) return;
            ((UIElement)sender).ReleaseMouseCapture();
            if (_pressedAt is not { } at) return; // the press started before the overlay was up
            PickAt(at.X, at.Y);
        });
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.System)
        {
            // Alt+F4 cancels; other Alt combinations are left alone.
            if (e.SystemKey != Key.F4) return;
            e.Handled = true;
            _finishKey = Key.F4;
            Finish(null);
            return;
        }

        e.Handled = true;
        if (e.IsRepeat && e.Key is Key.Enter or Key.Space or Key.Escape) return;
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        Guard(() =>
        {
            switch (e.Key)
            {
                case Key.Escape:
                    _finishKey = e.Key;
                    Finish(null);
                    break;
                case Key.Enter:
                case Key.Space:
                    if (_snapshots is null) break; // still capturing
                    _finishKey = e.Key;
                    var (x, y) = Pointer();
                    PickAt(x, y);
                    break;
                case Key.Left: Nudge(-step, 0); break;
                case Key.Right: Nudge(step, 0); break;
                case Key.Up: Nudge(0, -step); break;
                case Key.Down: Nudge(0, step); break;
            }
        });
    }

    private void Nudge(int dx, int dy)
    {
        if (_snapshots is null) return;
        var (x, y) = Pointer();
        var (nx, ny) = PickerGeometry.Nudge(_snapshots.Bounds, x, y, dx, dy);
        using (NativeScreen.PerMonitorDpi()) NativeScreen.SetCursorPosition(nx, ny);
        Track();
    }

    private void PickAt(int x, int y)
    {
        // The pointer can sit in a gap between monitors of different sizes: no capture holds
        // that point, so there is nothing to pick there.
        if (_snapshots?.ColorAt(x, y) is { } color) Finish(color);
    }

    /// <summary>
    /// Another app took the foreground (Alt+Tab, Win key): cancel instead of leaving overlays
    /// up. Also during the capture, so the pick never starts in the background.
    /// </summary>
    private void CancelIfInactive()
    {
        if (_finished || _overlays.Any(o => o.IsActive)) return;
        Finish(null);
    }

    /// <summary>Ends the pick once; a cancel before the overlays are up is kept and honoured.</summary>
    private void Finish(Rgb? color)
    {
        if (_finished) return;
        _finished = true;
        _result = color;
        if (_frame is not null) _frame.Continue = false;
        _frame = null;
    }

    /// <summary>Input handlers run inside Revit: any failure ends the pick and is reported by the caller.</summary>
    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _error ??= ex;
            Finish(null);
        }
    }

    /// <summary>
    /// Any other unhandled exception on the UI thread while picking: end the pick and take
    /// the overlays down first, so whatever reports the error doesn't open hidden under
    /// topmost windows.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionFilterEventArgs e)
    {
        Finish(null);
        Restore();
    }

    private static (int X, int Y) Pointer()
    {
        using (NativeScreen.PerMonitorDpi()) return NativeScreen.CursorPosition();
    }

    /// <summary>
    /// Gives the mouse to the first overlay (off-screen, already active) until the screen has
    /// been captured. WPF then no longer counts the pointer as over Revit's ribbon button,
    /// which drops its hover highlight, without moving the pointer. Best effort.
    /// </summary>
    private void HoldMouse() => Try(() => Mouse.Capture(_overlays[0]));

    /// <summary>Lets the mouse go again if an overlay of ours (still) holds it.</summary>
    private static void ReleaseMouse() => Try(() =>
    {
        if (Mouse.Captured is ScreenPickOverlay) Mouse.Capture(null);
    });

    /// <summary>
    /// One line per pick about how the screen looked to us. Anomalies are the cases where a
    /// pointer position may not be the pixel it seems to be: the per-monitor DPI scope didn't
    /// take, GDI reports a scaled screen, or an overlay isn't exactly its monitor.
    /// </summary>
    private static void LogDiagnostics(string level, ScreenDiagnostics screen, IReadOnlyList<OverlayInfo> overlays, long captureMs, long bytes)
    {
        try
        {
            var anomalies = new List<string>();
            if (level == "off") anomalies.Add("scope-inactive");
            if (screen.Virtualised) anomalies.Add("virtualised-dc");
            if (overlays.Any(o => o.Mismatch)) anomalies.Add("overlay-size");

            var anomaly = anomalies.Count == 0 ? "none" : string.Join("+", anomalies);
            var parts = new List<string>
            {
                FormattableString.Invariant(
                    $"pickdiag anomaly={anomaly} scope={level} thread={screen.ThreadAwareness} sysdpi={screen.SystemDpi} horzres={screen.HorzRes} desktophorzres={screen.DesktopHorzRes} capture_ms={captureMs} bytes={bytes} monitors={overlays.Count}"),
            };
            for (var i = 0; i < overlays.Count; i++)
            {
                var o = overlays[i];
                var m = o.Monitor;
                var hdr = o.Hdr switch { true => "on", false => "off", _ => "unknown" };
                parts.Add(FormattableString.Invariant(
                    $"m{i} rect={m.Left},{m.Top},{m.Width}x{m.Height} client={o.ClientWidth}x{o.ClientHeight} dpi={o.Dpi} m11={o.Scale:0.####} hdr={hdr}"));
            }
            PickLog.Diagnostic(string.Join(" | ", parts), anomalies.Count > 0);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Pick-It: eyedropper diagnostics: {ex.Message}");
        }
    }

    private void ActivateOverlayUnderPointer()
    {
        var (x, y) = Pointer();
        var index = PixelRect.IndexAt(_overlays.Select(o => o.Monitor).ToList(), x, y);
        var overlay = _overlays[Math.Max(0, index)];
        overlay.Activate();
        overlay.Focus();
    }

    // ---- taking the overlays down -----------------------------------------------------------

    /// <summary>
    /// Never throws, safe to call twice: closes the overlays and, when one of them still
    /// had the foreground (the user didn't switch to another app), gives it to Revit.
    /// </summary>
    private void Restore()
    {
        if (_frame is not null) _frame.Continue = false;
        _frame = null;

        var overlays = _overlays.ToList();
        _overlays.Clear();
        ReleaseMouse();
        var ours = false;
        Try(() =>
        {
            var foreground = NativeScreen.ForegroundWindow();
            ours = foreground != IntPtr.Zero && overlays.Any(o => o.Handle == foreground);
        });
        // Hand over while an overlay still holds the foreground: afterwards Windows would refuse.
        if (ours && _revitWindow != IntPtr.Zero) Try(() => NativeScreen.Activate(_revitWindow));
        foreach (var overlay in overlays) Try(overlay.Close);
        _snapshots = null;
    }

#if DEBUG
    // ---- Debug command hooks (Services/DebugCommands.cs) -------------------------------------

    /// <summary>
    /// Debug builds: a real pick session (overlays, capture, diagnostics) that picks physical
    /// pixel (x, y) by itself once the overlays are up. Null when there is no pixel there
    /// (a gap, or off every monitor) or the pick was cancelled first.
    /// </summary>
    internal static Rgb? DebugPickAt(IntPtr revitWindow, UiPalette palette, int x, int y)
    {
        var picker = new ScreenColorPicker(revitWindow, palette);
        picker._whenReady = p =>
        {
            p.PickAt(x, y);
            if (p._finished) return;
            PickLog.Write($"pick at={x},{y}: no monitor has that pixel");
            p.Finish(null);
        };
        return picker.Run();
    }

    /// <summary>
    /// Debug builds: opens the overlays as a pick would, hides the magnifiers, copies the
    /// screen again and compares it with the frozen captures, monitor by monitor, then
    /// cancels. Logs <c>overlaycheck monitor=i rect=… diff=N</c>: 0 means the overlay draws
    /// its capture one to one.
    /// </summary>
    internal static void DebugOverlayCheck(IntPtr revitWindow, UiPalette palette)
    {
        var picker = new ScreenColorPicker(revitWindow, palette);
        picker._whenReady = p => p.OverlayCheck();
        picker.Run();
    }

    private void OverlayCheck()
    {
        _quiet = true; // no magnifier while the screen is compared
        foreach (var overlay in _overlays) overlay.HideLoupe();
        Wait(TimeSpan.FromMilliseconds(400)); // the overlays draw their screenshots
        NativeScreen.FlushCompositor();
        NativeScreen.FlushCompositor();

        if (_snapshots is not { } frozen || _overlays.Count == 0)
        {
            PickLog.Write("overlaycheck: the overlays were taken down first");
            return;
        }
        List<ScreenSnapshot> now;
        using (NativeScreen.PerMonitorDpi()) now = NativeScreen.Capture(frozen.Snapshots.Select(s => s.Bounds).ToList());
        for (var i = 0; i < now.Count; i++)
        {
            var m = now[i].Bounds;
            var diff = frozen.Snapshots[i].DifferenceCount(now[i], out var fx, out var fy);
            var first = diff == 0 ? "" : $" first={fx},{fy} was={frozen.ColorAt(fx, fy)} now={now[i].ColorAt(fx, fy)}";
            PickLog.Write($"overlaycheck monitor={i} rect={m.Left},{m.Top},{m.Width}x{m.Height} diff={diff}{first}");
        }
        Finish(null);
    }
#endif

    /// <summary>
    /// For a moment after the pick, mouse buttons and key presses don't reach Revit's WPF
    /// UI (the ribbon: a double click's second click would start another pick), and a key
    /// that ended the pick is ignored until it is released (auto-repeat of a held Enter or
    /// Esc). Revit's native views are outside WPF's input manager and are not covered.
    /// Also used after the Shift-click menu closes, which sits over the ribbon too.
    /// </summary>
    internal static void SwallowTrailingInput(Key? heldKey)
    {
        var started = DateTime.UtcNow;
        bool Active() =>
            DateTime.UtcNow - started < TrailingInput
            || (heldKey is { } k && Keyboard.IsKeyDown(k) && DateTime.UtcNow - started < HeldKeyLimit);

        PreProcessInputEventHandler filter = (_, e) =>
        {
            var input = e.StagingItem.Input;
            if (DateTime.UtcNow - started < TrailingInput)
            {
                if (input is MouseButtonEventArgs || input is KeyEventArgs { IsDown: true }) e.Cancel();
            }
            else if (heldKey is { } k && input is KeyEventArgs { IsDown: true } key && (key.Key == Key.System ? key.SystemKey : key.Key) == k)
            {
                e.Cancel();
            }
        };

        try
        {
            InputManager.Current.PreProcessInput += filter;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Pick-It: eyedropper input filter: {ex.Message}");
            return;
        }

        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            if (Active()) return;
            timer.Stop();
            InputManager.Current.PreProcessInput -= filter;
        };
        timer.Start();
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Pick-It: eyedropper cleanup: {ex.Message}");
        }
    }

    /// <summary>Keeps the message loop running for <paramref name="duration"/> (windows repaint, DWM composes).</summary>
    private static void Wait(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
