using Flower.Models;
using Material.Icons;

namespace Flower.ViewModels;

public enum SidebarItemKind { Header, Songs, Albums, Artists, Playlist }

public class SidebarItem
{
    public string Name { get; }
    public SidebarItemKind Kind { get; }
    public MaterialIconKind Icon { get; }
    public Playlist? Playlist { get; }
    public bool IsHeader => Kind == SidebarItemKind.Header;
    public bool IsSelectable => !IsHeader;

    public SidebarItem(SidebarItemKind kind, string name, MaterialIconKind icon = MaterialIconKind.MusicNote, Playlist? playlist = null)
    {
        Kind = kind;
        Name = name;
        Icon = icon;
        Playlist = playlist;
    }
}
