using System;
using System.Net;

namespace Flower.Services;

// Which http:// origins this app is willing to dial.
//
// Android's answer to that question is a network security config, and it can
// only name hostnames and DNS suffixes - there is no CIDR form. The addresses
// that need permitting are IP literals a peer handed over by mDNS moments
// ago, so there is nothing to enumerate in a file, and the manifest ends up
// permitting cleartext outright (Flower.Android/Resources/xml/
// network_security_config.xml). This is the rule that manifest cannot state,
// written where it can be: an unencrypted origin is acceptable when the host
// it names is on a network the device is already on, and not otherwise.
//
// Deliberately not Android-only. The reason to refuse cleartext to a routable
// address is the same on a laptop as on a phone - a hand-typed
// "http://music.example.com" is a library, a pairing signature and a whole
// listening history crossing the open internet in the clear - and Flower
// reaches the same servers from five heads. See LanGuard, which is this
// predicate from the server's side.
public static class CleartextOrigins
{
    // Tailnet addresses (100.64.0.0/10) count as private here, matching
    // LanGuard's default. Cleartext inside a tailnet is not cleartext on the
    // wire: WireGuard has already encrypted it, and REMOTE-TRANSPORT-PLAN.md's
    // whole argument for Tailscale is that it carries exactly this traffic.
    // The range is shared with carrier-grade NAT, which is a real caveat there
    // and a smaller one here: reaching a Flower server still needs a pairing
    // signature it has no way to forge.
    public static bool IsAllowed(Uri origin, IPAddress? resolved = null)
    {
        // https, and anything that is not http at all, is not this function's
        // business - it says nothing about whether the certificate is any
        // good, which is PeerHttpClient's question.
        if (!string.Equals(origin.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return true;

        var host = origin.Host.Trim('[', ']');
        var address = IPAddress.TryParse(host, out var literal) ? literal : resolved;

        // A name nobody resolved is refused rather than allowed. The callers
        // that have a name to dial resolve it first (they need the address to
        // rank candidates anyway), so arriving here with null means the
        // lookup failed or was never attempted - neither of which is evidence
        // that the host is on this link.
        return address is not null && LanGuard.IsPrivateOrLoopback(address);
    }
}
