using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Persistence;

namespace Flower.ViewModels;

/// <summary>
/// What committing a sidebar rename needs from the view model that owns the
/// sidebar. Kept to the two effects the rename actually has, so the service is
/// constructible in a test over a five-line stand-in.
/// </summary>
public interface ISidebarRenameHost
{
    /// <summary>
    /// Re-derives every Device row's displayed name from the one place that
    /// decides what a device is called.
    /// </summary>
    void RefreshDeviceDisplayNames();

    void ScheduleContentSync();

    /// <summary>
    /// Takes back out a playlist created a moment ago and committed with no
    /// name - the user changing their mind.
    /// </summary>
    void DiscardNewPlaylist(SidebarItem row);
}

/// <summary>
/// Commits an in-place sidebar rename - a playlist's name, or a peer device's
/// local nickname. Extracted from <c>MainView</c>'s code-behind, where it was
/// mixed in with the editing TextBox's teardown: the persistence and the
/// empty-name rules are business logic and belong somewhere testable, while
/// focus handling and key routing stay in the view.
/// </summary>
public sealed class SidebarRenameService
{
    private readonly DeviceNicknameStore _deviceNicknames;
    private readonly ILogger<SidebarRenameService> _logger;

    public SidebarRenameService(DeviceNicknameStore deviceNicknames, ILogger<SidebarRenameService> logger)
    {
        _deviceNicknames = deviceNicknames;
        _logger = logger;
    }

    /// <summary>
    /// Applies <paramref name="item"/>'s edited name and leaves edit mode.
    /// A no-op if the item isn't being edited. Returns the committed name, or
    /// null if nothing was committed.
    /// </summary>
    public async Task<string?> CommitAsync(SidebarItem item, ISidebarRenameHost? host)
    {
        if (!item.IsEditing)
            return null;

        var name = item.Name?.Trim();

        if (item.Device is { Fingerprint.Length: > 0 } device)
        {
            item.IsEditing = false;
            _logger.LogInformation("Device nickname set for {Alias} ({Fingerprint}): {Nickname}",
                device.Alias, device.Fingerprint, name ?? "(cleared)");
            await _deviceNicknames.SetAsync(device.Fingerprint, name ?? "");

            // Re-derives item.Name (and every other Device row's) from
            // MainViewModel.ResolveDeviceDisplayName - the one place that
            // decides what a device is called - rather than duplicating its
            // empty-falls-back-to-device.Alias logic here too.
            host?.RefreshDeviceDisplayNames();
            return name ?? "";
        }

        // A new playlist committed with no name is one the user said no to,
        // and goes - the phone does the same with its draft row. An existing
        // one keeps the name it had: clearing the box is not a rename.
        if (string.IsNullOrEmpty(name) && item.IsNewPlaylist && host != null)
        {
            item.IsEditing = false;
            item.IsNewPlaylist = false;
            host.DiscardNewPlaylist(item);
            return null;
        }

        item.Name = string.IsNullOrEmpty(name) ? item.Playlist?.Name ?? "New Playlist" : name;
        item.IsEditing = false;
        item.IsNewPlaylist = false;

        if (item.Playlist == null || host == null)
            return item.Name;

        _logger.LogInformation("Playlist renamed: {Old} -> {New}", item.Playlist.Name, item.Name);
        // No save here: setting Name raises Playlist.Changed, which Library
        // relays as PlaylistsChanged, which is where Library persists it.
        item.Playlist.Name = item.Name;
        host.ScheduleContentSync();
        return item.Name;
    }
}
