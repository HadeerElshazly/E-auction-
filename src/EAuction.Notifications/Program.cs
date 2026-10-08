using EAuction.Core;
using EAuction.Notifications.Delivery;
using EAuction.Notifications.Domain;
using EAuction.Notifications.Integration;
using EAuction.Notifications.Persistence;
using EAuction.Security;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

var connectionString = builder.Configuration.GetConnectionString("Notifications")
    ?? "Host=localhost;Database=eauction_notifications;Username=eauction;Password=eauction";

builder.Services.AddDbContextFactory<NotificationsDbContext>(o => o.UseNpgsql(connectionString));

var bootstrap = builder.Configuration["Kafka:BootstrapServers"];

builder.Services.AddSingleton<IEventStream>(
    string.IsNullOrWhiteSpace(bootstrap)
        ? new InMemoryEventStream()
        : new KafkaEventStream(new KafkaEventStreamOptions
        {
            BootstrapServers = bootstrap,
            ConsumerGroup = "notifications"
        }));

// The outbound channel.
//
// Only the log channel exists: SMS to Saudi numbers needs a licensed aggregator
// and a registered sender name, and neither is contracted (P-7). Unlike the
// payment gateway there is no production guard here, and the difference is
// deliberate — a simulated gateway that settles everything qualifies bidders who
// have not paid, while an unsent SMS leaves the in-product inbox, which is a real
// delivered channel, working exactly as it should.
builder.Services.AddSingleton<INotificationChannel, LogChannel>();
builder.Services.AddHostedService<EventConsumer>();

builder.Services.AddEAuctionJwt(builder.Configuration, builder.Environment);
builder.Services.AddEAuctionCors(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();

app.UseEAuctionCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok("ok")).AllowAnonymous();
app.MapGet("/health/ready", async (
    IDbContextFactory<NotificationsDbContext> f, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);
    return await db.Database.CanConnectAsync(ct) ? Results.Ok("ok") : Results.StatusCode(503);
}).AllowAnonymous();

// --- the inbox -------------------------------------------------------------

// A bidder's own notifications, and nobody else's.
//
// There is deliberately no administrator override. One bidder's inbox is a list of
// which auctions they are registered for, when they were outbid and what they won —
// which is the whole of what D-22 keeps off the public topics, assembled in one
// place. Staff who need to know whether a notice was sent have the service's logs;
// staff who need to read a citizen's inbox do not have a reason.
app.MapGet("/notifications", async (
    HttpContext http, bool? unreadOnly, int? skip, int? take,
    IDbContextFactory<NotificationsDbContext> f, CancellationToken ct) =>
{
    var subject = http.User.SubjectId();
    if (subject is null) return Results.Forbid();

    await using var db = await f.CreateDbContextAsync(ct);

    var query = db.Notifications
        .AsNoTracking()
        .Where(x => x.BidderId == subject.Value);

    if (unreadOnly == true) query = query.Where(x => x.ReadAt == null);

    var slice = Slice.From(skip, take, 50);
    var total = await query.CountAsync(ct);
    var items = await query
        .OrderByDescending(x => x.CreatedAt)
        .Skip(slice.Skip).Take(slice.Take)
        .Select(x => new NotificationResponse(
            x.Id, x.AuctionId, x.Kind.ToString(), x.TitleAr, x.BodyAr,
            x.Actionable, x.CreatedAt, x.ReadAt))
        .ToListAsync(ct);

    // The unread count separately from the page, because the bell shows a number
    // and a page of fifty says nothing about the fifty-first.
    var unread = await db.Notifications
        .CountAsync(x => x.BidderId == subject.Value && x.ReadAt == null, ct);

    return Results.Ok(new { total, skip = slice.Skip, take = slice.Take, items, unread });
}).RequireAuthorization(Policies.Bidder);

app.MapPost("/notifications/{id:guid}/read", async (
    HttpContext http, Guid id,
    IDbContextFactory<NotificationsDbContext> f, CancellationToken ct) =>
{
    var subject = http.User.SubjectId();
    if (subject is null) return Results.Forbid();

    await using var db = await f.CreateDbContextAsync(ct);
    var notification = await db.Notifications.FindAsync(new object?[] { id }, ct);

    // 404 rather than 403 for someone else's: a notification id confirms nothing
    // about whose it is.
    if (notification is null || notification.BidderId != subject.Value)
        return Results.NotFound();

    notification.MarkRead(DateTimeOffset.UtcNow);
    await db.SaveChangesAsync(ct);

    return Results.NoContent();
}).RequireAuthorization(Policies.Bidder);

app.MapPost("/notifications/read-all", async (
    HttpContext http, IDbContextFactory<NotificationsDbContext> f, CancellationToken ct) =>
{
    var subject = http.User.SubjectId();
    if (subject is null) return Results.Forbid();

    var now = DateTimeOffset.UtcNow;

    await using var db = await f.CreateDbContextAsync(ct);
    var unread = await db.Notifications
        .Where(x => x.BidderId == subject.Value && x.ReadAt == null)
        .ToListAsync(ct);

    foreach (var notification in unread) notification.MarkRead(now);
    await db.SaveChangesAsync(ct);

    return Results.Ok(new { marked = unread.Count });
}).RequireAuthorization(Policies.Bidder);

app.Run();

// ---------------------------------------------------------------------------

/// <summary>
/// One notification, as the portal renders it.
///
/// The title and body are stored text, not a template re-rendered on read: a
/// bidder shown "you were outbid at 1,200,000" is shown what they were told at the
/// time, which is what matters if they later dispute it.
/// </summary>
public sealed record NotificationResponse(
    Guid Id, Guid AuctionId, string Kind, string TitleAr, string BodyAr,
    bool Actionable, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);
