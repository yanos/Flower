using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;

using Xunit;

namespace Flower.Tests.MusicLibrary;

// A rescan that finds every song as it was. Its change token is what every
// paired device polls: moving it for nothing sent each of them the whole
// catalog after every server rescan (CODE-REVIEW October C1.2).
public class LibraryNoOpRescanTests
{
    private sealed class RecordingTrackStore : ITrackStore
    {
        public int ReplaceAllCount { get; private set; }

        public void UpdateStats(Track track) { }
        public void SetStarred(StarTarget target, string value, bool starred) { }
        public void Upsert(Track track) { }
        public void UpsertMany(IReadOnlyCollection<Track> tracks) { }
        public void ReplaceAll(IEnumerable<Track> tracks) => ReplaceAllCount++;
        public void Delete(IReadOnlyCollection<Guid> ids) { }
    }

    // What the importer hands over for the files on disk: brand-new instances,
    // tags read afresh, nothing the library remembers about them.
    private static List<Track> Scan(params (string Title, string Path)[] files) =>
        files.Select(f => new Track
        {
            Title = f.Title, Artists = "Ichiko Aoba", Album = "Luminescent Creatures",
            Duration = TimeSpan.FromSeconds(200), Path = f.Path, ArtHash = "cover",
        }).ToList();

    private static (Library Library, RecordingTrackStore Store) Scanned(List<Track> tracks)
    {
        var store = new RecordingTrackStore();
        var library = new Library([], NullLogger<Library>.Instance, store);
        library.UpdateTracks(tracks);
        return (library, store);
    }

    [Fact]
    public void A_rescan_that_finds_every_song_as_it_was_keeps_the_token_and_writes_nothing()
    {
        var (library, store) = Scanned(Scan(("Pirsomnia", "/music/a.flac"), ("Luciférine", "/music/b.flac")));
        // What only the library knows, which a rescan carries forward.
        library.IncrementPlayCount(library.Tracks[0]);
        library.SetStarred(StarTarget.Song, library.Tracks[1].Id.ToString(), starred: true);
        var token = library.ChangeToken;
        var writes = store.ReplaceAllCount;

        // The same files, listed in another order.
        library.UpdateTracks(Scan(("Luciférine", "/music/b.flac"), ("Pirsomnia", "/music/a.flac")));

        Assert.Equal(token, library.ChangeToken);
        Assert.Equal(writes, store.ReplaceAllCount);
    }

    // The play queue was handed the scan's instances too, so they are the ones
    // the library keeps, and what is derived from the list is built over them.
    [Fact]
    public void A_rescan_that_changed_nothing_still_installs_the_instances_it_found()
    {
        var (library, _) = Scanned(Scan(("Pirsomnia", "/music/a.flac")));
        var rescanned = Scan(("Pirsomnia", "/music/a.flac"));

        library.UpdateTracks(rescanned);
        library.IncrementPlayCount(rescanned[0]);

        Assert.Same(rescanned[0], Assert.Single(library.Tracks));
        var listed = library.Snapshot.Albums.SelectMany(a => a.Tracks).Single();
        Assert.Same(rescanned[0], listed);
        Assert.Equal(1, listed.PlayCount);
    }

    [Fact]
    public void A_rescan_that_read_a_new_tag_moves_the_token_and_writes_the_rows()
    {
        var (library, store) = Scanned(Scan(("Pirsomnia", "/music/a.flac")));
        var token = library.ChangeToken;
        var writes = store.ReplaceAllCount;

        var retagged = Scan(("Pirsomnia", "/music/a.flac"));
        retagged[0].Genre = "Folk";
        library.UpdateTracks(retagged);

        Assert.NotEqual(token, library.ChangeToken);
        Assert.Equal(writes + 1, store.ReplaceAllCount);
    }

    [Fact]
    public void A_rescan_that_no_longer_finds_a_song_moves_the_token()
    {
        var (library, _) = Scanned(Scan(("Pirsomnia", "/music/a.flac"), ("Luciférine", "/music/b.flac")));
        var token = library.ChangeToken;

        library.UpdateTracks(Scan(("Pirsomnia", "/music/a.flac")));

        Assert.NotEqual(token, library.ChangeToken);
    }

    [Fact]
    public void A_rescan_that_finds_a_song_somewhere_new_moves_the_token()
    {
        var (library, _) = Scanned(Scan(("Pirsomnia", "/music/a.flac")));
        var token = library.ChangeToken;

        library.UpdateTracks(Scan(("Pirsomnia", "/music/moved/a.flac")));

        Assert.NotEqual(token, library.ChangeToken);
    }
}
