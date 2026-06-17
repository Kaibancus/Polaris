using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Polaris.Models;

namespace Polaris.Services;

/// <summary>
/// Loads and saves <see cref="AppConfig"/> to %AppData%\Polaris\config.json.
/// </summary>
public static class ConfigStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Polaris");

    private static readonly string FilePath = Path.Combine(Dir, "config.json");
    private static readonly string BackupPath = FilePath + ".bak";
    private static readonly string TempPath = FilePath + ".tmp";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppConfig Load()
    {
        // Try the primary file first, then the last-known-good backup. A crash or
        // power loss mid-write can truncate the primary file; the backup (written
        // only after a successful atomic replace) lets us recover instead of
        // silently resetting the user's icons and settings to defaults.
        if (TryLoadFrom(FilePath, out var cfg))
            return cfg;
        if (TryLoadFrom(BackupPath, out var backup))
            return backup;
        return new AppConfig();
    }

    private static bool TryLoadFrom(string path, out AppConfig config)
    {
        config = null!;
        try
        {
            if (!File.Exists(path))
                return false;

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return false;

            var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
            if (cfg == null)
                return false;

            cfg.Settings ??= new AppSettings();
            cfg.Apps ??= new();
            cfg.SideDockApps ??= new();
            MigrateAppsFolderPaths(cfg);
            config = cfg;
            return true;
        }
        catch
        {
            // Corrupt/partial file — let the caller fall back to the backup.
            return false;
        }
    }

    /// <summary>Upgrades entries saved before packaged-app (UWP / Microsoft
    /// Store) launch support: their <see cref="AppEntry.Path"/> was stored as a
    /// bare AUMID that could neither launch nor render an icon. Rewrite them to
    /// the "shell:AppsFolder\&lt;AUMID&gt;" form so existing dock icons heal.</summary>
    private static void MigrateAppsFolderPaths(AppConfig cfg)
    {
        void Fix(System.Collections.Generic.List<AppEntry>? list)
        {
            if (list == null)
                return;
            foreach (var app in list)
            {
                var norm = ShellNamespace.NormalizeAppsFolderPath(app.Path);
                // Heal known-folder-GUID-relative paths saved by older builds
                // (e.g. "{6D809377-…}\App\app.exe") into real file-system paths.
                norm = ShellNamespace.ExpandKnownFolderPath(norm);
                if (norm != app.Path)
                {
                    if (app.IconSource == app.Path || string.IsNullOrWhiteSpace(app.IconSource))
                        app.IconSource = norm;
                    app.Path = norm;
                }
            }
        }
        Fix(cfg.Apps);
        Fix(cfg.SideDockApps);
    }

    public static void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(config, Options);

            // Atomic write: serialise to a temp file first, then swap it into
            // place. File.Replace is atomic and rotates the previous good file
            // into the backup, so a crash can never leave a half-written config.
            File.WriteAllText(TempPath, json);

            if (File.Exists(FilePath))
            {
                File.Replace(TempPath, FilePath, BackupPath);
            }
            else
            {
                // First-ever save: no original to replace.
                File.Move(TempPath, FilePath);
            }
        }
        catch
        {
            // Best-effort persistence; ignore IO failures. Clean up a stray temp
            // file so a failed write does not linger.
            try
            {
                if (File.Exists(TempPath))
                    File.Delete(TempPath);
            }
            catch
            {
                // ignore cleanup failures
            }
        }
    }
}
