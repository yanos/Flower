using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Flower.Models;
using Flower.ViewModels;
using Xunit;

namespace Flower.Tests.ViewModels;

public class SidebarSelectionBindingTests
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
