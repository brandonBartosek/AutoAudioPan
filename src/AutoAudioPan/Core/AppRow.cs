using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using AutoAudioPan.Audio;

namespace AutoAudioPan.Core;

public sealed class AppRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? SettingChanged;

    private PanMode _mode;
    private double _manualPan, _volume = 100, _currentPan;
    private bool _muted, _active;
    private string _windowTitle = "";

    public uint RootPid { get; set; }
    public string ExeName { get; init; } = "";
    public string SettingsKey { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public ImageSource? Icon { get; init; }
    public bool AllHeld { get; set; } = true;
    public bool HeldOnce { get; set; }
    public AppPipeline? Pipeline { get; set; }
    public DateTime LastActive { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public string WindowTitle { get => _windowTitle; set => Set(ref _windowTitle, value); }
    public bool Active { get => _active; set { if (Set(ref _active, value)) Raise(nameof(IsIdle)); } }

    /// <summary>True while the browser extension pans this app per window, so the capture pipeline is not used.</summary>
    public bool ExtensionHandled { get; set; }

    public bool IsAuto { get => _mode == PanMode.Auto; set { if (value) Mode = PanMode.Auto; } }
    public bool IsManual { get => _mode == PanMode.Manual; set { if (value) Mode = PanMode.Manual; } }
    public bool IsOff { get => _mode == PanMode.Off; set { if (value) Mode = PanMode.Off; } }

    public PanMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            foreach (var n in new[] { nameof(IsAuto), nameof(IsManual), nameof(IsOff), nameof(PanEnabled), nameof(EffectivePan) })
                Raise(n);
            SettingChanged?.Invoke();
        }
    }

    public bool PanEnabled => _mode == PanMode.Manual;

    public double ManualPan
    {
        get => _manualPan;
        set { if (Set(ref _manualPan, Math.Round(value, 2))) { Raise(nameof(EffectivePan)); SettingChanged?.Invoke(); } }
    }

    /// <summary>Live auto pan, shown on the slider in Auto mode.</summary>
    public double CurrentPan
    {
        get => _currentPan;
        set { if (Set(ref _currentPan, value)) Raise(nameof(EffectivePan)); }
    }

    public double EffectivePan => _mode switch { PanMode.Manual => _manualPan, PanMode.Auto => _currentPan, _ => 0 };

    public double Volume
    {
        get => _volume;
        set { if (Set(ref _volume, Math.Round(value))) { Raise(nameof(VolumeText)); SettingChanged?.Invoke(); } }
    }

    public string VolumeText => $"{(int)_volume}%";

    public bool Muted
    {
        get => _muted;
        set { if (Set(ref _muted, value)) SettingChanged?.Invoke(); }
    }

    public void Load(AppSetting s)
    {
        _mode = s.Mode; _manualPan = s.ManualPan; _volume = s.Volume; _muted = s.Muted; CustomGroup = s.Group;
    }

    public void Store(AppSetting s)
    {
        s.Mode = _mode; s.ManualPan = _manualPan; s.Volume = _volume; s.Muted = _muted; s.Group = CustomGroup;
    }

    public string? CustomGroup { get; set; }
    public string GroupId => Grouping.GroupId(ExeName, CustomGroup);
    public bool HadSession { get; set; }
    public bool Pinned { get; set; }
    public bool IsIdle => !_active;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
