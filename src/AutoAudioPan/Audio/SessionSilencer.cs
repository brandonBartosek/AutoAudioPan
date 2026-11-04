using System.Text.Json;
using NAudio.CoreAudioApi;

namespace AutoAudioPan.Audio;

/// <summary>Mutes the original app sessions while we re-render them, and always restores them.</summary>
public sealed class SessionSilencer
{
    /// <summary>Loopback capture is taken after session volume, so originals are attenuated (inaudible) and the pipeline restores the gain.</summary>
    public const float Attenuation = 0.002f;
    private readonly Dictionary<string, (AudioSessionControl Session, float Original)> _held = new();
    private static string RecoveryPath => Path.Combine(Path.GetTempPath(), "AutoAudioPan.recovery.json");

    public static List<AudioSessionControl> Enumerate()
    {
        var result = new List<AudioSessionControl>();
        using var en = new MMDeviceEnumerator();
        var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = dev.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++) result.Add(sessions[i]);
        return result;
    }

    public void Silence(AudioSessionControl s)
    {
        var id = s.GetSessionInstanceIdentifier;
        if (_held.ContainsKey(id)) return;
        var vol = s.SimpleAudioVolume;
        _held[id] = (s, vol.Volume);
        vol.Volume = vol.Volume * Attenuation;
        SaveRecovery();
    }

    public bool IsHeld(AudioSessionControl s) => _held.ContainsKey(s.GetSessionInstanceIdentifier);

    public void RestoreAll()
    {
        foreach (var (s, orig) in _held.Values)
            try { s.SimpleAudioVolume.Volume = orig; } catch { }
        _held.Clear();
        try { File.Delete(RecoveryPath); } catch { }
    }

    public void Restore(IEnumerable<int> processIds)
    {
        var set = processIds.ToHashSet();
        foreach (var kv in _held.ToList())
        {
            uint pid;
            try { pid = kv.Value.Session.GetProcessID; } catch { _held.Remove(kv.Key); continue; }
            if (!set.Contains((int)pid)) continue;
            try { kv.Value.Session.SimpleAudioVolume.Volume = kv.Value.Original; } catch { }
            _held.Remove(kv.Key);
        }
        SaveRecovery();
    }

    private void SaveRecovery()
    {
        try
        {
            if (_held.Count == 0) { File.Delete(RecoveryPath); return; }
            File.WriteAllText(RecoveryPath, JsonSerializer.Serialize(_held.ToDictionary(k => k.Key, k => k.Value.Original)));
        }
        catch { }
    }

    /// <summary>After a crash, put back volumes we had muted.</summary>
    public static void RecoverFromCrash()
    {
        try
        {
            if (!File.Exists(RecoveryPath)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, float>>(File.ReadAllText(RecoveryPath));
            if (saved != null)
                foreach (var s in Enumerate())
                    if (saved.TryGetValue(s.GetSessionInstanceIdentifier, out var v))
                        try { s.SimpleAudioVolume.Volume = v; } catch { }
            File.Delete(RecoveryPath);
        }
        catch { }
    }
}
