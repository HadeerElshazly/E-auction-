using EAuction.Outbox;

namespace EAuction.Participant.Domain;

public enum InquiryStatus
{
    /// <summary>جديد — asked, nobody has replied.</summary>
    Open,

    /// <summary>تم الرد — staff replied to the bidder.</summary>
    Answered,

    /// <summary>مغلق — closed, with or without a reply: a duplicate, out of scope.</summary>
    Closed,
}

public enum ClarificationStatus
{
    None,

    /// <summary>A public version drafted by staff, waiting for approval.</summary>
    Drafted,

    /// <summary>Approved and on the auction page for everyone.</summary>
    Published,
}

/// <summary>
/// «الاستفسارات والإجابات» (المرحلة الأولى، الخاصية 10): a question a bidder asked
/// about an auction, the reply staff sent them, and the state it is in.
///
/// The reply is private: it goes to the bidder who asked and nobody else. What
/// everyone may read is a <em>clarification</em> — a separate text staff write for
/// the public, so that nothing the bidder wrote about themselves reaches it — and
/// it is published only once a second person has approved it. Drafting and
/// approving are deliberately two roles: the requirement says «بعد اعتماده».
/// </summary>
public sealed class Inquiry
{
    public const int MaxLength = 2000;

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid AuctionId { get; private set; }
    public Guid BidderId { get; private set; }
    public string Question { get; private set; } = "";
    public DateTimeOffset AskedAt { get; private set; }

    public InquiryStatus Status { get; private set; } = InquiryStatus.Open;
    public string? Answer { get; private set; }
    public DateTimeOffset? AnsweredAt { get; private set; }
    public Guid? AnsweredBy { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }

    public ClarificationStatus Clarification { get; private set; } = ClarificationStatus.None;
    public string? ClarificationQuestion { get; private set; }
    public string? ClarificationAnswer { get; private set; }
    public Guid? ClarificationDraftedBy { get; private set; }
    public DateTimeOffset? ClarificationDraftedAt { get; private set; }
    public Guid? ClarificationApprovedBy { get; private set; }
    public DateTimeOffset? ClarificationPublishedAt { get; private set; }

    private readonly List<IDomainEvent> _events = new();
    public IReadOnlyList<IDomainEvent> Events => _events;
    public void ClearEvents() => _events.Clear();

    private Inquiry() { }

    public static Inquiry Ask(Guid auctionId, Guid bidderId, string question, DateTimeOffset now)
    {
        var text = Required(question, "نص السؤال مطلوب.");
        return new Inquiry { AuctionId = auctionId, BidderId = bidderId, Question = text, AskedAt = now };
    }

    /// <summary>The reply to the bidder, and whether that closes it.</summary>
    public void Reply(string answer, bool close, Guid staff, DateTimeOffset now)
    {
        if (Status == InquiryStatus.Closed)
            throw new ParticipantValidationException(["الاستفسار مغلق."]);

        Answer = Required(answer, "نص الرد مطلوب.");
        AnsweredAt = now;
        AnsweredBy = staff;
        Status = close ? InquiryStatus.Closed : InquiryStatus.Answered;
        if (close) ClosedAt = now;

        _events.Add(new InquiryAnswered
        {
            InquiryId = Id,
            AuctionId = AuctionId,
            BidderId = BidderId,
            Closed = close,
            At = now,
        });
    }

    /// <summary>Closed without a reply — a duplicate, or not about this auction.</summary>
    public void Close(DateTimeOffset now)
    {
        if (Status == InquiryStatus.Closed)
            throw new ParticipantValidationException(["الاستفسار مغلق من قبل."]);
        Status = InquiryStatus.Closed;
        ClosedAt = now;
    }

    /// <summary>
    /// The public version, in staff's words. Redrafting replaces the draft; a
    /// published clarification is not edited in place, because what bidders already
    /// read is part of the record.
    /// </summary>
    public void DraftClarification(string question, string answer, Guid staff, DateTimeOffset now)
    {
        if (Clarification == ClarificationStatus.Published)
            throw new ParticipantValidationException(["نُشر التوضيح ولا يمكن تعديله."]);

        ClarificationQuestion = Required(question, "نص السؤال في التوضيح العام مطلوب.");
        ClarificationAnswer = Required(answer, "نص الإجابة في التوضيح العام مطلوب.");
        ClarificationDraftedBy = staff;
        ClarificationDraftedAt = now;
        Clarification = ClarificationStatus.Drafted;
    }

    /// <summary>
    /// Approved and published. Not by whoever drafted it: approval by the author is
    /// not an approval.
    /// </summary>
    public void ApproveClarification(Guid approver, DateTimeOffset now)
    {
        if (Clarification != ClarificationStatus.Drafted)
            throw new ParticipantValidationException(["لا يوجد توضيح بانتظار الاعتماد."]);
        if (approver == ClarificationDraftedBy)
            throw new ParticipantValidationException(["لا يعتمد التوضيحَ من صاغه."]);

        Clarification = ClarificationStatus.Published;
        ClarificationApprovedBy = approver;
        ClarificationPublishedAt = now;

        _events.Add(new ClarificationPublished
        {
            ClarificationId = Id,
            AuctionId = AuctionId,
            QuestionAr = ClarificationQuestion!,
            AnswerAr = ClarificationAnswer!,
            PublishedAt = now,
        });
    }

    private static string Required(string? text, string problem)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0) throw new ParticipantValidationException([problem]);
        if (trimmed.Length > MaxLength)
            throw new ParticipantValidationException([$"النص أطول من {MaxLength} حرف."]);
        return trimmed;
    }
}

/// <summary>
/// The bidder who asked has a reply. Carries no text: the notice says a reply
/// arrived, and the bidder reads it in the portal, where it is theirs alone.
/// </summary>
public sealed record InquiryAnswered : DomainEvent
{
    public required Guid InquiryId { get; init; }
    public required Guid AuctionId { get; init; }
    public required Guid BidderId { get; init; }
    public required bool Closed { get; init; }
    public required DateTimeOffset At { get; init; }

    public override string AggregateType => "participant-inquiries";
    public override string AggregateId => AuctionId.ToString();
}

/// <summary>A clarification approved for everyone: no bidder, only the staff's text.</summary>
public sealed record ClarificationPublished : DomainEvent
{
    public required Guid ClarificationId { get; init; }
    public required Guid AuctionId { get; init; }
    public required string QuestionAr { get; init; }
    public required string AnswerAr { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }

    public override string AggregateType => "participant-inquiries";
    public override string AggregateId => AuctionId.ToString();
}
