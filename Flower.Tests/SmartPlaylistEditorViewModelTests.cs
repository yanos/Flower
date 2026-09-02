using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests;

// Phase 4 of docs/SMART-PLAYLIST-PLAN.md: the rule editor's own logic - what a
// row builds, what a save stores, and what it refuses. No window is involved;
// everything the editor decides is decidable without one, which is the reason
// SmartPlaylistEditorViewModel knows nothing about dialogs.
public class SmartPlaylistEditorViewModelTests
{
    private static Track T(string title, int playCount = 0) =>
        new Track { Title = title, Path = $"/music/{title}.mp3", PlayCount = playCount };

    private sealed class NullPlaylistStore : IPlaylistStore
    {
        public void Save(IEnumerable<Playlist> playlists) { }
    }

    private static Library NewLibrary(params Track[] tracks) =>
        new(tracks.ToList(), NullLogger<Library>.Instance, null, new NullPlaylistStore());

    private static SmartPlaylistRefresher NewRefresher(Library library) =>
        new(library, NullLogger<SmartPlaylistRefresher>.Instance);

    private static SmartPlaylistEditorViewModel NewEditor(Library library, Playlist playlist, bool isNew = false) =>
        new(playlist, library, NewRefresher(library), isNew);

    private static Playlist AddPlaylist(Library library, string name, SmartPlaylistRules? rules = null)
    {
        var playlist = new Playlist(name, new List<Track>());
        if (rules != null)
            playlist.Rules = rules;
        library.AddPlaylist(playlist);
        return playlist;
    }

    // ── Rows ──────────────────────────────────────────────────────────────────

    [Fact]
    public void An_editor_over_a_playlist_with_no_rules_still_opens_on_one_row()
    {
        var library = NewLibrary(T("A"));
        var editor = NewEditor(library, AddPlaylist(library, "New"));

        Assert.Single(editor.Conditions);
        Assert.Equal(SmartField.Title, editor.Conditions[0].Field.Field);
    }

    [Fact]
    public void The_last_row_cannot_be_removed()
    {
        var library = NewLibrary(T("A"));
        var editor = NewEditor(library, AddPlaylist(library, "New"));

        editor.RemoveCondition(editor.Conditions[0]);

        Assert.Single(editor.Conditions);
    }

    [Fact]
    public void Adding_a_row_puts_it_directly_below_the_one_asked_from()
    {
        var library = NewLibrary(T("A"));
        var editor = NewEditor(library, AddPlaylist(library, "New"));
        editor.AddCondition();
        editor.AddCondition();

        var middle = editor.Conditions[0];
        editor.AddCondition(middle);

        Assert.Equal(4, editor.Conditions.Count);
        Assert.Equal(1, editor.Conditions.IndexOf(editor.Conditions[1]));
        Assert.Same(middle, editor.Conditions[0]);
    }

    // Is/IsNot exist for every value kind, so a field change that could keep the
    // operator should - retyping "is not" after switching Title to Album is the
    // kind of thing that makes an editor feel like it is fighting you.
    [Fact]
    public void Changing_the_field_keeps_an_operator_that_survives_the_move()
    {
        var library = NewLibrary(T("A"));
        var row = NewEditor(library, AddPlaylist(library, "New")).Conditions[0];

        row.Operator = row.Operators.First(o => o.Operator == SmartOperator.IsNot);
        row.Field = SmartConditionRowViewModel.AllFields.First(f => f.Field == SmartField.Year);

        Assert.Equal(SmartOperator.IsNot, row.Operator.Operator);
    }

    [Fact]
    public void Changing_the_field_replaces_an_operator_the_new_kind_does_not_have()
    {
        var library = NewLibrary(T("A"));
        var row = NewEditor(library, AddPlaylist(library, "New")).Conditions[0];

        row.Operator = row.Operators.First(o => o.Operator == SmartOperator.Contains);
        row.Field = SmartConditionRowViewModel.AllFields.First(f => f.Field == SmartField.PlayCount);

        Assert.Contains(row.Operator.Operator, SmartPlaylistFields.OperatorsFor(SmartField.PlayCount));
    }

    [Theory]
    [InlineData(SmartField.Title, SmartOperator.Contains, true, false, false, false)]
    [InlineData(SmartField.Title, SmartOperator.IsEmpty, false, false, false, false)]
    [InlineData(SmartField.DateAdded, SmartOperator.InTheLast, false, false, true, false)]
    [InlineData(SmartField.DateAdded, SmartOperator.GreaterThan, false, true, false, false)]
    [InlineData(SmartField.Starred, SmartOperator.Is, false, false, false, true)]
    public void Only_the_value_controls_the_pairing_needs_are_visible(
        SmartField field, SmartOperator op, bool text, bool date, bool relative, bool boolean)
    {
        var library = NewLibrary(T("A"));
        var row = NewEditor(library, AddPlaylist(library, "New")).Conditions[0];

        row.Field = SmartConditionRowViewModel.AllFields.First(f => f.Field == field);
        row.Operator = row.Operators.First(o => o.Operator == op);

        Assert.Equal(text, row.ShowValueBox);
        Assert.Equal(date, row.ShowDateBox);
        Assert.Equal(relative, row.ShowRelative);
        Assert.Equal(boolean, row.ShowBoolBox);
    }

