using EAuction.Core;

namespace EAuction.Participant.Domain;

/// <summary>
/// What this service needs to know about an auction, materialised from
/// <c>auctions.upcoming</c>.
///
/// Persisted rather than held in memory: a bidder mid-subscription when the
/// pod restarts should not find the auction has vanished. It carries no
/// reserve price — that topic is not readable here, by design (D-23).
/// </summary>
public sealed class AuctionTerms
{
    public Guid AuctionId { get; private set; }
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset EndsAt { get; private set; }
    public long DepositMinorUnits { get; private set; }
    public long BookletPriceMinorUnits { get; private set; }

    /// <summary>
    /// Whether this auction names its bidders (D-22). Held here, in the service that
    /// knows people's names, so that a masked auction's eligibility event can be
    /// published without a name in it at all — rather than published with one and
    /// filtered out downstream by whoever remembers to.
    /// </summary>
    public BidderVisibility BidderVisibility { get; private set; } = BidderVisibility.Masked;

    /// <summary>
    /// كراسة الشروط in the document service, so this service can mint a grant for
    /// a bidder who has paid for it.
    ///
    /// Held here because the rule — "has this bidder paid?" — is this service's,
    /// and the document service must not have to learn what a subscription is.
    /// </summary>
    public Guid? BookletDocumentId { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When auction-admin withdrew the auction, from the lifecycle topic. Without it
    /// this service went on taking enrolments and deposits for an auction that would
    /// never open — and a deposit paid after the cancellation released the others was
    /// held for good, because nothing would ever release it.
    /// </summary>
    public DateTimeOffset? CancelledAt { get; private set; }

    public void Cancel(DateTimeOffset at)
    {
        CancelledAt ??= at;
        Stage = AuctionStage.Cancelled;
    }

    /// <summary>The auction's Arabic name, from its approval — what a bidder searches «طلباتي» by.</summary>
    public string? NameAr { get; private set; }

    public void Name(string? nameAr) => NameAr = string.IsNullOrWhiteSpace(nameAr) ? NameAr : nameAr.Trim();

    /// <summary>
    /// Where the auction is, followed from the lifecycle topic, so «طلباتي» can be
    /// filtered here — on the server — by upcoming, running and finished.
    /// </summary>
    public AuctionStage Stage { get; private set; } = AuctionStage.Upcoming;

    /// <summary>
    /// Moves the stage forward on a lifecycle event, never back: the topic is replayed
    /// from the start, and a replayed «opened» must not undo a later «closed».
    /// </summary>
    public bool Advance(string eventType)
    {
        var next = eventType switch
        {
            "AuctionStarted" => AuctionStage.Live,
            "AuctionClosed" or "CandidateOffered" or "LadderExhausted" or "AwardConfirmed"
                or "WinnerDisqualified" or "NextBidderReferred" or "AuctionUnsold"
                or "AuctionSettled" => AuctionStage.Finished,
            "AuctionCancelled" => AuctionStage.Cancelled,
            _ => (AuctionStage?)null
        };
        if (next is null || next <= Stage) return false;
        Stage = next.Value;
        return true;
    }

    /// <summary>Refuses any enrolment or money step on a cancelled auction.</summary>
    public void RequireOpen()
    {
        if (CancelledAt is not null)
            throw new ParticipantValidationException(
                new[] { "أُلغي هذا المزاد ولا تُقبل فيه اشتراكات أو مدفوعات." });
    }

    private AuctionTerms() { }

    public AuctionTerms(
        Guid auctionId, DateTimeOffset startsAt, DateTimeOffset endsAt,
        long depositMinorUnits, long bookletPriceMinorUnits,
        BidderVisibility bidderVisibility = BidderVisibility.Masked,
        Guid? bookletDocumentId = null)
    {
        AuctionId = auctionId;
        BidderVisibility = bidderVisibility;
        StartsAt = startsAt;
        EndsAt = endsAt;
        DepositMinorUnits = depositMinorUnits;
        BookletPriceMinorUnits = bookletPriceMinorUnits;
        BookletDocumentId = bookletDocumentId;
    }

    public void Update(
        DateTimeOffset startsAt, DateTimeOffset endsAt,
        long depositMinorUnits, long bookletPriceMinorUnits, DateTimeOffset now,
        BidderVisibility bidderVisibility = BidderVisibility.Masked,
        Guid? bookletDocumentId = null)
    {
        BidderVisibility = bidderVisibility;
        StartsAt = startsAt;
        EndsAt = endsAt;
        DepositMinorUnits = depositMinorUnits;
        BookletPriceMinorUnits = bookletPriceMinorUnits;
        BookletDocumentId = bookletDocumentId;
        UpdatedAt = now;
    }
}

/// <summary>An auction's stage as «طلباتي» groups it. Ordered: it only moves forward.</summary>
public enum AuctionStage
{
    Upcoming = 0,
    Live = 1,
    Finished = 2,
    Cancelled = 3,
}
