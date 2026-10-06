using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EAuction.Sandbox;

/// <summary>
/// RFC 6238, in the shape Keycloak's OTP authenticator expects.
///
/// Here for one reason: the realm's step-up flow asks for a six-digit code, and the
/// only holders of the secret that would produce one are the test suite and this. A
/// person demonstrating the platform has no authenticator app with a dev secret in
/// it, so without this the gate on registration — which is the gate on everything,
/// since a bidder who cannot register cannot do anything else — is impassable by
/// hand.
///
/// The secret is read from configuration and the default is the one published in
/// `deploy/keycloak/eauction-realm.json`. Both of those facts are deliberate: this
/// computes a second factor, so the only secret it may ever see is one that is
/// already public, and it must be obvious from the configuration which one that is.
/// </summary>
public static class Totp
{
    /// <summary>
    /// The code for <paramref name="at"/>, and how long it lasts.
    ///
    /// The remaining seconds are returned rather than left to the caller, because a
    /// code shown without them is a code someone types as it expires and then
    /// distrusts the whole system over.
    /// </summary>
    public static (string Code, int SecondsRemaining) At(
        string secret, DateTimeOffset at, int digits = 6, int period = 30)
    {
        var seconds = at.ToUnixTimeSeconds();
        var counter = seconds / period;

        var message = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(message, counter);

        // ASCII bytes of the secret as written, not base32-decoded. That is how
        // Keycloak stores a realm-imported `secretData.value`, and it is what the
        // e2e suite's own TOTP helper does — the two have to agree or a code that
        // works in a test fails in a browser.
        using var hmac = new HMACSHA1(Encoding.ASCII.GetBytes(secret));
        var mac = hmac.ComputeHash(message);

        var offset = mac[^1] & 0x0f;
        var truncated = BinaryPrimitives.ReadUInt32BigEndian(mac.AsSpan(offset, 4)) & 0x7fff_ffff;

        var modulus = (uint)Math.Pow(10, digits);
        var code = (truncated % modulus).ToString().PadLeft(digits, '0');

        return (code, (int)(period - seconds % period));
    }
}
