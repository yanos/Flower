using System.Collections.Generic;
using System.Threading.Tasks;

using Flower.Services;

namespace Flower.Tests.TestSupport;

// An IPeerCredentials that always attaches the same one header.
//
// Stands in for the browser head in tests that are about a call *shape* rather
// than about a signature - which route it goes to, whether the credential
// reaches the wire at all. The real browser credential signs through WebCrypto
// (BrowserPeerCredentials), which needs a browser; what those tests were
// actually pinning is that whatever IPeerCredentials returns arrives, and that
// is what this makes checkable without one.
public sealed class StaticPeerCredentials(string headerName, string value) : IPeerCredentials
{
    public Task<IReadOnlyList<(string Key, string Value)>> AuthorizeAsync(
        string method, string absolutePath, IEnumerable<(string Key, string Value)> query, byte[] body) =>
        Task.FromResult<IReadOnlyList<(string Key, string Value)>>([(headerName, value)]);
}
