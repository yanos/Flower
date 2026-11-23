using System;

using Flower.Persistence.Sql;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.Persistence;

[Collection("PlatformDataDirectory")]
public class PlaylistDeletionRepositoryTests : IsolatedHome
{
    private static PlaylistDeletionRepository Log() => new(FlowerDb.OpenDefault());

    [Fact]
    public void A_deletion_is_remembered_for_its_listener_alone()
    {
        var id = Guid.NewGuid();

        Log().Record("guest", [id]);

        Assert.Contains(id, Log().DeletedFor("guest"));
        Assert.Empty(Log().DeletedFor("owner"));
    }

    [Fact]
    public void A_playlist_that_exists_again_is_forgotten()
    {
        var id = Guid.NewGuid();
        Log().Record("owner", [id]);

        Log().Forget("owner", [id]);

        Assert.Empty(Log().DeletedFor("owner"));
    }
}
