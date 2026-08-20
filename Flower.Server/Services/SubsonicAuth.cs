using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Http;

using Flower.Persistence;
using Flower.Services;

namespace Flower.Server.Services;

// Path B of SYNC-PLAN.md's "Passwordless by design": third-party Subsonic
// clients (DSub, substreamer, Symfonium) implement a published protocol and
// will send u/t/s or an API key, so they cannot join path A's keypair scheme.
// What they get instead is a per-client credential this server generated,
// scoped to it, individually revocable, and never chosen by a human - see
// SubsonicCredentialStore.
//
// This used to validate against a single configured admin username/password
// shared by every client, which meant one leaked phone was the whole library
// and revoking it meant re-pairing everything. That option is gone from
// FlowerServerOptions entirely rather than kept as a fallback.
//
// Two schemes accepted, both resolving to the same credential record:
//
// - Classic: t=md5(password+salt), which every real Subsonic client sends.
// - OpenSubsonic's apiKey extension: apiKey=<password>, no salt round trip.
//   Same secret either way, so adding it costs nothing and clients that
//   support it get a cleaner request.
public static class SubsonicAuth
{
    // Returns the authenticated username, or null. Callers use it to stamp
    // last-seen, which is what makes the admin's client list say which
    // credentials are actually in use.
    public static string? Validate(IQueryCollection query, SubsonicCredentialStore credentials)
    {
        var username = query["u"].ToString();
        if (string.IsNullOrEmpty(username))
            return null;

        var credential = credentials.Find(username);
        if (credential == null)
            return null;

        // apiKey is checked first only because it is the cheaper comparison;
        // a client sends one or the other, never both.
        var apiKey = query["apiKey"].ToString();
        if (!string.IsNullOrEmpty(apiKey))
        {
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(apiKey),
                Encoding.UTF8.GetBytes(credential.Password))
                ? credential.Username
                : null;
        }

        var token = query["t"].ToString();
        var salt = query["s"].ToString();
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(salt))
            return null;

        var expected = OpenSubsonicClient.ComputeToken(credential.Password, salt);
        // md5-of-a-known-salt is not a secret worth constant-time comparison in
        // the way the raw password is, but the cost is nil and it keeps the two
        // branches here from having visibly different timing shapes.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(expected.ToLowerInvariant()))
            ? credential.Username
            : null;
    }
}
