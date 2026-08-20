using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

using Flower.Persistence;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Tests;

// The security-critical services had no coverage at all before this project
// existed (ARCHITECTURE-REVIEW Tier 5.1) - these are the "a wrong answer here
// lets a stranger in" paths, so they get tested for what they reject as much
// as for what they accept.

// Path B of SYNC-PLAN.md's "Passwordless by design". These used to run against
// a single configured admin username/password; there is no such setting any
// more, so they run against a real SubsonicCredentialStore issuing real
// per-client credentials.
public class SubsonicAuthTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("flower-subsonic-auth-tests").FullName;
    private readonly string? _previousDataDirectory;
    private readonly SubsonicCredentialStore _store;
    private readonly SubsonicCredential _credential;

    public SubsonicAuthTests()
    {
        // SubsonicCredentialStore writes under AppDataDirectory - unpinned,
        // these tests would issue credentials into the real developer's own
        // Flower data folder.
        _previousDataDirectory = PlatformDataDirectory.Current;
        PlatformDataDirectory.Current = Path.Combine(_root, "appdata");

        _store = new SubsonicCredentialStore(NullLogger<SubsonicCredentialStore>.Instance);
        _credential = _store.IssueAsync("Test client").GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        PlatformDataDirectory.Current = _previousDataDirectory;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static IQueryCollection Query(params (string Key, string Value)[] pairs) =>
        new QueryCollection(pairs.ToDictionary(
            p => p.Key,
            p => new Microsoft.Extensions.Primitives.StringValues(p.Value)));

    [Fact]
    public void Accepts_a_correctly_salted_token()
    {
        var token = OpenSubsonicClient.ComputeToken(_credential.Password, "somesalt");

        Assert.Equal(_credential.Username, SubsonicAuth.Validate(
            Query(("u", _credential.Username), ("t", token), ("s", "somesalt")), _store));
    }

    [Fact]
    public void Accepts_a_token_in_either_case()
    {
        var token = OpenSubsonicClient.ComputeToken(_credential.Password, "somesalt");

        // Real Subsonic clients differ on hex casing - pin that both work.
        Assert.NotNull(SubsonicAuth.Validate(
            Query(("u", _credential.Username), ("t", token.ToUpperInvariant()), ("s", "somesalt")), _store));
    }

    [Fact]
    public void Accepts_the_OpenSubsonic_apiKey_form()
    {
        // The apiKey extension: same secret, no salt round trip, for clients
        // that support it.
        Assert.Equal(_credential.Username, SubsonicAuth.Validate(
            Query(("u", _credential.Username), ("apiKey", _credential.Password)), _store));
    }

    [Fact]
    public void Rejects_a_wrong_apiKey()
    {
        Assert.Null(SubsonicAuth.Validate(
            Query(("u", _credential.Username), ("apiKey", "not-the-key")), _store));
    }

    [Fact]
    public void Rejects_a_token_computed_with_a_different_salt()
    {
        var token = OpenSubsonicClient.ComputeToken(_credential.Password, "somesalt");

        // The salt is the whole point of the scheme: a token replayed with a
        // different salt must not validate.
        Assert.Null(SubsonicAuth.Validate(
            Query(("u", _credential.Username), ("t", token), ("s", "othersalt")), _store));
    }

    [Fact]
    public void Rejects_the_wrong_password()
    {
        var token = OpenSubsonicClient.ComputeToken("not-the-password", "somesalt");

        Assert.Null(SubsonicAuth.Validate(
            Query(("u", _credential.Username), ("t", token), ("s", "somesalt")), _store));
    }

    [Fact]
    public void Rejects_an_unknown_username()
    {
        var token = OpenSubsonicClient.ComputeToken(_credential.Password, "somesalt");

        Assert.Null(SubsonicAuth.Validate(
            Query(("u", "someone-else"), ("t", token), ("s", "somesalt")), _store));
    }

    [Fact]
    public async Task One_credential_does_not_authenticate_as_another()
    {
        // The point of per-client credentials over a shared password: each is
        // its own identity, so revoking one leaves the other working and
        // neither can impersonate the other.
        var other = await _store.IssueAsync("Second client");
        var token = OpenSubsonicClient.ComputeToken(_credential.Password, "somesalt");

        Assert.Null(SubsonicAuth.Validate(
            Query(("u", other.Username), ("t", token), ("s", "somesalt")), _store));
    }

    [Fact]
    public async Task A_revoked_credential_stops_authenticating()
    {
        var token = OpenSubsonicClient.ComputeToken(_credential.Password, "somesalt");
        Assert.NotNull(SubsonicAuth.Validate(
            Query(("u", _credential.Username), ("t", token), ("s", "somesalt")), _store));

        Assert.True(await _store.RevokeAsync(_credential.Username));

        Assert.Null(SubsonicAuth.Validate(
            Query(("u", _credential.Username), ("t", token), ("s", "somesalt")), _store));
    }

    [Theory]
    [InlineData("u")]
    [InlineData("t")]
    [InlineData("s")]
    public void Rejects_a_request_missing_any_required_parameter(string omit)
    {
        var token = OpenSubsonicClient.ComputeToken(_credential.Password, "somesalt");

        var pairs = new List<(string, string)>
        {
            ("u", _credential.Username), ("t", token), ("s", "somesalt"),
        };
        pairs.RemoveAll(p => p.Item1 == omit);

        Assert.Null(SubsonicAuth.Validate(Query(pairs.ToArray()), _store));
    }

    [Fact]
    public void Rejects_plaintext_password_auth()
    {
        // The legacy p= form is deliberately not supported. Without this test
        // nothing stops it being "helpfully" reintroduced.
        Assert.Null(SubsonicAuth.Validate(
            Query(("u", _credential.Username), ("p", _credential.Password)), _store));
    }
}

