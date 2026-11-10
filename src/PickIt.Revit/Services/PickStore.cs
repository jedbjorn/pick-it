using System.Diagnostics;
using System.IO;
using System.Text.Json;
using PickIt.Revit.Model;

namespace PickIt.Revit.Services;

/// <summary>
/// The picked colour and the recent colours, kept across Revit sessions in
/// <c>%APPDATA%\Designs-OS\PickIt\state.json</c>:
/// <code>{ "current": "#2F6AD0", "recent": ["#2F6AD0", "#D9D9D9"] }</code>
/// A missing, unreadable or malformed file is an empty state; a failed save is
/// ignored (the colour then lasts for this session only).
/// </summary>
internal sealed class PickStore
{
    /// <summary>How many recent colours are remembered.</summary>
    public const int MaxRecent = 10;

    private readonly string _path;
    private readonly List<Rgb> _recent = [];

    public PickStore(string path)
    {
        _path = path;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Designs-OS", "PickIt", "state.json");

    /// <summary>The colour on the ribbon button; null when none is picked (or it was cleared).</summary>
    public Rgb? Current { get; private set; }

    /// <summary>The last picks, newest first, without duplicates, at most <see cref="MaxRecent"/>.</summary>
    public IReadOnlyList<Rgb> Recent => _recent;

    /// <summary>Reads the file; whatever can't be read is dropped.</summary>
    public void Load()
    {
        Current = null;
        _recent.Clear();
        try
        {
            if (!File.Exists(_path)) return;
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (root.TryGetProperty("current", out var current) && current.ValueKind == JsonValueKind.String
                && Rgb.TryParse(current.GetString(), out var color))
                Current = color;
            if (!root.TryGetProperty("recent", out var recent) || recent.ValueKind != JsonValueKind.Array) return;
            foreach (var item in recent.EnumerateArray())
            {
                if (_recent.Count == MaxRecent) break;
                if (item.ValueKind == JsonValueKind.String && Rgb.TryParse(item.GetString(), out var entry)
                    && !_recent.Contains(entry))
                    _recent.Add(entry);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Pick-It: state not read: {ex.Message}");
            Current = null;
            _recent.Clear();
        }
    }

    /// <summary>A new pick: becomes the current colour and moves to the front of the recent colours.</summary>
    public void Pick(Rgb color)
    {
        Current = color;
        _recent.Remove(color);
        _recent.Insert(0, color);
        if (_recent.Count > MaxRecent) _recent.RemoveRange(MaxRecent, _recent.Count - MaxRecent);
        Save();
    }

    /// <summary>Makes a colour current without touching the recent colours (a recent swatch was chosen).</summary>
    public void SetCurrent(Rgb color)
    {
        Current = color;
        Save();
    }

    /// <summary>No current colour; the recent colours stay.</summary>
    public void Clear()
    {
        Current = null;
        Save();
    }

    /// <summary>Empties the recent colours; the current colour stays.</summary>
    public void ClearHistory()
    {
        _recent.Clear();
        Save();
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            // Written beside the file and moved over it, so a crash never leaves half a file.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, ToJson());
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Pick-It: state not saved: {ex.Message}");
        }
    }

    /// <summary>The colours are <c>#RRGGBB</c>, so nothing needs escaping.</summary>
    private string ToJson()
    {
        var current = Current is { } c ? $"\"{c.ToHex()}\"" : "null";
        var recent = string.Join(", ", _recent.Select(r => $"\"{r.ToHex()}\""));
        return $"{{\n  \"current\": {current},\n  \"recent\": [{recent}]\n}}\n";
    }
}