    [Fact]
    public void Between_shows_a_second_box_and_builds_a_range()
    {
        var library = NewLibrary(T("A"));
        var row = NewEditor(library, AddPlaylist(library, "New")).Conditions[0];

        row.Field = SmartConditionRowViewModel.AllFields.First(f => f.Field == SmartField.Year);
        row.Operator = row.Operators.First(o => o.Operator == SmartOperator.Between);
        row.ValueText = "1970";
        row.SecondValueText = "1979";

        Assert.True(row.ShowSecondValueBox);
        Assert.True(row.TryBuild(out var condition, out _));

        var range = Assert.IsType<SmartValue.Range>(condition!.Value);
        Assert.Equal(new SmartValue.Number(1970), range.From);
        Assert.Equal(new SmartValue.Number(1979), range.To);
    }

    [Fact]
    public void A_value_that_is_not_a_number_is_reported_rather_than_saved()
    {
        var library = NewLibrary(T("A"));
        var editor = NewEditor(library, AddPlaylist(library, "New"));
        var row = editor.Conditions[0];

        row.Field = SmartConditionRowViewModel.AllFields.First(f => f.Field == SmartField.Year);
        row.ValueText = "nineteen seventy";

        Assert.False(editor.Save());
        Assert.True(editor.HasError);
    }

    // "3:30" is three and a half minutes to everyone except TimeSpan.Parse,
    // which reads it as three and a half hours - hence the row's own parser.
    [Theory]
    [InlineData("210", 210)]
    [InlineData("3:30", 210)]
    [InlineData("1:03:30", 3810)]
    public void A_duration_is_read_the_way_it_is_written(string text, int seconds)
    {
        Assert.True(SmartConditionRowViewModel.TryParseDuration(text, out var duration));
        Assert.Equal(TimeSpan.FromSeconds(seconds), duration);
    }

    [Theory]
    [InlineData("")]
    [InlineData("later")]
    [InlineData("1:2:3:4")]
    public void Nonsense_is_not_a_duration(string text)
    {
        Assert.False(SmartConditionRowViewModel.TryParseDuration(text, out _));
    }

    // ── Round trip ────────────────────────────────────────────────────────────

    [Fact]
    public void Existing_rules_come_back_as_rows_and_survive_a_save_untouched()
    {
        var library = NewLibrary(T("A"));
        var rules = new SmartPlaylistRules(
            MatchMode.Any,
            [
                new SmartCondition(SmartField.Genre, SmartOperator.Is, new SmartValue.Text("Jazz")),
                new SmartCondition(SmartField.DateAdded, SmartOperator.InTheLast, new SmartValue.Relative(30, RelativeUnit.Days)),
                new SmartCondition(SmartField.Starred, SmartOperator.Is, new SmartValue.Bool(true)),
            ],
            new SmartLimit(25, LimitUnit.Items, LimitSelector.LeastRecentlyPlayed),
            LiveUpdating: false);

        var playlist = AddPlaylist(library, "Fresh Jazz", rules);
        var editor = NewEditor(library, playlist);

        Assert.Equal(3, editor.Conditions.Count);
        Assert.Equal(MatchMode.Any, editor.MatchMode.Mode);
        Assert.True(editor.LimitEnabled);
        Assert.Equal(25, editor.LimitAmount);
        Assert.False(editor.LiveUpdating);

        Assert.True(editor.Save());

        // Compared field by field rather than as records: SmartPlaylistRules'
        // generated equality compares Conditions by reference, so two rule sets
        // holding the same conditions in different list instances are unequal.
        var saved = playlist.Rules!;
        Assert.Equal(rules.Mode, saved.Mode);
        Assert.Equal(rules.Limit, saved.Limit);
        Assert.Equal(rules.LiveUpdating, saved.LiveUpdating);
        Assert.Equal(rules.Conditions, saved.Conditions);
    }

    [Fact]
    public void Saving_makes_an_ordinary_playlist_smart_and_fills_it_in()
    {
        var library = NewLibrary(T("Played", playCount: 3), T("Unplayed"));
        var playlist = AddPlaylist(library, "Heard");
        var editor = NewEditor(library, playlist);

        var row = editor.Conditions[0];
        row.Field = SmartConditionRowViewModel.AllFields.First(f => f.Field == SmartField.PlayCount);
        row.Operator = row.Operators.First(o => o.Operator == SmartOperator.GreaterThan);
        row.ValueText = "0";

        Assert.True(editor.Save());
        Assert.True(playlist.IsSmart);
        Assert.Equal(["Played"], playlist.Tracks.Select(t => t.Title));
    }