public class SubsonicCredentialStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("flower-subsonic-store-tests").FullName;
    private readonly string? _previousDataDirectory;
    private readonly SubsonicCredentialStore _store;

    public SubsonicCredentialStoreTests()
    {
        _previousDataDirectory = PlatformDataDirectory.Current;
        PlatformDataDirectory.Current = Path.Combine(_root, "appdata");
        _store = new SubsonicCredentialStore(NullLogger<SubsonicCredentialStore>.Instance);
    }

    public void Dispose()
    {
        PlatformDataDirectory.Current = _previousDataDirectory;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Issued_credentials_are_unique_even_for_an_identical_label()
    {
        var first = await _store.IssueAsync("Phone");
        var second = await _store.IssueAsync("Phone");

        Assert.NotEqual(first.Username, second.Username);
        Assert.NotEqual(first.Password, second.Password);
        // The username stays recognizable in a client's settings screen rather
        // than being an opaque id.
        Assert.StartsWith("phone-", first.Username);
    }

    [Fact]
    public async Task Passwords_avoid_visually_ambiguous_characters()
    {
        // These get copied by hand into a client's password box often enough
        // that 0/O and 1/I confusion is a real support cost.
        for (var i = 0; i < 20; i++)
        {
            var credential = await _store.IssueAsync("client");
            Assert.Equal(32, credential.Password.Length);
            Assert.DoesNotContain(credential.Password, c => c is '0' or 'o' or 'O' or '1' or 'l' or 'I');
        }
    }

    [Fact]
    public async Task A_label_that_slugifies_to_nothing_still_produces_a_usable_username()
    {
        var credential = await _store.IssueAsync("!!!");

        Assert.StartsWith("client-", credential.Username);
    }

    [Fact]
    public async Task Credentials_survive_a_new_store_over_the_same_directory()
    {
        var issued = await _store.IssueAsync("Phone");

        // A restart must not silently drop every client's credential.
        var reloaded = new SubsonicCredentialStore(NullLogger<SubsonicCredentialStore>.Instance);
        Assert.Equal(issued.Password, reloaded.Find(issued.Username)?.Password);
    }

    [Fact]
    public async Task Revoking_an_unknown_credential_is_a_no_op_rather_than_an_error()
    {
        Assert.False(await _store.RevokeAsync("never-issued"));
    }

    [Fact]
    public async Task Touch_records_a_last_seen_time()
    {
        var credential = await _store.IssueAsync("Phone");
        Assert.Null(credential.LastSeenAt);

        var now = DateTimeOffset.UtcNow;
        await _store.TouchAsync(credential.Username, now);

        Assert.Equal(now, _store.Find(credential.Username)?.LastSeenAt);
    }

    [Fact]
    public async Task Touch_does_not_rewrite_the_file_on_every_request()
    {
        // Last-seen is an admin convenience, and /rest is hot enough (one
        // getCoverArt per album tile) that a write per request would be a real
        // cost. Second touch inside the window must be dropped.
        var credential = await _store.IssueAsync("Phone");
        var first = DateTimeOffset.UtcNow;
        await _store.TouchAsync(credential.Username, first);
        await _store.TouchAsync(credential.Username, first.AddSeconds(5));

        Assert.Equal(first, _store.Find(credential.Username)?.LastSeenAt);

        // Past the window, it does update.
        await _store.TouchAsync(credential.Username, first.AddMinutes(2));
        Assert.Equal(first.AddMinutes(2), _store.Find(credential.Username)?.LastSeenAt);
    }
}

