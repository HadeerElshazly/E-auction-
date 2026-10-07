namespace EAuction.Participant.Domain;

/// <summary>
/// An award as its winner sees it: what they owe, what they have paid, and what
/// comes next. Copied from the administration service's <c>AwardFollowUpUpdated</c>
/// snapshot on <c>auctions.lifecycle</c> — this service never decides any of it, it
/// only answers the winner's «ما الخطوة التالية؟» without the bidder portal having
/// to reach the staff API.
///
/// One row per auction: an award withdrawn and passed to the next bidder overwrites
/// it, and <see cref="DisqualifiedAt"/> on the old winner's row is what tells them.
/// Keyed by the auction, so a second award on the same auction replaces the first.
/// </summary>
public sealed class WinnerAward
{
    public Guid AuctionId { get; private set; }
    public Guid AwardId { get; private set; }
    public Guid WinnerBidderId { get; private set; }
    public long AmountMinorUnits { get; private set; }
    public long BrokerageMinorUnits { get; private set; }
    public DateTimeOffset ConfirmedAt { get; private set; }
    public DateTimeOffset ComplianceDeadline { get; private set; }
    public Guid? SignedLetterDocumentId { get; private set; }
    public DateTimeOffset? WinnerNotifiedAt { get; private set; }
    public long PaidMinorUnits { get; private set; }
    public long RemainingMinorUnits { get; private set; }
    public string TransferStatus { get; private set; } = "NotStarted";
    public DateTimeOffset? TransferCompletedAt { get; private set; }
    public DateTimeOffset? SettledAt { get; private set; }
    public DateTimeOffset? DisqualifiedAt { get; private set; }

    /// <summary>When the snapshot was taken — an older one never overwrites a newer.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    private WinnerAward() { }

    public WinnerAward(Guid auctionId) => AuctionId = auctionId;

    /// <summary>Applies a snapshot; false when it is older than the one held.</summary>
    public bool Apply(
        Guid awardId, Guid winnerBidderId, long amount, long brokerage,
        DateTimeOffset confirmedAt, DateTimeOffset complianceDeadline,
        Guid? signedLetterDocumentId, DateTimeOffset? winnerNotifiedAt,
        long paid, long remaining, string transferStatus, DateTimeOffset? transferCompletedAt,
        DateTimeOffset? settledAt, DateTimeOffset? disqualifiedAt, DateTimeOffset at)
    {
        // A newer award on the same auction always wins; within one award, the newer
        // snapshot does.
        if (AwardId == awardId && at < UpdatedAt) return false;

        AwardId = awardId;
        WinnerBidderId = winnerBidderId;
        AmountMinorUnits = amount;
        BrokerageMinorUnits = brokerage;
        ConfirmedAt = confirmedAt;
        ComplianceDeadline = complianceDeadline;
        SignedLetterDocumentId = signedLetterDocumentId;
        WinnerNotifiedAt = winnerNotifiedAt;
        PaidMinorUnits = paid;
        RemainingMinorUnits = remaining;
        TransferStatus = transferStatus;
        TransferCompletedAt = transferCompletedAt;
        SettledAt = settledAt;
        DisqualifiedAt = disqualifiedAt;
        UpdatedAt = at;
        return true;
    }

    /// <summary>
    /// The one step the winner is waiting on or must take, in the order the award
    /// runs: the letter, then paying the price, then the transfer at the notary.
    /// </summary>
    public string NextStep => DisqualifiedAt is not null ? "Withdrawn"
        : WinnerNotifiedAt is null ? "AwaitingLetter"
        : RemainingMinorUnits > 0 ? "Pay"
        : TransferStatus != "Completed" ? "Transfer"
        : "Done";
}
