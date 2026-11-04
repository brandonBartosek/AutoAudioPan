using System.Collections.ObjectModel;
using System.Drawing;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AutoAudioPan.Audio;
using AutoAudioPan.Native;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AutoAudioPan.Core;

public sealed class AudioManager : IDisposable
{
    private static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan RowTtl = TimeSpan.FromSeconds(20);

    private readonly Settings _settings;
    private readonly SessionSilencer _silencer = new();
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<uint, AppRow> _byRoot = new();
    private readonly HashSet<uint> _presented = new();
    private readonly Dictionary<string, IntPtr> _lastWindow = new(StringComparer.OrdinalIgnoreCase);
    private readonly AudibleWindowDetector _detector = new();
    private readonly ExtensionBridge _bridge = new();
    private readonly OpenCvHeadYawProvider _headYawProvider = new();
    private readonly HeadYawFilter _headYawFilter = new();
    private int _tick;

    public ObservableCollection<AppRow> Rows { get; } = new();
    public ObservableCollection<AppGroup> Groups { get; } = new();
    public KnownApps Known { get; }
    public event Action? HeadTrackingStatusChanged;
    public string HeadTrackingStatus => _headYawProvider.Status;
    private string _lastHeadTrackingStatus = "";

    public AudioManager(Settings settings)
    {
        _settings = settings;
        Known = new KnownApps(settings.KnownApps);
        if (!settings.KnownAppsSeeded) { Known.Seed(); settings.KnownAppsSeeded = true; settings.Save(); }
        SessionSilencer.RecoverFromCrash();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        if (settings.HeadTracking.Enabled) _headYawProvider.Start();
    }

    public void SetHeadTrackingEnabled(bool enabled)
    {
        _settings.HeadTracking.Enabled = enabled;
        if (enabled) _headYawProvider.Start();
        else
        {
            _headYawProvider.Stop();
            _headYawFilter.Reset();
        }
        _settings.Save();
    }

    public void CalibrateHeadTracking()
    {
        _headYawProvider.CalibrateNeutral();
        _headYawFilter.Reset();
        _settings.Save();
    }

    private void Tick()
    {
        try
        {
            var windows = Win32.TopLevelWindows();
            if (_settings.Apps.TryGetValue("chrome.exe", out var cs))
                _bridge.Config = new ExtensionConfig(cs.Mode.ToString(), cs.ManualPan, cs.Volume, cs.Muted);
            UpdateHeadTracking();
            if (_tick++ % 2 == 0) RefreshSessions();
            UpdatePan(windows);
            EnsurePipelines();
            var status = _headYawProvider.Status;
            if (!status.Equals(_lastHeadTrackingStatus, StringComparison.Ordinal))
            {
                _lastHeadTrackingStatus = status;
                HeadTrackingStatusChanged?.Invoke();
            }
        }
        catch (Exception ex) { Log.Error("Tick", ex); }
    }

    private void UpdateHeadTracking()
    {
        var settings = _settings.HeadTracking;
        if (!settings.Enabled || !_headYawProvider.IsAvailable)
        {
            _headYawFilter.Reset();
            _bridge.HeadTracking = new HeadTrackingBridgeState(
                settings.Enabled, false, 0, settings.MaxCorrectionDegrees);
            return;
        }

        var yaw = _headYawFilter.Update(
            _headYawProvider.CurrentYawDegrees,
            settings.DeadZoneDegrees,
            settings.Smoothing);
        _bridge.HeadTracking = new HeadTrackingBridgeState(
            true, true, yaw, settings.MaxCorrectionDegrees);
    }

