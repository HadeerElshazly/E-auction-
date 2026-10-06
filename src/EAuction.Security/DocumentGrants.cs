using System.Security.Cryptography;
using System.Text;

namespace EAuction.Security;

/// <summary>
/// A short-lived, signed permission to read one document.
///
/// It exists because the question "may this person read this file" is not one the
/// document service can answer. Whether a bidder may read كراسة الشروط depends on
/// whether they paid for it; whether they may read an award letter depends on
/// whether they won. Those facts live in the participant service and auction-admin
/// respectively, and a document service that tried to learn them would need to
/// consume the auction domain — which is the opposite of what a document service
/// is for.
///
/// So the service that owns the rule mints a grant, and the document service
/// verifies the signature. The dependency points the right way: the document
/// service knows about HMAC and nothing about auctions.
///
/// <para>
/// The alternative, a presigned object-store URL, was rejected for two reasons: it
/// names the bucket and key in the URL, so it leaks the storage layout to the
/// browser, and it cannot be bound to a subject — anyone the link reaches can use
/// it. A grant names the subject and the document service checks it against the
/// caller's token, so a forwarded link is useless to the person it is forwarded to.
/// </para>
/// </summary>
public static class DocumentGrants
{
    /// <summary>
    /// Deliberately short. A grant is handed over at the moment of a click, so it
    /// needs to outlive a redirect and a slow connection and nothing more. A grant
    /// that lasted an hour would be an hour in which a leaked one works.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private const string Purpose = "eauction-document-grant-v1";

    public static byte[] NewKey()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    /// <summary>
    /// Mints a grant for one subject and one document.
    ///
    /// <paramref name="subject"/> is bound into the signature rather than merely
    /// written alongside it, so a grant cannot be re-pointed at another person by
    /// editing the part of the string that says who it is for.
    /// </summary>
    public static string Mint(
        byte[] key, Guid documentId, Guid subject, DateTimeOffset now)
    {
        var expires = now.Add(Lifetime).ToUnixTimeSeconds();
        var body = $"{documentId:N}.{subject:N}.{expires}";
        return $"{body}.{Convert.ToHexString(Sign(key, body))}";
    }

    /// <summary>
    /// Checks a grant against the document and the caller. Returns false for
    /// anything it does not like, and says nothing about which thing — a caller
    /// learning "the signature was fine but it expired" learns that the key is
    /// right, which is a hint worth not giving.
    /// </summary>
    public static bool Verify(
        byte[] key, string? grant, Guid documentId, Guid subject, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(grant)) return false;

        var parts = grant.Split('.');
        if (parts.Length != 4) return false;

        if (!Guid.TryParseExact(parts[0], "N", out var grantedDocument)
            || !Guid.TryParseExact(parts[1], "N", out var grantedSubject)
            || !long.TryParse(parts[2], out var expires))
            return false;

        if (grantedDocument != documentId || grantedSubject != subject) return false;
        if (DateTimeOffset.FromUnixTimeSeconds(expires) <= now) return false;

        byte[] presented;
        try { presented = Convert.FromHexString(parts[3]); }
        catch (FormatException) { return false; }

        // Fixed-time, because a comparison that returns early on the first wrong
        // byte tells an attacker how much of a forged signature was right.
        return CryptographicOperations.FixedTimeEquals(
            presented, Sign(key, $"{parts[0]}.{parts[1]}.{parts[2]}"));
    }

    private static byte[] Sign(byte[] key, string body) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Purpose + "\n" + body));
}
