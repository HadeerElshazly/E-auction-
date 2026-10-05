namespace EAuction.Security;

/// <summary>
/// Realm roles, issued by Keycloak and carried in the token.
///
/// Deliberately few. A role exists here only where a wrong answer would let
/// someone do something they must not — the committee's award confirmation is
/// a legal act, and an administrator preparing auctions is not entitled to
/// perform it.
/// </summary>
public static class Roles
{
    /// <summary>A registered, Nafath-verified individual who may bid.</summary>
    public const string Bidder = "bidder";

    /// <summary>مدير النظام — prepares auctions, uploads documents, sets dates.</summary>
    public const string AuctionAdmin = "auction-admin";

    /// <summary>
    /// ممثل لجنة الترسية — confirms awards and disqualifies winners.
    ///
    /// Separate from <see cref="AuctionAdmin"/> on purpose: the person who
    /// sets an auction's terms should not also be the one who decides who won
    /// it. Slide 6 shows them as different actors, and the separation is the
    /// point, not an accident of the diagram.
    /// </summary>
    public const string AwardCommittee = "award-committee";

    /// <summary>Floor operator entering bids for an on-site auction (في الموقع).</summary>
    public const string Operator = "operator";
}

/// <summary>Authorization policy names.</summary>
public static class Policies
{
    public const string Bidder = "policy:bidder";
    public const string AuctionAdmin = "policy:auction-admin";
    public const string AwardCommittee = "policy:award-committee";
    public const string Operator = "policy:operator";

    /// <summary>
    /// Who may post a frame to the bid catcher: a bidder bidding for themselves,
    /// or a clerk entering one for a bidder in the hall (§29). Which of the two is
    /// allowed for a given auction depends on its channel, and the catcher decides
    /// that from its own state — this is the outer gate, not the whole check.
    /// </summary>
    public const string SubmitsBids = "policy:submits-bids";

    /// <summary>
    /// Staff who legitimately see who is in the room: the clerk running a hall
    /// auction, and the administrators who prepare and supervise one (§29).
    /// </summary>
    public const string StaffOnTheFloor = "policy:staff-on-the-floor";

    /// <summary>
    /// A role AND a recent second factor. For the endpoints that move money or land
    /// — see <see cref="StepUpOptions"/> for why the role alone is not enough.
    /// </summary>
    public const string BidderStepUp = "policy:bidder+step-up";

    public const string AwardCommitteeStepUp = "policy:award-committee+step-up";
}
