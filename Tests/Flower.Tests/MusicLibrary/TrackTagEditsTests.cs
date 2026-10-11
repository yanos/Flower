using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.MusicLibrary;

// The save both Track Info screens share (TrackTagEdits), and the
// suggestions the phone's sheet offers under a box as it is typed in.
public class TrackTagEditsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("flower-tag-edits").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void An_edit_goes_into_the_file_and_then_the_song()
    {
        var path = SyntheticWav.CreateFile(_root, "song.wav", TimeSpan.FromSeconds(1), SyntheticWav.Marker(1));
        var song = new Track { Title = "Old", Path = path };

        var edited = TrackTagEdits.Apply([song], [(TrackTagFields.Title, "New")], _ => true, NullLogger.Instance);

        Assert.Equal([song], edited);
        Assert.Equal("New", song.Title);
        using var file = TagLib.File.Create(path);
        Assert.Equal("New", file.Tag.Title);
    }

    // The file is written first, so a song whose file refused the edit keeps
    // what it had rather than showing an edit its file does not have.
    [Fact]
    public void A_song_whose_file_refuses_the_edit_keeps_what_it_had()
    {
        var song = new Track { Title = "Old", Path = Path.Combine(_root, "missing.mp3") };

        var edited = TrackTagEdits.Apply([song], [(TrackTagFields.Title, "New")], _ => true, NullLogger.Instance);

        Assert.Empty(edited);
        Assert.Equal("Old", song.Title);
    }

    [Fact]
    public void A_song_this_device_may_not_edit_is_left_alone()
    {
        var song = new Track { Title = "Old" };

        Assert.Empty(TrackTagEdits.Apply([song], [(TrackTagFields.Title, "New")], _ => false, NullLogger.Instance));
        Assert.Equal("Old", song.Title);
    }

    [Fact]
    public void Several_songs_show_their_one_value_or_none()
    {
        var a = new Track { Title = "A", Album = "Same" };
        var b = new Track { Title = "B", Album = "Same" };

        Assert.Equal("Same", TrackTagFields.CommonValue(TrackTagFields.Album, [a, b]));
        Assert.Null(TrackTagFields.CommonValue(TrackTagFields.Title, [a, b]));
    }

    [Fact]
    public void Suggestions_start_with_the_text_then_contain_it_and_never_repeat_it()
    {
        List<string> pool = ["Add N to (X)", "Beirut", "Deerhoof", "The Add-Ons", "Eluvium"];

        Assert.Equal(new[] { "Add N to (X)", "The Add-Ons" }, TagSuggestionSource.Matching(pool, "add"));
        Assert.Empty(TagSuggestionSource.Matching(pool, "Beirut"));
        Assert.Empty(TagSuggestionSource.Matching(pool, "  "));
    }
}
