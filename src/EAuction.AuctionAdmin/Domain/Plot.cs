namespace EAuction.AuctionAdmin.Domain;

/// <summary>
/// A land parcel (قطعة). An auction sells 1..N of these as one indivisible
/// package — bidding is on the package, never on a plot within it (D-02).
/// </summary>
public sealed class Plot
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid AuctionId { get; private set; }
    public string DeedNumber { get; private set; } = "";
    public decimal AreaSqm { get; private set; }
    public string? Latitude { get; private set; }
    public string? Longitude { get; private set; }
    public string? DescriptionAr { get; private set; }
    public string? DescriptionEn { get; private set; }

    private Plot() { }

    public Plot(Guid auctionId, string deedNumber, decimal areaSqm,
        string? latitude = null, string? longitude = null,
        string? descriptionAr = null, string? descriptionEn = null)
    {
        if (string.IsNullOrWhiteSpace(deedNumber))
            throw new ArgumentException("Deed number is required.", nameof(deedNumber));
        if (areaSqm <= 0)
            throw new ArgumentException("Area must be positive.", nameof(areaSqm));

        AuctionId = auctionId;
        DeedNumber = deedNumber.Trim();
        AreaSqm = areaSqm;
        Latitude = latitude;
        Longitude = longitude;
        DescriptionAr = descriptionAr;
        DescriptionEn = descriptionEn;
    }
}
