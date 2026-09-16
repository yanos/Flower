using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Controls;
using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;
using Flower.Views.Mobile;
using Flower.Views.Mobile.Screens;

using Material.Icons.Avalonia;
using MaterialIcon = Material.Icons.Avalonia.MaterialIcon;

using Xunit;

using Track = Flower.Models.Track;

namespace Flower.Tests;

// Making a playlist on the phone. The plus that used to sit in the header band
// was an icon with no subject, so it is a "New Playlist" row under the title
// now, and tapping it opens an empty, focused name box where the playlist's
// own row will be - nothing is created until a name is committed, and an empty
// one creates nothing at all. One control does this (NewPlaylistEntry) and the
// add-to-playlist sheet shows the same one, which is what these assert: the
// behaviour, and that both hosts get it.
[Collection("PlatformDataDirectory")]
public class NewPlaylistEntryTests : PinnedDataDirectory
{
    public NewPlaylistEntryTests() => TestIoc.EnsureConfigured();

    private static Track SomeTrack(string title) => new()
    {
        Title = title,
        Album = "Bee Thousand",
        Artists = "Guided by Voices",
        Path = "/music/" + title + ".flac",
        DateAdded = DateTimeOffset.UtcNow,
    };

    private static MainViewModelHarness.MobileParts OnThePlaylistsTab()
    {
        var parts = MainViewModelHarness.BuildMobile(
            new Library(new List<Track> { SomeTrack("Tractor Rape Chain") }), new MainPlaylist(new List<Track>()));
        parts.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Playlists));
        Dispatcher.UIThread.RunJobs();
        return parts;
    }

    // The app's own mobile row styles live inline in MobileMainView.axaml, so a
    // screen shown on its own falls back to FluentTheme's padding - restated
    // here for the same reason AlbumDetailLayoutTests restates the track row's.
    private static Style PickerRowPadding()
    {
        var style = new Style(x => x.OfType<Button>().Class("pickerRow"));
        style.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(16, 10)));
        style.Setters.Add(new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        return style;
    }

    private static Window Show(Control view, MobileMainViewModel mobile, double width = 390, double height = 700)
    {
        view.DataContext = mobile;
        var window = new Window { Width = width, Height = height };
        window.Styles.Add(new FluentTheme());
        window.Styles.Add(new MaterialIconStyles(null));
        window.Styles.Add(new StyleInclude(new Uri("avares://Flower/Styles/"))
        {
            Source = new Uri("avares://Flower/Styles/IconButtons.axaml"),
        });
        window.Styles.Add(PickerRowPadding());
        window.Background = Brushes.Black;
        window.Content = view;
        window.Show();
        Pump(window, width, height);
        return window;
    }

    private static void Pump(Window window, double width = 390, double height = 700)
    {
        for (var i = 0; i < 2; i++)
        {
            window.Measure(new Size(width, height));
            window.Arrange(new Rect(0, 0, width, height));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
        // Hit testing and focus both read what the render pass computed.
        window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
    }

    // Main.Rows is built off an async rebuild - see
    // MainViewModelHarness.WaitForTheDrillIn for the same shape.
    private static TrackRowViewModel WaitForARow(MainViewModel main)
    {
        var deadline = Environment.TickCount64 + 2000;
        while (main.Rows.Count == 0 && Environment.TickCount64 < deadline)
            Dispatcher.UIThread.RunJobs();
        return main.Rows.First();
    }

    private static TextBox NameBox(Window window) =>
        window.GetVisualDescendants().OfType<NewPlaylistEntry>().Single()
            .GetVisualDescendants().OfType<TextBox>().Single();

    [AvaloniaFact]
    public void The_playlists_screen_offers_a_New_Playlist_row_above_its_playlists()
    {
        using var parts = OnThePlaylistsTab();
        parts.Parts.Main.Playlists.CreateNamed("Mixtape", []);
        Dispatcher.UIThread.RunJobs();

        var window = Show(new PlaylistPickerScreenView(), parts.Mobile);

        var entry = Assert.Single(window.GetVisualDescendants().OfType<NewPlaylistEntry>());
        var label = Assert.Single(entry.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == "New Playlist");
        Assert.True(label.IsVisible);

        // Above the playlist it already has, and ranged left with it.
        var playlistRow = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == "Mixtape");
        var entryAt = entry.TranslatePoint(default, window)!.Value;
        var rowAt = playlistRow.TranslatePoint(default, window)!.Value;
        Assert.True(entryAt.Y + entry.Bounds.Height <= rowAt.Y);

        // Ranged left with the rows: the circled plus starts where a playlist
        // row's own glyph does.
        var circle = Assert.Single(entry.GetVisualDescendants().OfType<Border>(),
            b => b.Classes.Contains("floating"));
        // The row's leading glyph, not its trailing "..." - see
        // PlaylistPickerScreenView, where a row now ends in its own menu.
        var rowGlyph = window.GetVisualDescendants().OfType<MaterialIcon>()
            .Single(i => i.Kind == Material.Icons.MaterialIconKind.PlaylistPlay
                         && !i.GetVisualAncestors().OfType<NewPlaylistEntry>().Any());
        Assert.Equal(rowGlyph.TranslatePoint(default, window)!.Value.X,
            circle.TranslatePoint(default, window)!.Value.X, 1);

        window.Close();
    }

    [AvaloniaFact]
    public void Tapping_it_opens_an_empty_focused_box_where_the_playlist_will_be()
    {
        using var parts = OnThePlaylistsTab();
        var window = Show(new PlaylistPickerScreenView(), parts.Mobile);

        parts.Mobile.BeginCreatePlaylistCommand.Execute(null);
        Pump(window);

        var box = NameBox(window);
        Assert.True(box.IsEffectivelyVisible);
        Assert.True(string.IsNullOrEmpty(box.Text));
        Assert.Equal("Playlist Name", box.PlaceholderText);
        Assert.True(box.IsFocused);
        // The row it replaced is gone while it is up, rather than both showing.
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Text == "New Playlist"), t => t.IsEffectivelyVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void A_name_that_was_typed_becomes_a_playlist()
    {
        using var parts = OnThePlaylistsTab();
        var window = Show(new PlaylistPickerScreenView(), parts.Mobile);

        parts.Mobile.BeginCreatePlaylistCommand.Execute(null);
        Pump(window);
        NameBox(window).Text = "Late Night";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        var playlist = Assert.Single(parts.Parts.Library.Playlists);
        Assert.Equal("Late Night", playlist.Name);
        Assert.False(parts.Mobile.IsNamingNewPlaylist);

        window.Close();
    }

    [AvaloniaFact]
    public void An_empty_name_creates_nothing_at_all()
    {
        using var parts = OnThePlaylistsTab();
        var window = Show(new PlaylistPickerScreenView(), parts.Mobile);

        parts.Mobile.BeginCreatePlaylistCommand.Execute(null);
        Pump(window);
        // Typed and taken back out again: the box is empty, which is how the
        // user says no.
        NameBox(window).Text = "   ";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(parts.Parts.Library.Playlists);
        Assert.False(parts.Mobile.IsNamingNewPlaylist);

        window.Close();
    }

    [AvaloniaFact]
    public void Escape_takes_the_draft_row_away()
    {
        using var parts = OnThePlaylistsTab();
        var window = Show(new PlaylistPickerScreenView(), parts.Mobile);

        parts.Mobile.BeginCreatePlaylistCommand.Execute(null);
        Pump(window);
        NameBox(window).Text = "Never Mind";
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(parts.Parts.Library.Playlists);
        Assert.False(parts.Mobile.IsNamingNewPlaylist);

        window.Close();
    }

    // The sheet reached from a song's menu is the same flow, and the playlist
    // it makes holds the song the menu was opened from.
    [AvaloniaFact]
    public void The_add_to_playlist_sheet_names_its_new_playlist_the_same_way()
    {
        using var parts = MainViewModelHarness.BuildMobile(
            new Library(new List<Track> { SomeTrack("Echos Myron") }), new MainPlaylist(new List<Track>()));
        var row = WaitForARow(parts.Parts.Main);
        parts.Mobile.OpenTrackActionsCommand.Execute(row);
        parts.Mobile.OpenAddToPlaylistCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var window = Show(new AddToPlaylistView(), parts.Mobile);
        Assert.Single(window.GetVisualDescendants().OfType<NewPlaylistEntry>());

        parts.Mobile.BeginCreatePlaylistCommand.Execute(null);
        Pump(window);
        NameBox(window).Text = "Road Trip";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        var playlist = Assert.Single(parts.Parts.Library.Playlists);
        Assert.Equal("Road Trip", playlist.Name);
        Assert.Equal("Echos Myron", Assert.Single(playlist.Tracks).Title);
        // The sheet took itself away once the playlist existed.
        Assert.Equal(MobileSheet.None, parts.Mobile.ActiveSheet);

        window.Close();
    }
}
