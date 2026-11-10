using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using PickIt.Revit.Model;

namespace PickIt.Revit.Services;

/// <summary>
/// How a palette write went. <see cref="Message"/> is for the user (empty on success);
/// <see cref="Trace"/> is the one-line detail for the Debug log.
/// </summary>
internal readonly record struct PaletteWriteResult(bool Ok, string Message, string Trace);

/// <summary>
/// Writes a colour into one of the 16 custom-colour slots of Revit's Color dialog. Revit
/// has no API for the custom colours, so this opens Revit's own
/// <see cref="ColorSelectionDialog"/> and operates it, unseen.
/// <para>
/// The dialog is the Windows common colour dialog (class <c>#32770</c>): edits 706, 707
/// and 708 are red, green and blue, button 712 is "Add", static 721 is the custom-colour
/// box (two rows of eight), 1 and 2 are OK and Cancel. A <see cref="DispatcherTimer"/>,
/// which keeps ticking inside the dialog's modal loop, drives it one step per tick:
/// click the slot's cell, set the three edits, press Add, press OK.
/// <see cref="ColorSelectionDialog.Show"/> then returns with the colour as its
/// selection, which is the success check. The palette lasts for the Revit session and
/// Revit saves it to Revit.ini on a clean exit.
/// </para>
/// <para>
/// Keeping it unseen: a hook on the UI thread's sent messages (WH_CALLWNDPROC) catches
/// the dialog while it is being set up (WM_INITDIALOG, before it is first shown) and
/// makes it all but invisible (alpha 1), then parks it off-screen as it is shown. If the hook
/// can't be installed or never sees the dialog, the timer still finds it on its first
/// tick and parks it off-screen (it is then on screen for a moment), and the write goes
/// ahead the same way.
/// </para>
/// <para>
/// Never leaving Revit in an unseen modal dialog: the controls are checked before
/// anything is sent; anything unexpected cancels the dialog; the timer gives up after a
/// few seconds; and a watchdog thread, which doesn't depend on the timer ticking,
/// cancels the dialog and finally makes it visible again for the user to close.
/// </para>
/// Must run on Revit's UI thread in a valid API context, with no window of ours in the way.
/// </summary>
internal sealed class PaletteWriter
{
    private const int IdOk = 1;
    private const int IdCancel = 2;
    private const int IdRed = 706;
    private const int IdGreen = 707;
    private const int IdBlue = 708;
    private const int IdAdd = 712;
    private const int IdCustomBox = 721;
    private const string DialogClass = "#32770";

    private const int Rows = RevitPalette.SlotCount / RevitPalette.Columns;

    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Ticks before the timer gives up (5 s).</summary>
    private const int MaxTicks = 100;

    /// <summary>Ticks an unrecognised dialog is given to turn into the Color dialog (1 s).</summary>
    private const int StrangerTicks = 20;

    /// <summary>Ticks between attempts to close a dialog that should be closing (0.5 s).</summary>
    private const int CloseRetryTicks = 10;

    private static readonly TimeSpan WatchdogCancel = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan WatchdogReveal = TimeSpan.FromSeconds(3);

    private enum Step
    {
        Find,
        SelectSlot,
        TypeColor,
        Scramble,
        Reselect,
        Verify,
        Add,
        Confirm,
        Closing,
    }

    private readonly int _slot;
    private readonly Rgb _color;
    private readonly ManualResetEventSlim _done = new(false);
    private HashSet<IntPtr> _before = [];
    private uint _uiThread;
    private NativeDialog.HookProc? _hookProc;
    private IntPtr _hook;
    private bool _inHook;
    private IntPtr _dialog;
    private bool _transparent;
    private bool _parked;
    private string _hiddenBy = "none";
    private string _hookNote = "";
    private Step _step = Step.Find;
    private int _ticks;
    private int _strangerTicks;
    private int _closingSince;
    private bool _added;
    private string? _failure;
    private string _readBack = "";
    private volatile bool _finished;

    private PaletteWriter(int slot, Rgb color)
    {
        _slot = slot;
        _color = color;
    }

    /// <summary>
    /// Writes <paramref name="color"/> into slot <paramref name="slot"/> (0 to 15,
    /// row × 8 + column). Returns when Revit's dialog has closed again; never throws.
    /// </summary>
    public static PaletteWriteResult Write(int slot, Rgb color)
    {
        if (slot is < 0 or >= RevitPalette.SlotCount)
            return new PaletteWriteResult(false, "That palette slot doesn't exist.", "slot out of range");
        try
        {
            return new PaletteWriter(slot, color).Run();
        }
        catch (Exception ex)
        {
            var reason = OneLine(ex.Message);
            return new PaletteWriteResult(false, $"Revit's Color dialog couldn't be used ({reason}).", $"{ex.GetType().Name}: {reason}");
        }
    }

