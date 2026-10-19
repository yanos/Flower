using Flower.Server.Services;

namespace Flower.Server.Tests.Security;

// Wrong pairing codes are counted across every source at once. The per-source
// limit alone left a 5-character code to anyone holding an IPv6 /48: 65,536
// sources at five guesses a minute is a one-in-ten shot at a code in its ten
// minutes.
public class PairingCodeGuessingTests
{
    // Now, because a code's own expiry is stamped from the real clock.
    private readonly DateTimeOffset Start = DateTimeOffset.UtcNow;

    private static string Wrong(string real) => real == "AAAAA" ? "BBBBB" : "AAAAA";

    [Fact]
    public void Enough_wrong_codes_cancel_every_outstanding_code()
    {
        var codes = new PairingCodeService();
        var (code, _) = codes.GenerateCode(grantsAdmin: true);

        int? cancelled = null;
        for (var i = 0; i < PairingCodeService.MaxWrongCodes; i++)
            Assert.False(codes.TryConsume(Wrong(code), Start.AddSeconds(i), out _, out cancelled));

        Assert.Equal(1, cancelled);
        Assert.False(codes.TryConsume(code, Start.AddSeconds(30), out _, out _));
    }

    [Fact]
    public void A_few_typos_leave_the_code_redeemable()
    {
        var codes = new PairingCodeService();
        var (code, _) = codes.GenerateCode(grantsAdmin: false);

        for (var i = 0; i < PairingCodeService.MaxWrongCodes - 1; i++)
        {
            Assert.False(codes.TryConsume(Wrong(code), Start.AddSeconds(i), out _, out var cancelled));
            Assert.Null(cancelled);
        }

        Assert.True(codes.TryConsume(code, Start.AddSeconds(30), out _, out _));
    }

    // The budget is per code lifetime, not for ever: an ordinary mistyped
    // code a week apart never adds up to an attack.
    [Fact]
    public void Wrong_codes_older_than_a_code_lifetime_no_longer_count()
    {
        var codes = new PairingCodeService();
        for (var i = 0; i < PairingCodeService.MaxWrongCodes - 1; i++)
            codes.TryConsume("ZZZZZ", Start, out _, out _);

        var (code, _) = codes.GenerateCode();
        Assert.False(codes.TryConsume(Wrong(code), Start.AddMinutes(11), out _, out var cancelled));

        Assert.Null(cancelled);
        Assert.True(codes.TryConsume(code, DateTimeOffset.UtcNow, out _, out _));
    }

    // Counted afresh after a cancellation, so a code issued once the guessing
    // stops is not cancelled by the next typo.
    [Fact]
    public void A_code_issued_after_a_cancellation_has_a_budget_of_its_own()
    {
        var codes = new PairingCodeService();
        for (var i = 0; i < PairingCodeService.MaxWrongCodes; i++)
            codes.TryConsume("ZZZZZ", Start, out _, out _);

        var (code, _) = codes.GenerateCode();
        Assert.False(codes.TryConsume(Wrong(code), Start.AddSeconds(1), out _, out var cancelled));

        Assert.Null(cancelled);
        Assert.True(codes.TryConsume(code, DateTimeOffset.UtcNow, out _, out _));
    }
}
