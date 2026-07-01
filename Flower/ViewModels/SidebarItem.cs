using Flower.Models;
using Material.Icons;

namespace Flower.ViewModels;

public enum SidebarItemKind { Header, Songs, Albums, Artists, Playlist }

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
    public bool IsHeader => Kind == SidebarItemKind.Header;
    public bool IsSelectable => !IsHeader;

    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set { _isEditing = value; OnPropertyChanged(); }
    }

    public SidebarItem(SidebarItemKind kind, string name, MaterialIconKind icon = MaterialIconKind.MusicNote, Playlist? playlist = null)
    {
        Kind = kind;
        _name = name;
        Icon = icon;
        Playlist = playlist;
    }
}
