using System.Diagnostics;
using Microsoft.Win32;

namespace AutoAudioPan.Core;

/// <summary>Launches Chrome with a dedicated profile so its audio runs in a separate process tree.</summary>
public static class SeparateChrome
{
    private static string ProfilesRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoAudioPan", "ChromeProfiles");

    public static string? FindChrome()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            var p = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe")?.GetValue(null) as string;
            if (p != null && File.Exists(p)) return p;
        }
        foreach (var env in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData })
        {
            var p = Path.Combine(Environment.GetFolderPath(env), @"Google\Chrome\Application\chrome.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Chrome holds an exclusive lock on "lockfile" while a profile is running.</summary>
    private static bool InUse(string dir)
    {
        var lockFile = Path.Combine(dir, "lockfile");
        if (!File.Exists(lockFile)) return false;
        try { using var fs = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
    }

    public static string? Launch()
    {
        var chrome = FindChrome();
        if (chrome == null) return "Chrome was not found.";
        for (int i = 1; i <= 20; i++)
        {
            var dir = Path.Combine(ProfilesRoot, $"Window{i}");
            if (InUse(dir)) continue;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(chrome, $"--user-data-dir=\"{dir}\" --no-first-run --new-window") { UseShellExecute = false });
            return null;
        }
        return "Too many separate Chrome windows are already open.";
    }
}
