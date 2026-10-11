using Flower.Services;

namespace Flower.Tests.Sync;

public class SyncRolePolicyTests
{
    [Fact]
    public void MayRequestFrom_allows_the_one_paired_server()
    {
        Assert.True(SyncRolePolicy.MayRequestFrom(pairedServerFingerprint: "abc", peerFingerprint: "abc"));
    }

    [Fact]
    public void MayRequestFrom_refuses_any_other_peer()
    {
        Assert.False(SyncRolePolicy.MayRequestFrom(pairedServerFingerprint: "abc", peerFingerprint: "xyz"));
    }

    [Fact]
    public void MayRequestFrom_refuses_everything_while_unpaired()
    {
        Assert.False(SyncRolePolicy.MayRequestFrom(pairedServerFingerprint: null, peerFingerprint: "abc"));
    }

    // An unresolved peer has an empty fingerprint, which must not be allowed to
    // match an equally-empty paired pointer into a free pass.
    [Fact]
    public void MayRequestFrom_refuses_an_unresolved_peer()
    {
        Assert.False(SyncRolePolicy.MayRequestFrom(pairedServerFingerprint: "abc", peerFingerprint: ""));
        Assert.False(SyncRolePolicy.MayRequestFrom(pairedServerFingerprint: "", peerFingerprint: ""));
    }
    // Music.app history: a device of its own, or a server's administrator,
    // whose imports go up to the server. Never a guest.
    [Theory]
    [InlineData(null, false, true)]
    [InlineData("abc", true, true)]
    [InlineData("abc", false, false)]
    public void MayImportFromMusicApp_is_a_device_of_its_own_or_an_administrators(string? paired, bool admin, bool expected) =>
        Assert.Equal(expected, SyncRolePolicy.MayImportFromMusicApp(paired, admin));

    // Play counts, not when the server counts the same Music.app's plays
    // itself - and a server's flag means nothing to a device with no server.
    [Theory]
    [InlineData(null, false, false, true)]
    [InlineData(null, false, true, true)]
    [InlineData("abc", true, false, true)]
    [InlineData("abc", true, true, false)]
    [InlineData("abc", false, false, false)]
    public void MayImportPlayCountsFromMusicApp_also_leaves_a_server_that_imports_its_own(
        string? paired, bool admin, bool serverImports, bool expected) =>
        Assert.Equal(expected, SyncRolePolicy.MayImportPlayCountsFromMusicApp(paired, admin, serverImports));
}
