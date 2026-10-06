namespace EAuction.Documents;

/// <summary>
/// Who may read a document. Decided when it is uploaded, by the service that
/// uploads it, because that is the only moment anyone knows what the file is for.
/// </summary>
public enum DocumentAccess
{
    /// <summary>
    /// Anyone, with no token at all. The auction's cover image: it is on the
    /// public catalogue, which an anonymous citizen reads before registering.
    /// </summary>
    Public = 0,

    /// <summary>
    /// The person who uploaded it, or staff. The bank guarantee — a bidder
    /// uploads it and an administrator verifies it, and nobody else has any
    /// business with a document that names a citizen's bank.
    /// </summary>
    Private = 1,

    /// <summary>
    /// Nobody, by identity alone. Only a grant opens it.
    ///
    /// For documents whose readers are decided by a rule this service cannot
    /// evaluate: كراسة الشروط, which a bidder may read once they have paid for it,
    /// and the award letter, which belongs to whoever won. See
    /// <see cref="EAuction.Security.DocumentGrants"/>.
    /// </summary>
    Restricted = 2,
}

/// <summary>
/// How a stored classification is read back.
///
/// Its own function because the fallback matters more than the parse: an object
/// whose access this build does not recognise — written by a previous version, or
/// by a later one — must read as <see cref="DocumentAccess.Restricted"/>. Failing
/// open on an unknown value is how a bank guarantee becomes anonymously readable
/// after a deployment that renamed an enum member, and the only sign would be the
/// absence of a complaint.
/// </summary>
public static class DocumentAccessValues
{
    public static DocumentAccess Parse(string? stored) =>
        // IsDefined as well as TryParse, because TryParse happily accepts a number
        // outside the enum and hands back (DocumentAccess)99 — and accepts "0",
        // which is Public. Stored values are always names, so a number is never
        // something to honour.
        Enum.TryParse<DocumentAccess>(stored, ignoreCase: true, out var parsed)
        && Enum.IsDefined(parsed)
        && !char.IsAsciiDigit(stored![0])
            ? parsed
            : DocumentAccess.Restricted;
}

/// <summary>
/// What is known about a stored document, without its bytes.
///
/// Held as object metadata on the object itself rather than in a database. The
/// document service is then stateless and needs no migration, and a file and the
/// facts about it cannot drift apart or be restored out of step — which they can
/// when the bytes are in object storage and the row is in Postgres.
/// </summary>
public sealed record DocumentMetadata
{
    public required Guid Id { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required long SizeBytes { get; init; }
    public required DocumentAccess Access { get; init; }

    /// <summary>Who uploaded it. The subject of <see cref="DocumentAccess.Private"/>.</summary>
    public required Guid OwnerSubject { get; init; }

    /// <summary>
    /// SHA-256 of the bytes, computed on the way in.
    ///
    /// A signed award letter and a bank guarantee are both evidence, and evidence
    /// that cannot be shown to be unaltered since upload is weaker evidence. It is
    /// returned on upload so the uploader can check it, and on every read.
    /// </summary>
    public required string Sha256 { get; init; }

    public required DateTimeOffset UploadedAt { get; init; }
}

/// <summary>A document's bytes, with the facts about them.</summary>
public sealed record DocumentContent(DocumentMetadata Metadata, Stream Bytes) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Bytes.DisposeAsync();
}

/// <summary>
/// The object store, behind a seam (D-17).
///
/// MinIO is what compose runs and what the proposal names, but the on-prem
/// environment is not yet known and could just as easily hand over a NetApp
/// StorageGRID or an Azure Blob endpoint. Everything above this interface is
/// unaffected either way.
/// </summary>
public interface IDocumentStore
{
    Task PutAsync(DocumentMetadata metadata, Stream bytes, CancellationToken ct);

    /// <summary>Null when there is no such document. The caller turns that into a 404.</summary>
    Task<DocumentMetadata?> HeadAsync(Guid id, CancellationToken ct);

    Task<DocumentContent?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Creates the bucket if it is missing. Called once at startup.</summary>
    Task EnsureReadyAsync(CancellationToken ct);
}
