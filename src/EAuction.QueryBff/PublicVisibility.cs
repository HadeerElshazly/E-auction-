using System.Text.Json;
using EAuction.Core;

namespace EAuction.QueryBff;

/// <summary>
/// «إعدادات العرض للزوار» as currently set. Until the topic says otherwise — a new
/// platform, or before the replay reaches it — the defaults hold, which are the
/// stricter reading of the requirements: only the schedule is open.
/// </summary>
public sealed class PublicVisibilityState
{
    private volatile PublicVisibilityPolicy _current = PublicVisibilityPolicy.Default;

    public PublicVisibilityPolicy Current => _current;

    public void Set(PublicVisibilityPolicy policy) => _current = policy;

    /// <summary>
    /// The policy that applies to this caller: null for anyone signed in — a bidder
    /// or a member of staff sees the whole public auction — and the setting for a
    /// visitor.
    /// </summary>
    public PublicVisibilityPolicy? For(HttpContext http) =>
        http.User.Identity?.IsAuthenticated == true ? null : _current;
}

/// <summary>Replays <c>platform.settings</c> into <see cref="PublicVisibilityState"/>.</summary>
public sealed class PublicVisibilityConsumer(
    PublicVisibilityState state, IEventStream events, ILogger<PublicVisibilityConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var record in events.ReadAsync(Topics.Settings, ct))
            {
                if (record.EventType != "PublicVisibilityChanged") continue;
                try
                {
                    var p = JsonSerializer.Deserialize<Payload>(record.Payload, Json);
                    if (p?.Public is null) continue;
                    state.Set(PublicVisibilityPolicy.From(p.Public));
                    logger.LogInformation("Visitor visibility updated: hidden {Hidden}.",
                        string.Join(",", state.Current.Hidden()));
                }
                catch (JsonException e)
                {
                    logger.LogError(e, "Unreadable visibility setting at offset {Offset}.", record.Offset);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private sealed record Payload(Dictionary<string, bool>? Public);
}