    // LiveUpdating = false is left out of the recurring pass entirely, so if the
    // editor did not evaluate it on save it would sit empty forever.
    [Fact]
    public void A_playlist_that_is_not_live_updating_is_still_filled_in_once_on_save()
    {
        var library = NewLibrary(T("Played", playCount: 3), T("Unplayed"));
        var playlist = AddPlaylist(library, "Heard");
        var editor = NewEditor(library, playlist);
        editor.LiveUpdating = false;

        var row = editor.Conditions[0];
        row.Field = SmartConditionRowViewModel.AllFields.First(f => f.Field == SmartField.PlayCount);
        row.Operator = row.Operators.First(o => o.Operator == SmartOperator.GreaterThan);
        row.ValueText = "0";

        Assert.True(editor.Save());
        Assert.Equal(["Played"], playlist.Tracks.Select(t => t.Title));
    }

    [Fact]
    public void A_renamed_playlist_keeps_the_new_name()
    {
        var library = NewLibrary(T("A"));
        var playlist = AddPlaylist(library, "New Smart Playlist");
        var editor = NewEditor(library, playlist);
        editor.Name = "Sunday Morning";

        Assert.True(editor.Save());
        Assert.Equal("Sunday Morning", playlist.Name);
    }

    // ── Membership rules ──────────────────────────────────────────────────────

    [Fact]
    public void A_membership_row_offers_every_playlist_but_this_one()
    {
        var library = NewLibrary(T("A"));
        var other = AddPlaylist(library, "Ordinary");
        var editing = AddPlaylist(library, "Editing");

        var editor = NewEditor(library, editing);

        Assert.Equal([other.Id], editor.PlaylistCandidates.Select(p => p.Id));
    }

    // The editor is what keeps a loop out of the database in the first place -
    // SmartPlaylistRefresher can only refuse the whole pass once one exists.
    [Fact]
    public void A_playlist_that_already_depends_on_this_one_is_not_offered()
    {
        var library = NewLibrary(T("A"));
        var editing = AddPlaylist(library, "Editing");
        var dependent = AddPlaylist(library, "Dependent", SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.Playlist, SmartOperator.Is, new SmartValue.PlaylistRef(editing.Id))));

        var editor = NewEditor(library, editing);

        Assert.DoesNotContain(dependent.Id, editor.PlaylistCandidates.Select(p => p.Id));
    }

    [Fact]
    public void A_membership_row_builds_a_reference_to_the_picked_playlist()
    {
        var library = NewLibrary(T("A"));
        var other = AddPlaylist(library, "Ordinary");
        var editing = AddPlaylist(library, "Editing");
        var editor = NewEditor(library, editing);

        var row = editor.Conditions[0];
        row.Field = SmartConditionRowViewModel.AllFields.First(f => f.Field == SmartField.Playlist);

        Assert.True(editor.Save());
        Assert.Equal(
            new SmartValue.PlaylistRef(other.Id),
            editing.Rules!.Conditions[0].Value);
    }

    // ── Refusals ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_limit_of_zero_is_refused()
    {
        var library = NewLibrary(T("A"));
        var editor = NewEditor(library, AddPlaylist(library, "New"));
        editor.LimitEnabled = true;
        editor.LimitAmount = 0;

        Assert.False(editor.Save());
        Assert.True(editor.HasError);
    }

    [Fact]
    public void An_error_from_an_earlier_attempt_is_cleared_by_a_successful_one()
    {
        var library = NewLibrary(T("A"));
        var editor = NewEditor(library, AddPlaylist(library, "New"));
        editor.LimitEnabled = true;
        editor.LimitAmount = 0;
        Assert.False(editor.Save());

        editor.LimitEnabled = false;

        Assert.True(editor.Save());
        Assert.False(editor.HasError);
    }

    // ── Cancel ────────────────────────────────────────────────────────────────

    [Fact]
    public void Cancelling_a_playlist_created_for_this_edit_removes_it_again()
    {
        var library = NewLibrary(T("A"));
        var playlist = AddPlaylist(library, "New Smart Playlist");

        NewEditor(library, playlist, isNew: true).Cancel();

        Assert.DoesNotContain(playlist, library.Playlists);
    }

    [Fact]
    public void Cancelling_an_edit_of_an_existing_playlist_leaves_it_alone()
    {
        var library = NewLibrary(T("A"));
        var playlist = AddPlaylist(library, "Existing", SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.Title, SmartOperator.Contains, new SmartValue.Text("a"))));

        NewEditor(library, playlist).Cancel();

        Assert.Contains(playlist, library.Playlists);
        Assert.True(playlist.IsSmart);
    }
}
