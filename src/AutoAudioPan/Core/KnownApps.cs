namespace AutoAudioPan.Core;

public enum KnownSource { Seed, Learned, User }

public sealed class KnownAppEntry
{
    public string Exe { get; set; } = "";
    public KnownSource Source { get; set; } = KnownSource.User;
}

/// <summary>Registry of executables known to make sound, so they can be routed before their first sound.</summary>
public sealed class KnownApps
{
    public static readonly string[] SeedExes =
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe",
        "spotify.exe", "discord.exe", "slack.exe", "teams.exe", "ms-teams.exe", "zoom.exe",
        "outlook.exe", "olk.exe", "code.exe", "code - insiders.exe", "vlc.exe", "wmplayer.exe",
        "mpc-hc64.exe", "steam.exe", "telegram.exe", "whatsapp.exe", "signal.exe",
        "githubcopilot.exe", "github copilot.exe", "copilot.exe", "github desktop.exe",
        "applemusic.exe", "itunes.exe", "obs64.exe", "skype.exe", "webex.exe"
    };

    private readonly List<KnownAppEntry> _entries;

    public KnownApps(List<KnownAppEntry> entries) => _entries = entries;

    public IReadOnlyList<KnownAppEntry> Entries => _entries;

    /// <summary>Adds seed entries once; returns true if the list changed.</summary>
    public bool Seed()
    {
        bool changed = false;
        foreach (var e in SeedExes) changed |= Register(e, KnownSource.Seed);
        return changed;
    }

    public bool IsKnown(string exe) => _entries.Any(e => e.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase));

    public bool Register(string exe, KnownSource source)
    {
        exe = exe.Trim();
        if (exe.Length == 0 || IsKnown(exe)) return false;
        _entries.Add(new KnownAppEntry { Exe = exe, Source = source });
        return true;
    }

    public bool Remove(string exe) =>
        _entries.RemoveAll(e => e.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)) > 0;
}

/// <summary>Resolves which parent group a child row belongs to.</summary>
public static class Grouping
{
    public const string UserPrefix = "user:";

    public static string GroupId(string exe, string? customGroup) =>
        string.IsNullOrWhiteSpace(customGroup) ? exe.ToLowerInvariant() : customGroup;

    public static string NewUserGroupId(string name) => UserPrefix + name.Trim();

    public static string DisplayName(string groupId, string exeFriendly, IReadOnlyDictionary<string, string> renames)
    {
        if (renames.TryGetValue(groupId, out var n) && !string.IsNullOrWhiteSpace(n)) return n;
        return groupId.StartsWith(UserPrefix, StringComparison.Ordinal) ? groupId[UserPrefix.Length..] : exeFriendly;
    }
}
