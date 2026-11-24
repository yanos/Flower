using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Flower.Models;
using Flower.Persistence.Sql;
using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.Persistence;

// What a device last agreed with a server about each playlist: the revision,
// and that version's whole content, which is what "did I change this?" and a
// merge are worked out from (docs/PLAYLIST-SYNC-PLAN.md, "The agreed copy").
[Collection("PlatformDataDirectory")]
public class PlaylistAgreementRepositoryTests : PinnedDataDirectory
{
    private string DbPath => Path.Combine(DataDirectory, "flower.db");

    private PlaylistAgreementRepository Open() => new(new FlowerDb(DbPath));

    private static readonly SmartPlaylistRules Rules = new(
        MatchMode.All,
        [new SmartCondition(SmartField.Genre, SmartOperator.Contains, new SmartValue.Text("Jazz"))],
        new SmartLimit(25, LimitUnit.Items, LimitSelector.MostRecentlyAdded));

    [Fact]
    public void An_agreed_copy_comes_back_whole_after_reopening()
    {
        var id = Guid.NewGuid();
        var folder = Guid.NewGuid();
        var rows = new List<PlaylistSyncTrackDto>
        {
            new("Here", "Artist", "Album", 200, "song-1", Guid.NewGuid()),
            new("Only Elsewhere", "Someone", "Somewhere", 123, null, Guid.NewGuid()),
        };
        var copy = new PlaylistSyncPlaylistDto(id, "Mix", new DateTimeOffset(2026, 11, 1, 9, 30, 0, TimeSpan.Zero), rows,
            ParentId: folder, SortKey: 2.5);

        Open().Save("server-fp", new Dictionary<Guid, PlaylistAgreement?> { [id] = new(1_763_000_000_123, copy) });
        var read = Open().Load("server-fp")[id];

        Assert.Equal(1_763_000_000_123, read.Revision);
        Assert.Equal("Mix", read.Copy!.Name);
        Assert.Equal(copy.UpdatedAt, read.Copy.UpdatedAt);
        Assert.Equal((folder, 2.5), (read.Copy.ParentId!.Value, read.Copy.SortKey!.Value));
        Assert.Equal(rows, read.Copy.Tracks);
    }

    [Fact]
    public void A_smart_playlists_rules_survive_the_agreed_copy()
    {
        var id = Guid.NewGuid();

        Open().Save("server-fp", new Dictionary<Guid, PlaylistAgreement?>
        {
            [id] = new(7, new PlaylistSyncPlaylistDto(id, "Recent Jazz", DateTimeOffset.UtcNow, [], Rules)),
        });

        Assert.True(SmartPlaylistRules.Equivalent(Rules, Open().Load("server-fp")[id].Copy!.Rules));
    }

    // The answer to "Deleted on your server. Keep it?" is an edit over the
    // deletion, so the device has to be able to agree that a playlist is gone.
    [Fact]
    public void An_agreement_that_a_playlist_was_deleted_is_kept_as_such()
    {
        var id = Guid.NewGuid();

        Open().Save("server-fp", new Dictionary<Guid, PlaylistAgreement?> { [id] = new(42, null) });

        var read = Open().Load("server-fp")[id];
        Assert.Equal(42, read.Revision);
        Assert.Null(read.Copy);
    }

    [Fact]
    public void Agreements_with_one_server_are_not_another_servers()
    {
        var id = Guid.NewGuid();

        Open().Save("home", new Dictionary<Guid, PlaylistAgreement?>
        {
            [id] = new(5, new PlaylistSyncPlaylistDto(id, "Mix", DateTimeOffset.UtcNow, [])),
        });

        Assert.Empty(Open().Load("cabin"));
        Assert.Single(Open().Load("home"));
    }

    // A save names only what changed: a removal takes that one agreement, and
    // the rest are left as they were.
    [Fact]
    public void Saving_changes_only_the_agreements_it_names()
    {
        var (kept, replaced, removed) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        PlaylistSyncPlaylistDto Copy(Guid id, string name) => new(id, name, DateTimeOffset.UtcNow, []);
        Open().Save("server-fp", new Dictionary<Guid, PlaylistAgreement?>
        {
            [kept] = new(1, Copy(kept, "Kept")),
            [replaced] = new(1, Copy(replaced, "Before")),
            [removed] = new(1, Copy(removed, "Removed")),
        });

        Open().Save("server-fp", new Dictionary<Guid, PlaylistAgreement?>
        {
            [replaced] = new(2, Copy(replaced, "After")),
            [removed] = null,
        });

        var read = Open().Load("server-fp");
        Assert.Equal(new[] { kept, replaced }.Order(), read.Keys.Order());
        Assert.Equal(("Kept", 1L), (read[kept].Copy!.Name, read[kept].Revision));
        Assert.Equal(("After", 2L), (read[replaced].Copy!.Name, read[replaced].Revision));
    }
}
