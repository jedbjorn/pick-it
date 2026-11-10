using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PickIt.Revit.Model;
using PickIt.Revit.Services;
using PickIt.Revit.UI.Picking;
using PickIt.Revit.UI.Theming;

namespace PickIt.Revit.UI.MenuPopup;

/// <summary>
/// How the menu is opened. The defaults are the Shift-click: at the pointer, on the menu
/// view, open until dismissed. The rest is for the Debug command file.
/// </summary>
/// <param name="AtCentre">At the centre of the screen Revit is on, instead of at the pointer.</param>
/// <param name="PaletteView">Start on the custom-colour grid.</param>
/// <param name="CloseAfterMs">Close by itself after this long; 0 to stay until dismissed.</param>
internal sealed record MenuRequest(bool AtCentre = false, bool PaletteView = false, int CloseAfterMs = 0);

/// <summary>
/// The Shift-click menu: a small themed popup at the pointer.
/// <list type="bullet">
/// <item>Header: the current colour, the one the menu acts on.</item>
/// <item>Recent colours, two rows of five, newest first; a click makes one current and
/// closes the menu.</item>
/// <item>Clear, Clear history, Add to palette…; the last switches to a second view with
/// Revit's 16 custom colours as two rows of eight, as in Revit's Color dialog. A click
/// on a slot writes the current colour into it and reports how that went.</item>
/// </list>
/// <para>
/// It runs inside the command (<see cref="Run"/> returns when the menu has closed), so
/// the palette write happens in the command's API context: the slot click ends the
/// message loop, the menu hides, <see cref="PaletteWriter"/> runs Revit's dialog, and
/// the menu comes back with the result.
/// </para>
/// <para>
/// Dismissal: Esc (from the palette view it goes back first), a click anywhere outside,
/// losing activation, Alt+F4. While open the window holds the mouse the way a menu does,
/// so the click that dismisses it doesn't also land on Revit.
/// </para>
/// </summary>
public partial class PickMenuWindow : Window
{
    /// <summary>The transparent margin around the card (room for its shadow), as in the XAML.</summary>
    private const double ShadowMargin = 10;

    /// <summary>How long the "saved" result stays up before the menu closes by itself.</summary>
    private static readonly TimeSpan SuccessLinger = TimeSpan.FromMilliseconds(1200);

    /// <summary>After opening, an activation Revit takes back this soon is taken again instead of closing.</summary>
    private static readonly TimeSpan OpeningGrace = TimeSpan.FromMilliseconds(400);

    private static readonly FontFamily IconFont = new("Segoe MDL2 Assets");

    private readonly IntPtr _revitWindow;
    private readonly Stopwatch _sinceShown = new();
    private IntPtr _handle;
    private DispatcherFrame? _frame;
    private DispatcherTimer? _closeTimer;
    private DispatcherTimer? _inactiveTimer;
    private int _inactiveTicks;
    private bool _done;
    private bool _closed;
    private bool _suspended;
    private bool _reactivated;
    private bool _paletteView;
    private bool _openedOnPalette;
    private bool _keyboardUsed;
    private int? _pendingSlot;

    private PickMenuWindow(IntPtr revitWindow)
    {
        InitializeComponent();
        _revitWindow = revitWindow;
        // Owned by Revit: stays above it without being topmost, and hands activation back when it closes.
        if (revitWindow != IntPtr.Zero) new WindowInteropHelper(this).Owner = revitWindow;

        SourceInitialized += (_, _) => _handle = new WindowInteropHelper(this).Handle;
        Deactivated += (_, _) => WhenDeactivated();
        Closed += (_, _) =>
        {
            _closed = true;
            Finish();
        };
        LostMouseCapture += (_, _) => OnLostCapture();
        AddHandler(Mouse.PreviewMouseDownOutsideCapturedElementEvent, new MouseButtonEventHandler(OnMouseDownOutside));
    }

