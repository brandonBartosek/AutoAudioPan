namespace AutoAudioPan.Core;

public static class Log
{
    private static readonly string Path_ = Path.Combine(Path.GetTempPath(), "AutoAudioPan.log");
    private static string _last = "";

    public static void Info(string msg)
    {
        try { File.AppendAllText(Path_, $"{DateTime.Now:O} {msg}{Environment.NewLine}"); } catch { }
    }

    public static void Error(string where, Exception ex)
    {
        var msg = $"{where}: {ex.GetType().Name} {ex.Message}";
        if (msg == _last) return;
        _last = msg;
        try { File.AppendAllText(Path_, $"{DateTime.Now:O} {msg}{Environment.NewLine}"); } catch { }
    }
}
