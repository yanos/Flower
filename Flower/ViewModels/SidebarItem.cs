using Flower.Models;
using Flower.Services;

using Material.Icons;

namespace Flower.ViewModels;

public enum SidebarItemKind { Header, Songs, Albums, Artists, Playlist, Device }

public class SidebarItem : ViewModelBase
{
    private string _name;
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public SidebarItemKind Kind { get; }
    public MaterialIconKind Icon { get; }
    public Playlist? Playlist { get; }
    public DiscoveredDevice? Device { get; }
    public bool IsHeader => Kind == SidebarItemKind.Header;
    public bool IsSelectable => !IsHeader;

    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set { _isEditing = value; OnPropertyChanged(); }
    }

    // Highlighted while a dragged track is hovering this row during a
    // drag-onto-playlist gesture - see MainView.axaml.cs's ContentGrid_DragOver.
    private bool _isDropTarget;
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set { _isDropTarget = value; OnPropertyChanged(); }
    }

    public SidebarItem(SidebarItemKind kind, string name, MaterialIconKind icon = MaterialIconKind.MusicNote,
        Playlist? playlist = null, DiscoveredDevice? device = null)
    {
        Kind = kind;
        _name = name;
        Icon = icon;
        Playlist = playlist;
        Device = device;
    }
}
