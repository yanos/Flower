using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using Flower.Models;
using Flower.Persistence.Sql;
using Flower.SemanticAudio;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests.SemanticAudio;

// The analysis job against songs that exist only as lengths
// (docs/VIBE-PLAYLIST-PLAN.md, "Analysing songs" and "Clips"): what it
// analyses, what it keeps, and above all what it does not analyse again.
public class SemanticAudioAnalyzerTests : IDisposable
{
    private const string Model = "music_audioset_epoch_15_esc_90.14/int8";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "flower-semantic-audio-" + Guid.NewGuid().ToString("N"));
    private readonly SemanticAudioRepository _repository;
    private readonly FakeClipSource _clips = new();
    private readonly FakeClapAudioEncoder _encoder = new();
    private readonly HashSet<string> _files = new();
    private readonly List<Track> _tracks = new();

    public SemanticAudioAnalyzerTests()
    {
        Directory.CreateDirectory(_directory);
        _repository = new SemanticAudioRepository(new FlowerDb(Path.Combine(_directory, "flower.db")));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A pooled handle outliving the test is not what is under test.
        }
    }

    private Track Song(string name, double seconds, DateTimeOffset? added = null, int plays = 0)
    {
        var path = "/music/" + name + ".flac";
        var track = new Track
        {
            Title = name,
            Path = path,
            Duration = TimeSpan.FromSeconds(seconds),
            DateAdded = added ?? DateTimeOffset.UtcNow.AddYears(-1),
            PlayCount = plays,
        };
        _clips.Add(path, track.Duration);
        _files.Add(path);
        _tracks.Add(track);
        return track;
    }

    private SemanticAudioAnalyzer Analyzer(string model = Model) =>
        new(_repository, _clips, () => _encoder, model, NullLogger.Instance, fileExists: _files.Contains);

    private void Pass(int clipsPerSong, SemanticAudioAnalyzer? analyzer = null) =>
        (analyzer ?? Analyzer()).RunPass(_tracks, clipsPerSong, CancellationToken.None);

    [Fact]
    public void Analyses_the_windows_the_setting_selects_and_keeps_them()
    {
        var song = Song("a", 240);

        Pass(6);

        var stored = _repository.LoadSongs()[song.Id];
        Assert.Equal([3, 6, 10, 13, 17, 21], stored.Windows.Order());
        Assert.Equal([3, 6, 10, 13, 17, 21], _encoder.Encoded);
        Assert.Equal(24, stored.WindowCount);
    }

    [Fact]
    public void Lowering_the_setting_analyses_nothing()
    {
        Song("a", 240);
        Song("b", 183);
        Pass(12);
        _encoder.Encoded.Clear();

        Pass(6);
        Pass(3);

        Assert.Empty(_encoder.Encoded);
    }

    [Fact]
    public void Going_three_to_whole_song_to_six_never_analyses_a_window_twice()
    {
        var song = Song("a", 240);

        Pass(3);
        Pass(ClipGrid.WholeSong);
        var afterWholeSong = _encoder.Encoded.Count;
        Pass(6);

        Assert.Equal(_encoder.Encoded.Count, _encoder.Encoded.Distinct().Count());
        Assert.Equal(24, afterWholeSong);
        Assert.Equal(24, _encoder.Encoded.Count);
        Assert.Equal(Enumerable.Range(0, 24), _repository.LoadSongs()[song.Id].Windows.Order());
    }

    [Fact]
    public void Raising_the_setting_analyses_only_the_windows_missing()
    {
        Song("a", 240);
        Pass(3);
        var first = _encoder.Encoded.ToList();

        Pass(6);

        var added = _encoder.Encoded.Skip(first.Count).ToList();
        Assert.NotEmpty(added);
        Assert.DoesNotContain(added, first.Contains);
    }

    [Fact]
    public void A_replaced_file_is_analysed_again_from_scratch()
    {
        var song = Song("a", 240);
        Pass(ClipGrid.WholeSong);
        _encoder.Encoded.Clear();

        song.FileReplacedAt = DateTimeOffset.UtcNow;
        Pass(3);

        Assert.Equal(3, _encoder.Encoded.Count);
        Assert.Equal(3, _repository.LoadSongs()[song.Id].Windows.Count);
    }

    [Fact]
    public void A_re_encode_is_a_change()
    {
        var song = Song("a", 240);
        song.Bitrate = 256;
        Pass(6);
        _encoder.Encoded.Clear();

        song.Bitrate = 320;
        Pass(6);

        Assert.Equal(6, _encoder.Encoded.Count);
    }

    [Fact]
    public void A_tag_or_artwork_edit_is_not_a_change()
    {
        var song = Song("a", 240);
        Pass(6);
        _encoder.Encoded.Clear();

        song.Title = "Renamed";
        song.Genre = "Jazz";
        song.TagsEditedAt = DateTimeOffset.UtcNow;
        song.ArtEditedAt = DateTimeOffset.UtcNow;
        Pass(6);

        Assert.Empty(_encoder.Encoded);
    }

    [Fact]
    public void Another_export_of_the_model_makes_every_song_stale()
    {
        Song("a", 240);
        Pass(6);
        _encoder.Encoded.Clear();

        Pass(6, Analyzer("music_audioset_epoch_15_esc_90.14/int4"));

        Assert.Equal(6, _encoder.Encoded.Count);
    }

    [Fact]
    public void A_song_that_cannot_be_read_is_recorded_and_not_tried_again_until_it_changes()
    {
        var song = Song("broken", 240);
        _clips.MakeUnreadable(song.Path!);

        var analyzer = Analyzer();
        analyzer.RunPass(_tracks, 6, CancellationToken.None);
        analyzer.RunPass(_tracks, 6, CancellationToken.None);

        Assert.True(_repository.LoadSongs()[song.Id].Failed);
        Assert.Equal(1, analyzer.Progress.Failed);
        Assert.Single(_clips.Opens);

        song.FileReplacedAt = DateTimeOffset.UtcNow;
        analyzer.RunPass(_tracks, 6, CancellationToken.None);
        Assert.Equal(2, _clips.Opens.Count);
    }

    [Fact]
    public void A_song_that_leaves_the_library_loses_what_was_heard_of_it()
    {
        var kept = Song("kept", 240);
        var gone = Song("gone", 240);
        var analyzer = Analyzer();
        analyzer.RunPass(_tracks, 6, CancellationToken.None);
        IReadOnlyCollection<Guid>? removed = null;
        analyzer.SongsRemoved += ids => removed = ids;

        _tracks.Remove(gone);
        analyzer.RunPass(_tracks, 6, CancellationToken.None);

        Assert.Equal([gone.Id], removed);
        Assert.Equal([kept.Id], _repository.LoadSongs().Keys);
    }

    [Fact]
    public void A_song_whose_file_is_missing_is_left_alone_and_not_counted()
    {
        var song = Song("a", 240);
        _files.Remove(song.Path!);

        var analyzer = Analyzer();
        analyzer.RunPass(_tracks, 6, CancellationToken.None);

        Assert.Empty(_encoder.Encoded);
        Assert.Equal(0, analyzer.Progress.Eligible);
    }

    [Fact]
    public void Recently_added_songs_come_first_then_the_most_played()
    {
        var now = DateTimeOffset.UtcNow;
        var old = Song("old", 240, now.AddYears(-2), plays: 1);
        var favourite = Song("favourite", 240, now.AddYears(-2), plays: 50);
        var newest = Song("newest", 240, now.AddDays(-1));
        var newer = Song("newer", 240, now.AddDays(-10));

        var plan = SemanticAudioAnalyzer.MakePlan(_tracks, new Dictionary<Guid, AnalysedSong>(), 6, Model, _ => true, now);

        Assert.Equal([newest.Id, newer.Id, favourite.Id, old.Id], plan.Work.Select(w => w.Track.Id));
    }

    [Fact]
    public void Counts_progress_over_the_songs_it_can_hear()
    {
        Song("a", 240);
        Song("b", 240);
        Song("c", 240);
        var analyzer = Analyzer();
        analyzer.RunPass(_tracks.Take(1).ToList(), 6, CancellationToken.None);

        analyzer.RunPass(_tracks, 6, CancellationToken.None);

        var progress = analyzer.Progress;
        Assert.Equal((3, 3, 0, null, null), (progress.Done, progress.Eligible, progress.Failed, progress.CurrentTitle, progress.CurrentArtist));
    }
}
