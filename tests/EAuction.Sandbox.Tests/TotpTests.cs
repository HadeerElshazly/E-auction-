using EAuction.Sandbox;
using Xunit;

namespace EAuction.Sandbox.Tests;

/// <summary>
/// The codes the sandbox prints.
///
/// This is the one piece of the sandbox that can be wrong in a way nobody notices
/// until a demonstration: a code that looks plausible, is rejected by Keycloak, and
/// leaves the operator unable to tell whether the sandbox, the realm or the login is
/// at fault. So the vectors below are not this implementation's own output. They
/// were produced by the e2e suite's TOTP helper — a separate implementation, in a
/// different language, which the Playwright walk-through proves against the real
/// Keycloak every time it crosses the step-up gate.
///
/// Agreeing with it is therefore evidence about Keycloak, which is the thing that
/// actually has to accept these codes.
/// </summary>
public class TotpTests
{
    private const string Secret = "eauctiondevsecret1234567890";

    public static TheoryData<long, string> Vectors => new()
    {
        // Start, middle and last millisecond of the first window: one code.
        { 0L, "494905" },
        { 1_000L, "494905" },
        { 29_999L, "494905" },
        // The counter rolls over and the code changes.
        { 30_000L, "436019" },
        { 59_999L, "436019" },
        // A real instant, and the rollover either side of it.
        { 1_700_000_000_000L, "471916" },
        { 1_700_000_029_999L, "048296" },
        { 1_700_000_030_000L, "048296" },
        // Far enough out to catch a 32-bit counter truncation.
        { 1_767_225_600_000L, "526903" },
        { 2_000_000_000_000L, "741237" },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void It_agrees_with_the_e2e_suites_implementation(long atMs, string expected)
    {
        var (code, _) = Totp.At(Secret, DateTimeOffset.FromUnixTimeMilliseconds(atMs));

        Assert.Equal(expected, code);
    }

    [Fact]
    public void A_code_is_six_digits_including_the_leading_zeros()
    {
        // 048296 above is why. Formatting that dropped the leading zero would give a
        // five-digit code that Keycloak's form silently truncates nothing from and
        // simply rejects — roughly one window in ten.
        foreach (var (atMs, _) in Vectors.Select(v => ((long)v[0]!, (string)v[1]!)))
        {
            var (code, _) = Totp.At(Secret, DateTimeOffset.FromUnixTimeMilliseconds(atMs));

            Assert.Equal(6, code.Length);
            Assert.True(code.All(char.IsAsciiDigit), code);
        }
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 29)]
    [InlineData(29, 1)]
    [InlineData(30, 30)]
    [InlineData(45, 15)]
    public void The_countdown_says_how_long_this_code_lasts(long second, int expected)
    {
        // Shown beside the code, because a code displayed without it is one somebody
        // types as it expires and then distrusts the whole system over. Never zero:
        // at the instant a window opens there are a full thirty seconds left, and a
        // "0 seconds" on a fresh code would read as broken.
        var (_, remaining) = Totp.At(Secret, DateTimeOffset.FromUnixTimeSeconds(second));

        Assert.Equal(expected, remaining);
    }

    [Fact]
    public void A_different_secret_gives_a_different_code()
    {
        // Guards against the one mistake that would make every assertion above pass
        // while the sandbox ignored its configuration: reading the secret from
        // somewhere other than the argument.
        var at = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        Assert.NotEqual(Totp.At(Secret, at).Code, Totp.At(Secret + "x", at).Code);
    }
}
