using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels;

// Track Info's one definition, for the desktop window and the phone's sheet:
// they used to be written once each and had drifted - two rules for songs
// that disagree, and a phone that could not set the compilation flag. The
// songs here have no file, so a save edits the songs alone, as it does for
// one this device streams from its server.
[Collection("PlatformDataDirectory")]
public class TrackInfoViewModelTests : PinnedDataDirectory
{
    private static Track Song(string title, string album = "Doolittle", string? artist = "Pixies") =>
        new() { Title = title, Album = album, Artists = artist, Duration = TimeSpan.FromMinutes(3) };

    private static TrackInfoViewModel Open(Library library, IReadOnlyList<Track> tracks, Func<Track, bool>? mayEdit = null)
    {
        var info = new TrackInfoViewModel(library, mayEdit ?? (_ => true), _ => null, NullLogger.Instance);
        info.Load(tracks);
        return info;
    }

    [Fact]
    public void Songs_that_disagree_leave_the_box_empty_and_say_so_and_ones_that_agree_show_their_value()
    {
        var songs = new[] { Song("Debaser"), Song("Tame") };
        var info = Open(new Library([.. songs]), songs);

        Assert.Equal("", info.Title.Text);
        Assert.Equal(TrackInfoViewModel.MixedValues, info.Title.Placeholder);
        Assert.Equal(TrackInfoViewModel.MixedValues, info.TitleText);

        Assert.Equal("Doolittle", info.Album.Text);
        Assert.Null(info.Album.Placeholder);
        Assert.Equal("Doolittle", info.AlbumText);
    }

    // Leaving "Multiple values" alone is what keeps each song's own title.
    [Fact]
    public void Only_what_was_typed_is_saved_to_every_song()
    {
        var songs = new[] { Song("Debaser"), Song("Tame") };
        var info = Open(new Library([.. songs]), songs);

        info.Album.Text = "Surfer Rosa";
        var edited = info.Save();

        Assert.Equal(songs, edited);
        Assert.All(songs, s => Assert.Equal("Surfer Rosa", s.Album));
        Assert.Equal(["Debaser", "Tame"], songs.Select(s => s.Title));
    }

    // Prev/Next saves on the way, and OK saves again: the second must not
    // write the same edit twice.
    [Fact]
    public void A_second_save_with_nothing_new_typed_writes_nothing()
    {
        var song = Song("Debaser");
        var info = Open(new Library([song]), [song]);

        info.Title.Text = "Debaser (Demo)";
        Assert.Single(info.Save());

        Assert.Empty(info.Save());
    }

    [Fact]
    public void The_compilation_flag_is_staged_with_the_tags_and_left_alone_when_untouched()
    {
        var songs = new[] { Song("Debaser"), Song("Tame") };
        songs[0].IsCompilation = true;
        var info = Open(new Library([.. songs]), songs);

        Assert.Null(info.IsCompilation);
        Assert.True(info.CompilationIsThreeState);
        info.Title.Text = "Same for both";
        info.Save();
        Assert.Equal([true, false], songs.Select(s => s.IsCompilation));

        info.IsCompilation = true;
        Assert.All(songs.Skip(1), s => Assert.False(s.IsCompilation));
        info.Save();
        Assert.All(songs, s => Assert.True(s.IsCompilation));
    }

    [Fact]
    public void Songs_this_device_may_not_edit_are_shown_but_not_saved()
    {
        var song = Song("Debaser");
        var info = Open(new Library([song]), [song], mayEdit: _ => false);

        Assert.False(info.IsEditable);
        Assert.NotEqual("", info.OptionsUnavailableText);

        info.Title.Text = "Something else";
        info.Save();

        Assert.Equal("Debaser", song.Title);
    }

    // An empty sort box reads as "sorts under its own name", not as a field
    // nobody filled in.
    [Fact]
    public void An_empty_sort_box_shows_the_value_it_sorts()
    {
        var song = Song("The Holiday Song");
        var info = Open(new Library([song]), [song]);

        Assert.Equal("", info.TitleSort.Text);
        Assert.Equal("The Holiday Song", info.TitleSort.Placeholder);
    }

    // Flower's own record rather than a tag: written as it is changed, with
    // no Save, and turning it off forgets where the song had got to.
    [Fact]
    public void Turning_off_remembered_position_forgets_it_at_once()
    {
        var song = Song("A Long Mix");
        song.RememberPlaybackPosition = true;
        song.ResumePosition = TimeSpan.FromMinutes(42);
        var library = new Library([song]);
        var info = Open(library, [song]);
        var changes = new List<TrackChange>();
        library.TrackChanged += (_, e) => changes.Add(e.Change);

        Assert.NotEqual("", info.ResumePositionText);
        info.RememberPlaybackPosition = false;

        Assert.False(song.RememberPlaybackPosition);
        Assert.Null(song.ResumePosition);
        Assert.Equal("", info.ResumePositionText);
        Assert.Contains(TrackChange.Options, changes);
    }

    [Fact]
    public void The_star_goes_on_every_song_unless_they_all_have_it()
    {
        var songs = new[] { Song("Debaser"), Song("Tame") };
        var library = new Library([.. songs]);
        var info = Open(library, songs);
        library.SetStarred(StarTarget.Song, songs[0].Id.ToString(), true);
        info.Load(songs);

        Assert.Equal("1 of 2 starred", info.StarText);
        info.ToggleStar();
        Assert.All(songs, s => Assert.True(s.Starred));

        info.ToggleStar();
        Assert.All(songs, s => Assert.False(s.Starred));
    }
}
