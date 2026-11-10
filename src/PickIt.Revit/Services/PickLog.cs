using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace PickIt.Revit.Services;

/// <summary>
/// The log a tester reads from outside Revit: <c>%LOCALAPPDATA%\Temp\PickIt\pick.log</c>.
/// The folder is spelled out because <c>Path.GetTempPath()</c> inside Revit is a
/// per-session folder.
/// <list type="bullet">
/// <item><see cref="Write"/>: Debug builds only, one line per pick, cancel, menu action,
/// palette write or failure. Calls compile away in Release builds.</item>
/// <item><see cref="Diagnostic"/>: the eyedropper's one line per pick about the screen
/// (DPI awareness, overlay sizes, capture time). Debug builds always write it; Release
/// builds write it when it reports an anomaly, or when a file named
/// <c>diagnostics.on</c> exists in the log folder.</item>
/// </list>
/// </summary>
internal static class PickLog
{
    /// <summary>The log is started afresh once it is this long, so a Release build can't grow it without end.</summary>
    private const long MaxLength = 4 * 1024 * 1024;

    /// <summary><c>%LOCALAPPDATA%\Temp\PickIt</c>: the log, the diagnostics flag file, and the Debug command file.</summary>
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "PickIt");

    /// <summary>The flag file that switches the diagnostic line on in Release builds; its content doesn't matter.</summary>
    public static string DiagnosticsFlagPath => Path.Combine(Folder, "diagnostics.on");

    [Conditional("DEBUG")]
    public static void Write(string line) => Append(line);

    /// <summary>
    /// Writes a diagnostic line, in Release builds too, when it reports an
    /// <paramref name="anomaly"/> or diagnostics are switched on (Debug build, or the
    /// <c>diagnostics.on</c> flag file). Never throws.
    /// </summary>
    public static void Diagnostic(string line, bool anomaly)
    {
        if (!anomaly && !DiagnosticsOn()) return;
        try
        {
            var log = new FileInfo(Path.Combine(Folder, "pick.log"));
            if (log.Exists && log.Length > MaxLength) log.Delete();
        }
        catch (Exception)
        {
            // Best effort.
        }
        Append(line);
    }

    private static bool DiagnosticsOn()
    {
#if DEBUG
        return true;
#else
        try
        {
            return File.Exists(DiagnosticsFlagPath);
        }
        catch (Exception)
        {
            return false;
        }
#endif
    }

    private static void Append(string line)
    {
        try
        {
            var directory = Folder;
            Directory.CreateDirectory(directory);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            File.AppendAllText(Path.Combine(directory, "pick.log"), $"{stamp} {line}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Best effort.
        }
    }
}
