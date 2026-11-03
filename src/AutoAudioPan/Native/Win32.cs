using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AutoAudioPan.Native;

public sealed record WindowInfo(IntPtr Handle, uint ProcessId, string Title, double Left, double Right, bool Minimized);

public static class Win32
{
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int idx);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr s, ref ProcessEntry e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr s, ref ProcessEntry e);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId; public UIntPtr DefaultHeapId; public uint ModuleId, Threads, ParentProcessId;
        public int PriClassBase; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    public static (double Left, double Width) VirtualDesktop() =>
        (GetSystemMetrics(76), GetSystemMetrics(78)); // SM_XVIRTUALSCREEN, SM_CXVIRTUALSCREEN

    public static Dictionary<uint, (uint Parent, string Exe)> ProcessTable()
    {
        var map = new Dictionary<uint, (uint, string)>();
        var snap = CreateToolhelp32Snapshot(2, 0);
        if (snap == new IntPtr(-1)) return map;
        try
        {
            var e = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                map[e.ProcessId] = (e.ParentProcessId, e.ExeFile);
        }
        finally { CloseHandle(snap); }
        return map;
    }

    /// <summary>Topmost ancestor that still runs the same executable (e.g. Chrome's main process).</summary>
    public static uint RootOfTree(uint pid, Dictionary<uint, (uint Parent, string Exe)> table)
    {
        if (!table.TryGetValue(pid, out var cur)) return pid;
        var exe = cur.Exe;
        uint root = pid;
        for (int i = 0; i < 16; i++)
        {
            if (!table.TryGetValue(root, out var info)) break;
            if (!table.TryGetValue(info.Parent, out var parent) || !parent.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)) break;
            root = info.Parent;
        }
        return root;
    }

    public static List<WindowInfo> TopLevelWindows()
    {
        var list = new List<WindowInfo>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            if ((GetWindowLong(h, -20) & 0x80) != 0) return true; // WS_EX_TOOLWINDOW
            if (DwmGetWindowAttribute(h, 14, out int cloaked, 4) == 0 && cloaked != 0) return true;
            if (!GetWindowRect(h, out var r)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            if (sb.Length == 0) return true;
            list.Add(new WindowInfo(h, pid, sb.ToString(), r.Left, r.Right, IsIconic(h)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static string FriendlyName(string exe)
    {
        var n = System.IO.Path.GetFileNameWithoutExtension(exe);
        return n.Length == 0 ? exe : char.ToUpper(n[0]) + n[1..];
    }

    public static string? ExePath(uint pid)
    {
        try { return Process.GetProcessById((int)pid).MainModule?.FileName; } catch { return null; }
    }
}