    /// <summary>
    /// Shows the menu and returns when it has closed, on the calling (Revit UI) thread and
    /// in its API context. <paramref name="revitWindow"/> is Revit's main window.
    /// </summary>
    internal static void Run(IntPtr revitWindow, MenuRequest request)
    {
        PickItTheme.Refresh(); // before the window loads Theme.xaml
        var window = new PickMenuWindow(revitWindow);
        try
        {
            window.Loop(request);
        }
        finally
        {
            window.TakeDown();
            // The menu sat over the ribbon: a double click's second click mustn't start a pick.
            ScreenColorPicker.SwallowTrailingInput(null);
        }
    }

    private void Loop(MenuRequest request)
    {
        _openedOnPalette = request.PaletteView;
        RefreshCurrent();
        RefreshRecent();
        RefreshPalette(null);
        if (request.PaletteView) ShowView(palette: true);

        Show(); // off-screen (Left/Top in the XAML) until placed
        UpdateLayout();
        Place(request.AtCentre);
        Place(request.AtCentre); // again: landing on a monitor with another scale factor resizes the window
        Activate();
        _sinceShown.Restart();
        FocusFirst();
        TakeCapture();
        PickLog.Write($"menu open view={(_paletteView ? "palette" : "main")} active={IsActive} theme={PickItTheme.Current}");

        if (request.CloseAfterMs > 0)
        {
            StartCloseTimer(TimeSpan.FromMilliseconds(request.CloseAfterMs));
        }
        else
        {
            // Without activation neither Esc nor "lost activation" can close the menu: don't linger.
            _inactiveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
            _inactiveTimer.Tick += (_, _) => CloseIfLeftInactive();
            _inactiveTimer.Start();
        }

        while (!_done)
        {
            _frame = new DispatcherFrame();
            Dispatcher.PushFrame(_frame);
            _frame = null;
            if (_done || _pendingSlot is not { } slot) continue;
            _pendingSlot = null;
            WriteSlot(slot);
        }
        PickLog.Write("menu close");
    }

    // ---- content ----------------------------------------------------------------------------

    private void RefreshCurrent()
    {
        var current = App.Store.Current;
        if (current is { } color)
        {
            CurrentChip.Background = Fill(color);
            CurrentChip.BorderBrush = (Brush)FindResource("ChipBorderBrush");
            CurrentText.Text = $"RGB {color.R}, {color.G}, {color.B}";
            CurrentDetail.Text = color.ToHex();
            CurrentDetail.FontFamily = new FontFamily("Consolas");
        }
        else
        {
            CurrentChip.Background = (Brush)FindResource("SurfaceAltBrush");
            CurrentChip.BorderBrush = (Brush)FindResource("DividerBrush");
            CurrentText.Text = "No colour picked";
            CurrentDetail.Text = "Click Pick-It without Shift to pick one from the screen.";
        }
        ClearButton.IsEnabled = current is not null;
        AddToPaletteButton.IsEnabled = current is not null;
    }

    /// <summary>Ten cells, newest first: a swatch per recent colour, quiet placeholders for the rest.</summary>
    private void RefreshRecent()
    {
        RecentGrid.Children.Clear();
        var recent = App.Store.Recent;
        var swatch = (Style)FindResource("SwatchButton");
        var placeholder = (Style)FindResource("SwatchPlaceholder");
        for (var i = 0; i < PickStore.MaxRecent; i++)
        {
            if (i >= recent.Count)
            {
                RecentGrid.Children.Add(new Border { Style = placeholder });
                continue;
            }
            var color = recent[i];
            var button = new Button
            {
                Style = swatch,
                Background = Fill(color),
                ToolTip = color.ToDisplay(),
                Tag = color,
            };
            AutomationProperties.SetAutomationId(button, $"PickMenu.Recent.{i}");
            AutomationProperties.SetName(button, color.ToDisplay());
            button.Click += OnRecentClick;
            RecentGrid.Children.Add(button);
        }
        ClearHistoryButton.IsEnabled = recent.Count > 0;
    }

