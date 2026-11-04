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
    private readonly HeadTrackingStabilizer _headTracking = new();
    private readonly GazeAssistStabilizer _gazeAssist = new();
    private int _tick;
    private bool _showHiddenApps;
    private DateTime _calibratingUntil;

    public ObservableCollection<AppRow> Rows { get; } = new();
    public ObservableCollection<AppGroup> Groups { get; } = new();
    public KnownApps Known { get; }
    public event Action? HeadTrackingStatusChanged;
    public string HeadTrackingStatus => _headYawProvider.Status;
    public HeadTrackingSnapshot HeadTrackingDiagnostics { get; private set; } =
        new(HeadTrackingMode.Disabled, 0, 0, TimeSpan.Zero, false);
    public GazeAssistSnapshot GazeDiagnostics { get; private set; } =
        new(GazeAssistMode.Disabled, 0, 0, 0, TimeSpan.Zero, false);
    public bool ShowHiddenApps
    {
        get => _showHiddenApps;
        set
        {
            if (_showHiddenApps == value) return;
            _showHiddenApps = value;
            RefreshRowVisibility();
        }
    }

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
            _headTracking.Reset();
            _gazeAssist.Reset();
        }
        _settings.Save();
    }

    public void CalibrateHeadTracking()
    {
        _headYawProvider.CalibrateNeutral();
        _headTracking.Reset();
        _gazeAssist.Reset();
        _calibratingUntil = DateTime.UtcNow.AddSeconds(1);
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
            HeadTrackingStatusChanged?.Invoke();
        }
        catch (Exception ex) { Log.Error("Tick", ex); }
    }

    private void UpdateHeadTracking()
    {
        var settings = _settings.HeadTracking;
        HeadTrackingDiagnostics = _headTracking.Update(
            settings.Enabled,
            _headYawProvider.IsAvailable,
            _headYawProvider.HasSample,
            _headYawProvider.CurrentYawDegrees,
            _headYawProvider.Confidence,
            _headYawProvider.LastSampleUtc,
            DateTime.UtcNow,
            settings.DeadZoneDegrees,
            settings.Smoothing);
        if (settings.Enabled && DateTime.UtcNow <= _calibratingUntil)
            HeadTrackingDiagnostics = HeadTrackingDiagnostics with
            {
                Mode = HeadTrackingMode.Calibrating,
                CorrectionEnabled = false
            };
        GazeDiagnostics = _gazeAssist.Update(
            settings.Enabled && settings.GazeAssistEnabled,
            _headYawProvider.IsAvailable,
            _headYawProvider.HasGazeSample,
            _headYawProvider.CurrentGazeDirection,
            _headYawProvider.GazeConfidence,
            _headYawProvider.Blink,
            _headYawProvider.LastGazeSampleUtc,
            DateTime.UtcNow,
            settings.GazeConfidenceThreshold,
            TimeSpan.FromMilliseconds(Math.Clamp(settings.GazeDwellMilliseconds, 100, 2000)),
            TimeSpan.FromMilliseconds(Math.Clamp(settings.GazeRetentionMilliseconds, 250, 3000)),
            settings.GazeSmoothing);
        var gazeFocus = settings.GazeAssistEnabled && GazeDiagnostics.FocusConfirmed;
        _bridge.HeadTracking = new HeadTrackingBridgeState(
            settings.Enabled,
            HeadTrackingDiagnostics.CorrectionEnabled || gazeFocus,
            HeadTrackingDiagnostics.YawDegrees,
            settings.MaxCorrectionDegrees,
            settings.MaxPanMagnitude,
            settings.FocusAttenuationEnabled,
            settings.FocusFloor,
            settings.FocusWidth,
            settings.GazeAssistEnabled,
            gazeFocus,
            GazeDiagnostics.Direction,
            GazeDiagnostics.Confidence,
            GazeDiagnostics.DwellProgress,
            GazeDiagnostics.FocusConfirmed);
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
        row.SettingChanged += () =>
        {
            row.Store(_settings.For(row.SettingsKey));
            _settings.Save();
            RefreshRowVisibility();
        };
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
            if (s.State == AudioSessionState.AudioSessionStateActive)
            {
                row.LastActive = now;
                row.ProducedSoundThisSession = true;
            }
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
            if (RowVisibility.ShouldDisplay(row.Active, row.ProducedSoundThisSession, row.Hidden, ShowHiddenApps))
                AddToGroup(row);
            else RemoveFromGroup(row);
            if (now - row.LastSeen > RowTtl)
            {
                StopPipeline(row, table);
                _byRoot.Remove(row.RootPid);
            }
        }

    }

    private void RefreshRowVisibility()
    {
        foreach (var row in _byRoot.Values)
        {
            if (RowVisibility.ShouldDisplay(row.Active, row.ProducedSoundThisSession, row.Hidden, ShowHiddenApps))
                AddToGroup(row);
            else
                RemoveFromGroup(row);
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
                row.CurrentFocusGain = 1;
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
                var sourcePan = visible.Average(w => PanMath.FromWindow(w.Left, w.Right, dl, dw));
                double pan = sourcePan;
                if (_settings.HeadTracking.Enabled && HeadTrackingDiagnostics.CorrectionEnabled)
                    pan = HeadYawMath.CorrectPan(
                        sourcePan, HeadTrackingDiagnostics.YawDegrees, _settings.HeadTracking.MaxCorrectionDegrees);
                row.CurrentPan = Math.Round(
                    HeadYawMath.ApplyPanCap(pan, _settings.HeadTracking.MaxPanMagnitude), 2);
                var gazeFocus = _settings.HeadTracking.GazeAssistEnabled && GazeDiagnostics.FocusConfirmed;
                var focusDirection = gazeFocus
                    ? GazeDiagnostics.Direction
                    : HeadTrackingDiagnostics.YawDegrees / Math.Max(0.001, _settings.HeadTracking.MaxCorrectionDegrees);
                row.CurrentFocusGain = HeadYawMath.FocusGainForDirection(
                    sourcePan,
                    focusDirection,
                    _settings.HeadTracking.FocusAttenuationEnabled &&
                    (HeadTrackingDiagnostics.CorrectionEnabled || gazeFocus),
                    _settings.HeadTracking.FocusFloor,
                    _settings.HeadTracking.FocusWidth);
            }
            else row.CurrentFocusGain = 1;
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
                row.Pipeline.TargetPan = (float)HeadYawMath.ApplyPanCap(
                    row.EffectivePan, _settings.HeadTracking.MaxPanMagnitude);
                row.Pipeline.Volume = (float)(row.Volume / 100.0);
                row.Pipeline.FocusGain = (float)row.CurrentFocusGain;
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
