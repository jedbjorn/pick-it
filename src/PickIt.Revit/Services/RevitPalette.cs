using System.Diagnostics;
using System.Globalization;
using System.IO;
using PickIt.Revit.Model;

namespace PickIt.Revit.Services;

/// <summary>
/// What Pick-It knows about the 16 custom colours of Revit's Color dialog. Revit has no
/// API for them, so this is a copy: read once at startup from the <c>[Colors]</c> section
/// of the user's Revit.ini (which Revit writes on a clean exit), then kept up to date
/// with Pick-It's own successful writes. A slot is unknown (null) when the file, the
/// section or its key is missing or unreadable; it is then drawn as unknown, never
/// guessed. Changes the user makes in Revit's dialog during the session aren't seen.
/// Pick-It never writes Revit.ini.
/// <para>
/// Slots are numbered 0 to 15 in the order of Revit's dialog: two rows of eight,
/// slot = row × 8 + column. Revit.ini's <c>CustomColorN</c> is slot N − 1, six hex
/// digits in blue-green-red order.
/// </para>
/// </summary>
internal sealed class RevitPalette
{
    public const int SlotCount = 16;
    public const int Columns = 8;

    private const string Section = "[Colors]";
    private const string KeyPrefix = "CustomColor";

    private readonly Rgb?[] _slots = new Rgb?[SlotCount];

    /// <summary>The colour of a slot; null when it isn't known.</summary>
    public Rgb? this[int slot] => slot is >= 0 and < SlotCount ? _slots[slot] : null;

    /// <summary><c>%APPDATA%\Autodesk\Revit\Autodesk Revit &lt;year&gt;\Revit.ini</c>.</summary>
    public static string IniPath(string year) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Autodesk", "Revit", $"Autodesk Revit {year}", "Revit.ini");

    /// <summary>Reads the slots from a Revit.ini; whatever can't be read stays unknown. Returns how many are known.</summary>
    public int Load(string path)
    {
        Array.Clear(_slots);
        try
        {
            if (!File.Exists(path)) return 0;
            // Revit writes the file as UTF-16 with a byte-order mark, which ReadAllText detects.
            var parsed = Parse(File.ReadAllText(path));
            Array.Copy(parsed, _slots, SlotCount);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Pick-It: Revit.ini not read: {ex.Message}");
            Array.Clear(_slots);
        }
        return _slots.Count(s => s is not null);
    }

    /// <summary>Pick-It wrote this colour into the slot.</summary>
    public void Set(int slot, Rgb color)
    {
        if (slot is >= 0 and < SlotCount) _slots[slot] = color;
    }

    /// <summary>The <c>CustomColor1..16</c> values of the <c>[Colors]</c> section of an ini text.</summary>
    internal static Rgb?[] Parse(string text)
    {
        var slots = new Rgb?[SlotCount];
        var inSection = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inSection = line.Equals(Section, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection || !line.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var equals = line.IndexOf('=');
            if (equals < 0) continue;
            var number = line[KeyPrefix.Length..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > SlotCount)
                continue;
            if (value.Length is < 1 or > 6
                || !uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var bgr))
                continue;
            // A COLORREF written as hex: 0xBBGGRR.
            slots[n - 1] = new Rgb((byte)bgr, (byte)(bgr >> 8), (byte)(bgr >> 16));
        }
        return slots;
    }
}
