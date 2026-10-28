using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Controls;
using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;
using Flower.Views;
using Flower.Views.Mobile;

using Material.Icons.Avalonia;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

using Track = Flower.Models.Track;

namespace Flower.Tests.ViewModels.Mobile;

// The phone's New Smart Playlist row. It goes through the same
// MainViewModel.NewSmartPlaylist the desktop sidebar does, and the sheet binds
// the same SmartPlaylistEditorViewModel the desktop window does - so what is
// the phone's own is how an edit ends: the check saves, and every other way off
// the sheet is a cancel, which removes a playlist created only to be edited.
[Collection("PlatformDataDirectory")]
public class MobileSmartPlaylistEditorTests : PinnedDataDirectory
{
    public MobileSmartPlaylistEditorTests() => TestIoc.EnsureConfigured();

    private static Track SomeTrack(string title) => new()
    {
        Title = title,
        Album = "Bee Thousand",
        Artists = "Guided by Voices",
        Path = "/music/" + title + ".flac",
        DateAdded = DateTimeOffset.UtcNow,
    };

    private sealed class Scope : IDisposable
    {
        public required MainViewModelHarness.MobileParts Parts { get; init; }
        public required SmartPlaylistRefresher Refresher { get; init; }
        public MobileMainViewModel Mobile => Parts.Mobile;
        public Library Library => Parts.Parts.Library;

        public void Dispose()
        {
            Parts.Dispose();
            Refresher.Dispose();
        }
    }

    private static Scope Build()
    {
        var library = new Library(new List<Track> { SomeTrack("Tractor Rape Chain"), SomeTrack("Echos Myron") });
        var refresher = new SmartPlaylistRefresher(library, NullLogger<SmartPlaylistRefresher>.Instance);
        var parts = MainViewModelHarness.BuildMobile(library, new MainPlaylist(new List<Track>()), refresher);
        Dispatcher.UIThread.RunJobs();
        return new Scope { Parts = parts, Refresher = refresher };
    }

    [AvaloniaFact]
    public void The_row_opens_the_editor_over_a_new_playlist()
    {
        using var scope = Build();

        Assert.True(scope.Mobile.CanCreateSmartPlaylist);
        scope.Mobile.NewSmartPlaylistCommand.Execute(null);

        Assert.True(scope.Mobile.IsShowingSmartPlaylistEditor);
        Assert.NotNull(scope.Mobile.SmartPlaylistEditor);
        Assert.Contains(scope.Mobile.SmartPlaylistEditor!.Playlist, scope.Library.Playlists);
    }

    [AvaloniaFact]
    public void Backing_out_removes_the_playlist_it_was_opened_to_make()
    {
        using var scope = Build();
        scope.Mobile.NewSmartPlaylistCommand.Execute(null);
        var draft = scope.Mobile.SmartPlaylistEditor!.Playlist;

        scope.Mobile.CloseSheetCommand.Execute(null);

        Assert.False(scope.Mobile.IsShowingSmartPlaylistEditor);
        Assert.DoesNotContain(draft, scope.Library.Playlists);
    }

    [AvaloniaFact]
    public void The_check_saves_the_rules_and_keeps_the_playlist()
    {
        using var scope = Build();
        scope.Mobile.NewSmartPlaylistCommand.Execute(null);
        var editor = scope.Mobile.SmartPlaylistEditor!;
        editor.Name = "Tractors";
        var row = editor.Conditions[0];
        row.Field = row.Fields.First(f => f.Field == SmartField.Title);
        row.Operator = row.Operators.First(o => o.Operator == SmartOperator.Contains);
        row.ValueText = "Tractor";

        scope.Mobile.SaveSmartPlaylistCommand.Execute(null);

        Assert.False(scope.Mobile.IsShowingSmartPlaylistEditor);
        var saved = Assert.Single(scope.Library.Playlists, p => p.Name == "Tractors");
        Assert.True(saved.IsSmart);
        Assert.Equal(["Tractor Rape Chain"], saved.Tracks.Select(t => t.Title));
        Assert.Contains(scope.Parts.Parts.Main.SidebarItems, i => i.Playlist == saved);
    }

