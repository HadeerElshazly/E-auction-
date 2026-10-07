using EAuction.Participant.Domain;
using Xunit;

namespace EAuction.Participant.Tests;

/// <summary>
/// «الاستفسارات والإجابات» (الخاصية 10): a reply goes to the asker alone; a public
/// clarification is staff's own text and is published only once someone other than
/// its author approves it.
/// </summary>
public class InquiryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.NewGuid();
    private static readonly Guid Committee = Guid.NewGuid();

    private static Inquiry Asked() => Inquiry.Ask(Guid.NewGuid(), Guid.NewGuid(), "هل تشمل المساحة الارتداد؟", Now);

    [Fact]
    public void A_reply_tells_the_asker_and_sets_the_status()
    {
        var inquiry = Asked();
        Assert.Equal(InquiryStatus.Open, inquiry.Status);

        inquiry.Reply("نعم، تشملها.", close: false, Admin, Now);

        Assert.Equal(InquiryStatus.Answered, inquiry.Status);
        var answered = Assert.IsType<InquiryAnswered>(Assert.Single(inquiry.Events));
        Assert.Equal(inquiry.BidderId, answered.BidderId);
    }

    [Fact]
    public void A_closed_inquiry_takes_no_further_reply()
    {
        var inquiry = Asked();
        inquiry.Reply("نعم.", close: true, Admin, Now);

        Assert.Equal(InquiryStatus.Closed, inquiry.Status);
        Assert.Throws<ParticipantValidationException>(() => inquiry.Reply("ثانٍ", false, Admin, Now));
        Assert.Throws<ParticipantValidationException>(() => inquiry.Close(Now));
    }

    [Fact]
    public void A_question_cannot_be_empty_or_endless()
    {
        Assert.Throws<ParticipantValidationException>(() => Inquiry.Ask(Guid.NewGuid(), Guid.NewGuid(), "  ", Now));
        Assert.Throws<ParticipantValidationException>(
            () => Inquiry.Ask(Guid.NewGuid(), Guid.NewGuid(), new string('س', Inquiry.MaxLength + 1), Now));
    }

    [Fact]
    public void A_clarification_is_published_only_after_someone_else_approves_it()
    {
        var inquiry = Asked();
        Assert.Throws<ParticipantValidationException>(() => inquiry.ApproveClarification(Committee, Now));

        inquiry.DraftClarification("هل تشمل المساحة الارتداد؟", "نعم.", Admin, Now);
        Assert.Equal(ClarificationStatus.Drafted, inquiry.Clarification);
        Assert.Empty(inquiry.Events);

        // Approval by the author is not an approval.
        Assert.Throws<ParticipantValidationException>(() => inquiry.ApproveClarification(Admin, Now));

        inquiry.ApproveClarification(Committee, Now.AddHours(1));
        var published = Assert.IsType<ClarificationPublished>(Assert.Single(inquiry.Events));
        Assert.Equal("نعم.", published.AnswerAr);

        // What bidders already read is part of the record.
        Assert.Throws<ParticipantValidationException>(
            () => inquiry.DraftClarification("س", "ج", Admin, Now));
    }

    [Fact]
    public void The_published_event_names_nobody()
    {
        var inquiry = Asked();
        inquiry.DraftClarification("سؤال عام", "إجابة عامة", Admin, Now);
        inquiry.ApproveClarification(Committee, Now);

        var json = System.Text.Json.JsonSerializer.Serialize(
            (ClarificationPublished)inquiry.Events.Single(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.DoesNotContain(inquiry.BidderId.ToString(), json);
        Assert.DoesNotContain(inquiry.Question, json);
    }
}
