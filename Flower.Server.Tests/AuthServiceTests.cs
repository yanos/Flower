using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

using Flower.Server.Configuration;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Tests;

// The security-critical services had no coverage at all before this project
// existed (ARCHITECTURE-REVIEW Tier 5.1) - these are the "a wrong answer here
// lets a stranger in" paths, so they get tested for what they reject as much
// as for what they accept.
public class SubsonicAuthTests
{
    private static FlowerServerOptions Options() =>
        new() { AdminUsername = "admin", AdminPassword = "hunter2" };

    private static IQueryCollection Query(params (string Key, string Value)[] pairs) =>
        new QueryCollection(pairs.ToDictionary(
            p => p.Key,
            p => new Microsoft.Extensions.Primitives.StringValues(p.Value)));

    [Fact]
    public void Accepts_a_correctly_salted_token()
    {
        var options = Options();
        var token = OpenSubsonicClient.ComputeToken(options.AdminPassword, "somesalt");

        Assert.True(SubsonicAuth.Validate(
            Query(("u", "admin"), ("t", token), ("s", "somesalt")), options));
    }

    [Fact]
    public void Accepts_a_token_in_either_case()
    {
        var options = Options();
        var token = OpenSubsonicClient.ComputeToken(options.AdminPassword, "somesalt");

        // Real Subsonic clients differ on hex casing, which is why the
        // comparison is OrdinalIgnoreCase - pin that it stays that way.
        Assert.True(SubsonicAuth.Validate(
            Query(("u", "admin"), ("t", token.ToUpperInvariant()), ("s", "somesalt")), options));
    }

    [Fact]
    public void Rejects_a_token_computed_with_a_different_salt()
    {
        var options = Options();
        var token = OpenSubsonicClient.ComputeToken(options.AdminPassword, "somesalt");

        // The salt is the whole point of the scheme: a token replayed with a
        // different salt must not validate.
        Assert.False(SubsonicAuth.Validate(
            Query(("u", "admin"), ("t", token), ("s", "othersalt")), options));
    }

    [Fact]
    public void Rejects_the_wrong_password()
    {
        var options = Options();
        var token = OpenSubsonicClient.ComputeToken("not-the-password", "somesalt");

        Assert.False(SubsonicAuth.Validate(
            Query(("u", "admin"), ("t", token), ("s", "somesalt")), options));
    }

    [Fact]
    public void Rejects_the_wrong_username()
    {
        var options = Options();
        var token = OpenSubsonicClient.ComputeToken(options.AdminPassword, "somesalt");

        Assert.False(SubsonicAuth.Validate(
            Query(("u", "someone-else"), ("t", token), ("s", "somesalt")), options));
    }

    [Theory]
    [InlineData("u")]
    [InlineData("t")]
    [InlineData("s")]
    public void Rejects_a_request_missing_any_required_parameter(string omit)
    {
        var options = Options();
        var token = OpenSubsonicClient.ComputeToken(options.AdminPassword, "somesalt");

        var pairs = new List<(string, string)>
        {
            ("u", "admin"), ("t", token), ("s", "somesalt"),
        };
        pairs.RemoveAll(p => p.Item1 == omit);

        Assert.False(SubsonicAuth.Validate(Query(pairs.ToArray()), options));
    }

    [Fact]
    public void Rejects_plaintext_password_auth()
    {
        // The legacy p= form is deliberately not supported. Without this test
        // nothing stops it being "helpfully" reintroduced.
        var options = Options();

        Assert.False(SubsonicAuth.Validate(
            Query(("u", "admin"), ("p", "hunter2")), options));
    }
}

public class AdminAuthServiceTests
{
    private static AdminAuthService Service(string user = "admin", string password = "hunter2") =>
        new(Microsoft.Extensions.Options.Options.Create(
            new FlowerServerOptions { AdminUsername = user, AdminPassword = password }));

    [Fact]
    public void Accepts_the_configured_credentials()
    {
        Assert.True(Service().ValidateCredentials("admin", "hunter2"));
    }

