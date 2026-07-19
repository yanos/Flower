using Flower.Services;

namespace Flower.Tests;

public class SyncRolePolicyTests
{
    [Fact]
    public void ShouldInitiateSync_is_false_for_a_server_even_with_a_paired_fingerprint()
    {
        Assert.False(SyncRolePolicy.ShouldInitiateSync(isServer: true, pairedServerFingerprint: "abc", peerFingerprint: "abc"));
    }

    [Fact]
    public void ShouldInitiateSync_is_true_for_a_client_whose_peer_is_its_paired_server()
    {
        Assert.True(SyncRolePolicy.ShouldInitiateSync(isServer: false, pairedServerFingerprint: "abc", peerFingerprint: "abc"));
    }

    [Fact]
    public void ShouldInitiateSync_is_false_for_a_client_whose_peer_is_not_its_paired_server()
    {
        Assert.False(SyncRolePolicy.ShouldInitiateSync(isServer: false, pairedServerFingerprint: "abc", peerFingerprint: "xyz"));
    }

    [Fact]
    public void ShouldInitiateSync_is_false_for_a_client_with_no_paired_server_yet()
    {
        Assert.False(SyncRolePolicy.ShouldInitiateSync(isServer: false, pairedServerFingerprint: null, peerFingerprint: "abc"));
    }

    [Fact]
    public void ShouldInitiateSync_is_false_when_the_peer_fingerprint_is_empty()
    {
        Assert.False(SyncRolePolicy.ShouldInitiateSync(isServer: false, pairedServerFingerprint: "abc", peerFingerprint: ""));
    }

    [Fact]
    public void ShouldRejectPeerAsServer_rejects_a_server_to_server_call()
    {
        Assert.True(SyncRolePolicy.ShouldRejectPeerAsServer(weAreServer: true, callerIsServer: true));
    }

    [Fact]
    public void ShouldRejectPeerAsServer_allows_a_client_calling_a_server()
    {
        Assert.False(SyncRolePolicy.ShouldRejectPeerAsServer(weAreServer: true, callerIsServer: false));
    }

    [Fact]
    public void ShouldRejectPeerAsServer_allows_a_server_calling_a_client()
    {
        Assert.False(SyncRolePolicy.ShouldRejectPeerAsServer(weAreServer: false, callerIsServer: true));
    }

    [Fact]
    public void ShouldRejectPeerAsServer_allows_a_client_calling_a_client()
    {
        Assert.False(SyncRolePolicy.ShouldRejectPeerAsServer(weAreServer: false, callerIsServer: false));
    }
}
