using System;
using System.Linq;

using Avalonia.Threading;

using Flower.Services;

using Material.Icons;

namespace Flower.ViewModels;

// The Duplicates page and the banner leading to it. Only for someone who can
// act on it: a device on its own, or an administrator of its server. A
// listener cannot remove the server's songs, so telling them would only be
// a worry they can do nothing about.
public partial class MainViewModel
{
    private DuplicateSongsNoticeViewModel? _duplicateSongsNotice;
    public DuplicateSongsNoticeViewModel DuplicateSongsNotice =>
        _duplicateSongsNotice ??= new DuplicateSongsNoticeViewModel(_appSettings, SaveSettings, ShowDuplicateSongs);

    // How a phone opens the page, as a screen pushed over Home. A desktop
    // leaves it null and selects the sidebar row.
    public Action<SidebarItem>? OpenDuplicateSongs { get; set; }

    // The page is up, so its rows say which file each copy is.
    public bool IsShowingDuplicateSongs => _selectedSidebarItem?.Kind == SidebarItemKind.Duplicates;

    private bool _managedDuplicateSongs;

    private bool MayManageDuplicateSongs =>
        !OperatingSystem.IsBrowser()
        && (_appSettings.PairedServerFingerprint is not { Length: > 0 }
            || (_reachability?.PairedServerDevice?.WeAreAdmin ?? _appSettings.PairedServerGrantsAdmin));

    private void RefreshDuplicateSongs()
    {
        _managedDuplicateSongs = MayManageDuplicateSongs;
        var keys = _managedDuplicateSongs
            ? DuplicateSongs.Find(Library.Tracks).Select(group => group.Key).ToList()
            : [];
        DuplicateSongsNotice.Update(keys);

        var row = _sidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Duplicates);
        if (keys.Count > 0 && row == null)
        {
            var artists = _sidebarItems.ToList().FindIndex(i => i.Kind == SidebarItemKind.Artists);
            _sidebarItems.Insert(artists + 1,
                new SidebarItem(SidebarItemKind.Duplicates, "Duplicates", MaterialIconKind.ContentDuplicate));
        }
        // Left in place while it is the page on screen, which would otherwise
        // vanish under someone who just removed the last duplicate.
        else if (keys.Count == 0 && row != null && row != _selectedSidebarItem)
        {
            _sidebarItems.Remove(row);
        }
    }

    private void RefreshDuplicateSongsIfRoleChanged()
    {
        if (MayManageDuplicateSongs != _managedDuplicateSongs)
            Dispatcher.UIThread.Post(RefreshDuplicateSongs);
    }

    private void ShowDuplicateSongs()
    {
        if (_sidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Duplicates) is not { } row)
            return;

        if (OpenDuplicateSongs is { } open)
            open(row);
        else
            SelectedSidebarItem = row;
    }
}
