using System.Net;

namespace Flower.Server.Tests;

// The LAN guard drops a caller it does not recognise rather than answering it
// 403 (see Program.cs: a refusal is still a reply, and a reply tells a stranger
// there is something here). TestServer surfaces that as the
// OperationCanceledException an aborted request raises; over a real socket it
// is a connection closed with nothing written to it.
//
// Declared once so the several suites that probe the gate all say "got nothing
// back" the same way, and so that a regression which turns the drop back into
// an answer fails with the status it answered rather than with a bare
// exception.
internal static class GuardedRequest
{
    public static async Task AssertDropped(Func<Task<HttpStatusCode>> send)
    {
        try
        {
            var status = await send();
            Assert.Fail($"Expected the request to be dropped, but it was answered {(int)status} {status}.");
        }
        catch (OperationCanceledException)
        {
        }
    }
}
