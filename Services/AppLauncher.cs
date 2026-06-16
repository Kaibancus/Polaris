using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Polaris.Models;

namespace Polaris.Services;

/// <summary>
/// THE single place that turns a clicked <see cref="AppEntry"/> into a running
/// (or freshly-launched) window. Both docks delegate here so the launch-vs-
/// activate decision tree — File Explorer, shell-namespace objects, packaged
/// (UWP) apps, non-packaged AppsFolder launchers (iQiyi, VS Code…) and ordinary
/// executables — can never drift between them. Historically this logic was
/// duplicated in RadialWindow and LeftDockWindow, and every fix had to be made
/// twice (and was occasionally missed); centralizing it removes that whole class
/// of bug.
/// </summary>
public static class AppLauncher
{
    /// <summary>
    /// Launches the app, or brings its existing window forward when it is already
    /// running and is the kind of app we activate rather than re-spawn.
    /// <paramref name="dismiss"/> runs first so the caller can hide its own dock
    /// UI before focus moves to the target window.
    /// </summary>
    public static void Launch(AppEntry entry, Action? dismiss = null)
    {
        dismiss?.Invoke();
        if (entry == null || string.IsNullOrWhiteSpace(entry.Path))
            return;

        // Shell-namespace objects (This PC, Recycle Bin…) and shell:AppsFolder
        // launchers (packaged UWP apps, iQiyi, File Explorer…) all live behind a
        // shell token rather than a file-system exe.
        if (entry.IsShellItem)
        {
            LaunchShellItem(entry);
            return;
        }

        // Ordinary executable. If it is already running, bring its window forward
        // instead of starting a second instance — except the genuine File
        // Explorer (explorer.exe is also the desktop shell, whose "main window"
        // is the desktop/taskbar; activating it does nothing, so always open a
        // fresh Explorer window).
        if (!WindowPreviewService.IsFileExplorer(entry.Path, entry.Arguments))
        {
            try
            {
                if (RunningAppTracker.ActivateExisting(entry.Path, entry.Arguments))
                    return;
            }
            catch (System.Exception ex) { Log.Debug("AppLauncher", "activate existing window failed; launching fresh", ex); }
        }

        LaunchExecutable(entry);
    }

    private static void LaunchShellItem(AppEntry entry)
    {
        // File Explorer (and other shell-hosted AppsFolder items) resolve to the
        // always-running explorer.exe shell process; "activating" it does nothing
        // visible, so skip activation and always open fresh. Other AppsFolder /
        // packaged apps own real windows, so bring an existing one forward.
        if (!WindowPreviewService.IsShellHostedLauncher(entry.Path, entry.Arguments))
        {
            try
            {
                if (RunningAppTracker.ActivateExisting(entry.Path, entry.Arguments))
                    return;
            }
            catch (System.Exception ex) { Log.Debug("AppLauncher", "activate existing window failed; opening fresh", ex); }
        }

        // Non-packaged AppsFolder launchers (iQiyi, VS Code…) resolve to a real
        // executable; launching it directly is far more reliable than
        // `explorer.exe shell:AppsFolder\<id>`, which can silently no-op for
        // desktop-bridge apps. Genuine UWP apps resolve to null and fall through
        // to the shell launch below.
        if (ShellNamespace.TryLaunchAppsFolderTargetExe(entry.Path, entry.Arguments))
            return;

        try
        {
            ShellNamespace.Launch(entry.Path);
        }
        catch (Exception ex)
        {
            ShowError("无法打开", entry.Name, ex);
        }
    }

    private static void LaunchExecutable(AppEntry entry)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = entry.Path,
                Arguments = entry.Arguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(entry.WorkingDirectory)
                    ? Path.GetDirectoryName(entry.Path) ?? string.Empty
                    : entry.WorkingDirectory,
                UseShellExecute = true,
            };
            var started = Process.Start(psi);
            RunningAppTracker.EnsureRestoredWhenReady(started);
        }
        catch (Exception ex)
        {
            ShowError("无法启动", entry.Name, ex);
        }
    }

    private static void ShowError(string verb, string name, Exception ex) =>
        MessageBox.Show($"{verb} {name}:\n{ex.Message}", "Polaris",
            MessageBoxButton.OK, MessageBoxImage.Warning);
}
