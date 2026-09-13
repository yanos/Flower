using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;

using Flower.Services;

using Xunit;

namespace Flower.Tests;

// A peer that has gone away is the normal steady state of /info polling, not an
// error - see NetworkDiscoveryService.IsRoutineUnreachable. These pin which
// failures are which, because the cost of getting it wrong is invisible in a
// passing build: either a stack trace per poll in the Log window, or a genuine
// bug logged as a one-liner with nothing to debug from.
public class RoutineUnreachableTests
{
    [Fact]
    public void A_refused_connection_is_routine()
    {
        var ex = new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused));
        Assert.True(NetworkDiscoveryService.IsRoutineUnreachable(ex));
    }

    [Fact]
    public void An_unresolvable_host_is_routine()
    {
        var ex = new HttpRequestException("no such host", new SocketException((int)SocketError.HostNotFound));
        Assert.True(NetworkDiscoveryService.IsRoutineUnreachable(ex));
    }

    // HttpClient's own timeout arrives as a cancellation, which is why this is
    // listed at all.
    [Fact]
    public void A_timed_out_request_is_routine()
    {
        Assert.True(NetworkDiscoveryService.IsRoutineUnreachable(new TaskCanceledException()));
    }

    // EnsureSuccessStatusCode's throw: the peer answered and said no.
    [Fact]
    public void A_refusal_with_a_status_code_is_routine()
    {
        var ex = new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden);
        Assert.True(NetworkDiscoveryService.IsRoutineUnreachable(ex));
    }

    [Fact]
    public void A_bare_socket_failure_is_routine()
    {
        Assert.True(NetworkDiscoveryService.IsRoutineUnreachable(new SocketException((int)SocketError.NetworkUnreachable)));
    }

    // A server that has just restarted presents a certificate this side has not
    // pinned yet, so the poll that catches it mid-start is refused by our own
    // validation callback and the next one succeeds. Same steady state as a
    // refused connection, and the SslStream frames say nothing about which
    // certificate or why.
    [Fact]
    public void A_certificate_this_side_refused_is_routine()
    {
        var ex = new HttpRequestException(
            HttpRequestError.SecureConnectionError,
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate was rejected by the provided RemoteCertificateValidationCallback."));

        Assert.True(NetworkDiscoveryService.IsRoutineUnreachable(ex));
    }

    // What the one-line form has to carry once the stack trace is gone: the
    // wrapper message alone is "see inner exception", which is no information
    // at all.
    [Fact]
    public void The_one_line_description_names_the_real_failure()
    {
        var ex = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate was rejected."));

        Assert.Equal(
            "The SSL connection could not be established, see inner exception. -> The remote certificate was rejected.",
            NetworkDiscoveryService.Describe(ex));
    }

    [Fact]
    public void The_one_line_description_of_a_lone_exception_is_just_its_message()
    {
        Assert.Equal("boom", NetworkDiscoveryService.Describe(new InvalidOperationException("boom")));
    }

    // The case the whole distinction exists to protect: anything that isn't a
    // reachability problem still gets its stack trace.
    [Fact]
    public void A_bug_is_not_routine()
    {
        Assert.False(NetworkDiscoveryService.IsRoutineUnreachable(new InvalidOperationException("boom")));
        Assert.False(NetworkDiscoveryService.IsRoutineUnreachable(new HttpRequestException("tls handshake failed")));
    }
}
