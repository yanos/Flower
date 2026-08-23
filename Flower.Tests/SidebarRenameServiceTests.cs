using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests;

// docs/ARCHITECTURE-REVIEW.md Tier 4.3: committing a sidebar rename used to be
// a private method on MainView's code-behind, mixing the editing TextBox's
// teardown with DeviceNicknameStore/playlist persistence. The persistence half
// is now SidebarRenameService, testable without a window.
[Collection("PlatformDataDirectory")]
public class SidebarRenameServiceTests : PinnedDataDirectory
{
    private sealed class Host : ISidebarRenameHost
    {
        public int DeviceNameRefreshes;
        public int ContentSyncs;

        public void RefreshDeviceDisplayNames() => DeviceNameRefreshes++;
        public void ScheduleContentSync() => ContentSyncs++;
    }

    private readonly DeviceNicknameStore _nicknames = new(NullLogger<DeviceNicknameStore>.Instance);
    private readonly Host _host = new();
    private readonly SidebarRenameService _service;

    public SidebarRenameServiceTests()
    {
        _service = new SidebarRenameService(_nicknames, NullLogger<SidebarRenameService>.Instance);
    }

    private static SidebarItem PlaylistItem(string name, out Playlist playlist)
    {
        playlist = new Playlist(name, new List<Track>());
        return new SidebarItem(SidebarItemKind.Playlist, name, playlist: playlist) { IsEditing = true };
    }

    private static SidebarItem DeviceItem(string name, string fingerprint = "FP1")
    {
        var device = new DiscoveredDevice
        {
            InstanceName = "peer",
            BaseUri = NetworkDiscoveryService.HttpOrigin(new IPEndPoint(IPAddress.Loopback, 5000)),
            Alias = "Peer",
            Fingerprint = fingerprint,
        };
        return new SidebarItem(SidebarItemKind.Device, name, device: device) { IsEditing = true };
    }

    [Fact]
    public async Task Renaming_a_playlist_writes_through_to_the_playlist_and_schedules_a_sync()
    {
        var item = PlaylistItem("Old", out var playlist);
        item.Name = "  New  ";

        var committed = await _service.CommitAsync(item, _host);

        Assert.Equal("New", committed);
        Assert.Equal("New", playlist.Name);
        Assert.False(item.IsEditing);
        Assert.Equal(1, _host.ContentSyncs);
    }

    [Fact]
    public async Task An_empty_playlist_name_falls_back_to_New_Playlist()
    {
        var item = PlaylistItem("Old", out var playlist);
        item.Name = "   ";

        await _service.CommitAsync(item, _host);

        Assert.Equal("New Playlist", item.Name);
        Assert.Equal("New Playlist", playlist.Name);
    }

    [Fact]
    public async Task Committing_an_item_that_is_not_being_edited_does_nothing()
    {
        var item = PlaylistItem("Old", out var playlist);
        item.IsEditing = false;
        item.Name = "New";

        Assert.Null(await _service.CommitAsync(item, _host));

        Assert.Equal("Old", playlist.Name);
        Assert.Equal(0, _host.ContentSyncs);
    }

    [Fact]
    public async Task Renaming_a_device_persists_a_nickname_and_refreshes_the_display_names()
    {
        var item = DeviceItem("Peer");
        item.Name = " Kitchen ";

        var committed = await _service.CommitAsync(item, _host);

        Assert.Equal("Kitchen", committed);
        Assert.Equal("Kitchen", _nicknames.Get("FP1"));
        Assert.False(item.IsEditing);
        Assert.Equal(1, _host.DeviceNameRefreshes);
        // A device rename is local display state, not library content.
        Assert.Equal(0, _host.ContentSyncs);
    }

    [Fact]
    public async Task Clearing_a_device_name_clears_the_nickname_rather_than_storing_an_empty_one()
    {
        await _nicknames.SetAsync("FP1", "Kitchen");

        var item = DeviceItem("Kitchen");
        item.Name = "";

        await _service.CommitAsync(item, _host);

        Assert.Null(_nicknames.Get("FP1"));
        Assert.Equal(1, _host.DeviceNameRefreshes);
    }

    [Fact]
    public async Task A_rename_with_no_host_still_commits_the_name()
    {
        // MainView's editing TextBox can lose focus after the DataContext has
        // already gone; the name must still be applied, just with nothing to
        // notify.
        var item = PlaylistItem("Old", out var playlist);
        item.Name = "New";

        Assert.Equal("New", await _service.CommitAsync(item, host: null));

        Assert.False(item.IsEditing);
        Assert.Equal("Old", playlist.Name);
    }
}
