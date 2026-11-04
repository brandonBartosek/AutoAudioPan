using System.Windows;
using System.Windows.Controls;
using AutoAudioPan.Core;
using Wpf.Ui.Controls;

namespace AutoAudioPan;

public partial class MainWindow : FluentWindow
{
    private readonly AudioManager _manager;
    private readonly Settings _settings;
    private bool _loading = true;

    public MainWindow(AudioManager manager, Settings settings)
    {
        _manager = manager;
        _settings = settings;
        InitializeComponent();
        RowsList.ItemsSource = manager.Groups;
        manager.Groups.CollectionChanged += (_, _) => UpdateEmpty();
        RefreshKnown();
        AutostartToggle.IsChecked = settings.StartWithWindows;
        HeadTrackingToggle.IsChecked = settings.HeadTracking.Enabled;
        TrackingModeCombo.SelectedIndex = settings.HeadTracking.GazeAssistEnabled ? 1 : 0;
        FocusAttenuationToggle.IsChecked = settings.HeadTracking.FocusAttenuationEnabled;
        ShowHiddenToggle.IsChecked = manager.ShowHiddenApps;
        RefreshHeadTrackingStatus();
        manager.HeadTrackingStatusChanged += RefreshHeadTrackingStatus;
        _loading = false;
        UpdateEmpty();
    }

    private void RefreshKnown()
    {
        KnownList.ItemsSource = null;
        KnownList.ItemsSource = _manager.Known.Entries.OrderBy(e => e.Exe, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void AddKnown_Click(object sender, RoutedEventArgs e)
    {
        _manager.AddKnown(KnownInput.Text);
        KnownInput.Text = "";
        RefreshKnown();
    }

    private void RemoveKnown_Click(object sender, RoutedEventArgs e)
    {
        if (KnownList.SelectedItem is KnownAppEntry k) { _manager.RemoveKnown(k.Exe); RefreshKnown(); }
    }

    private static string? Prompt(string title, string initial)
    {
        var box = new System.Windows.Controls.TextBox { Text = initial, Margin = new Thickness(12) };
        var ok = new System.Windows.Controls.Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(12, 0, 12, 12), HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new System.Windows.Controls.StackPanel();
        panel.Children.Add(box);
        panel.Children.Add(ok);
        var win = new Window { Title = title, Content = panel, Width = 320, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        ok.Click += (_, _) => win.DialogResult = true;
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : null;
    }

    private void RenameGroup_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AppGroup g) return;
        var name = Prompt("Rename group", g.Name);
        if (name != null) _manager.RenameGroup(g, name);
    }

    private void MoveToGroup_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AppRow row) return;
        var name = Prompt("Move to group (new or existing name)", "");
        if (name != null) _manager.MoveToGroup(row, name);
    }

    private void ResetGroup_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is AppRow row) _manager.MoveToGroup(row, null);
    }

    private void UpdateEmpty() =>
        EmptyText.Visibility = _manager.Groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = AutostartToggle.IsChecked == true;
        _settings.StartWithWindows = on;
        _settings.StartMinimized = on;
        _settings.Save();
        App.SetAutostart(on);
    }

    private void HeadTracking_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.HeadTracking.Enabled = HeadTrackingToggle.IsChecked == true;
        _settings.Save();
        _manager.SetHeadTrackingEnabled(_settings.HeadTracking.Enabled);
    }

    private void HeadTrackingCalibrate_Click(object sender, RoutedEventArgs e)
    {
        _manager.CalibrateHeadTracking();
    }

    private void TrackingMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TrackingModeCombo.SelectedIndex < 0) return;
        _settings.HeadTracking.GazeAssistEnabled = TrackingModeCombo.SelectedIndex == 1;
        _settings.Save();
    }

    private void FocusAttenuation_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.HeadTracking.FocusAttenuationEnabled = FocusAttenuationToggle.IsChecked == true;
        _settings.Save();
    }

    private void ShowHidden_Changed(object sender, RoutedEventArgs e)
    {
        _manager.ShowHiddenApps = ShowHiddenToggle.IsChecked == true;
    }

    private void RefreshHeadTrackingStatus()
    {
        var d = _manager.HeadTrackingDiagnostics;
        HeadTrackingStatus.Text = _manager.HeadTrackingStatus;
        var age = d.SampleAge == TimeSpan.MaxValue ? "n/a" : $"{d.SampleAge.TotalMilliseconds:0} ms";
        HeadTrackingDetails.Text =
            $"State: {d.Mode}; yaw: {d.YawDegrees:+0.0;-0.0;0.0}°; confidence: {d.Confidence:0.00}; " +
            $"last sample: {age}; pan cap: {_settings.HeadTracking.MaxPanMagnitude:0.00}; " +
            $"focus: {(_settings.HeadTracking.FocusAttenuationEnabled ? "on" : "off")}; " +
            $"gaze: {(_manager.GazeDiagnostics.Mode)} " +
            $"confidence {_manager.GazeDiagnostics.Confidence:0.00}, dwell {_manager.GazeDiagnostics.DwellProgress:P0}. " +
            "Camera frames are processed locally and never saved or transmitted.";
    }

    // The slider is read-only in Auto/Off; user drags only apply in Manual mode.
    private void PanSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var slider = (Slider)sender;
        if (slider.IsEnabled && (slider.IsMouseCaptureWithin || slider.IsKeyboardFocusWithin))
            if (slider.DataContext is AppRow row && row.Mode == PanMode.Manual)
                row.ManualPan = e.NewValue;
    }
}