    // A smart playlist's own screen has a pencil beside play and shuffle, and
    // it opens the same editor over the playlist that is already there -
    // the phone had no other way back into its rules.
    [AvaloniaFact]
    public async Task The_pencil_on_a_smart_playlist_s_screen_edits_its_rules()
    {
        using var scope = Build();
        scope.Mobile.NewSmartPlaylistCommand.Execute(null);
        var editor = scope.Mobile.SmartPlaylistEditor!;
        editor.Name = "Tractors";
        var row = editor.Conditions[0];
        row.Field = row.Fields.First(f => f.Field == SmartField.Title);
        row.Operator = row.Operators.First(o => o.Operator == SmartOperator.Contains);
        row.ValueText = "Tractor";
        scope.Mobile.SaveSmartPlaylistCommand.Execute(null);
        var saved = scope.Library.Playlists.Single(p => p.Name == "Tractors");

        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Playlists));
        scope.Mobile.SelectPlaylistCommand.Execute(scope.Mobile.PlaylistPickerItems.Single(i => i.Playlist == saved));
        await UiWait.Until(() => scope.Mobile.CurrentPlaylist == saved, "never drilled into the saved playlist");

        Assert.True(scope.Mobile.CanEditCurrentPlaylist);
        scope.Mobile.EditCurrentPlaylistCommand.Execute(null);

        Assert.True(scope.Mobile.IsShowingSmartPlaylistEditor);
        Assert.Same(saved, scope.Mobile.SmartPlaylistEditor!.Playlist);
        Assert.False(scope.Mobile.CurrentPlaylistItem!.IsEditing);
    }

    [AvaloniaFact]
    public void A_rejected_save_keeps_the_sheet_up_and_says_why()
    {
        using var scope = Build();
        scope.Mobile.NewSmartPlaylistCommand.Execute(null);
        var editor = scope.Mobile.SmartPlaylistEditor!;
        editor.Conditions[0].Field = editor.Conditions[0].Fields.First(f => f.Field == SmartField.Year);
        editor.Conditions[0].ValueText = "nineteen";

        scope.Mobile.SaveSmartPlaylistCommand.Execute(null);

        Assert.True(scope.Mobile.IsShowingSmartPlaylistEditor);
        Assert.True(editor.HasError);

        // And backing out of a failed save is still a cancel.
        scope.Mobile.CloseSheetCommand.Execute(null);
        Assert.DoesNotContain(editor.Playlist, scope.Library.Playlists);
    }

    private static (Window Window, SmartPlaylistEditorSheetView View) Render(Scope scope)
    {
        const double width = 390, height = 844;
        var view = new SmartPlaylistEditorSheetView { DataContext = scope.Mobile };
        var window = new Window { Width = width, Height = height, Background = Brushes.Black };
        window.Styles.Add(new FluentTheme());
        window.Styles.Add(new MaterialIconStyles(null));
        window.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Flower/"))
        {
            Source = new Uri("avares://Flower/Styles/IconButtons.axaml"),
        });
        window.Content = view;
        window.Show();
        Settle(window);
        return (window, view);
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 2; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void A_new_playlist_opens_unnamed_and_can_be_saved()
    {
        using var scope = Build();
        scope.Mobile.NewSmartPlaylistCommand.Execute(null);
        var editor = scope.Mobile.SmartPlaylistEditor!;

        Assert.Equal(string.Empty, editor.Name);

        // Left empty, the playlist keeps the name it was made under.
        var originalName = editor.Playlist.Name;
        var row = editor.Conditions[0];
        row.Field = row.Fields.First(f => f.Field == SmartField.Title);
        row.ValueText = "Tractor";
        scope.Mobile.SaveSmartPlaylistCommand.Execute(null);
        Assert.Contains(scope.Library.Playlists, p => p.IsSmart && p.Name == originalName);
    }

    [AvaloniaFact]
    public void The_songs_the_rules_pick_are_listed_as_they_are_typed()
    {
        using var scope = Build();
        scope.Mobile.NewSmartPlaylistCommand.Execute(null);
        var editor = scope.Mobile.SmartPlaylistEditor!;
        var row = editor.Conditions[0];
        row.Field = row.Fields.First(f => f.Field == SmartField.Title);
        row.Operator = row.Operators.First(o => o.Operator == SmartOperator.Contains);

        // Nothing typed yet says nothing about which songs are wanted.
        Flush(editor);
        Assert.Empty(scope.Mobile.SmartPlaylistPreviewRows);

        row.ValueText = "Tr";
        Flush(editor);
        Assert.Equal(["Tractor Rape Chain"], scope.Mobile.SmartPlaylistPreviewRows.Select(r => r.Track.Title));

        editor.AddCondition(row);
        editor.MatchMode = editor.MatchModes.First(m => m.Mode == MatchMode.Any);
        var second = editor.Conditions[1];
        second.Field = second.Fields.First(f => f.Field == SmartField.Title);
        second.Operator = second.Operators.First(o => o.Operator == SmartOperator.Contains);
        second.ValueText = "Myron";
        Flush(editor);
        Assert.Equal(2, scope.Mobile.SmartPlaylistPreviewRows.Count);

        editor.RemoveCondition(row);
        Flush(editor);
        Assert.Equal(["Echos Myron"], scope.Mobile.SmartPlaylistPreviewRows.Select(r => r.Track.Title));

        // Rendered with the Songs tab's own row.
        var (window, view) = Render(scope);
        Assert.Contains(view.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("trackRow"));
        window.Close();

        // A save stores exactly what the preview showed.
        scope.Mobile.SaveSmartPlaylistCommand.Execute(null);
        Assert.Equal(["Echos Myron"], editor.Playlist.Tracks.Select(t => t.Title));
    }

    // Typing waits for a pause before the list follows it, so the library is
    // walked once per word rather than once per letter.
    [AvaloniaFact]
    public void The_list_waits_for_typing_to_pause()
    {
        using var scope = Build();
        scope.Mobile.NewSmartPlaylistCommand.Execute(null);
        var editor = scope.Mobile.SmartPlaylistEditor!;
        var row = editor.Conditions[0];
        row.Field = row.Fields.First(f => f.Field == SmartField.Title);
        row.Operator = row.Operators.First(o => o.Operator == SmartOperator.Contains);
        Flush(editor);

        var typed = System.Diagnostics.Stopwatch.StartNew();
        row.ValueText = "T";
        row.ValueText = "Tr";
        Pump(TimeSpan.FromMilliseconds(300));
        Assert.Empty(scope.Mobile.SmartPlaylistPreviewRows);

        // Another letter restarts the wait.
        row.ValueText = "Tra";
        typed.Restart();
        while (scope.Mobile.SmartPlaylistPreviewRows.Count == 0 && typed.Elapsed < TimeSpan.FromSeconds(5))
            Pump(TimeSpan.FromMilliseconds(20));

        Assert.Equal(["Tractor Rape Chain"], scope.Mobile.SmartPlaylistPreviewRows.Select(r => r.Track.Title));
        Assert.True(typed.Elapsed >= SmartPlaylistEditorViewModel.TypingPause - TimeSpan.FromMilliseconds(50),
            $"The list followed the typing after {typed.Elapsed.TotalMilliseconds:0}ms");
    }

    // A pick from a list is a finished thought: no pause, just the pass itself.
    [AvaloniaFact]
    public void A_pick_from_a_list_updates_the_list_without_the_typing_pause()
    {
        using var scope = Build();
        scope.Mobile.NewSmartPlaylistCommand.Execute(null);
        var editor = scope.Mobile.SmartPlaylistEditor!;
        var row = editor.Conditions[0];
        row.Field = row.Fields.First(f => f.Field == SmartField.Title);
        row.Operator = row.Operators.First(o => o.Operator == SmartOperator.Contains);
        row.ValueText = "Tr";
        Flush(editor);
        Assert.Single(scope.Mobile.SmartPlaylistPreviewRows);

        var picked = System.Diagnostics.Stopwatch.StartNew();
        row.Operator = row.Operators.First(o => o.Operator == SmartOperator.DoesNotContain);
        while (scope.Mobile.SmartPlaylistPreviewRows.FirstOrDefault()?.Track.Title != "Echos Myron"
               && picked.Elapsed < TimeSpan.FromSeconds(5))
            Pump(TimeSpan.FromMilliseconds(20));

        Assert.Equal(["Echos Myron"], scope.Mobile.SmartPlaylistPreviewRows.Select(r => r.Track.Title));
        Assert.True(picked.Elapsed < SmartPlaylistEditorViewModel.TypingPause,
            $"The list followed the pick after {picked.Elapsed.TotalMilliseconds:0}ms");
    }

    // Runs the preview now, skipping the typing pause, and lets its result land.
    private static void Flush(SmartPlaylistEditorViewModel editor)
    {
        var pass = editor.RefreshPreviewNowAsync();
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!pass.IsCompleted && waited.Elapsed < TimeSpan.FromSeconds(5))
            Pump(TimeSpan.FromMilliseconds(5));
        Assert.True(pass.IsCompleted, "The preview pass never finished");
        Dispatcher.UIThread.RunJobs();
    }

    private static void Pump(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        do
        {
            Dispatcher.UIThread.RunJobs();
            System.Threading.Thread.Sleep(5);
        }
        while (DateTime.UtcNow < until);
        Dispatcher.UIThread.RunJobs();
    }
}