    private AppRow CreateRow(uint root, string exe)
    {
        var tag = exe.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase) ? Win32.ChromeProfileTag(root) : null;
        var row = new AppRow
        {
            RootPid = root, ExeName = exe, SettingsKey = tag == null ? exe : $"{exe}#{tag}",
            DisplayName = tag == null ? Win32.FriendlyName(exe) : $"Chrome ({tag.Replace("Window", "separate window ")})",
            Icon = LoadIcon(root),
            Pinned = true
        };
        row.Load(_settings.For(row.SettingsKey));
        row.SettingChanged += () => { row.Store(_settings.For(row.SettingsKey)); _settings.Save(); };
        _byRoot[root] = row;
        Log.Info($"row {exe} root={root}");
        return row;
    }

    private string GroupName(AppRow row) =>
        Grouping.DisplayName(row.GroupId, Win32.FriendlyName(row.ExeName), _settings.GroupNames);

    private void AddToGroup(AppRow row)
    {
        if (!_presented.Add(row.RootPid)) return;
        Rows.Add(row);
        var g = Groups.FirstOrDefault(x => x.Id.Equals(row.GroupId, StringComparison.OrdinalIgnoreCase));
        if (g == null) { g = new AppGroup(row.GroupId, GroupName(row)); Groups.Add(g); }
        g.Children.Add(row);
        g.RefreshIcon();
    }

    private void RemoveFromGroup(AppRow row)
    {
        _presented.Remove(row.RootPid);
        Rows.Remove(row);
        foreach (var g in Groups.ToList())
            if (g.Children.Remove(row) && g.Children.Count == 0) Groups.Remove(g);
    }

    /// <summary>Moves a child to a user group (null = back to its default per-app group).</summary>
    public void MoveToGroup(AppRow row, string? groupName)
    {
        RemoveFromGroup(row);
        row.CustomGroup = string.IsNullOrWhiteSpace(groupName) ? null : Grouping.NewUserGroupId(groupName);
        row.Store(_settings.For(row.SettingsKey));
        _settings.Save();
        AddToGroup(row);
    }

    public void RenameGroup(AppGroup g, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _settings.GroupNames[g.Id] = name.Trim();
        g.Name = name.Trim();
        _settings.Save();
    }

    public void AddKnown(string exe)
    {
        if (Known.Register(exe, KnownSource.User)) _settings.Save();
    }

    public void RemoveKnown(string exe)
    {
        if (Known.Remove(exe)) _settings.Save();
    }

    private void RefreshSessions()
    {
        var table = Win32.ProcessTable();
        var sessions = SessionSilencer.Enumerate();
        var now = DateTime.UtcNow;
        var self = (uint)Environment.ProcessId;

        foreach (var r in _byRoot.Values) { r.AllHeld = true; r.HadSession = false; }

        foreach (var s in sessions)
        {
            uint pid;
            try { pid = s.GetProcessID; } catch { continue; }
            if (pid == 0 || pid == self || !table.TryGetValue(pid, out var info)) continue;
            var root = Win32.RootOfTree(pid, table);
            if (!_byRoot.TryGetValue(root, out var row))
            {
                var exe = table.TryGetValue(root, out var ri) ? ri.Exe : info.Exe;
                if (s.State != AudioSessionState.AudioSessionStateActive && !Known.IsKnown(exe)) continue;
                if (Known.Register(exe, KnownSource.Learned)) _settings.Save();
                row = CreateRow(root, exe);
            }
            row.HadSession = true;
            row.LastSeen = now;
            if (s.State == AudioSessionState.AudioSessionStateActive) row.LastActive = now;
            if (row.Pipeline != null && !_silencer.IsHeld(s))
            {
                try { _silencer.Silence(s); } catch { row.AllHeld = false; }
            }
        }

        // Pre-register running known apps so capture is ready before their first sound.
        foreach (var (pid, p) in table)
        {
            if (pid == self || !Known.IsKnown(p.Exe) || Win32.RootOfTree(pid, table) != pid) continue;
            if (_byRoot.TryGetValue(pid, out var existing)) { existing.LastSeen = now; continue; }
            var row = CreateRow(pid, p.Exe);
            row.LastActive = DateTime.MinValue;
            row.LastSeen = now;
        }

        foreach (var row in _byRoot.Values.ToList())
        {
            row.HeldOnce = row.Pipeline != null && row.HadSession;
            row.Active = now - row.LastActive < IdleGrace;
            if (RowVisibility.ShouldDisplay(row.Active, false)) AddToGroup(row);
            else RemoveFromGroup(row);
            if (now - row.LastSeen > RowTtl)
            {
                StopPipeline(row, table);
                _byRoot.Remove(row.RootPid);
            }
        }
    }

    private void UpdatePan(List<WindowInfo> windows)
    {
        var table = _byRoot.Count == 0 ? null : Win32.ProcessTable();
        var (dl, dw) = Win32.VirtualDesktop();
        var fg = Win32.GetForegroundWindow();

        var candidates = new List<IntPtr>();

        foreach (var row in _byRoot.Values)
        {
            // Only the user's main Chrome (not the separate-profile windows) has the extension installed.
            row.ExtensionHandled = _bridge.Connected && row.SettingsKey.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase);
            if (row.ExtensionHandled)
            {
                row.WindowTitle = "Per-window pan via browser extension";
                row.CurrentPan = Math.Round(_bridge.LastPan, 2);
                continue;
            }

            var mine = windows.Where(w => table!.TryGetValue(w.ProcessId, out var p)
                                          && p.Exe.Equals(row.ExeName, StringComparison.OrdinalIgnoreCase)
                                          && Win32.RootOfTree(w.ProcessId, table) == row.RootPid).ToList();
            if (mine.Count == 0) continue;
            candidates.AddRange(mine.Where(AudibleWindowDetector.IsCandidate).Select(w => w.Handle));

            // Windows whose tab strip reports "Audio playing" are the real sources.
            var audible = new List<(WindowInfo Win, string Title)>();
            foreach (var w in mine)
                if (_detector.TryGet(w.Handle, out var tab) && !string.IsNullOrEmpty(tab))
                    audible.Add((w, tab));

            List<WindowInfo> sources;
            string title;
            if (audible.Count > 0)
            {
                sources = audible.Select(a => a.Win).ToList();
                title = audible[0].Title + (audible.Count > 1 ? $"  (+{audible.Count - 1} more window{(audible.Count > 2 ? "s" : "")})" : "");
            }
            else
            {
                // No tab info: foreground, else last foreground, else first.
                var w0 = mine.FirstOrDefault(w => w.Handle == fg);
                if (w0 != null) _lastWindow[row.SettingsKey] = w0.Handle;
                else if (_lastWindow.TryGetValue(row.SettingsKey, out var last)) w0 = mine.FirstOrDefault(w => w.Handle == last);
                w0 ??= mine[0];
                sources = new List<WindowInfo> { w0 };
                title = w0.Title;
            }

            row.WindowTitle = title;
            var visible = sources.Where(w => !w.Minimized).ToList();
            if (row.Mode == PanMode.Auto && visible.Count > 0)
            {
                // One audio stream per browser process: with several audible windows the best we can do is the average.
                var pan = visible.Average(w => PanMath.FromWindow(w.Left, w.Right, dl, dw));
                if (_settings.HeadTracking.Enabled && _headYawProvider.IsAvailable)
                    pan = (float)HeadYawMath.CorrectPan(
                        pan, _headYawFilter.CurrentDegrees, _settings.HeadTracking.MaxCorrectionDegrees);
                row.CurrentPan = Math.Round(pan, 2);
            }
        }

        _detector.SetCandidates(candidates);
    }

    private void EnsurePipelines()
    {
        foreach (var row in _byRoot.Values)
        {
            bool wanted = (row.Active || row.Pinned) && !row.ExtensionHandled
                          && (row.Mode != PanMode.Off || row.Volume < 100 || row.Muted);
            if (wanted && row.Pipeline == null)
            {
                try
                {
                    row.Pipeline = new AppPipeline(row.RootPid);
                    row.Pipeline.Failed += ex => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { Log.Error("PipelineFailed", ex); StopPipeline(row, Win32.ProcessTable()); });
                }
                catch (Exception ex) { Log.Error("StartPipeline", ex); row.Pipeline = null; continue; }
                SilenceNow(row);
                Log.Info($"pipeline started {row.ExeName}");
            }
            else if (!wanted && row.Pipeline != null)
            {
                StopPipeline(row, Win32.ProcessTable());
            }

            if (row.Pipeline != null)
            {
                row.Pipeline.TargetPan = (float)row.EffectivePan;
                row.Pipeline.Volume = (float)(row.Volume / 100.0);
                row.Pipeline.Muted = row.Muted;
                row.Pipeline.InputGain = row.AllHeld && row.HeldOnce ? 1f / SessionSilencer.Attenuation : 1f;
            }
        }
    }

    // Silence the original sessions immediately so the app isn't heard directly alongside the pipeline.
    private void SilenceNow(AppRow row)
    {
        try
        {
            var table = Win32.ProcessTable();
            foreach (var s in SessionSilencer.Enumerate())
            {
                uint pid;
                try { pid = s.GetProcessID; } catch { continue; }
                if (pid == 0 || !table.ContainsKey(pid) || Win32.RootOfTree(pid, table) != row.RootPid) continue;
                try { _silencer.Silence(s); row.HadSession = true; } catch { row.AllHeld = false; }
            }
            row.HeldOnce = row.HadSession;
        }
        catch (Exception ex) { Log.Error("SilenceNow", ex); }
    }

    private void StopPipeline(AppRow row, Dictionary<uint, (uint Parent, string Exe)> table)
    {
        if (row.Pipeline == null) return;
        row.Pipeline.Dispose();
        row.Pipeline = null;
        var pids = table.Keys.Where(p => Win32.RootOfTree(p, table) == row.RootPid).Select(p => (int)p).Append((int)row.RootPid);
        _silencer.Restore(pids);
    }

    private static ImageSource? LoadIcon(uint pid)
    {
        try
        {
            var path = Win32.ExePath(pid);
            if (path == null) return null;
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _timer.Stop();
        _headYawProvider.Dispose();
        _detector.Dispose();
        _bridge.Dispose();
        foreach (var r in _byRoot.Values) { r.Pipeline?.Dispose(); r.Pipeline = null; }
        _silencer.RestoreAll();
    }
}
