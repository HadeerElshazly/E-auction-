namespace EAuction.Participant.Domain;

/// <summary>
/// A registered bidder (مزايد). Individuals only in v1 — company bidders need
/// Nafath's delegation path, which is a different integration.
/// </summary>
public sealed class Bidder
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>
    /// National ID or iqama, as returned by Nafath. Stored because the award
    /// and the title transfer are both against a legal identity, not a
    /// username — but it is personal data under PDPL and never leaves the
    /// service in an event payload.
    /// </summary>
    public string NationalId { get; private set; } = "";

    public string NameAr { get; private set; } = "";
    public string NameEn { get; private set; } = "";
    public string? Phone { get; private set; }
    public string? Email { get; private set; }

    /// <summary>
    /// When Nafath confirmed this identity. Null means self-asserted, which is
    /// never enough to bid.
    /// </summary>
    public DateTimeOffset? NafathVerifiedAt { get; private set; }

    public DateTimeOffset? ProfileCompletedAt { get; private set; }
    public DateTimeOffset RegisteredAt { get; private set; } = DateTimeOffset.UtcNow;

    public bool IsVerified => NafathVerifiedAt is not null;
    public bool IsProfileComplete => ProfileCompletedAt is not null;

    private Bidder() { }

    /// <summary>
    /// Created from a Nafath assertion, never from a form. The identity is the
    /// one thing a bidder cannot be allowed to assert about themselves.
    /// </summary>
    public static Bidder FromNafath(
        string nationalId, string nameAr, string nameEn, DateTimeOffset verifiedAt)
    {
        if (string.IsNullOrWhiteSpace(nationalId))
            throw new ParticipantValidationException(new[] { "National ID is required." });

        return new Bidder
        {
            NationalId = nationalId.Trim(),
            NameAr = nameAr?.Trim() ?? "",
            NameEn = nameEn?.Trim() ?? "",
            NafathVerifiedAt = verifiedAt
        };
    }

    /// <summary>إكمال الملف الشخصي — contact details the bidder supplies themselves.</summary>
    public void CompleteProfile(string phone, string email, DateTimeOffset now)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(phone)) problems.Add("Phone is required.");
        if (string.IsNullOrWhiteSpace(email)) problems.Add("Email is required.");
        else if (!email.Contains('@')) problems.Add("Email is not valid.");
        if (problems.Count > 0) throw new ParticipantValidationException(problems);

        Phone = phone.Trim();
        Email = email.Trim();
        ProfileCompletedAt = now;
    }
}