    /// <summary>
    /// Revit's 16 custom colours, slot = row × 8 + column. A slot whose colour isn't known
    /// shows "?" rather than a guess; <paramref name="justWritten"/> gets a check mark.
    /// </summary>
    private void RefreshPalette(int? justWritten)
    {
        PaletteGrid.Children.Clear();
        var swatch = (Style)FindResource("SwatchButton");
        var canWrite = App.Store.Current is not null;
        for (var slot = 0; slot < RevitPalette.SlotCount; slot++)
        {
            var button = new Button { Style = swatch, Height = 22, Tag = slot, IsEnabled = canWrite };
            string name;
            if (App.Palette[slot] is { } color)
            {
                name = $"Slot {slot + 1} · {color.ToDisplay()}";
                button.Background = Fill(color);
                if (slot == justWritten)
                {
                    button.Content = new TextBlock
                    {
                        Text = "",
                        FontFamily = IconFont,
                        FontSize = 11,
                        Foreground = color.PrefersBlackText() ? Brushes.Black : Brushes.White,
                    };
                }
            }
            else
            {
                name = $"Slot {slot + 1} · colour not known";
                button.Background = (Brush)FindResource("SurfaceAltBrush");
                button.Content = "?";
            }
            button.ToolTip = name;
            AutomationProperties.SetAutomationId(button, $"PickMenu.Slot.{slot}");
            AutomationProperties.SetName(button, name);
            button.Click += OnSlotClick;
            PaletteGrid.Children.Add(button);
        }
    }

    private void ShowView(bool palette)
    {
        _paletteView = palette;
        MainView.Visibility = palette ? Visibility.Collapsed : Visibility.Visible;
        PaletteView.Visibility = palette ? Visibility.Visible : Visibility.Collapsed;
        if (palette)
        {
            PaletteHint.Visibility = Visibility.Visible;
            StatusRow.Visibility = Visibility.Collapsed;
        }
        if (!IsVisible) return;
        // The other view has another height: keep the card on the screen, and the keyboard in it.
        UpdateLayout();
        KeepOnScreen();
        FocusFirst();
    }

    private void ShowStatus(PaletteWriteResult result, int slot)
    {
        PaletteHint.Visibility = Visibility.Collapsed;
        StatusRow.Visibility = Visibility.Visible;
        StatusGlyph.Text = result.Ok ? "" : "";
        StatusGlyph.Foreground = (Brush)FindResource(result.Ok ? "AccentForegroundBrush" : "ErrorBrush");
        StatusText.Text = result.Ok ? $"Saved to slot {slot + 1}." : $"Not saved. {result.Message}";
    }

    private static SolidColorBrush Fill(Rgb color)
    {
        var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    // ---- actions ----------------------------------------------------------------------------

    private void OnRecentClick(object sender, RoutedEventArgs e)
    {
        if (_suspended || sender is not Button { Tag: Rgb color }) return;
        App.SetCurrent(color);
        PickLog.Write($"menu recent rgb={color.R},{color.G},{color.B} hex={color.ToHex()}");
        Finish();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (_suspended) return;
        App.Clear();
        PickLog.Write("menu clear");
        Finish();
    }

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        if (_suspended) return;
        App.ClearHistory();
        PickLog.Write("menu clear-history");
        Finish();
    }

