using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Polaris.Services;

/// <summary>
/// Checks GitHub Releases for a newer build of Polaris and, when the user
/// confirms, downloads it and swaps it in. Versions are compared numerically:
/// the version embedded in a release's tag (e.g. "alpha_v1.0" -> 1.0) is parsed
/// into a <see cref="System.Version"/> and a strictly-greater value counts as
/// an update.
/// </summary>
public static class UpdateService
{
    private const string Owner = "Kaibancus";
    private const string Repo = "Polaris";
    private const string LatestReleaseApi =
        "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";

    public sealed class ReleaseInfo
    {
        public required Version Version { get; init; }
        public required string TagName { get; init; }
        public required string HtmlUrl { get; init; }
        /// <summary>Download URL of the .zip asset (the FD single-file build), or null.</summary>
        public string? ZipAssetUrl { get; init; }
        public string? ZipAssetName { get; init; }
    }

    /// <summary>The running assembly's version, normalised to at least major.minor.</summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v ?? new Version(0, 0);
        }
    }

    /// <summary>
    /// Queries GitHub for the latest release. Returns its parsed info, or null
    /// when there is no release or the tag carries no recognisable version.
    /// </summary>
    public static async Task<ReleaseInfo?> GetLatestReleaseAsync()
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(20);
        // GitHub's API rejects requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Polaris-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        using var resp = await http.GetAsync(LatestReleaseApi).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;

        string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
        var version = ExtractVersion(tag);
        if (version == null)
            return null;

        string htmlUrl = root.TryGetProperty("html_url", out var hu) ? (hu.GetString() ?? "") : "";

        string? zipUrl = null, zipName = null;
        if (root.TryGetProperty("assets", out var assets) &&
            assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    zipUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    zipName = name;
                    break;
                }
            }
        }

        return new ReleaseInfo
        {
            Version = version,
            TagName = tag,
            HtmlUrl = htmlUrl,
            ZipAssetUrl = zipUrl,
            ZipAssetName = zipName,
        };
    }

    /// <summary>
    /// Pulls the first dotted-number run out of an arbitrary tag string and
    /// parses it as a <see cref="System.Version"/>. "alpha_v1.0" -> 1.0,
    /// "v2.3.1" -> 2.3.1. A lone integer ("v3") is padded to "3.0". Returns null
    /// when no number is present.
    /// </summary>
    public static Version? ExtractVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        // Prefer a dotted version (1.0, 1.2.3); fall back to a lone integer.
        var m = Regex.Match(tag, @"\d+(?:\.\d+)+");
        string num;
        if (m.Success)
        {
            num = m.Value;
        }
        else
        {
            var single = Regex.Match(tag, @"\d+");
            if (!single.Success)
                return null;
            num = single.Value + ".0";
        }

        return Version.TryParse(num, out var v) ? v : null;
    }

    /// <summary>True when <paramref name="release"/> is strictly newer than the
    /// running build.</summary>
    public static bool IsNewer(ReleaseInfo release) =>
        Normalize(release.Version) > Normalize(CurrentVersion);

    /// <summary>Pads a version to 4 components (missing parts -> 0) so that, for
    /// example, 1.0 and 1.0.0.0 compare equal instead of 1.0 sorting lower (its
    /// unspecified Build/Revision are -1).</summary>
    private static Version Normalize(Version v) =>
        new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    /// <summary>
    /// Downloads the release zip, extracts the new Polaris.exe, and schedules a
    /// helper script that waits for this process to exit, swaps the executable
    /// in place, and relaunches it. The caller should shut the app down right
    /// after this returns true.
    /// </summary>
    public static async Task<bool> DownloadAndApplyAsync(ReleaseInfo release)
    {
        if (string.IsNullOrEmpty(release.ZipAssetUrl))
            return false;

        string? currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe) || !currentExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return false;

        string workDir = Path.Combine(Path.GetTempPath(), "Polaris_update_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        string zipPath = Path.Combine(workDir, "update.zip");
        using (var http = new HttpClient())
        {
            http.Timeout = TimeSpan.FromMinutes(5);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Polaris-Updater");
            using var resp = await http.GetAsync(release.ZipAssetUrl).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return false;
            await using var fs = File.Create(zipPath);
            await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
        }

        string extractDir = Path.Combine(workDir, "extracted");
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

        // Find the new Polaris.exe inside the extracted contents.
        string? newExe = null;
        foreach (var f in Directory.EnumerateFiles(extractDir, "*.exe", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(f).Equals("Polaris.exe", StringComparison.OrdinalIgnoreCase))
            {
                newExe = f;
                break;
            }
        }
        if (newExe == null)
            return false;

        // Helper batch: wait for THIS process to exit, replace the exe, relaunch.
        string batPath = Path.Combine(workDir, "apply_update.bat");
        int pid = Environment.ProcessId;
        string bat =
            "@echo off\r\n" +
            "setlocal\r\n" +
            ":waitloop\r\n" +
            "tasklist /fi \"PID eq " + pid + "\" 2>nul | find \"" + pid + "\" >nul\r\n" +
            "if not errorlevel 1 (\r\n" +
            "  timeout /t 1 /nobreak >nul\r\n" +
            "  goto waitloop\r\n" +
            ")\r\n" +
            "copy /y \"" + newExe + "\" \"" + currentExe + "\" >nul\r\n" +
            "start \"\" \"" + currentExe + "\"\r\n" +
            "rmdir /s /q \"" + workDir + "\"\r\n";
        File.WriteAllText(batPath, bat);

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c \"" + batPath + "\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process.Start(psi);
        return true;
    }
}
