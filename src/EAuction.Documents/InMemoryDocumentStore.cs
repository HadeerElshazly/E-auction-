using System.Collections.Concurrent;

namespace EAuction.Documents;

/// <summary>
/// Documents in memory, for development and tests.
///
/// Mirrors the one behaviour of the S3 store that the rest of the service depends
/// on — a missing document is null, not an exception — and nothing else. It is not
/// a stand-in for durability: <c>Program.cs</c> refuses to use it in Production,
/// because a deployment that accepted a signed award letter and lost it on the next
/// pod restart would look like it was working.
/// </summary>
public sealed class InMemoryDocumentStore : IDocumentStore
{
    private readonly ConcurrentDictionary<Guid, (DocumentMetadata Metadata, byte[] Bytes)> _objects
        = new();

    public int Count => _objects.Count;

    public Task EnsureReadyAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task PutAsync(DocumentMetadata metadata, Stream bytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await bytes.CopyToAsync(buffer, ct);
        _objects[metadata.Id] = (metadata, buffer.ToArray());
    }

    public Task<DocumentMetadata?> HeadAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(_objects.TryGetValue(id, out var found) ? found.Metadata : null);

    public Task<DocumentContent?> GetAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(_objects.TryGetValue(id, out var found)
            ? new DocumentContent(found.Metadata, new MemoryStream(found.Bytes, writable: false))
            : null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
