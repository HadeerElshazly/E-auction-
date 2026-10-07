using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace EAuction.Outbox;

/// <summary>
/// Something a member of staff did that an auditor will ask about.
///
/// Shared rather than redeclared per service — unlike every other event contract
/// here, where the dependency deliberately points from the consumer to the
/// producer's own type. The reason is that this one has no owning domain: it is not
/// an auction fact or a participant fact, it is a fact about a person using the
/// platform, and three services raise the identical shape. A copy each would drift,
/// and the audit service's whole value is that the entries are comparable.
///
/// <para>
/// It lives in EAuction.Outbox because that is what the services that raise it
/// already depend on, and because raising it <em>through the outbox</em> is the
/// point (D-44): the row and the change it describes are written in one
/// transaction, so an auction cannot be approved without the record of who
/// approved it, and a record cannot survive a change that was rolled back.
/// </para>
/// </summary>
public sealed record StaffActionRecorded : IDomainEvent
{
    /// <summary>Who did it — the token's subject, never a name they typed.</summary>
    public required Guid ActorSubject { get; init; }

    /// <summary>
    /// The roles they held at that moment, as the token carried them.
    ///
    /// Recorded rather than looked up later, because roles change: an auditor
    /// asking "was this person entitled to approve that?" needs what was true then,
    /// not what is true now.
    /// </summary>
    public required string ActorRoles { get; init; }

    /// <summary>What they did, as a stable name: <c>ApproveAuction</c>, <c>ReadDocument</c>.</summary>
    public required string Action { get; init; }

    /// <summary>
    /// What they did it to, as <c>type/id</c>: <c>auction/&lt;guid&gt;</c>,
    /// <c>subscription/&lt;auction&gt;:&lt;bidder&gt;</c>, <c>document/&lt;guid&gt;</c>.
    ///
    /// One string rather than a type and an id, because the audit service does not
    /// know what any of them mean and should not have to: it stores and returns
    /// them, and whoever is reading knows an auction when they see one.
    /// </summary>
    public required string Subject { get; init; }

    /// <summary>
    /// A short summary, composed where the action happened.
    ///
    /// Written at the call site deliberately, because only the call site knows what
    /// may be said. The reserve price is the case that proves it: an entry recording
    /// "reserve changed from 1,200,000 to 1,400,000" would put the one number the
    /// whole auction turns on onto a topic that D-23 exists to keep it off. That
    /// call site records that the reserve changed and by whom, and no figures.
    /// </summary>
    public string? Details { get; init; }

    /// <summary>
    /// Where the request came from, when the service could tell.
    ///
    /// Null behind a proxy that does not forward it, which is most of them until
    /// someone configures it — recorded as null rather than as the gateway's own
    /// address, because a column that always says the same thing is worse than an
    /// empty one.
    /// </summary>
    public string? SourceAddress { get; init; }

    public required DateTimeOffset At { get; init; }

    /// <summary>
    /// Who, as a person reads it — the account's name — beside the subject id that
    /// identifies them for certain. An auditor asked "who approved this" should not
    /// have to resolve a GUID to answer it.
    /// </summary>
    public string? ActorName { get; init; }

    /// <summary>What, as a person reads it — the auction's name, the bidder's — beside <see cref="Subject"/>.</summary>
    public string? SubjectLabel { get; init; }

    /// <summary>
    /// Composes an entry from an actor and what they did.
    ///
    /// Takes the actor's three values rather than a type from EAuction.Security,
    /// because that reference would have to run the other way: this assembly is
    /// used by a console tool with no ASP.NET framework reference.
    /// </summary>
    public static StaffActionRecorded By(
        Guid actorSubject, string actorRoles, string? sourceAddress,
        string action, string subject, string? details = null,
        string? actorName = null, string? subjectLabel = null) => new()
    {
        ActorName = actorName,
        SubjectLabel = subjectLabel,
        ActorSubject = actorSubject,
        ActorRoles = actorRoles,
        SourceAddress = sourceAddress,
        Action = action,
        Subject = subject,
        Details = details,
        At = DateTimeOffset.UtcNow,
    };

    [JsonIgnore]
    public string AggregateType => "staff-action";

    /// <summary>
    /// The subject, so a topic reader can see at a glance what an entry concerns.
    ///
    /// Not used for compaction — <c>staff.actions</c> must never be compacted, and
    /// <c>ControlTopics</c> says so — but Kafka keys every record and a meaningless
    /// one would be a missed opportunity.
    /// </summary>
    [JsonIgnore]
    public string AggregateId => Subject;
}

/// <summary>
/// How the three services spell the thing an action was performed on.
///
/// Centralised where the action names deliberately are not. An action name appears
/// once, at the site that performs it, and reads best as a literal there. A subject
/// is a <em>format</em> shared by three services and read back by an auditor
/// filtering on it, and a format agreed by convention across three codebases is a
/// format that drifts.
/// </summary>
public static class AuditSubject
{
    public static string Auction(Guid id) => $"auction/{id}";

    /// <summary>
    /// One bidder's standing in one auction. Both ids, because neither alone is
    /// what was acted on: revoking a bidder's eligibility in مخطط السعيد says
    /// nothing about their eligibility anywhere else.
    /// </summary>
    public static string Subscription(Guid auctionId, Guid bidderId) =>
        $"subscription/{auctionId}:{bidderId}";

    public static string Document(Guid id) => $"document/{id}";

    public static string Bidder(Guid id) => $"bidder/{id}";
}

/// <summary>
/// Queues a staff action on the acting service's own outbox.
///
/// An extension on <see cref="DbContext"/> rather than on each service's own
/// context, because the only thing it needs is the outbox set, and every service
/// that performs an auditable action already has one.
///
/// <para>
/// It takes the actor's three values loose rather than a type from
/// <c>EAuction.Security</c>, and that is the one piece of friction here worth
/// keeping. The reference would have to run that way round — Security holds the
/// reader of an <c>HttpContext</c> — and this assembly carries EF Core Relational
/// and Confluent.Kafka. Pointing Security at it would hand both to
/// <c>EAuction.QueryBff</c>, which is the one service here that answers
/// unauthenticated requests from the public internet and wants nothing from either
/// but JWT validation. Its dependency surface is worth more than one saved
/// argument.
/// </para>
///
/// <para>
/// The document service does reference this assembly, for
/// <see cref="StaffActionRecorded"/> alone, and so does acquire EF Core Relational
/// transitively without ever loading it. That is a real if small cost, accepted
/// because it produces audit records and the alternative is a second copy of the
/// contract — which is the drift the shared type exists to prevent.
/// </para>
/// </summary>
public static class StaffAuditOutbox
{
    /// <summary>
    /// Adds the row. It is the caller's <c>SaveChangesAsync</c> that commits it,
    /// which is the whole design (D-44): the record and the change it describes
    /// are one transaction, so neither can exist alone.
    /// </summary>
    public static void RecordStaffAction(
        this DbContext db,
        Guid actorSubject, string actorRoles, string? sourceAddress,
        string action, string subject, string? details = null,
        string? actorName = null, string? subjectLabel = null) =>
        db.Set<OutboxMessage>().Add(OutboxMessage.From(StaffActionRecorded.By(
            actorSubject, actorRoles, sourceAddress, action, subject, details,
            actorName, subjectLabel)));
}