    [Theory]
    [InlineData("admin", "wrong")]
    [InlineData("wrong", "hunter2")]
    [InlineData("ADMIN", "hunter2")]   // case-sensitive by design
    [InlineData("admin", "HUNTER2")]
    [InlineData("admin", "hunter2 ")]  // no trimming
    [InlineData("", "hunter2")]
    [InlineData("admin", "")]
    [InlineData(null, null)]
    public void Rejects_anything_else(string? user, string? password)
    {
        Assert.False(Service().ValidateCredentials(user, password));
    }

    [Fact]
    public void Rejects_a_password_that_is_a_prefix_of_the_real_one()
    {
        // FixedTimeEquals over UTF-8 bytes of differing length must not
        // shortcut to "equal so far, good enough".
        Assert.False(Service().ValidateCredentials("admin", "hunter"));
    }

    [Fact]
    public void An_issued_token_validates_and_an_unissued_one_does_not()
    {
        var service = Service();
        var token = service.IssueToken();

        Assert.True(service.ValidateToken(token));
        Assert.False(service.ValidateToken("not-a-real-token"));
        Assert.False(service.ValidateToken(""));
        Assert.False(service.ValidateToken(null));
    }

    [Fact]
    public void Each_issued_token_is_distinct_and_independently_valid()
    {
        var service = Service();
        var tokens = Enumerable.Range(0, 50).Select(_ => service.IssueToken()).ToList();

        Assert.Equal(tokens.Count, tokens.Distinct().Count());
        Assert.All(tokens, t => Assert.True(service.ValidateToken(t)));
        // 32 random bytes, hex-encoded.
        Assert.All(tokens, t => Assert.Equal(64, t.Length));
    }

    [Fact]
    public void A_revoked_token_stops_validating_and_leaves_the_others_alone()
    {
        // Before this, a stolen bearer token was good for its full 24 hours
        // with a process restart as the only lever (ARCHITECTURE-REVIEW Tier
        // 3.4).
        var service = Service();
        var stolen = service.IssueToken();
        var other = service.IssueToken();

        Assert.True(service.Revoke(stolen));
        Assert.False(service.ValidateToken(stolen));
        Assert.True(service.ValidateToken(other));

        // Revoking something already gone (or never issued) is a no-op, not
        // an error - /logout can be called twice.
        Assert.False(service.Revoke(stolen));
        Assert.False(service.Revoke("not-a-real-token"));
        Assert.False(service.Revoke(null));
    }

    [Fact]
    public void RevokeAll_invalidates_every_outstanding_session()
    {
        var service = Service();
        var tokens = Enumerable.Range(0, 5).Select(_ => service.IssueToken()).ToList();

        Assert.Equal(5, service.RevokeAll());
        Assert.All(tokens, t => Assert.False(service.ValidateToken(t)));
        Assert.Equal(0, service.ActiveTokenCount);

        // Still usable afterwards - this is a sign-out, not a shutdown.
        Assert.True(service.ValidateToken(service.IssueToken()));
    }

    [Fact]
    public void A_token_from_one_service_instance_is_not_valid_in_another()
    {
        // Tokens live in memory only - a restart invalidates every session.
        var token = Service().IssueToken();

        Assert.False(Service().ValidateToken(token));
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
        Assert.True(service.TryConsume(code));
        // Single-use is the entire security property: a code overheard or
        // reused must not pair a second device.
        Assert.False(service.TryConsume(code));
    }

    [Fact]
    public void Consumption_is_case_and_whitespace_insensitive()
    {
        var service = new PairingCodeService();
        var (code, _) = service.GenerateCode();

        // The code is read off a screen and typed in by hand.
        Assert.True(service.TryConsume($"  {code.ToLowerInvariant()}  "));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("NOTACODE")]
    public void An_unknown_code_is_rejected(string? code)
    {
        Assert.False(new PairingCodeService().TryConsume(code));
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
        var (second, _) = service.GenerateCode();

        Assert.NotEqual(first, second);
        Assert.True(service.TryConsume(first));
        // Burning one code must not invalidate another still-outstanding one.
        Assert.True(service.TryConsume(second));
    }
}
