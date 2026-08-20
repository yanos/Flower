using System;
using System.Collections.Generic;

namespace Flower.Services;

// The contents of the pairing QR code, and of the link a user can paste
// instead of scanning one (SYNC-PLAN.md, "Passwordless by design" path A).
//
// Shared rather than built ad-hoc on each side, for the usual reason a wire
// format is: Flower.Server writes these and every Flower head reads them, so
// a change to the shape has to be a change to one function or the two sides
// stop agreeing. There is no versioning here - see CLAUDE.md, this protocol
// has no third-party implementers to break.
//
// The interesting field is Fingerprint. A bare code proves to the *server*
// that the human at the admin screen authorized this device; it proves nothing
// to the *device* about which server it just handed its public key to. Pinning
// the server's fingerprint at pair time is what makes the QR a mutual
// bootstrap: the device knows, out of band, the key it should expect, so a
// machine-in-the-middle on a plain-HTTP LAN cannot pose as the server without
// also producing that key. A client that ignores this field is back to
// trust-on-first-use, which is exactly what the field exists to avoid.
public sealed record PairingInvite(string Host, string Code, string Fingerprint)
{
    public const string Scheme = "flower";
    public const string PairHost = "pair";

    public Uri ToUri() => new(ToString());

    // Assembled by hand rather than with UriBuilder, which insists on a path
    // and renders this as flower://pair/?... - harmless to parse, but it is
    // the string a user sees under a QR code and reads back over the phone,
    // so the stray slash is worth not having.
    public override string ToString() =>
        $"{Scheme}://{PairHost}?host={Uri.EscapeDataString(Host)}"
        + $"&code={Uri.EscapeDataString(Code)}"
        + $"&fp={Uri.EscapeDataString(Fingerprint)}";

    // Null rather than throwing on anything malformed: this parses text a user
    // pasted or a camera decoded, where "that isn't a pairing link" is an
    // ordinary outcome and not an exceptional one.
    public static PairingInvite? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri))
            return null;
        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
            return null;
        if (!string.Equals(uri.Host, PairHost, StringComparison.OrdinalIgnoreCase))
            return null;

        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("host", out var host) || string.IsNullOrEmpty(host))
            return null;
        if (!query.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
            return null;
        // Deliberately required, not optional-with-a-fallback: an invite
        // without a server fingerprint can only be completed by trusting
        // whatever answers at that address, and silently degrading to that is
        // the failure this field was added to prevent.
        if (!query.TryGetValue("fp", out var fingerprint) || string.IsNullOrEmpty(fingerprint))
            return null;

        return new PairingInvite(host, code, fingerprint);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
                continue;
            result[Uri.UnescapeDataString(pair[..separator])] = Uri.UnescapeDataString(pair[(separator + 1)..]);
        }
        return result;
    }
}
