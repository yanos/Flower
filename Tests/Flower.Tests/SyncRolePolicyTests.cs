using Flower.Services;

namespace Flower.Tests;

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
}
