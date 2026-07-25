using Flower.Models;
using Flower.Services;

using Material.Icons;

namespace Flower.ViewModels;

public enum SidebarItemKind { Header, RecentlyAdded, Songs, Albums, Artists, Playlist, Device }

public class SidebarItem : ViewModelBase
{
    private string _name;
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public SidebarItemKind Kind { get; }

    // Settable, not just init - a Device row's icon changes in place if the
    // peer's advertised role (DiscoveredDevice.IsServer) changes after this
    // row was created - see MainViewModel.RelocateDeviceSidebarItemIfNeeded.
    private MaterialIconKind _icon;
    public MaterialIconKind Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(); }
    }

    public Playlist? Playlist { get; }

    // Settable (not just init) - see MainViewModel.FindDeviceSidebarItem: a
    // device row can end up re-pointed at a different DiscoveredDevice
    // instance than the one it was created with, once a Fingerprint collision
    // with an already-resolved device is ruled out.
    private DiscoveredDevice? _device;
    public DiscoveredDevice? Device
    {
        get => _device;
        set { _device = value; OnPropertyChanged(); }
    }

    public bool IsHeader => Kind == SidebarItemKind.Header;
    public bool IsSelectable => !IsHeader;

    // Second line shown under Name in the sidebar - currently only used for a
    // Device row whose display name collides with another device's (see
    // MainViewModel.RefreshDeviceDisplayNames), showing that device's IP to
    // tell them apart; null everywhere else.
    private string? _subtitle;
    public string? Subtitle
    {
        get => _subtitle;
        set { _subtitle = value; OnPropertyChanged(); }
    }

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

    // Only ever true for the Device row matching MainViewModel's one paired
    // Server, while a sync with it is in flight - see
    // MainViewModel.NotifyIsSyncingChanged/AddOrUpdateDeviceSidebarItem.
    private bool _isSyncing;
    public bool IsSyncing
    {
        get => _isSyncing;
        set { _isSyncing = value; OnPropertyChanged(); }
    }

    // True only for the one Device row representing MainViewModel's paired
    // Server (see AppSettings.PairedServerFingerprint) - that row is pinned
    // in place for the whole session (MainViewModel.BuildSidebarItems/
    // PairWithServer/RemoveDeviceItem) instead of disappearing the moment
    // it's no longer discovered like an ordinary Devices/Server row, so it
    // needs its own reachability glyph (ShowReachableIcon/
    // ShowUnreachableIcon below, MainView.axaml's check/exclamation icons)
    // to show whether it's actually reachable right now.
    private bool _isPairedServer;
    public bool IsPairedServer
    {
        get => _isPairedServer;
        set
        {
            _isPairedServer = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowReachableIcon));
            OnPropertyChanged(nameof(ShowUnreachableIcon));
        }
    }

    // Only meaningful while IsPairedServer - written exclusively by
    // MainViewModel.SyncPairedServerSidebarRow, which sources it from
    // PairedServerReachability (the single source of truth for this - see
    // that class's own doc comment).
    private bool _isReachable;
    public bool IsReachable
    {
        get => _isReachable;
        set
        {
            _isReachable = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowReachableIcon));
            OnPropertyChanged(nameof(ShowUnreachableIcon));
        }
    }

    public bool ShowReachableIcon   => IsPairedServer && IsReachable;
    public bool ShowUnreachableIcon => IsPairedServer && !IsReachable;

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