    private PaletteWriteResult Run()
    {
        var clock = Stopwatch.StartNew();
        _uiThread = NativeDialog.CurrentThreadId();
        _before = NativeDialog.ThreadWindows(_uiThread).ToHashSet();

        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TickInterval };
        timer.Tick += (_, _) =>
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Fail($"Revit's Color dialog couldn't be driven ({OneLine(ex.Message)}).");
            }
        };

        var shown = "not shown";
        var matches = false;
        try
        {
            InstallHook();
            timer.Start();
            StartWatchdog();
            using var dialog = new ColorSelectionDialog();
            // Never the colour being written, so the selection only matches when the edits took it.
            var grey = _color == new Rgb(128, 128, 128) ? (byte)127 : (byte)128;
            dialog.OriginalColor = new Autodesk.Revit.DB.Color(grey, grey, grey);
            var result = dialog.Show();
            shown = result.ToString();
            var selected = dialog.SelectedColor;
            matches = result == ItemSelectionDialogResult.Confirmed
                && selected is { IsValid: true }
                && selected.Red == _color.R && selected.Green == _color.G && selected.Blue == _color.B;
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _failure, $"Revit's Color dialog couldn't be opened ({OneLine(ex.Message)}).", null);
        }
        finally
        {
            _finished = true;
            _done.Set();
            timer.Stop();
            RemoveHook();
        }

        var trace = $"hide={_hiddenBy} ms={clock.ElapsedMilliseconds} ticks={_ticks} step={_step} show={shown}{_readBack}{_hookNote}";
        // Add was pressed and Revit confirmed the colour: the slot holds it, whatever else was noted.
        if (matches && _added) return new PaletteWriteResult(true, "", trace);
        var reason = _failure ?? "Revit didn't take the colour.";
        return new PaletteWriteResult(false, reason, $"{reason} {trace}");
    }

    // ---- the timer: one step per tick, inside the dialog's modal loop -------------------------

    private void Tick()
    {
        if (_finished) return;
        _ticks++;
        if (_step == Step.Closing)
        {
            // OK or Cancel was posted; a dialog that is still there is asked again, more bluntly.
            if (NativeDialog.Exists(_dialog) && _ticks - _closingSince >= CloseRetryTicks)
            {
                _closingSince = _ticks;
                NativeDialog.Post(_dialog, NativeDialog.WmClose, IntPtr.Zero, IntPtr.Zero);
            }
            return;
        }
        if (_ticks > MaxTicks)
        {
            Fail("Revit's Color dialog didn't respond in time.");
            return;
        }
        if (_step != Step.Find && !NativeDialog.Exists(_dialog))
        {
            Fail("Revit's Color dialog closed unexpectedly.");
            return;
        }

        switch (_step)
        {
            case Step.Find:
                if (Find()) _step = Step.SelectSlot;
                break;
            case Step.SelectSlot:
                ClickSlot(_slot);
                _step = Step.TypeColor;
                break;
            case Step.TypeColor:
                if (SetEdit(IdRed, _color.R) && SetEdit(IdGreen, _color.G) && SetEdit(IdBlue, _color.B))
                    _step = Step.Add;
                else
                    Fail("Revit's Color dialog didn't accept the RGB values.");
                break;
            case Step.Add:
                NativeDialog.Send(_dialog, NativeDialog.WmCommand, NativeDialog.CommandParam(IdAdd, 0), NativeDialog.Control(_dialog, IdAdd));
                _step = Step.Scramble;
                break;
            case Step.Scramble:
                // Prove the slot took it: move the dialog's colour away, select the slot again
                // and read its colour back from the edits.
                if (SetEdit(IdRed, (byte)(_color.R ^ 0x80)))
                    _step = Step.Reselect;
                else
                    Fail("Revit's Color dialog didn't accept the RGB values.");
                break;
            case Step.Reselect:
                ClickSlot(_slot);
                _step = Step.Verify;
                break;
            case Step.Verify:
                if (ReadEdit(IdRed) == _color.R && ReadEdit(IdGreen) == _color.G && ReadEdit(IdBlue) == _color.B)
                {
                    _added = true;
                    _step = Step.Confirm;
                }
                else
                {
                    _readBack = $" read={ReadEdit(IdRed)},{ReadEdit(IdGreen)},{ReadEdit(IdBlue)}";
                    Fail("Revit didn't store the colour in that slot.");
                }
                break;
            case Step.Confirm:
                Press(IdOk);
                _closingSince = _ticks;
                _step = Step.Closing;
                break;
        }
    }

    /// <summary>
    /// Settles on the dialog (the hook may have found it already) and makes sure it is out
    /// of sight. False while it isn't there yet.
    /// </summary>
    private bool Find()
    {
        if (_dialog == IntPtr.Zero)
        {
            var fresh = Strays();
            var match = fresh.FirstOrDefault(h => IsColorDialog(h));
            if (match == IntPtr.Zero)
            {
                // Some other dialog came up instead (or the Color dialog changed): don't drive it.
                if (fresh.Count > 0 && ++_strangerTicks >= StrangerTicks)
                {
                    // Not ours to close: the watchdog steps in if nothing else ends the wait.
                    Fail("Revit's Color dialog wasn't recognised.");
                }
                return false;
            }
            _dialog = match;
        }
        if (!NativeDialog.Exists(_dialog))
        {
            Fail("Revit's Color dialog closed unexpectedly.");
            return false;
        }
        if (!NativeDialog.IsVisible(_dialog)) return false;

        // The fallback when the hook didn't get there first, and the second line of defence when it did.
        if (!_parked || NativeDialog.RectOf(_dialog).Left > NativeDialog.OffScreen + 1000)
        {
            NativeDialog.MoveOffScreen(_dialog);
            _parked = true;
            if (!_transparent) _hiddenBy = "timer";
        }
        return true;
    }

    /// <summary>The Color dialog and nothing else: a top-level dialog with the controls this class drives.</summary>
    private static bool IsColorDialog(IntPtr hwnd) =>
        NativeDialog.IsTopLevel(hwnd)
        && NativeDialog.ClassOf(hwnd) == DialogClass
        && NativeDialog.Control(hwnd, IdCustomBox) != IntPtr.Zero
        && IsControl(hwnd, IdRed, "Edit") && IsControl(hwnd, IdGreen, "Edit") && IsControl(hwnd, IdBlue, "Edit")
        && IsControl(hwnd, IdAdd, "Button") && IsControl(hwnd, IdOk, "Button") && IsControl(hwnd, IdCancel, "Button");

    private static bool IsControl(IntPtr dialog, int id, string className)
    {
        var control = NativeDialog.Control(dialog, id);
        return control != IntPtr.Zero && string.Equals(NativeDialog.ClassOf(control), className, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A click in the middle of the slot's cell of the custom-colour box, sent to the dialog
    /// (the box is a static; the dialog handles its mouse). That selects the slot "Add" fills.
    /// </summary>
    private void ClickSlot(int slot)
    {
        var (left, top, right, bottom) = NativeDialog.RectOf(NativeDialog.Control(_dialog, IdCustomBox));
        var column = slot % RevitPalette.Columns;
        var row = slot / RevitPalette.Columns;
        var x = left + (int)((column + 0.5) * (right - left) / RevitPalette.Columns);
        var y = top + (int)((row + 0.5) * (bottom - top) / Rows);
        var (cx, cy) = NativeDialog.ToClient(_dialog, x, y);
        var at = NativeDialog.PointParam(cx, cy);
        NativeDialog.Send(_dialog, NativeDialog.WmLButtonDown, new IntPtr(1) /* MK_LBUTTON */, at);
        NativeDialog.Send(_dialog, NativeDialog.WmLButtonUp, IntPtr.Zero, at);
    }

    /// <summary>Types a value into an edit the way the dialog expects to hear about it; false when it didn't stick.</summary>
    private bool SetEdit(int id, byte value)
    {
        var edit = NativeDialog.Control(_dialog, id);
        if (edit == IntPtr.Zero) return false;
        var text = value.ToString(CultureInfo.InvariantCulture);
        NativeDialog.Focus(edit);
        NativeDialog.SetText(edit, text);
        NativeDialog.Send(_dialog, NativeDialog.WmCommand, NativeDialog.CommandParam(id, NativeDialog.EnChange), edit);
        NativeDialog.Send(_dialog, NativeDialog.WmCommand, NativeDialog.CommandParam(id, NativeDialog.EnKillFocus), edit);
        return NativeDialog.TextOf(edit).Trim() == text;
    }

    /// <summary>The number in an edit; -1 when it isn't one.</summary>
    private int ReadEdit(int id)
    {
        var edit = NativeDialog.Control(_dialog, id);
        if (edit == IntPtr.Zero) return -1;
        return int.TryParse(NativeDialog.TextOf(edit).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : -1;
    }

    private void Press(int id)
    {
        if (!NativeDialog.Exists(_dialog)) return;
        NativeDialog.Post(_dialog, NativeDialog.WmCommand, NativeDialog.CommandParam(id, 0), NativeDialog.Control(_dialog, id));
    }

    /// <summary>Notes the first reason and cancels the dialog; the write is then reported as failed.</summary>
    private void Fail(string reason)
    {
        Interlocked.CompareExchange(ref _failure, reason, null);
        _closingSince = _ticks;
        _step = Step.Closing;
        Press(IdCancel);
    }

    /// <summary>Dialogs that appeared on the UI thread since the write started.</summary>
    private List<IntPtr> Strays() => NativeDialog.ThreadWindows(_uiThread)
        .Where(h => !_before.Contains(h) && NativeDialog.IsVisible(h) && NativeDialog.ClassOf(h) == DialogClass)
        .ToList();

    // ---- the hook: out of sight before it is ever shown ---------------------------------------

    private void InstallHook()
    {
        try
        {
            _hookProc = OnSentMessage;
            _hook = NativeDialog.Hook(NativeDialog.WhCallWndProc, _hookProc, _uiThread);
            if (_hook == IntPtr.Zero) _hookNote = $" hook=refused({Marshal.GetLastWin32Error()})";
        }
        catch (Exception ex)
        {
            _hook = IntPtr.Zero;
            _hookNote = $" hook=failed({ex.GetType().Name})";
        }
    }

    private void RemoveHook()
    {
        if (_hook != IntPtr.Zero)
        {
            try
            {
                NativeDialog.Unhook(_hook);
            }
            catch (Exception)
            {
                // Nothing to do about it; the procedure ignores everything once finished.
            }
            _hook = IntPtr.Zero;
        }
        GC.KeepAlive(_hookProc);
    }

    /// <summary>
    /// Called by Windows before any window of the UI thread gets a sent message, so it does
    /// next to nothing for all but the three messages a dialog gets on its way to the
    /// screen. Must not throw: it runs inside Revit's message handling.
    /// </summary>
    private IntPtr OnSentMessage(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0 && lParam != IntPtr.Zero && !_inHook && !_finished)
            {
                // CWPSTRUCT: lParam, wParam, message, hwnd.
                var message = Marshal.ReadInt32(lParam, 2 * IntPtr.Size);
                if (message is NativeDialog.WmInitDialog or NativeDialog.WmShowWindow or NativeDialog.WmWindowPosChanging)
                {
                    _inHook = true;
                    try
                    {
                        var showing = message == NativeDialog.WmShowWindow && Marshal.ReadIntPtr(lParam, IntPtr.Size) != IntPtr.Zero;
                        Conceal(Marshal.ReadIntPtr(lParam, 3 * IntPtr.Size), showing);
                    }
                    finally
                    {
                        _inHook = false;
                    }
                }
            }
        }
        catch (Exception)
        {
            // The timer does the concealing instead.
        }
        return NativeDialog.NextHook(code, wParam, lParam);
    }

    private void Conceal(IntPtr hwnd, bool showing)
    {
        if (hwnd == IntPtr.Zero) return;
        if (_dialog == IntPtr.Zero)
        {
            if (_before.Contains(hwnd) || !IsColorDialog(hwnd)) return;
            _dialog = hwnd;
        }
        else if (hwnd != _dialog)
        {
            return;
        }

        if (!_transparent)
        {
            _transparent = NativeDialog.MakeTransparent(hwnd);
            if (_transparent) _hiddenBy = "hook";
        }
        // Parked only once the dialog has placed itself, or its own centring would undo it.
        if (showing && !_parked)
        {
            NativeDialog.MoveOffScreen(hwnd);
            _parked = true;
            if (!_transparent) _hiddenBy = "hook-offscreen";
        }
    }

    // ---- the watchdog: works even if the timer never ticks ------------------------------------

    private void StartWatchdog()
    {
        var thread = new Thread(() =>
        {
            try
            {
                if (_done.Wait(WatchdogCancel)) return;
                Interlocked.CompareExchange(ref _failure, "Revit's Color dialog got stuck and was cancelled.", null);
                foreach (var hwnd in Strays())
                    NativeDialog.Post(hwnd, NativeDialog.WmCommand, NativeDialog.CommandParam(IdCancel, 0), NativeDialog.Control(hwnd, IdCancel));

                if (_done.Wait(WatchdogReveal)) return;
                // Still open: hand it to the user rather than leave Revit modal behind nothing.
                foreach (var hwnd in Strays())
                {
                    // Close first: Reveal sends to the UI thread and would block if it isn't pumping.
                    NativeDialog.Post(hwnd, NativeDialog.WmClose, IntPtr.Zero, IntPtr.Zero);
                    NativeDialog.Reveal(hwnd);
                }
            }
            catch (Exception)
            {
                // A background thread must not take Revit down.
            }
        })
        {
            IsBackground = true,
            Name = "Pick-It palette watchdog",
        };
        thread.Start();
    }

    private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
