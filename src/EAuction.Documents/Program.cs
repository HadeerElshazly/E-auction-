using System.Security.Cryptography;
using EAuction.Core;
using EAuction.Documents;
using EAuction.Outbox;
using EAuction.Security;

var builder = WebApplication.CreateBuilder(args);

// --- the object store ------------------------------------------------------

var serviceUrl = builder.Configuration["Storage:ServiceUrl"];
var accessKey = builder.Configuration["Storage:AccessKey"];
var secretKey = builder.Configuration["Storage:SecretKey"];

if (string.IsNullOrWhiteSpace(serviceUrl))
{
    // Nothing configured. In development that is ordinary — the whole point of
    // the seam — and the in-memory store keeps the rest of the stack runnable
    // without MinIO. In production it means an unconfigured deployment that
    // would accept uploads and lose them on the next restart.
    if (builder.Environment.IsProduction())
        throw new InvalidOperationException(
            "Storage:ServiceUrl is required. Without it documents are held in "
            + "memory and lost when the pod restarts — including signed award "
            + "letters and bank guarantees.");

    builder.Services.AddSingleton<IDocumentStore, InMemoryDocumentStore>();
}
else
{
    if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
        throw new InvalidOperationException(
            "Storage:ServiceUrl is set but its credentials are not.");

    builder.Services.AddSingleton<IDocumentStore>(new S3DocumentStore(new S3Options
    {
        ServiceUrl = serviceUrl,
        AccessKey = accessKey,
        SecretKey = secretKey,
        Bucket = builder.Configuration["Storage:Bucket"] ?? "eauction-documents",
    }));
}

// --- the grant key ---------------------------------------------------------
//
// Shared with every service that decides who may read a document: auction-admin
// mints grants for award letters, the participant service for booklets. The same
// arrangement as the bidder master key (D-20) — a key held in two places, and
// nothing derived from it ever published.
var grantKeyHex = builder.Configuration["Documents:GrantKeyHex"];
if (string.IsNullOrWhiteSpace(grantKeyHex))
{
    if (builder.Environment.IsProduction())
        throw new InvalidOperationException(
            "Documents:GrantKeyHex is required, and must match the services that "
            + "mint grants. Without it no bidder can open a booklet or an award letter.");

    grantKeyHex = Convert.ToHexString(DocumentGrants.NewKey());
}
var grantKey = Convert.FromHexString(grantKeyHex);

// --- the audit channel -----------------------------------------------------
//
// Straight onto the topic, with no outbox and no transaction, and that is the one
// asymmetry in the audit trail worth being plain about.
//
// The other two producers write their audit row in the same transaction as the
// change it describes, so neither can exist without the other (D-44). There is
// nothing here to join: this service keeps no database, and the "change" being
// recorded is that bytes left the building — which has already happened by the
// time anything could be rolled back. So a reach-record here is best-effort: if
// the broker is down the read still succeeds and the record is lost.
//
// That is the right way round. A document service that refused to hand a winner
// their award letter because Kafka was unavailable would be a worse service and a
// worse audit story, since the pressure would be to turn the auditing off.
var auditBootstrap = builder.Configuration["Kafka:BootstrapServers"];

if (string.IsNullOrWhiteSpace(auditBootstrap) && builder.Environment.IsProduction())
    throw new InvalidOperationException(
        "Kafka:BootstrapServers is required. Without it a staff member reading a "
        + "citizen's bank guarantee leaves no record anywhere.");

// Registered rather than captured in a local, which is how the other services do
// it and matters here for one more reason: it is the seam a test substitutes to
// assert that a staff read of someone else's bank guarantee actually produces a
// record. A dependency only reachable through a closure is a dependency nothing
// can check.
builder.Services.AddSingleton<IEventStream>(
    string.IsNullOrWhiteSpace(auditBootstrap)
        ? new InMemoryEventStream()
        : new KafkaEventStream(new KafkaEventStreamOptions
        {
            BootstrapServers = auditBootstrap,
            ConsumerGroup = "documents"
        }));

// Guards against an upload that would fill the disk. A كراسة الشروط with site
// plans in it is genuinely tens of megabytes, so this is generous; it is a
// backstop, not a policy.
var maxUploadBytes = builder.Configuration.GetValue("Documents:MaxUploadBytes", 64L * 1024 * 1024);