public class PairingCodeServiceTests
{
    [Fact]
    public void A_generated_code_can_be_consumed_exactly_once()
    {
        var service = new PairingCodeService();
        var (code, expiresAt) = service.GenerateCode();

        Assert.True(expiresAt > DateTimeOffset.UtcNow);
        Assert.True(service.TryConsume(code, out _));
        // Single-use is the entire security property: a code overheard or
        // reused must not pair a second device.
        Assert.False(service.TryConsume(code, out _));
    }

    [Fact]
    public void An_ordinary_code_does_not_confer_admin()
    {
        var service = new PairingCodeService();
        var (code, _) = service.GenerateCode();

        Assert.True(service.TryConsume(code, out var grantsAdmin));
        Assert.False(grantsAdmin);
    }

    [Fact]
    public void An_admin_granting_code_reports_that_at_redemption()
    {
        var service = new PairingCodeService();
        var (code, _) = service.GenerateCode(grantsAdmin: true);

        Assert.True(service.TryConsume(code, out var grantsAdmin));
        Assert.True(grantsAdmin);
    }

    [Fact]
    public void A_rejected_code_never_reports_admin()
    {
        // grantsAdmin must be false on every failure path, not left at
        // whatever the caller passed in - a caller that ignores the return
        // value must still not end up granting anything.
        var service = new PairingCodeService();
        var (code, _) = service.GenerateCode(grantsAdmin: true);
        Assert.True(service.TryConsume(code, out _));

        Assert.False(service.TryConsume(code, out var replayed));
        Assert.False(replayed);
        Assert.False(service.TryConsume("NOTACODE", out var unknown));
        Assert.False(unknown);
    }