    private void OnAddToPalette(object sender, RoutedEventArgs e)
    {
        if (_suspended) return;
        PickLog.Write("menu add-to-palette");
        StopCloseTimer();
        ShowView(palette: true);
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_suspended) return;
        StopCloseTimer();
        ShowView(palette: false);
    }

    /// <summary>The write itself can't run in here (a nested message loop): hand the slot to <see cref="Loop"/>.</summary>
    private void OnSlotClick(object sender, RoutedEventArgs e)
    {
        if (_suspended || _done || sender is not Button { Tag: int slot }) return;
        _pendingSlot = slot;
        if (_frame is not null) _frame.Continue = false;
    }

    /// <summary>
    /// Out of the way, Revit's dialog driven, back with the result. On success the menu
    /// closes by itself shortly after; on failure it stays, with the reason, for another try.
    /// </summary>
    private void WriteSlot(int slot)
    {
        if (App.Store.Current is not { } color) return;
        _suspended = true;
        StopCloseTimer();
        ReleaseCapture();
        Hide();

        var result = App.AddToPalette(slot, color);

        if (_closed)
        {
            Finish();
            return;
        }
        RefreshPalette(result.Ok ? slot : null);
        ShowStatus(result, slot);
        Show();
        UpdateLayout();
        KeepOnScreen();
        Activate();
        _inactiveTicks = 0;
        _suspended = false;
        FocusFirst();
        TakeCapture();
        if (result.Ok) StartCloseTimer(SuccessLinger);
    }

    // ---- keyboard ---------------------------------------------------------------------------

    private void OnMenuKeyDown(object sender, KeyEventArgs e)
    {
        if (_suspended) return;
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                if (_paletteView && !_openedOnPalette) ShowView(palette: false);
                else Finish();
                break;
            case Key.Tab:
            case Key.Left:
            case Key.Right:
            case Key.Up:
            case Key.Down:
                // The first navigation key puts the focus (and its ring) on the first item,
                // as in a menu; after that WPF moves it.
                if (!_keyboardUsed)
                {
                    _keyboardUsed = true;
                    if (Keyboard.FocusedElement is not Button)
                    {
                        e.Handled = true;
                        FocusFirst();
                    }
                }
                break;
            case Key.Enter:
                // Space presses a focused button by itself; Enter is made to do the same.
                if (Keyboard.FocusedElement is Button { IsEnabled: true } button)
                {
                    e.Handled = true;
                    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                }
                break;
        }
    }

    /// <summary>
    /// Where the keyboard is. Opened by the mouse, the menu shows no focus ring: the window
    /// itself has the focus (Esc works) until the first Tab or arrow key, which starts on the
    /// first thing that can be used. Once the keyboard is in use, every view starts there.
    /// </summary>
    private void FocusFirst()
    {
        if (!_keyboardUsed)
        {
            Focus();
            return;
        }
        var panel = _paletteView ? PaletteView : MainView;
        var first = Descendants(panel).OfType<Button>().FirstOrDefault(b => b.IsEnabled && b.IsVisible);
        if (first is not null) first.Focus();
        else Focus();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var below in Descendants(child)) yield return below;
        }
    }

    // ---- staying a menu: the mouse, activation, timers ----------------------------------------

    /// <summary>
    /// Holds the mouse for the whole window, as menus do: a press anywhere else comes here
    /// (<see cref="OnMouseDownOutside"/>) instead of going to Revit.
    /// </summary>
    private void TakeCapture()
    {
        if (_done || _suspended || !IsVisible) return;
        // A pressed button of ours has it for now; anything else (the ribbon button that was clicked) gives it up.
        if (Mouse.Captured is DependencyObject holder && ReferenceEquals(GetWindow(holder), this)) return;
        Mouse.Capture(this, CaptureMode.SubTree);
    }

    private void ReleaseCapture()
    {
        if (ReferenceEquals(Mouse.Captured, this)) Mouse.Capture(null);
    }

    /// <summary>
    /// A pressed button of ours takes the mouse and lets it go on release, and Windows can
    /// take it away: once things have settled, take it back if the menu is still the active window.
    /// </summary>
    private void OnLostCapture()
    {
        if (_done || _suspended) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsActive) TakeCapture();
        }));
    }

    private void OnMouseDownOutside(object sender, MouseButtonEventArgs e)
    {
        if (_suspended) return;
        e.Handled = true;
        Finish();
    }

    /// <summary>A press inside the window but off the card (its shadow margin) is a press outside.</summary>
    private void OnMenuMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_suspended || Card.IsMouseOver) return;
        e.Handled = true;
        Finish();
    }

    private void WhenDeactivated()
    {
        if (_suspended || _done) return;
        // Decided once the activation change has settled.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_suspended || _done || IsActive) return;
            // Revit taking activation back right after the ribbon click isn't the user leaving: once, take it again.
            if (!_reactivated && _sinceShown.Elapsed < OpeningGrace && _revitWindow != IntPtr.Zero
                && NativeScreen.ForegroundWindow() == _revitWindow)
            {
                _reactivated = true;
                PickLog.Write("menu reactivated");
                Activate();
                FocusFirst();
                TakeCapture();
                return;
            }
            Finish();
        }));
    }

    private void CloseIfLeftInactive()
    {
        if (_suspended || _done) return;
        if (IsActive)
        {
            _inactiveTicks = 0;
            return;
        }
        if (++_inactiveTicks >= 2) Finish();
    }

    private void StartCloseTimer(TimeSpan after)
    {
        StopCloseTimer();
        _closeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = after };
        _closeTimer.Tick += (_, _) =>
        {
            StopCloseTimer();
            Finish();
        };
        _closeTimer.Start();
    }

    private void StopCloseTimer()
    {
        _closeTimer?.Stop();
        _closeTimer = null;
    }

    // ---- placing ------------------------------------------------------------------------------

    /// <summary>
    /// Puts the card's top-left corner at the pointer (or the card in the middle of Revit's
    /// screen), kept inside the monitor's work area. In window pixels throughout, whatever
    /// the scale factor, like the eyedropper's overlays.
    /// </summary>
    private void Place(bool atCentre)
    {
        // Deliberately not inside NativeScreen.PerMonitorDpi(), here and in KeepOnScreen: the window
        // rectangle, the pointer, the work area and the move all use this popup's own DPI context.
        if (_handle == IntPtr.Zero) return;
        var (left, top, right, bottom) = NativeScreen.WindowRect(_handle);
        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0) return;

        int x;
        int y;
        PixelRect work;
        if (atCentre)
        {
            var (rl, rt, rr, rb) = NativeScreen.WindowRect(_revitWindow);
            work = NativeScreen.WorkAreaAt((rl + rr) / 2, (rt + rb) / 2);
            x = work.Left + (work.Width - width) / 2;
            y = work.Top + (work.Height - height) / 2;
        }
        else
        {
            var (px, py) = NativeScreen.CursorPosition();
            work = NativeScreen.WorkAreaAt(px, py);
            var margin = MarginPixels(width);
            x = px - margin;
            y = py - margin;
        }
        MoveInside(work, x, y, width, height);
    }

    /// <summary>After the content changed height: where it is, but not hanging off the screen.</summary>
    private void KeepOnScreen()
    {
        if (_handle == IntPtr.Zero) return;
        var (left, top, right, bottom) = NativeScreen.WindowRect(_handle);
        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0) return;
        var margin = MarginPixels(width);
        MoveInside(NativeScreen.WorkAreaAt(left + margin, top + margin), left, top, width, height);
    }

    /// <summary>Moves the window so its card (the window less its shadow margin) lies inside <paramref name="work"/>.</summary>
    private void MoveInside(PixelRect work, int x, int y, int width, int height)
    {
        var (nx, ny) = PickerGeometry.MoveInside(work, x, y, width, height, MarginPixels(width));
        NativeScreen.MoveTo(_handle, nx, ny);
    }

    /// <summary>The shadow margin in window pixels, from the window's pixel width and its WPF width.</summary>
    private int MarginPixels(int width) =>
        ActualWidth > 0 ? (int)Math.Round(ShadowMargin * width / ActualWidth) : (int)ShadowMargin;

    // ---- ending -------------------------------------------------------------------------------

    /// <summary>Ends the menu once; <see cref="Run"/> then takes the window down.</summary>
    private void Finish()
    {
        _done = true;
        if (_frame is not null) _frame.Continue = false;
    }

    /// <summary>Never throws, safe to call twice: stops the timers, lets the mouse go and closes the window.</summary>
    private void TakeDown()
    {
        _done = true;
        _suspended = true;
        StopCloseTimer();
        _inactiveTimer?.Stop();
        _inactiveTimer = null;
        Try(ReleaseCapture);
        if (_closed) return;
        // Hand over while the menu still holds the foreground: afterwards Windows would refuse.
        Try(() =>
        {
            if (_handle != IntPtr.Zero && _revitWindow != IntPtr.Zero && NativeScreen.ForegroundWindow() == _handle)
                NativeScreen.Activate(_revitWindow);
        });
        Try(Close);
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Pick-It: menu cleanup: {ex.Message}");
        }
    }
}