builder.Services.AddEAuctionJwt(builder.Configuration, builder.Environment);
builder.Services.AddEAuctionCors(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();

app.UseEAuctionCors();
app.UseAuthentication();
app.UseAuthorization();

var store = app.Services.GetRequiredService<IDocumentStore>();
await store.EnsureReadyAsync(CancellationToken.None);

app.MapGet("/health/live", () => Results.Ok("ok")).AllowAnonymous();
app.MapGet("/health/ready", async (IDocumentStore s, CancellationToken ct) =>
{
    try
    {
        await s.EnsureReadyAsync(ct);
        return Results.Ok("ok");
    }
    catch (Exception)
    {
        // Ready means the bucket is reachable. A document service that answers
        // ready while the object store is down accepts uploads it cannot keep.
        return Results.StatusCode(503);
    }
}).AllowAnonymous();

// --- upload ----------------------------------------------------------------
//
// Multipart, because a browser uploads a file with a form and this has to be
// reachable from the admin portal without a bespoke client.
app.MapPost("/documents", async (
    HttpRequest request, HttpContext http, IDocumentStore s, CancellationToken ct) =>
{
    var subject = http.User.SubjectId();
    if (subject is null) return Results.Forbid();

    if (!request.HasFormContentType)
        return Results.BadRequest(new { problems = new[] { "Expected a multipart form." } });

    var form = await request.ReadFormAsync(ct);
    var file = form.Files["file"];

    if (file is null || file.Length == 0)
        return Results.BadRequest(new { problems = new[] { "No file was uploaded." } });

    if (file.Length > maxUploadBytes)
        return Results.BadRequest(new
        {
            problems = new[] { $"The file is {file.Length} bytes; the limit is {maxUploadBytes}." }
        });

    // Unrecognised or absent, it is Restricted. Defaulting to Public would mean a
    // caller that forgot the field published a bank guarantee, and the mistake
    // would be invisible — the upload succeeds either way.
    var access = DocumentAccessValues.Parse(form["access"]);

    // Only staff publish. A bidder uploading a bank guarantee must not be able to
    // ask for it to be world-readable, by mistake or otherwise.
    if (access == DocumentAccess.Public && !http.User.IsInRole(Roles.AuctionAdmin)
                                        && !http.User.IsInRole(Roles.AwardCommittee))
        return Results.Forbid();

    // Hashed on the way through rather than after, so the bytes are read once.
    // It also means the hash is of what was actually stored.
    await using var uploaded = file.OpenReadStream();
    using var buffer = new MemoryStream();
    var hash = await HashingCopyAsync(uploaded, buffer, ct);
    buffer.Position = 0;

    var metadata = new DocumentMetadata
    {
        Id = Guid.NewGuid(),

        // The name is the uploader's, so it is treated as hostile: it reaches a
        // Content-Disposition header on the way out, and a newline in it would
        // let the uploader write headers of their own.
        FileName = DocumentNames.Safe(file.FileName),
        ContentType = string.IsNullOrWhiteSpace(file.ContentType)
            ? "application/octet-stream"
            : file.ContentType,
        SizeBytes = buffer.Length,
        Access = access,
        OwnerSubject = subject.Value,
        Sha256 = hash,
        UploadedAt = DateTimeOffset.UtcNow,
    };

    await s.PutAsync(metadata, buffer, ct);

    return Results.Created($"/documents/{metadata.Id}", metadata);
})
    // Staff and bidders both upload — the booklet and the bank guarantee — so the
    // outer gate is "authenticated", and the classification rules above decide
    // what each of them may do.
    .RequireAuthorization()
    .DisableAntiforgery();

// --- read ------------------------------------------------------------------

app.MapGet("/documents/{id:guid}", async (
    Guid id, string? grant, HttpContext http, IDocumentStore s,
    IEventStream audit, ILoggerFactory loggers, CancellationToken ct) =>
{
    var found = await s.GetAsync(id, ct);
    if (found is null) return Results.NotFound();

    if (!MayRead(grantKey, found.Metadata, grant, http))
    {
        await found.DisposeAsync();

        // 404, not 403, for a document the caller may not read.
        //
        // A 403 confirms the id exists, and these ids travel: they are in the
        // auction's public event, in award letters, in support tickets. "This
        // document exists and is not yours" is a fact worth not confirming.
        return Results.NotFound();
    }

    var metadata = found.Metadata;

    await AuditReadAsync(audit, loggers, metadata, http, "ReadDocument", ct);

    // The hash on the way out, so a caller can check the bytes they received
    // against what was stored without a second request.
    http.Response.Headers["X-Document-SHA256"] = metadata.Sha256;

    // Never sniffed, and never rendered in place.
    //
    // These are files a citizen uploaded. Serving one inline from this origin
    // means an uploaded .html — or a .pdf the browser decides is really HTML —
    // runs as a page on a domain the platform's own cookies belong to. That is
    // stored cross-site scripting with an upload form for a delivery mechanism,
    // and it is the one thing a document service gets wrong that costs the whole
    // platform. Content-Disposition names the file as an attachment and nosniff
    // stops the browser second-guessing the type.
    http.Response.Headers["X-Content-Type-Options"] = "nosniff";

    return Results.Stream(
        found.Bytes, metadata.ContentType,
        fileDownloadName: metadata.FileName,
        enableRangeProcessing: true);
}).AllowAnonymous();

app.MapGet("/documents/{id:guid}/metadata", async (
    Guid id, string? grant, HttpContext http, IDocumentStore s,
    IEventStream audit, ILoggerFactory loggers, CancellationToken ct) =>
{
    var metadata = await s.HeadAsync(id, ct);
    if (metadata is null) return Results.NotFound();

    if (!MayRead(grantKey, metadata, grant, http)) return Results.NotFound();

    // Audited too. It carries the uploader's subject, the file name and the size,
    // which is enough to confirm that a named citizen filed a bank guarantee —
    // less than the bytes, and not nothing.
    await AuditReadAsync(audit, loggers, metadata, http, "ReadDocumentMetadata", ct);

    return Results.Ok(metadata);
}).AllowAnonymous();

app.Run();

// ---------------------------------------------------------------------------

/// <summary>
/// The whole access decision, in one place.
///
/// Public is anonymous. Private is the owner or staff. Restricted needs a grant
/// minted by the service that owns the rule — and a grant is bound to a subject,
/// so an anonymous caller can never present a valid one.
/// </summary>
static bool MayRead(
    byte[] grantKey, DocumentMetadata metadata, string? grant, HttpContext http)
{
    if (metadata.Access == DocumentAccess.Public) return true;

    var subject = http.User.SubjectId();
    if (subject is null) return false;

    // A grant opens anything it names, including a Private document — which is
    // what lets an administrator hand a bidder their own guarantee back without
    // this service learning what a subscription is.
    if (DocumentGrants.Verify(grantKey, grant, metadata.Id, subject.Value, DateTimeOffset.UtcNow))
        return true;

    return metadata.Access switch
    {
        DocumentAccess.Private =>
            metadata.OwnerSubject == subject.Value
            || http.User.IsInRole(Roles.AuctionAdmin)
            || http.User.IsInRole(Roles.AwardCommittee),

        // Restricted means exactly that: no role opens it, not even an
        // administrator's. An award letter is the winner's.
        _ => false,
    };
}

/// <summary>
/// Records a staff read of something that is not theirs and not public.
///
/// Three exclusions, and each of them is the difference between a trail somebody
/// reads and a trail nobody does. A Public document is a cover image on the
/// catalogue, fetched by every visitor. An owner reading their own file is a
/// citizen using the product. And an anonymous caller cannot be named, so there is
/// nothing to record about them beyond an address the access log already has.
///
/// What is left is the case the trail exists for: a member of staff, or a bidder
/// holding a grant, opening a document belonging to someone else — a bank
/// guarantee, a signed award letter, a terms booklet.
/// </summary>
static async Task AuditReadAsync(
    IEventStream stream, ILoggerFactory loggers, DocumentMetadata metadata,
    HttpContext http, string action, CancellationToken ct)
{
    if (metadata.Access == DocumentAccess.Public) return;

    var subject = http.User.SubjectId();
    if (subject is null || subject == metadata.OwnerSubject) return;

    var (who, roles, source) = StaffAudit.ActorOf(http);
    var entry = StaffActionRecorded.By(
        who, roles, source, action, AuditSubject.Document(metadata.Id),
        $"{metadata.Access} document owned by {metadata.OwnerSubject}, {metadata.FileName}.");

    var logger = loggers.CreateLogger("EAuction.Documents.Audit");

    // The log line first, and unconditionally. It is not a fallback for the topic
    // being unavailable — it is the record that exists even when this service
    // cannot reach a broker at all, which is the state a developer runs it in.
    logger.LogInformation(
        "Document audit: {Actor} {Action} {Document} ({Access}, owner {Owner})",
        who, action, metadata.Id, metadata.Access, metadata.OwnerSubject);

    try
    {
        await stream.PublishAsync(
            Topics.StaffActions, entry.AggregateId,
            System.Text.Json.JsonSerializer.Serialize(
                entry, new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerDefaults.Web)),
            nameof(StaffActionRecorded), ct);
    }
    catch (Exception e)
    {
        // Never fails the read. See the note where auditStream is built: refusing a
        // winner their award letter because the audit topic is unreachable makes
        // both the service and the audit story worse.
        logger.LogError(
            e, "Document audit: could not publish the read of {Document} to {Topic}.",
            metadata.Id, Topics.StaffActions);
    }
}

static async Task<string> HashingCopyAsync(Stream source, Stream destination, CancellationToken ct)
{
    using var sha = SHA256.Create();
    var buffer = new byte[81920];

    while (true)
    {
        var read = await source.ReadAsync(buffer, ct);
        if (read == 0) break;

        sha.TransformBlock(buffer, 0, read, null, 0);
        await destination.WriteAsync(buffer.AsMemory(0, read), ct);
    }

    sha.TransformFinalBlock([], 0, 0);
    return Convert.ToHexString(sha.Hash!);
}
