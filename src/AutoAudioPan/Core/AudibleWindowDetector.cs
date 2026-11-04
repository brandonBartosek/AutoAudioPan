using System.Windows.Automation;
using AutoAudioPan.Native;

namespace AutoAudioPan.Core;

/// <summary>
/// Chromium browsers mark audible tabs with "Audio playing" in the tab's accessible name.
/// Polls on a background thread so UI Automation never blocks the UI.
/// </summary>
public sealed class AudibleWindowDetector : IDisposable
{
    private const string ChromiumClass = "Chrome_WidgetWin_1";
    private volatile Dictionary<IntPtr, string?> _result = new();
    private volatile List<IntPtr> _candidates = new();
    private readonly CancellationTokenSource _cts = new();

    public AudibleWindowDetector()
    {
        var t = new Thread(Loop) { IsBackground = true, Name = "AudibleDetector" };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
    }

    public static bool IsCandidate(WindowInfo w) => w.ClassName == ChromiumClass;

    public void SetCandidates(IEnumerable<IntPtr> handles) => _candidates = handles.ToList();

    /// <summary>null = unknown (no tab strip found); "" = known, not audible; otherwise the audible tab title.</summary>
    public bool TryGet(IntPtr hwnd, out string? audibleTab) => _result.TryGetValue(hwnd, out audibleTab);

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            var next = new Dictionary<IntPtr, string?>();
            foreach (var h in _candidates.ToList())
            {
                try { next[h] = Probe(h); } catch { }
            }
            _result = next;
            _cts.Token.WaitHandle.WaitOne(700);
        }
    }

    private static string? Probe(IntPtr hwnd)
    {
        var win = AutomationElement.FromHandle(hwnd);
        var tabs = win.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
        if (tabs.Count == 0) return null;
        foreach (AutomationElement tab in tabs)
        {
            var name = tab.Current.Name ?? "";
            if (name.EndsWith("Audio playing", StringComparison.OrdinalIgnoreCase))
                return name[..^"Audio playing".Length].TrimEnd(' ', '-');
        }
        return "";
    }

    public void Dispose() => _cts.Cancel();
}
