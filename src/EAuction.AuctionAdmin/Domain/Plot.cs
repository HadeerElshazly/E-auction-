namespace EAuction.AuctionAdmin.Domain;

/// <summary>
/// A land parcel (قطعة). An auction sells 1..N of these as one indivisible
/// package — bidding is on the package, never on a plot within it (D-02).
/// </summary>
public sealed class Plot
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid AuctionId { get; private set; }

    /// <summary>
    /// رقم القطعة — the number the plot carries on the approved plan, which is how
    /// a citizen, a surveyor and the municipality all refer to it.
    ///
    /// This replaced the title deed number (رقم الصك). A deed number identifies the
    /// *ownership record*, not the parcel being sold, and publishing it on an open
    /// catalogue hands out a reference into the land registry for no gain: the plan
    /// number is what a bidder needs to find the land, and it is already public on
    /// the plan itself.
    /// </summary>
    public string PlotNumber { get; private set; } = "";

    public decimal AreaSqm { get; private set; }

    /// <summary>
    /// عرض الشارع — the width in metres of the street the plot fronts onto, and
    /// الواجهة, the length in metres of that frontage. Both bear directly on what
    /// the land is worth and what may be built on it, so both are on the public
    /// listing rather than buried in the booklet.
    ///
    /// Nullable because a plot may be listed before the survey figures are in hand,
    /// and a zero would read as a measured zero.
    /// </summary>
    public decimal? StreetWidthMeters { get; private set; }

    public decimal? FrontageMeters { get; private set; }

    /// <summary>
    /// الاستخدام — what the land is zoned for. A bidder weighs a residential plot and
    /// a commercial one differently, and the plan says which; null until it is set.
    /// </summary>
    public LandUse? LandUse { get; private set; }

    public string? Latitude { get; private set; }
    public string? Longitude { get; private set; }
    public string? DescriptionAr { get; private set; }
    public string? DescriptionEn { get; private set; }

    private Plot() { }

    public Plot(Guid auctionId, string plotNumber, decimal areaSqm,
        string? latitude = null, string? longitude = null,
        string? descriptionAr = null, string? descriptionEn = null,
        decimal? streetWidthMeters = null, decimal? frontageMeters = null,
        LandUse? landUse = null)
    {
        if (string.IsNullOrWhiteSpace(plotNumber))
            throw new ArgumentException("رقم القطعة مطلوب.", nameof(plotNumber));
        if (areaSqm <= 0)
            throw new ArgumentException("المساحة يجب أن تكون أكبر من صفر.", nameof(areaSqm));
        if (streetWidthMeters is <= 0)
            throw new ArgumentException("عرض الشارع يجب أن يكون أكبر من صفر.", nameof(streetWidthMeters));
        if (frontageMeters is <= 0)
            throw new ArgumentException("طول الواجهة يجب أن يكون أكبر من صفر.", nameof(frontageMeters));

        AuctionId = auctionId;
        PlotNumber = plotNumber.Trim();
        AreaSqm = areaSqm;
        StreetWidthMeters = streetWidthMeters;
        FrontageMeters = frontageMeters;
        LandUse = landUse;
        Latitude = latitude;
        Longitude = longitude;
        DescriptionAr = descriptionAr;
        DescriptionEn = descriptionEn;
    }
}

/// <summary>الاستخدام: the uses an approved plan zones a plot for.</summary>
public enum LandUse
{
    Residential,
    Commercial,
    ResidentialCommercial,
    Industrial,
    Agricultural,
    Other,
}
