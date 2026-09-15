using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Flower.Models;
using Flower.ViewModels;
using Xunit;

namespace Flower.Tests;

public class SidebarSelectionTests
{
    private readonly SidebarItem _header = new(SidebarItemKind.Header, "Library");
    private readonly SidebarItem _songs = new(SidebarItemKind.Songs, "Songs");
    private readonly SidebarItem _one = Row("One");
    private readonly SidebarItem _two = Row("Two");

    private static SidebarItem Row(string name)
    {
        var playlist = new Playlist(name, new List<Track>());
        return new SidebarItem(SidebarItemKind.Playlist, name, SidebarItem.IconFor(playlist), playlist);
    }

    [Fact]
    public void Several_playlists_stay_selected()
    {
        Assert.Null(SidebarSelection.Settle(new[] { _one, _two }, new[] { _two }, _one));
    }

    [Fact]
    public void One_row_stays_selected()
    {
        Assert.Null(SidebarSelection.Settle(new[] { _songs }, new[] { _songs }, _one));
    }

    [Fact]
    public void Adding_a_playlist_to_songs_starts_again_from_the_playlist()
    {
        Assert.Equal(new[] { _one }, SidebarSelection.Settle(new[] { _songs, _one }, new[] { _one }, _songs));
    }

    [Fact]
    public void Adding_songs_to_playlists_starts_again_from_songs()
    {
        Assert.Equal(new[] { _songs }, SidebarSelection.Settle(new[] { _one, _two, _songs }, new[] { _songs }, _one));
    }

    [Fact]
    public void A_header_alone_goes_back_to_the_last_row()
    {
        Assert.Equal(new[] { _one }, SidebarSelection.Settle(new[] { _header }, new[] { _header }, _one));
    }

    [Fact]
    public void A_header_among_playlists_is_dropped_and_the_playlists_kept()
    {
        Assert.Equal(new[] { _one, _two }, SidebarSelection.Settle(new[] { _one, _two, _header }, new[] { _header }, _one));
    }

    [Fact]
    public void Deselecting_the_only_row_keeps_it()
    {
        Assert.Equal(new[] { _one }, SidebarSelection.Settle(new SidebarItem[0], new SidebarItem[0], _one));
    }

    // MainView collapses a selection through SelectedItem so the track list
    // moves once rather than through every row removed - which only works if
    // the setter replaces a multiple selection instead of adding to it.
    [AvaloniaFact]
    public void Setting_SelectedItem_on_a_multiple_selection_ListBox_replaces_the_selection()
    {
        var list = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            ItemsSource = new[] { _songs, _one, _two },
        };
        var window = new Window { Content = list };
        window.Show();
        list.SelectedItems!.Add(_one);
        list.SelectedItems.Add(_two);

        list.SelectedItem = _songs;

        Assert.Equal(new object[] { _songs }, list.SelectedItems.Cast<object>());
        window.Close();
    }
}
