using System;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Flower.Services;

using Xunit;

namespace Flower.Tests;

// The certificate half of remote access: that a server's TLS certificate
// carries the very key a paired client already holds, and that the client
// accepts it for that reason and for no other. See DeviceCertificate and
// PeerHttpClient.
public class DeviceCertificateTests
{
    private static ECDsa NewDeviceKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Fact]
    public void A_certificate_carries_the_device_key_it_was_made_from()
    {
        using var key = NewDeviceKey();
        using var certificate = DeviceCertificate.CreateSelfSigned(key, "basement", ["basement"], []);

        // The whole design rests on these two being the same bytes: one is what
        // pairing wrote into trusted-peers.json, the other is what a TLS
        // handshake presents.
        Assert.Equal(DeviceCertificate.PublicKeyRaw(key), DeviceCertificate.PublicKeyRawOf(certificate));
    }

    [Fact]
    public void Two_devices_do_not_produce_matching_certificates()
    {
        using var mine = NewDeviceKey();
        using var theirs = NewDeviceKey();
        using var certificate = DeviceCertificate.CreateSelfSigned(theirs, "elsewhere", ["elsewhere"], []);

        Assert.NotEqual(DeviceCertificate.PublicKeyRaw(mine), DeviceCertificate.PublicKeyRawOf(certificate));
    }

    [Fact]
    public void A_certificate_covers_the_names_and_addresses_it_was_given()
    {
        using var key = NewDeviceKey();
        using var certificate = DeviceCertificate.CreateSelfSigned(
            key, "basement", ["basement", "localhost"], [IPAddress.Loopback, IPAddress.Parse("192.168.1.40")]);

        // Not the security boundary - a pinning client never reads these - but
        // the difference between a warning and a hard failure for anything that
        // validates the ordinary way. See CreateSelfSigned's remarks.
        var subjectAlternativeNames = certificate.Extensions
            .OfType<X509SubjectAlternativeNameExtension>()
            .Single();

        Assert.Contains("localhost", subjectAlternativeNames.EnumerateDnsNames());
        Assert.Contains(IPAddress.Parse("192.168.1.40"), subjectAlternativeNames.EnumerateIPAddresses());
    }

    [Fact]
    public void A_certificate_that_is_not_one_of_ours_has_no_device_key_to_read()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName("CN=somebody-else"), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        // Null rather than a throw: the caller's next move is ordinary chain
        // validation, which is a perfectly good outcome for a real certificate.
        Assert.Null(DeviceCertificate.PublicKeyRawOf(certificate));
    }
}

// The rule PeerHttpClient applies to every certificate a Flower server
// presents. Exercised directly rather than through a handshake, which
// PinnedTlsHandshakeTests in Flower.Server.Tests does end to end.
public class PinnedCertificateAcceptanceTests : IDisposable
{
    private readonly Func<byte[], bool>? _original = PeerHttpClient.IsPinnedServerKey;

    public void Dispose() => PeerHttpClient.IsPinnedServerKey = _original;

    private static X509Certificate2 SelfSignedFor(ECDsa key) =>
        DeviceCertificate.CreateSelfSigned(key, "basement", ["basement"], []);

    [Fact]
    public void A_certificate_that_validates_normally_is_accepted_without_any_pin()
    {
        // No predicate set at all - a real certificate from a real authority
        // must not depend on this device having paired with anything.
        PeerHttpClient.IsPinnedServerKey = null;

        Assert.True(PeerHttpClient.IsAcceptable(certificate: null, SslPolicyErrors.None));
    }

    [Fact]
    public void A_self_signed_certificate_is_accepted_when_its_key_is_one_we_paired_with()
    {
        using var serverKey = NewKey();
        using var certificate = SelfSignedFor(serverKey);
        var paired = DeviceCertificate.PublicKeyRaw(serverKey);

        PeerHttpClient.IsPinnedServerKey = key => key.SequenceEqual(paired);

        Assert.True(PeerHttpClient.IsAcceptable(certificate, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void A_name_mismatch_is_forgiven_by_the_same_pin()
    {
        // Expected rather than exceptional: a certificate minted for a machine
        // name is dialled at a bare IP as often as not.
        using var serverKey = NewKey();
        using var certificate = SelfSignedFor(serverKey);
        var paired = DeviceCertificate.PublicKeyRaw(serverKey);

        PeerHttpClient.IsPinnedServerKey = key => key.SequenceEqual(paired);

        Assert.True(PeerHttpClient.IsAcceptable(
            certificate, SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void A_self_signed_certificate_from_a_stranger_is_refused()
    {
        using var serverKey = NewKey();
        using var attackerKey = NewKey();
        using var certificate = SelfSignedFor(attackerKey);
        var paired = DeviceCertificate.PublicKeyRaw(serverKey);

        PeerHttpClient.IsPinnedServerKey = key => key.SequenceEqual(paired);

        // The point of the whole exercise: this is not "trust anything
        // self-signed". Without the pin there is no argument for skipping the
        // warning a browser would show.
        Assert.False(PeerHttpClient.IsAcceptable(certificate, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void With_nothing_paired_yet_an_untrusted_certificate_is_refused()
    {
        using var serverKey = NewKey();
        using var certificate = SelfSignedFor(serverKey);

        PeerHttpClient.IsPinnedServerKey = null;

        // A null predicate cannot vouch for anything, and the fail-closed
        // direction is the only safe reading of "we do not know yet".
        Assert.False(PeerHttpClient.IsAcceptable(certificate, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void A_handshake_that_presented_no_certificate_at_all_is_refused()
    {
        PeerHttpClient.IsPinnedServerKey = _ => true;

        Assert.False(PeerHttpClient.IsAcceptable(certificate: null, SslPolicyErrors.RemoteCertificateNotAvailable));
    }

    private static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);
}
