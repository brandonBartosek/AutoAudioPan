using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoAudioPan.Core;

public enum PanMode { Auto, Manual, Off }

public sealed class AppSetting
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PanMode Mode { get; set; } = PanMode.Auto;
    public double ManualPan { get; set; }
    public double Volume { get; set; } = 100;
    public bool Muted { get; set; }
}

public sealed class Settings
{
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public Dictionary<string, AppSetting> Apps { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static string FilePath
    {
        get
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var portable = Path.Combine(exeDir, "settings.json");
            if (File.Exists(portable) || CanWrite(exeDir)) return portable;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoAudioPan");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var p = Path.Combine(dir, ".w");
            File.WriteAllText(p, "");
            File.Delete(p);
            return true;
        }
        catch { return false; }
    }

    public static Settings Load()
    {
        try
        {
            var path = FilePath;
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }

    public AppSetting For(string exe)
    {
        if (!Apps.TryGetValue(exe, out var s)) Apps[exe] = s = new AppSetting();
        return s;
    }
}
