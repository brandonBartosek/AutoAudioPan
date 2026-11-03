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
        RowsList.ItemsSource = manager.Rows;
        manager.Rows.CollectionChanged += (_, _) => UpdateEmpty();
        AutostartToggle.IsChecked = settings.StartWithWindows;
        _loading = false;
        UpdateEmpty();
    }

    private void UpdateEmpty() =>
        EmptyText.Visibility = _manager.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = AutostartToggle.IsChecked == true;
        _settings.StartWithWindows = on;
        _settings.StartMinimized = on;
        _settings.Save();
        App.SetAutostart(on);
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
