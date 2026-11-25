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

    // UF-9: a file that refuses the edit is said out loud, and what was typed
    // is still there to try again.
    [Fact]
    public void A_file_that_refuses_the_edit_is_reported_and_keeps_what_was_typed()
    {
        var song = Song("Debaser");
        song.Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.mp3");
        var info = Open(new Library([song]), [song]);
        info.Title.Text = "Debaser (Demo)";

        Assert.Empty(info.Save());

        Assert.Contains("Debaser", info.SaveError);
        Assert.Equal("Debaser (Demo)", info.Title.Text);
        Assert.Equal("Debaser", song.Title);
    }

    // What plays from a server is a private copy whose Path is the stream's
    // URL. Editing that copy always failed, and left the library's song alone.
    [Fact]
    public void Info_on_a_streaming_song_edits_the_librarys_own()
    {
        var song = Song("Debaser");
        var streaming = song.Clone();
        streaming.Path = "https://server.example/stream/1";
        var info = Open(new Library([song]), [streaming]);
        info.Title.Text = "Debaser (Demo)";

        info.Save();

        Assert.Null(info.SaveError);
        Assert.Equal("Debaser (Demo)", song.Title);
    }

    [Fact]
    public void Songs_that_disagree_leave_the_box_empty_and_say_so_and_ones_that_agree_show_their_value()
    {
        var songs = new[] { Song("Debaser"), Song("Tame") };
        var info = Open(new Library([.. songs]), songs);

        Assert.Equal("", info.Title.Text);
        Assert.False(string.IsNullOrWhiteSpace(info.Title.Placeholder));

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

    // UF-14: an empty box over songs that disagree leaves each as it is, so
    // emptying the field for all of them is its own action.
    [Fact]
    public void Clearing_a_box_that_shows_multiple_values_empties_it_for_every_song()
    {
        var songs = new[] { Song("Debaser", album: "Doolittle"), Song("Gigantic", album: "Surfer Rosa") };
        var info = Open(new Library([.. songs]), songs);
        Assert.True(info.Album.OffersClear);

        info.Album.ClearCommand.Execute(null);
        info.Save();

        Assert.All(songs, s => Assert.True(string.IsNullOrEmpty(s.Album)));
        Assert.Equal(["Debaser", "Gigantic"], songs.Select(s => s.Title));
        Assert.Empty(info.Save());
    }

    // A box all the songs agree on is cleared the ordinary way, by emptying it.
    [Fact]
    public void A_box_the_songs_agree_on_offers_no_separate_clear()
    {
        var songs = new[] { Song("Debaser"), Song("Tame") };
        var info = Open(new Library([.. songs]), songs);

        Assert.False(info.Album.OffersClear);
        Assert.False(info.Album.ClearCommand.CanExecute(null));
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
        library.SetStarred(songs[0].Id.ToString(), true);
        info.Load(songs);

        Assert.True(info.IsAnyStarred);
        Assert.False(info.IsAllStarred);
        info.ToggleStar();
        Assert.All(songs, s => Assert.True(s.Starred));

        info.ToggleStar();
        Assert.All(songs, s => Assert.False(s.Starred));
    }

    // What TagLib reads off a .dsf, beside what it reads off a FLAC.
    [Fact]
    public void A_DSD_song_says_it_is_decoded_to_PCM_and_a_PCM_one_does_not()
    {
        var dsd = Song("Debaser");
        (dsd.Codec, dsd.SampleRate, dsd.BitsPerSample) = ("DSF Audio", 2822400, 1);
        var flac = Song("Tame");
        (flac.Codec, flac.SampleRate, flac.BitsPerSample) = ("Flac Audio", 96000, 24);
        var library = new Library([dsd, flac]);

        Assert.Contains("PCM", Open(library, [dsd]).CodecValue);
        Assert.Equal("Flac Audio", Open(library, [flac]).CodecValue);
    }

    // All that goes to the server: the rest of a streamed song's tags may
    // never have been sent to this device, and would go up as blanks.
    [Fact]
    public void A_save_records_only_the_fields_that_were_edited()
    {
        var song = Song("Debaser");
        var info = Open(new Library([song]), [song]);

        info.Title.Text = "Debaser (Demo)";
        info.Save();

        Assert.Equal(Flower.Services.TrackTagFieldSet.Title, song.TagFieldsToSend);
    }
}
