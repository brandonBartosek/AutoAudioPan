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
    private readonly Dictionary<string, IntPtr> _lastWindow = new(StringComparer.OrdinalIgnoreCase);
    private int _tick;

    public ObservableCollection<AppRow> Rows { get; } = new();

    public AudioManager(Settings settings)
    {
        _settings = settings;
        SessionSilencer.RecoverFromCrash();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Tick()
    {
        try
        {
            var windows = Win32.TopLevelWindows();
            if (_tick++ % 5 == 0) RefreshSessions();
            UpdatePan(windows);
            EnsurePipelines();
        }
        catch (Exception ex) { Log.Error("Tick", ex); }
    }

    private void RefreshSessions()
    {
        var table = Win32.ProcessTable();
        var sessions = SessionSilencer.Enumerate();
        var now = DateTime.UtcNow;
        var self = (uint)Environment.ProcessId;

        foreach (var r in _byRoot.Values) r.AllHeld = true;

        foreach (var s in sessions)
        {
            uint pid;
            try { pid = s.GetProcessID; } catch { continue; }
            if (pid == 0 || pid == self || !table.TryGetValue(pid, out var info)) continue;
            var root = Win32.RootOfTree(pid, table);
            if (!_byRoot.TryGetValue(root, out var row))
            {
                if (s.State != AudioSessionState.AudioSessionStateActive) continue;
                var exe = table.TryGetValue(root, out var ri) ? ri.Exe : info.Exe;
                row = new AppRow
                {
                    RootPid = root, ExeName = exe, DisplayName = Win32.FriendlyName(exe), Icon = LoadIcon(root)
                };
                row.Load(_settings.For(exe));
                row.SettingChanged += () => { row.Store(_settings.For(row.ExeName)); _settings.Save(); };
                _byRoot[root] = row;
                Log.Info($"row {exe} root={root}");
                Rows.Add(row);
            }
            row.LastSeen = now;
            if (s.State == AudioSessionState.AudioSessionStateActive) row.LastActive = now;
            if (row.Pipeline != null && !_silencer.IsHeld(s))
            {
                try { _silencer.Silence(s); } catch { row.AllHeld = false; }
            }
        }

        foreach (var row in _byRoot.Values.ToList())
        {
            row.HeldOnce = row.Pipeline != null;
            row.Active = now - row.LastActive < IdleGrace;
            if (now - row.LastSeen > RowTtl)
            {
                StopPipeline(row, table);
                _byRoot.Remove(row.RootPid);
                Rows.Remove(row);
            }
        }
    }

    private void UpdatePan(List<WindowInfo> windows)
    {
        var table = _byRoot.Count == 0 ? null : Win32.ProcessTable();
        var (dl, dw) = Win32.VirtualDesktop();
        var fg = Win32.GetForegroundWindow();

        foreach (var row in _byRoot.Values)
        {
            var mine = windows.Where(w => table!.TryGetValue(w.ProcessId, out var p)
                                          && p.Exe.Equals(row.ExeName, StringComparison.OrdinalIgnoreCase)
                                          && Win32.RootOfTree(w.ProcessId, table) == row.RootPid).ToList();
            if (mine.Count == 0) continue;

            // Audible-window heuristic: foreground, else last foreground, else first.
            var w0 = mine.FirstOrDefault(w => w.Handle == fg);
            if (w0 != null) _lastWindow[row.ExeName] = w0.Handle;
            else if (_lastWindow.TryGetValue(row.ExeName, out var last)) w0 = mine.FirstOrDefault(w => w.Handle == last);
            w0 ??= mine[0];

            row.WindowTitle = w0.Title;
            if (row.Mode == PanMode.Auto && !w0.Minimized)
                row.CurrentPan = Math.Round(PanMath.FromWindow(w0.Left, w0.Right, dl, dw), 2);
        }
    }

    private void EnsurePipelines()
    {
        foreach (var row in _byRoot.Values)
        {
            bool wanted = row.Active && (row.Mode != PanMode.Off || row.Volume < 100 || row.Muted);
            if (wanted && row.Pipeline == null)
            {
                try
                {
                    row.Pipeline = new AppPipeline(row.RootPid);
                    row.Pipeline.Failed += ex => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { Log.Error("PipelineFailed", ex); StopPipeline(row, Win32.ProcessTable()); });
                }
                catch (Exception ex) { Log.Error("StartPipeline", ex); row.Pipeline = null; continue; }
                _tick = 0; // silence original sessions on next refresh
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
        foreach (var r in _byRoot.Values) { r.Pipeline?.Dispose(); r.Pipeline = null; }
        _silencer.RestoreAll();
    }
}
