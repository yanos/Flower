using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Flower.Controls;

public class MusicColumnDefinition : INotifyPropertyChanged
{
    public string Id { get; }
    public string Header { get; }
    public double MinWidth { get; }

    private double _width;
    private bool _isVisible;
    private int _order;

    public double Width
    {
        get => _width;
        set { _width = value; OnPropertyChanged(); }
    }

    public bool IsVisible
    {
        get => _isVisible;
        set { _isVisible = value; OnPropertyChanged(); }
    }

    public int Order
    {
        get => _order;
        set { _order = value; OnPropertyChanged(); }
    }

    public MusicColumnDefinition(string id, string header, double width, double minWidth, bool isVisible, int order)
    {
        Id = id;
        Header = header;
        _width = width;
        MinWidth = minWidth;
        _isVisible = isVisible;
        _order = order;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
