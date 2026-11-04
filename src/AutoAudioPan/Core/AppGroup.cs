using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;

namespace AutoAudioPan.Core;

/// <summary>Parent row: purely organizational, holds child AppRows and no audio controls.</summary>
public sealed class AppGroup : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private string _name;
    private bool _expanded = true;

    public AppGroup(string id, string name) { Id = id; _name = name; Children.CollectionChanged += (_, _) => Raise(nameof(Summary)); }

    public string Id { get; }
    public ObservableCollection<AppRow> Children { get; } = new();

    public string Name { get => _name; set { if (_name != value) { _name = value; Raise(nameof(Name)); } } }
    public bool IsExpanded { get => _expanded; set { if (_expanded != value) { _expanded = value; Raise(nameof(IsExpanded)); } } }
    public ImageSource? Icon => Children.FirstOrDefault()?.Icon;
    public string Summary => Children.Count == 1 ? "1 app" : $"{Children.Count} apps";

    public void RefreshIcon() => Raise(nameof(Icon));
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