    [Fact]
    public void Consumption_is_case_whitespace_and_separator_insensitive()
    {
        var service = new PairingCodeService();
        var (code, _) = service.GenerateCode();

        // The code is read off a screen and typed in by hand, sometimes copied
        // with the dashes the admin UI groups it with.
        var typed = $"  {code[..4].ToLowerInvariant()}-{code[4..].ToLowerInvariant()}  ";
        Assert.True(service.TryConsume(typed, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("NOTACODE")]
    public void An_unknown_code_is_rejected(string? code)
    {
        Assert.False(new PairingCodeService().TryConsume(code, out _));
    }

    [Fact]
    public void Codes_avoid_visually_ambiguous_characters()
    {
        var service = new PairingCodeService();

        // 0/O and 1/I are excluded so an operator reading a code aloud or
        // off a screen can't produce an unredeemable one.
        for (var i = 0; i < 200; i++)
        {
            var (code, _) = service.GenerateCode();
            Assert.Equal(8, code.Length);
            Assert.DoesNotContain(code, c => c is '0' or 'O' or '1' or 'I');
            Assert.All(code, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
        }
    }

    [Fact]
    public void Outstanding_codes_are_independent_of_each_other()
    {
        var service = new PairingCodeService();
        var (first, _) = service.GenerateCode();
        var (second, _) = service.GenerateCode(grantsAdmin: true);

        Assert.NotEqual(first, second);
        Assert.True(service.TryConsume(first, out var firstAdmin));
        Assert.False(firstAdmin);
        // Burning one code must not invalidate another still-outstanding one,
        // nor leak its grant into the other's answer.
        Assert.True(service.TryConsume(second, out var secondAdmin));
        Assert.True(secondAdmin);
    }
}

public class StreamTicketServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void A_ticket_redeems_for_the_track_it_was_minted_for()
    {
        var service = new StreamTicketService();
        var (ticket, expiresAt) = service.Issue("track-1", "fingerprint-a");

        Assert.True(expiresAt > Now);
        Assert.True(service.TryRedeem(ticket, "track-1", Now));
    }

    [Fact]
    public void A_ticket_is_reusable_within_its_lifetime()
    {
        // Unlike a pairing code: one playback is many requests (an initial
        // probe, then a range request per seek), so burning the ticket on
        // first use would break playback immediately.
        var service = new StreamTicketService();
        var (ticket, _) = service.Issue("track-1", "fingerprint-a");

        Assert.True(service.TryRedeem(ticket, "track-1", Now));
        Assert.True(service.TryRedeem(ticket, "track-1", Now));
        Assert.True(service.TryRedeem(ticket, "track-1", Now));
    }

    [Fact]
    public void A_ticket_does_not_unlock_a_different_track()
    {
        // The whole reason a ticket is acceptable as a bearer token in a URL:
        // it is a key to one track, not to the library.
        var service = new StreamTicketService();
        var (ticket, _) = service.Issue("track-1", "fingerprint-a");

        Assert.False(service.TryRedeem(ticket, "track-2", Now));
    }

    [Fact]
    public void An_expired_ticket_is_rejected()
    {
        var service = new StreamTicketService();
        var (ticket, expiresAt) = service.Issue("track-1", "fingerprint-a");

        Assert.False(service.TryRedeem(ticket, "track-1", expiresAt.AddSeconds(1)));
    }

    [Theory]
    [InlineData("", "track-1")]
    [InlineData(null, "track-1")]
    [InlineData("not-a-ticket", "track-1")]
    public void An_unknown_ticket_is_rejected(string? ticket, string trackId)
    {
        Assert.False(new StreamTicketService().TryRedeem(ticket, trackId, Now));
    }

    [Fact]
    public void A_ticket_without_a_track_id_is_rejected()
    {
        var service = new StreamTicketService();
        var (ticket, _) = service.Issue("track-1", "fingerprint-a");

        Assert.False(service.TryRedeem(ticket, null, Now));
        Assert.False(service.TryRedeem(ticket, "", Now));
    }

    [Fact]
    public void Revoking_a_peer_invalidates_the_tickets_it_minted_and_no_others()
    {
        // Otherwise "revoke this device" would leave its already-minted stream
        // URLs playable for the rest of their lifetime, which makes the revoke
        // button a promise the server doesn't keep.
        var service = new StreamTicketService();
        var (revoked, _) = service.Issue("track-1", "fingerprint-a");
        var (kept, _) = service.Issue("track-2", "fingerprint-b");

        Assert.Equal(1, service.RevokeFor("fingerprint-a"));
        Assert.False(service.TryRedeem(revoked, "track-1", Now));
        Assert.True(service.TryRedeem(kept, "track-2", Now));
    }

    [Fact]
    public void Every_issued_ticket_is_distinct()
    {
        var service = new StreamTicketService();
        var tickets = Enumerable.Range(0, 50).Select(_ => service.Issue("track-1", "fp").Ticket).ToList();

        Assert.Equal(tickets.Count, tickets.Distinct().Count());
        // 32 random bytes, hex-encoded.
        Assert.All(tickets, t => Assert.Equal(64, t.Length));
    }
}
