using System;
using System.Security.Cryptography;

using Flower.Services;

namespace Flower.Tests.TestSupport;

// A throwaway P-256 keypair wrapped in the same DeviceSigningKey the app
// uses, standing in for a peer device's identity. The uncompressed-point
// encoding here (0x04 || X || Y) is the exact shape DeviceKeyStore persists
// and SyncHttpServer.VerifySelfSigned validates, so a key built this way is
// indistinguishable from a real device's as far as the wire protocol is
// concerned.
internal static class TestSigningKey
{
    public static DeviceSigningKey Create()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = ecdsa.ExportParameters(false).Q;
        var raw = new byte[65];
        raw[0] = 0x04;
        Buffer.BlockCopy(q.X!, 0, raw, 1, 32);
        Buffer.BlockCopy(q.Y!, 0, raw, 33, 32);
        return new DeviceSigningKey(ecdsa, raw);
    }
}
