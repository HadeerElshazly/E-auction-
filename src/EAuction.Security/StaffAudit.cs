using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace EAuction.Security;

/// <summary>
/// Who is making this request, as an audit entry needs them.
///
/// Three values, all taken from the token and the connection and none of them from
/// the request body. That is the whole point of the type: a service that recorded a
/// name out of a body would produce an audit trail saying whatever the audited
/// person typed.
/// </summary>
public sealed record StaffActor(Guid Subject, string Roles, string? SourceAddress)
{
    /// <summary>The account's display name, from the token — for the audit trail's reader.</summary>
    public string? Name { get; init; }
}

/// <summary>
/// Reads the actor out of a request.
///
/// Here rather than in each service because every caller is a Keycloak token and
/// pulling a subject, its roles and the peer address out of one is the same work
/// every time. It deliberately stops at the actor and does not build the outbox row
/// — that needs <c>EAuction.Outbox</c>, which carries EF Core and Kafka, and the
/// two services that only want JWT validation (the document service and the query
/// BFF) should not acquire either.
/// </summary>
public static class StaffAudit
{
    public static StaffActor ActorOf(HttpContext http) => new(
        http.User.SubjectId() ?? Guid.Empty,
        RolesOf(http.User),
        SourceOf(http))
    {
        Name = http.User.FindFirst("name")?.Value
               ?? http.User.FindFirst("preferred_username")?.Value,
    };

    /// <summary>
    /// The roles the token carried, ordered so two entries for the same person are
    /// comparable as strings.
    ///
    /// Recorded rather than looked up later, because roles change: an auditor
    /// asking "was this person entitled to approve that?" needs what was true then,
    /// not what is true now.
    /// </summary>
    private static string RolesOf(ClaimsPrincipal principal) =>
        string.Join(
            ",",
            principal.FindAll(ClaimTypes.Role)
                .Select(c => c.Value)
                .Distinct()
                .OrderBy(role => role, StringComparer.Ordinal));

    /// <summary>
    /// The caller's address, as far as it can be trusted.
    ///
    /// The connection's own peer, never <c>X-Forwarded-For</c>: unless ASP.NET's
    /// forwarded-headers middleware has been configured to accept it from a known
    /// proxy, that header is whatever the client wrote. An audit trail carrying a
    /// self-declared address is worse than one carrying none, because it reads as
    /// evidence. Configuring the middleware is the deployment's job, and then this
    /// returns the forwarded value without changing.
    /// </summary>
    private static string? SourceOf(HttpContext http) =>
        http.Connection.RemoteIpAddress?.ToString();
}
