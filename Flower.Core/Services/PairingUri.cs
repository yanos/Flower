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

// What the user actually put in the pairing box, which is one of two things:
// a bare code, read out over the phone and typed by hand, or a whole
// flower:// invite, scanned off a QR or pasted from the server's console.
//
// Both are accepted at the same entry point because the user should not have
// to tell the app which one they have - and because the difference is not
// cosmetic. A bare code authorizes the *server* to accept this device, and
// nothing more; an invite additionally tells the *device* which server it is
// allowed to end up trusting. The second is strictly stronger, and this type
// exists so the stronger case cannot be silently downgraded to the weaker one
// by a caller that forgot to look.
public sealed record PairingEntry(string Code, string? ExpectedFingerprint)
{
    // Deliberately total: anything that is not a well-formed invite is taken
    // as a bare code rather than refused. A mistyped link would otherwise be
    // reported as "that code is wrong" by the server a round trip later,
    // which is a worse message but the same outcome - whereas refusing here
    // would mean a code that happens to contain a colon could not be typed.
    public static PairingEntry Parse(string? text)
    {
        var invite = PairingInvite.TryParse(text);
        return invite != null
            ? new PairingEntry(invite.Code, invite.Fingerprint)
            : new PairingEntry(text?.Trim() ?? "", ExpectedFingerprint: null);
    }

    // Whether the server that answered is the one the invite named, asked of
    // the public key that server served at /info.
    //
    // This is the whole point of the fingerprint field. The key arrives over
    // the same connection being authenticated - plain HTTP on a LAN, as often
    // as not - so on its own it proves nothing: a machine-in-the-middle serves
    // its own key and the device pins the attacker. The invite is the out-of-
    // band channel that breaks that circle, because it travelled by QR or by
    // voice rather than over the wire.
    //
    // Returns a showable sentence rather than a bool, for the reason
    // PeerPairingService.DescribeRejectionAsync does: the two ways this fails
    // want different next moves, and "pairing failed" tells the user neither.
    // The fingerprint a base64 public key derives to, or null if it is not a
    // key at all. Shared with callers that have a key and no invite, so that
    // "what does this key call itself?" is answered the same way in both
    // places rather than re-decoded by hand at each one.
    public static string? FingerprintOf(string publicKeyBase64)
    {
        try
        {
            return SignedRequestCanonicalizer.ComputeFingerprint(Convert.FromBase64String(publicKeyBase64));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public string? RejectionFor(string servedPublicKeyBase64)
    {
        if (ExpectedFingerprint == null)
            return null;

        // No key yet means /info has not been read, or was read and carried
        // none. Refused rather than waved through: an invite that names a
        // fingerprint has asked for a check, and skipping a check because the
        // thing to check is missing is how a security control becomes
        // decorative.
        if (string.IsNullOrEmpty(servedPublicKeyBase64))
            return "That server has not identified itself yet. Wait for it to appear as online, then try the code again.";

        if (FingerprintOf(servedPublicKeyBase64) is not { } actual)
            return "That server sent an identity key this app could not read. It may not be a Flower server.";

        if (string.Equals(actual, ExpectedFingerprint, StringComparison.OrdinalIgnoreCase))
            return null;

        // Worth saying plainly rather than softening. The ordinary cause is
        // dull - the invite was for a different server, or the server was
        // rebuilt and has a new key - but the interesting cause is not, and a
        // user who is told "something went wrong" will simply retry into it.
        return $"This is not the server that invite came from. It identifies itself as {actual}, "
             + $"but the invite named {ExpectedFingerprint}. Pairing was stopped; if you did not expect this, "
             + "do not retry until you know why.";
    }
}
