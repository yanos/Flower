using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests;

// The server's half of an upload - see LibraryIngest. A real library, a real
// music folder and real tagged files: what is under test is where a file ends
// up, what the library says about it afterwards, and everything this will not
// write.
public sealed class LibraryIngestTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("flower-ingest").FullName;
    private readonly string _music;
    private readonly string _source;
    private readonly Library _library = new([]);
    private readonly LibraryIngest _ingest;

    public LibraryIngestTests()
    {
        _music = Path.Combine(_root, "music");
        _source = Path.Combine(_root, "device");
        Directory.CreateDirectory(_music);
        _ingest = new LibraryIngest(
            _library, () => [_music], Path.Combine(_root, "staging"),
            new Flower.Importer.Importer(NullLogger<Flower.Importer.Importer>.Instance), NullLogger.Instance);
    }

    public void Dispose() => TempDirectory.DeleteWhenReleased(_root);

    private static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static LibraryUploadRequestDto RequestFor(string relativePath, byte[] bytes, DateTimeOffset? dateAdded = null) =>
        new(relativePath, bytes.Length, Sha256Of(bytes), dateAdded ?? DateTimeOffset.UtcNow);

    private byte[] SongBytes(string title, byte seed = 1) =>
        File.ReadAllBytes(TaggedAudioFile.Create(_source, $"{Guid.NewGuid():N}.wav", title, seed: seed));

    // The whole exchange, the way a device runs it: begin, then pieces from
    // wherever the server says it has got to.
    private async Task<IngestResult> UploadAsync(string relativePath, byte[] bytes, int chunk = 4096, DateTimeOffset? dateAdded = null)
    {
        var result = await _ingest.BeginAsync(RequestFor(relativePath, bytes, dateAdded), TestContext.Current.CancellationToken);
        while (result is { Outcome: IngestOutcome.Accepted, Status: { UploadId: { } id, Offset: var offset } })
        {
            var piece = bytes.AsMemory((int)offset, (int)Math.Min(chunk, bytes.Length - offset));
            result = await _ingest.AppendAsync(id, offset, piece, TestContext.Current.CancellationToken);
        }

        return result;
    }

    [Fact]
    public async Task An_uploaded_file_lands_at_the_same_path_below_the_music_folder_and_is_in_the_library()
    {
        var bytes = SongBytes("Fabienk");

        var result = await UploadAsync("Angine de Poitrine/Vol.II/01 Fabienk.wav", bytes);

        Assert.Equal(IngestOutcome.Completed, result.Outcome);
        var landed = Path.Combine(_music, "Angine de Poitrine", "Vol.II", "01 Fabienk.wav");
        Assert.Equal(bytes, File.ReadAllBytes(landed));
        var track = Assert.Single(_library.Tracks);
        Assert.Equal("Fabienk", track.Title);
        Assert.Equal(landed, track.Path);
        Assert.Equal(track.Id.ToKey(), result.Status!.TrackId);
        Assert.Null(result.Status.UploadId);
        Assert.Equal(_library.ChangeToken, result.Status.LibraryToken);
    }

    [Fact]
    public async Task The_song_is_dated_by_the_device_it_came_from()
    {
        var since = new DateTimeOffset(2018, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var result = await UploadAsync("a.wav", SongBytes("A"), dateAdded: since);

        Assert.Equal(since, _library.Tracks.Single().DateAdded);
        Assert.Equal(since, result.Status!.DateAdded);
    }

    // Nothing is in the music folder until all of it is: a scan that runs in
    // the middle of an upload finds no half-file to import.
    [Fact]
    public async Task Nothing_appears_in_the_music_folder_until_the_last_piece_arrives()
    {
        var bytes = SongBytes("A");
        var begun = await _ingest.BeginAsync(RequestFor("a.wav", bytes), TestContext.Current.CancellationToken);

        var partway = await _ingest.AppendAsync(begun.Status!.UploadId!, 0, bytes.AsMemory(0, 1000), TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.Accepted, partway.Outcome);
        Assert.Equal(1000, partway.Status!.Offset);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_music));
        Assert.Empty(_library.Tracks);
    }

    // The phone went to sleep. Beginning again is told where the server got
    // to, and only the rest is sent.
    [Fact]
    public async Task An_interrupted_upload_resumes_from_where_the_server_got_to()
    {
        var bytes = SongBytes("A");
        var begun = await _ingest.BeginAsync(RequestFor("a.wav", bytes), TestContext.Current.CancellationToken);
        await _ingest.AppendAsync(begun.Status!.UploadId!, 0, bytes.AsMemory(0, 1000), TestContext.Current.CancellationToken);

        var resumed = await _ingest.BeginAsync(RequestFor("a.wav", bytes), TestContext.Current.CancellationToken);
        Assert.Equal(1000, resumed.Status!.Offset);

        var finished = await _ingest.AppendAsync(resumed.Status.UploadId!, 1000, bytes.AsMemory(1000), TestContext.Current.CancellationToken);
        Assert.Equal(IngestOutcome.Completed, finished.Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_music, "a.wav")));
    }

    // A retry of a piece that did land: nothing is written twice, and the
    // answer says where to carry on.
    [Fact]
    public async Task A_piece_sent_at_the_wrong_offset_is_not_written_and_the_answer_says_where_to_carry_on()
    {
        var bytes = SongBytes("A");
        var begun = await _ingest.BeginAsync(RequestFor("a.wav", bytes), TestContext.Current.CancellationToken);
        var id = begun.Status!.UploadId!;
        await _ingest.AppendAsync(id, 0, bytes.AsMemory(0, 1000), TestContext.Current.CancellationToken);

        var again = await _ingest.AppendAsync(id, 0, bytes.AsMemory(0, 1000), TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.Accepted, again.Outcome);
        Assert.Equal(1000, again.Status!.Offset);
    }

    [Fact]
    public async Task A_file_that_arrives_different_from_what_was_promised_is_discarded()
    {
        var bytes = SongBytes("A");
        var begun = await _ingest.BeginAsync(RequestFor("a.wav", bytes), TestContext.Current.CancellationToken);
        var tampered = (byte[])bytes.Clone();
        tampered[^1] ^= 0xFF;

        var result = await _ingest.AppendAsync(begun.Status!.UploadId!, 0, tampered, TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.Corrupt, result.Outcome);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_music));
        Assert.Empty(_library.Tracks);

        // And the next attempt starts clean rather than on the bad bytes.
        var retry = await _ingest.BeginAsync(RequestFor("a.wav", bytes), TestContext.Current.CancellationToken);
        Assert.Equal(0, retry.Status!.Offset);
    }

    // Which of two different files is right is not something a sync can know.
    [Fact]
    public async Task A_name_that_is_taken_by_a_different_file_is_refused_and_nothing_is_overwritten()
    {
        var first = SongBytes("A", seed: 1);
        await UploadAsync("a.wav", first);

        var result = await UploadAsync("a.wav", SongBytes("A", seed: 2));

        Assert.Equal(IngestOutcome.Conflict, result.Outcome);
        Assert.Equal(first, File.ReadAllBytes(Path.Combine(_music, "a.wav")));
        Assert.Single(_library.Tracks);
    }

    // The same bytes under the same name got here some other way. Taking the
    // file in is what the upload was for, and sending it again is not.
    [Fact]
    public async Task A_file_the_server_already_holds_byte_for_byte_is_taken_in_without_being_sent()
    {
        var bytes = SongBytes("A");
        File.WriteAllBytes(Path.Combine(_music, "a.wav"), bytes);

        var result = await _ingest.BeginAsync(RequestFor("a.wav", bytes), TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.Completed, result.Outcome);
        Assert.Equal("A", _library.Tracks.Single().Title);
    }

    // Removed from the library and kept on disk means kept out of every scan.
    // An owner's device offering it again is the owner putting it back.
    [Fact]
    public async Task Uploading_a_song_that_was_removed_and_kept_on_disk_puts_it_back_as_the_song_it_was()
    {
        var bytes = SongBytes("A");
        await UploadAsync("a.wav", bytes);
        var original = _library.Tracks.Single();
        original.PlayCount = 9;
        _library.RemoveTracks([original], excludePaths: [original.Path!]);
        Assert.Single(_library.ExcludedPaths);

        var result = await _ingest.BeginAsync(RequestFor("a.wav", bytes), TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.Completed, result.Outcome);
        Assert.Empty(_library.ExcludedPaths);
        Assert.Equal(original.Id, _library.Tracks.Single().Id);
        Assert.Equal(9, _library.Tracks.Single().PlayCount);
    }

    [Fact]
    public async Task Uploading_a_song_whose_file_was_deleted_restores_what_the_library_knew_about_it()
    {
        var bytes = SongBytes("A");
        await UploadAsync("a.wav", bytes, dateAdded: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var original = _library.Tracks.Single();
        original.Starred = true;
        _library.RemoveTracks([original], excludePaths: []);
        File.Delete(original.Path!);

        var result = await UploadAsync("somewhere else/a.wav", bytes);

        Assert.Equal(IngestOutcome.Completed, result.Outcome);
        var back = _library.Tracks.Single();
        Assert.Equal(original.Id, back.Id);
        Assert.True(back.Starred);
        Assert.Equal(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), back.DateAdded);
    }

    // ── A new version of a song already here ─────────────────────────────

    [Fact]
    public async Task A_new_version_of_a_song_replaces_its_file_and_keeps_everything_known_about_it()
    {
        await UploadAsync("album/song.wav", SongBytes("Teh Song"));
        var original = _library.Tracks.Single();
        original.PlayCount = 4;
        original.Starred = true;
        var corrected = SongBytes("The Song");

        var result = await UploadReplacementAsync(original.Id.ToKey(), "wherever/the/device/keeps/it.wav", corrected);

        Assert.Equal(IngestOutcome.Completed, result.Outcome);
        var song = _library.Tracks.Single();
        Assert.Equal(original.Id, song.Id);
        Assert.Equal("The Song", song.Title);
        Assert.Equal(4, song.PlayCount);
        Assert.True(song.Starred);

        // Where the server kept the song, not where the device does - and
        // nothing left lying beside it.
        Assert.Equal(corrected, File.ReadAllBytes(Path.Combine(_music, "album", "song.wav")));
        Assert.Equal(["song.wav"], Directory.EnumerateFiles(Path.Combine(_music, "album")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_new_version_identical_to_what_the_server_has_is_not_sent()
    {
        var bytes = SongBytes("A");
        await UploadAsync("a.wav", bytes);
        var id = _library.Tracks.Single().Id.ToKey();

        var result = await _ingest.BeginAsync(
            RequestFor("a.wav", bytes) with { ReplacesTrackId = id }, TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.Completed, result.Outcome);
        Assert.Equal(id, result.Status!.TrackId);
    }

    // The importer cannot read the new one: the library keeps the copy it could.
    [Fact]
    public async Task A_new_version_the_importer_cannot_read_leaves_the_old_one_in_place()
    {
        var good = SongBytes("A");
        await UploadAsync("a.wav", good);
        var id = _library.Tracks.Single().Id.ToKey();
        var noise = new byte[5000];
        Random.Shared.NextBytes(noise);

        var result = await UploadReplacementAsync(id, "a.wav", noise);

        Assert.Equal(IngestOutcome.Invalid, result.Outcome);
        Assert.Equal(good, File.ReadAllBytes(Path.Combine(_music, "a.wav")));
        Assert.Equal(["a.wav"], Directory.EnumerateFiles(_music).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_new_version_of_a_song_this_server_no_longer_has_is_refused()
    {
        var result = await _ingest.BeginAsync(
            RequestFor("a.wav", SongBytes("A")) with { ReplacesTrackId = Guid.NewGuid().ToString("N") },
            TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.Invalid, result.Outcome);
    }

    private async Task<IngestResult> UploadReplacementAsync(string trackId, string relativePath, byte[] bytes)
    {
        var result = await _ingest.BeginAsync(
            RequestFor(relativePath, bytes) with { ReplacesTrackId = trackId }, TestContext.Current.CancellationToken);
        while (result is { Outcome: IngestOutcome.Accepted, Status: { UploadId: { } id, Offset: var offset } })
        {
            var piece = bytes.AsMemory((int)offset, (int)Math.Min(4096, bytes.Length - offset));
            result = await _ingest.AppendAsync(id, offset, piece, TestContext.Current.CancellationToken);
        }

        return result;
    }

    // ── A file that was set aside ────────────────────────────────────────

    // Removed from the library and waiting to be cleaned up is not the same
    // as being kept. A different file wanting the name gets it.
    [Fact]
    public async Task A_removed_file_that_was_set_aside_gives_way_to_a_different_one_uploaded_in_its_place()
    {
        await UploadAsync("a.wav", SongBytes("A", seed: 1));
        var original = _library.Tracks.Single();
        _library.RemoveTracks([original], excludePaths: [original.Path!]);
        var different = SongBytes("A", seed: 2);

        var result = await UploadAsync("a.wav", different);

        Assert.Equal(IngestOutcome.Completed, result.Outcome);
        Assert.Equal(different, File.ReadAllBytes(Path.Combine(_music, "a.wav")));
        Assert.Empty(_library.ExcludedPaths);
        Assert.Single(_library.Tracks);
    }

    // ── A move ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_move_puts_the_file_at_its_new_path_and_changes_nothing_else_about_the_song()
    {
        await UploadAsync("Artist/Old/01 Song.wav", SongBytes("Song"));
        await UploadAsync("Artist/Other/keep.wav", SongBytes("Keep"));
        var song = _library.Tracks.Single(t => t.Title == "Song");
        song.PlayCount = 3;

        var result = await _ingest.MoveAsync(
            new LibraryMoveRequestDto(song.Id.ToKey(), "Artist/New/1 Song.wav"), TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.Completed, result.Outcome);
        Assert.Equal("Artist/New/1 Song.wav", result.Moved!.RelativePath);
        var moved = _library.Find(song.Id.ToKey())!;
        Assert.Equal(Path.Combine(_music, "Artist", "New", "1 Song.wav"), moved.Path);
        Assert.Equal(3, moved.PlayCount);
        Assert.True(File.Exists(moved.Path));

        // The folder it emptied goes; its neighbour, and the artist, stay.
        Assert.False(Directory.Exists(Path.Combine(_music, "Artist", "Old")));
        Assert.True(Directory.Exists(Path.Combine(_music, "Artist", "Other")));
        Assert.Empty(_library.RemovedTracks);
    }

    [Fact]
    public async Task A_move_onto_another_songs_file_is_refused()
    {
        await UploadAsync("a.wav", SongBytes("A", seed: 1));
        await UploadAsync("b.wav", SongBytes("B", seed: 2));
        var a = _library.Tracks.Single(t => t.Title == "A");

        var result = await _ingest.MoveAsync(new LibraryMoveRequestDto(a.Id.ToKey(), "b.wav"), TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.Conflict, result.Outcome);
        Assert.True(File.Exists(Path.Combine(_music, "a.wav")));
        Assert.Equal(Path.Combine(_music, "a.wav"), a.Path);
    }

    [Theory]
    [InlineData("../escaped.wav")]
    [InlineData(".hidden/a.wav")]
    [InlineData("a.mp3")]
    public async Task A_move_is_held_to_the_same_rules_about_paths_as_an_upload_and_cannot_change_the_file_type(string relativePath)
    {
        await UploadAsync("a.wav", SongBytes("A"));
        var a = _library.Tracks.Single();

        var result = await _ingest.MoveAsync(new LibraryMoveRequestDto(a.Id.ToKey(), relativePath), TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.Invalid, result.Outcome);
        Assert.True(File.Exists(Path.Combine(_music, "a.wav")));
    }

    [Fact]
    public async Task A_move_of_a_song_this_server_does_not_have_says_so()
    {
        var result = await _ingest.MoveAsync(
            new LibraryMoveRequestDto(Guid.NewGuid().ToString("N"), "a.wav"), TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.UnknownUpload, result.Outcome);
    }

    // ── Cleaning up ──────────────────────────────────────────────────────

    // Only what is on the list, whatever is asked for.
    [Fact]
    public async Task Deleting_removed_files_touches_only_files_that_were_removed()
    {
        await UploadAsync("album/gone.wav", SongBytes("Gone", seed: 1));
        await UploadAsync("album/kept.wav", SongBytes("Kept", seed: 2));
        var gone = _library.Tracks.Single(t => t.Title == "Gone");
        var kept = _library.Tracks.Single(t => t.Title == "Kept");
        _library.RemoveTracks([gone], excludePaths: [gone.Path!]);

        var (deleted, notDeleted) = LibraryRemoval.DeleteRemovedFiles(
            _library, [gone.Path!, kept.Path!, Path.Combine(_root, "device", "anything.wav")], [_music], NullLogger.Instance);

        Assert.Equal((1, 0), (deleted, notDeleted));
        Assert.False(File.Exists(gone.Path));
        Assert.True(File.Exists(kept.Path));
        Assert.Empty(_library.ExcludedPaths);
        Assert.Single(_library.Tracks);

        // Deleted, and still on record: put back one day, it is itself again.
        Assert.Contains(_library.RemovedTracks, r => r.Track.Id == gone.Id);
    }

    // Held to the bar the server's own scan holds a file to, no higher: what
    // its importer cannot read does not stay in the folder.
    [Fact]
    public async Task A_file_the_importer_cannot_read_is_refused_and_taken_back_out()
    {
        var noise = new byte[5000];
        Random.Shared.NextBytes(noise);

        var result = await UploadAsync("noise.flac", noise);

        Assert.Equal(IngestOutcome.Invalid, result.Outcome);
        Assert.False(File.Exists(Path.Combine(_music, "noise.flac")));
        Assert.Empty(_library.Tracks);
    }

    // The one place a path arrives from the wire.
    [Theory]
    [InlineData("../outside.wav")]
    [InlineData("album/../../outside.wav")]
    [InlineData("/etc/passwd.wav")]
    [InlineData("album//song.wav")]
    [InlineData("./song.wav")]
    [InlineData(".hidden/song.wav")]
    [InlineData("album/.Trash-1000/song.wav")]
    [InlineData("album/song.exe")]
    [InlineData("album/song")]
    [InlineData("")]
    public async Task A_path_that_leaves_the_folder_hides_in_it_or_is_not_music_is_refused(string relativePath)
    {
        var result = await _ingest.BeginAsync(
            new LibraryUploadRequestDto(relativePath, 10, new string('a', 64), DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.Invalid, result.Outcome);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_music));
        Assert.False(Directory.Exists(Path.Combine(_root, "staging")));
    }

    [Theory]
    [InlineData("R.E.M./Out of Time/02 Losing My Religion.flac")]
    [InlineData("Artist/Album (Deluxe) [2019]/1-01 Song, Pt. 2.m4a")]
    [InlineData("just a file.mp3")]
    [InlineData("album/.38 Special.mp3")]
    public void An_ordinary_music_path_resolves_inside_the_folder(string relativePath)
    {
        var refusal = LibraryIngest.ResolveTarget(relativePath, [_music], out var target);

        Assert.Null(refusal);
        Assert.StartsWith(Path.GetFullPath(_music) + Path.DirectorySeparatorChar, target);
        Assert.EndsWith(Path.GetFileName(relativePath), target);
    }

    [Fact]
    public async Task A_server_with_no_music_folder_says_so_rather_than_inventing_one()
    {
        var ingest = new LibraryIngest(
            _library, () => [Path.Combine(_root, "not-there")], Path.Combine(_root, "staging"),
            new Flower.Importer.Importer(NullLogger<Flower.Importer.Importer>.Instance), NullLogger.Instance);

        var result = await ingest.BeginAsync(RequestFor("a.wav", SongBytes("A")), TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.Unavailable, result.Outcome);
    }

    // A server that restarted in the middle forgot the upload, not the bytes.
    [Fact]
    public async Task A_piece_for_an_upload_this_server_does_not_know_is_told_to_begin_again()
    {
        var result = await _ingest.AppendAsync(new string('a', 64) + "-10", 0, new byte[10], TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.UnknownUpload, result.Outcome);
    }
}
