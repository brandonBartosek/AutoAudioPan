using System.Windows;
using AutoAudioPan.Core;
using H.NotifyIcon;
using Microsoft.Win32;

namespace AutoAudioPan;

public partial class App : Application
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "AutoAudioPan";

    private Mutex? _mutex;
    private AudioManager? _manager;
    private TaskbarIcon? _tray;
    private MainWindow? _window;
    private bool _exiting;

    public Settings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, "AutoAudioPan.SingleInstance", out bool first);
        if (!first) { Shutdown(); return; }

        Settings = Settings.Load();
        _manager = new AudioManager(Settings);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _manager?.Dispose();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _manager?.Dispose();
        _window = new MainWindow(_manager, Settings);
        _window.Closing += (_, ev) =>
        {
            if (_exiting) return;
            ev.Cancel = true;
            _window.Hide();
        };

        _tray = new TaskbarIcon
        {
            ToolTipText = "AutoAudioPan",
            Icon = CreateTrayIcon(),
            ContextMenu = BuildMenu()
        };
        _tray.ForceCreate();
        _tray.TrayMouseDoubleClick += (_, _) => ShowWindow();

        if (!(Settings.StartMinimized && e.Args.Contains("--tray"))) ShowWindow();
    }

    private static System.Drawing.Icon CreateTrayIcon()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0, 120, 212));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 3);
            g.DrawLine(pen, 9, 16, 23, 16);
            g.FillEllipse(System.Drawing.Brushes.White, 6, 13, 6, 6);
            g.FillEllipse(System.Drawing.Brushes.White, 20, 13, 6, 6);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private System.Windows.Controls.ContextMenu BuildMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        var open = new System.Windows.Controls.MenuItem { Header = "Open" };
        open.Click += (_, _) => ShowWindow();
        var exit = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(open);
        menu.Items.Add(exit);
        return menu;
    }

    private void ShowWindow()
    {
        _window!.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void ExitApp()
    {
        _exiting = true;
        _tray?.Dispose();
        _manager?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _manager?.Dispose();
        base.OnExit(e);
    }

    public static void SetAutostart(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key == null) return;
        if (enable) key.SetValue(RunName, $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue(RunName, false);
    }
}
