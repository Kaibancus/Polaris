using System;
using System.Globalization;
using System.IO;

namespace Polaris.Services;

/// <summary>Severity for <see cref="Log"/> entries.</summary>
public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

/// <summary>
/// Lightweight, dependency-free application log. Writes to
/// <c>%AppData%\Polaris\errors.log</c> (the same file the global crash handler
/// uses) so a user only has to attach one file when reporting a problem.
///
/// Normal Release runs persist <see cref="LogLevel.Warn"/> and above to keep the
/// file small; set <c>POLARIS_LOG=1</c> (always on in Debug builds) to also
/// capture <see cref="LogLevel.Debug"/>/<see cref="LogLevel.Info"/>. That is what
/// gives the many best-effort paths — which otherwise swallow their exception
/// silently — a diagnostic trail when something misbehaves on a user's machine.
///
/// Every method is thread-safe and never throws.
/// </summary>
public static class Log
{
    private static readonly object _gate = new();

    /// <summary>Full path of the shared log file.</summary>
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Polaris", "errors.log");

    private static readonly LogLevel _min =
#if DEBUG
        LogLevel.Debug;
#else
        string.Equals(Environment.GetEnvironmentVariable("POLARIS_LOG"), "1",
            StringComparison.Ordinal) ? LogLevel.Debug : LogLevel.Warn;
#endif

    public static void Debug(string area, string message, Exception? ex = null)
        => Write(LogLevel.Debug, area, message, ex);

    public static void Info(string area, string message, Exception? ex = null)
        => Write(LogLevel.Info, area, message, ex);

    public static void Warn(string area, string message, Exception? ex = null)
        => Write(LogLevel.Warn, area, message, ex);

    public static void Error(string area, string message, Exception? ex = null)
        => Write(LogLevel.Error, area, message, ex);

    /// <summary>Appends one entry if <paramref name="level"/> meets the active
    /// threshold. Best-effort: any failure to write is itself swallowed so a
    /// logging fault can never take down a caller.</summary>
    public static void Write(LogLevel level, string area, string message, Exception? ex = null)
    {
        if (level < _min)
            return;
        try
        {
            string line = string.Format(CultureInfo.InvariantCulture,
                "[{0:o}] {1,-5} ({2}) {3}{4}\n",
                DateTime.Now,
                level.ToString().ToUpperInvariant(),
                area,
                message,
                ex is null ? string.Empty : "\n" + ex);

            var dir = System.IO.Path.GetDirectoryName(Path);
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(Path, line);
            }
        }
        catch
        {
            // Logging must never throw.
        }
    }
}
