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

namespace Flower.Tests;

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
    public void A_new_playlist_opens_unnamed_under_a_title_that_says_so()
    {
        using var scope = Build();
        scope.Mobile.NewSmartPlaylistCommand.Execute(null);
        var editor = scope.Mobile.SmartPlaylistEditor!;

        Assert.Equal("New Smart Playlist", editor.Title);
        Assert.Equal(string.Empty, editor.Name);

        var (window, view) = Render(scope);
        var nameBox = view.GetVisualDescendants().OfType<TextInput>().First();
        Assert.Equal("Playlist Name", nameBox.PlaceholderText);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == "New Smart Playlist" && t.IsEffectivelyVisible);
        window.Close();

        // Left empty, the playlist keeps the name it was made under.
        var row = editor.Conditions[0];
        row.Field = row.Fields.First(f => f.Field == SmartField.Title);
        row.ValueText = "Tractor";
        scope.Mobile.SaveSmartPlaylistCommand.Execute(null);
        Assert.Contains(scope.Library.Playlists, p => p.IsSmart && p.Name == "New Smart Playlist");
    }

    // Changing the field must not reset the operator. Kept when the new field
    // has it, stood in for when it does not, and brought back when the field
    // goes back - checked on the rendered boxes, since what looked like a
    // reset is what the operator box shows.
    [AvaloniaFact]
    public void Changing_the_field_keeps_the_operator_that_was_picked()
    {
        using var scope = Build();
        scope.Mobile.NewSmartPlaylistCommand.Execute(null);
        var row = scope.Mobile.SmartPlaylistEditor!.Conditions[0];
        row.Field = row.Fields.First(f => f.Field == SmartField.Title);

        var (window, view) = Render(scope);
        var boxes = view.GetVisualDescendants().OfType<ComboBox>()
            .Where(b => b.IsEffectivelyVisible).ToList();
        var fieldBox = boxes.Single(b => b.SelectedItem is SmartConditionRowViewModel.FieldOption);
        var operatorBox = boxes.Single(b => b.SelectedItem is SmartConditionRowViewModel.OperatorOption);

        SmartOperator Shown() => Assert.IsType<SmartConditionRowViewModel.OperatorOption>(operatorBox.SelectedItem).Operator;

        void Pick(SmartField field)
        {
            fieldBox.IsDropDownOpen = true;
            Settle(window);
            fieldBox.SelectedItem = row.Fields.First(f => f.Field == field);
            fieldBox.IsDropDownOpen = false;
            Settle(window);
            Assert.Equal(field, row.Field.Field);
        }

        operatorBox.SelectedItem = row.Operators.First(o => o.Operator == SmartOperator.Contains);
        Settle(window);

        // Same kind: the very same list, so the box has nothing to reset.
        var titleOperators = operatorBox.ItemsSource;
        Pick(SmartField.Artists);
        Assert.Same(titleOperators, operatorBox.ItemsSource);
        Assert.Equal(SmartOperator.Contains, Shown());

        // A number has no "contains"; "is" stands in, and "contains" comes back.
        Pick(SmartField.Year);
        Assert.Equal(SmartOperator.Is, Shown());
        Pick(SmartField.Genre);
        Assert.Equal(SmartOperator.Contains, Shown());
        Assert.Equal(SmartOperator.Contains, row.Operator.Operator);

        // One both kinds have is simply kept.
        operatorBox.SelectedItem = row.Operators.First(o => o.Operator == SmartOperator.IsNot);
        Settle(window);
        foreach (var field in new[] { SmartField.Year, SmartField.DateAdded, SmartField.Title })
        {
            Pick(field);
            Assert.Equal(SmartOperator.IsNot, Shown());
        }

        window.Close();
    }

    [AvaloniaFact]
    public void The_value_box_offers_to_clear_itself_and_reaches_the_row_buttons()
    {
        using var scope = Build();
        scope.Mobile.NewSmartPlaylistCommand.Execute(null);
        var row = scope.Mobile.SmartPlaylistEditor!.Conditions[0];
        row.Field = row.Fields.First(f => f.Field == SmartField.Title);
        row.ValueText = "Tractor";

        var (window, view) = Render(scope);
        var editorView = view.GetVisualDescendants().OfType<SmartPlaylistEditorView>().Single();
        var valueBox = editorView.GetVisualDescendants().OfType<TextInput>()
            .Single(t => t.IsEffectivelyVisible && t.Text == "Tractor");
        var clear = valueBox.InnerRightContent as Button;
        Assert.NotNull(clear);
        Assert.True(clear!.IsEffectivelyVisible);

        var plus = editorView.GetVisualDescendants().OfType<Button>()
            .Single(b => b.IsEffectivelyVisible && ToolTip.GetTip(b) as string == "Add a rule below this one");
        var boxRight = valueBox.TranslatePoint(new Point(valueBox.Bounds.Width, 0), window)!.Value.X;
        var buttonsLeft = plus.GetVisualAncestors().OfType<StackPanel>().First()
            .TranslatePoint(new Point(0, 0), window)!.Value.X;
        Assert.InRange(buttonsLeft - boxRight, 0, 8);

        // Round: the ring is as wide as it is tall.
        var ring = plus.GetVisualAncestors().OfType<Border>().First();
        Assert.Equal(ring.Bounds.Width, ring.Bounds.Height);
        Assert.Contains("floating", ring.Classes);

        window.Close();
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
        Assert.Equal("1 song matches these rules.", editor.PreviewSummary);

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

    // A phone is ~390 wide; the desktop row is five controls on one line and
    // several hundred pixels wider than that. Nothing in the compact layout
    // may reach past the screen's edge.
    [AvaloniaFact]
    public void Nothing_on_the_sheet_runs_off_a_phone_screen()
    {
        using var scope = Build();
        scope.Mobile.NewSmartPlaylistCommand.Execute(null);
        var editor = scope.Mobile.SmartPlaylistEditor!;
        editor.MatchMode = editor.MatchModes.First(m => m.Mode == MatchMode.Any);
        editor.Conditions[0].Field = editor.Conditions[0].Fields.First(f => f.Field == SmartField.Title);
        editor.Conditions[0].Operator = editor.Conditions[0].Operators.First(o => o.Operator == SmartOperator.Contains);
        editor.Conditions[0].ValueText = "Tractor";
        // The widest row there is: a date range.
        editor.AddCondition();
        editor.Conditions[1].Field = editor.Conditions[1].Fields.First(f => f.Field == SmartField.DateAdded);
        editor.Conditions[1].Operator = editor.Conditions[1].Operators.First(o => o.Operator == SmartOperator.Between);
        Flush(editor);

        const double width = 390;
        var (window, view) = Render(scope);

        var frame = window.CaptureRenderedFrame();
        var path = Environment.GetEnvironmentVariable("FLOWER_SMART_EDITOR_SNAPSHOT");
        if (!string.IsNullOrEmpty(path))
            frame?.Save(path);

        var editorView = view.GetVisualDescendants().OfType<SmartPlaylistEditorView>().Single();
        Assert.True(editorView.IsCompact);
        // The preview's song rows included - they share the page.
        Assert.NotEmpty(scope.Mobile.SmartPlaylistPreviewRows);
        var offenders = view.GetVisualDescendants()
            .OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0)
            .Select(c => (c, box: c.TranslatePoint(new Point(c.Bounds.Width, 0), window)))
            .Where(x => x.box is { } p && p.X > width + 0.5)
            .Select(x => $"{x.c.GetType().Name} right edge at {x.box!.Value.X:0}")
            .ToList();
        Assert.Empty(offenders);

        window.Close();
    }
}
