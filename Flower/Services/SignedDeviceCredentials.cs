using System.Collections.Generic;
using System.Threading.Tasks;

using Flower.Persistence;

namespace Flower.Services;

// Native signing stays in-process; browser signing stays behind WebCrypto.
// Both return SignedIdentity's raw values for the header or query transport.
public sealed class SignedDeviceCredentials(
    DeviceIdentity identity, DeviceSigningKey signingKey) : IPeerCredentials
{
    // Already-completed, always: this device holds its own key in-process, so
    // there is nothing to await. The task in the interface is there for the
    // browser, whose key lives behind crypto.subtle - see IPeerCredentials.
    public Task<IReadOnlyList<(string Key, string Value)>> AuthorizeAsync(
        string method, string absolutePath, IEnumerable<(string Key, string Value)> query, byte[] body)
    {
        // Identity headers are excluded from canonicalization in both transports.
        var signed = signingKey.Sign(method, absolutePath, query, body);
        return Task.FromResult(SignedIdentity.Create(
            identity.Fingerprint, identity.Alias, signingKey.PublicKeyBase64, signed));
    }
}
