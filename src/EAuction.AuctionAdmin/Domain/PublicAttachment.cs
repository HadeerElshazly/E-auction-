namespace EAuction.AuctionAdmin.Domain;

/// <summary>
/// A document anyone may read from the catalogue — a site plan, plot photographs,
/// the plan's approval — as opposed to كراسة الشروط, which only a paying bidder can
/// open. The requirements ask that a visitor reach a published plot "and its public
/// documents" without exceeding what the paid booklet restricts, so these are
/// uploaded as Public in the document service and listed on the approval event.
/// </summary>
public sealed class PublicAttachment
{
    public Guid DocumentId { get; private set; }
    public string TitleAr { get; private set; } = "";

    private PublicAttachment() { }

    public PublicAttachment(Guid documentId, string titleAr)
    {
        DocumentId = documentId;
        TitleAr = titleAr;
    }
}
