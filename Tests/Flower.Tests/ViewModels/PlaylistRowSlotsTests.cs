using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels;

// UF-8: the list on screen can be sorted, so a row's position is not its place
// in the playlist; for a song in it twice, which row it is says which entry.
public class PlaylistRowSlotsTests
{
    private static Track T(string title) => new() { Title = title, Path = $"/music/{title}.mp3" };

    private static Playlist Of(params Track[] tracks) =>
        new(Guid.NewGuid(), "Mix", [], DateTimeOffset.UtcNow, entries: tracks.Select(PlaylistEntry.Of).ToList());

    private static TrackRowViewModel Row(PlaylistEntry entry) => new() { Track = entry.Track!, EntryId = entry.EntryId };

    [Fact]
    public void Each_row_of_a_repeated_song_is_its_own_entry()
    {
        var (a, b) = (T("A"), T("B"));
        var playlist = Of(a, b, a);
        var rows = playlist.Entries.Select(Row).ToArray();

        Assert.Equal([2], PlaylistRowSlots.SlotsOf(playlist, [rows[2]]));
        Assert.Equal([0], PlaylistRowSlots.SlotsOf(playlist, [rows[0]]));
    }

    [Fact]
    public void Rows_in_another_order_still_find_their_entries()
    {
        var playlist = Of(T("A"), T("B"), T("C"));
        var rows = playlist.Entries.Select(Row).Reverse().ToArray();

        Assert.Equal([1, 0], PlaylistRowSlots.SlotsOf(playlist, [rows[1], rows[2]]));
    }

    // A song this device cannot play is hidden, and a slot counts only the
    // songs shown.
    [Fact]
    public void A_slot_counts_only_the_songs_shown()
    {
        var (a, b) = (T("A"), T("B"));
        var away = new PlaylistEntry(Guid.NewGuid(), null, AwayTrackId: Guid.NewGuid());
        var playlist = new Playlist(Guid.NewGuid(), "Mix", [], DateTimeOffset.UtcNow,
            entries: [PlaylistEntry.Of(a), away, PlaylistEntry.Of(b)]);

        Assert.Equal([1], PlaylistRowSlots.SlotsOf(playlist, [Row(playlist.Entries[2])]));
    }

    [Fact]
    public void A_row_that_is_not_in_the_list_has_no_slot()
    {
        var a = T("A");

        Assert.Empty(PlaylistRowSlots.SlotsOf(Of(a), [Row(PlaylistEntry.Of(a))]));
    }
}
