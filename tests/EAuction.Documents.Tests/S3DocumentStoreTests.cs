using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace EAuction.Documents.Tests;

/// <summary>
/// The S3 store against a real S3 endpoint over HTTP.
///
/// Every other test in this assembly runs on <see cref="InMemoryDocumentStore"/>,
/// which means the client configuration — path-style addressing, how metadata is
/// encoded into headers, what a missing object looks like — is exercised by nothing
/// at all. Those are precisely the things an in-memory stand-in cannot get wrong
/// and a real deployment can.
///
/// <para>
/// Skipped without <c>S3_ENDPOINT</c>, the same arrangement as the Kafka tests, so
/// the suite still runs on a machine with no object store. MinIO is what compose
/// provides; anything that speaks S3 will do, and what these assert is the protocol
/// surface this service depends on rather than any one implementation's behaviour.
/// </para>
/// </summary>
public class S3DocumentStoreTests
{
    /// <summary>A deadline, so a wrong endpoint fails rather than hangs the suite.</summary>
    private static readonly CancellationTokenSource Timeout = new(TimeSpan.FromSeconds(30));

    private static CancellationToken Ct => Timeout.Token;

    private static S3DocumentStore Store() => new(new S3Options
    {
        ServiceUrl = Environment.GetEnvironmentVariable("S3_ENDPOINT")!,
        AccessKey = Environment.GetEnvironmentVariable("S3_ACCESS_KEY") ?? "eauction",
        SecretKey = Environment.GetEnvironmentVariable("S3_SECRET_KEY") ?? "eauction123",

        // Its own bucket per run, so a repeated run is not testing against the
        // objects the last one left.
        Bucket = "eauction-test-" + Guid.NewGuid().ToString("N")[..12],
    });

    private static DocumentMetadata Metadata(
        string fileName, long size, string sha, DocumentAccess access, Guid owner) => new()
    {
        Id = Guid.NewGuid(),
        FileName = fileName,
        ContentType = "application/pdf",
        SizeBytes = size,
        Access = access,
        OwnerSubject = owner,
        Sha256 = sha,
        UploadedAt = DateTimeOffset.UtcNow,
    };

    [RequiresS3Fact]
    public async Task A_document_survives_a_round_trip_with_its_metadata()
    {
        using var store = Store();
        await store.EnsureReadyAsync(Ct);

        var body = Encoding.UTF8.GetBytes("كراسة الشروط — مخطط السعيد");
        var sha = Convert.ToHexString(SHA256.HashData(body));
        var owner = Guid.NewGuid();

        // An Arabic filename, because S3 user metadata is ASCII-only and this is
        // the case that silently turns a booklet's name into question marks.
        var metadata = Metadata(
            "كراسة الشروط.pdf", body.Length, sha, DocumentAccess.Restricted, owner);

        await store.PutAsync(metadata, new MemoryStream(body), Ct);

        var head = await store.HeadAsync(metadata.Id, Ct);

        Assert.NotNull(head);
        Assert.Equal("كراسة الشروط.pdf", head.FileName);
        Assert.Equal(DocumentAccess.Restricted, head.Access);
        Assert.Equal(owner, head.OwnerSubject);
        Assert.Equal(sha, head.Sha256);
        Assert.Equal(body.Length, head.SizeBytes);

        await using var got = await store.GetAsync(metadata.Id, Ct);
        Assert.NotNull(got);

        using var read = new MemoryStream();
        await got.Bytes.CopyToAsync(read, Ct);

        Assert.Equal(body, read.ToArray());
        Assert.Equal("كراسة الشروط.pdf", got.Metadata.FileName);
    }

    [RequiresS3Fact]
    public async Task A_missing_document_is_null_rather_than_an_exception()
    {
        // The service turns null into a 404. An exception here would be a 500 on a
        // public endpoint, reachable by typing a GUID.
        using var store = Store();
        await store.EnsureReadyAsync(Ct);

        Assert.Null(await store.HeadAsync(Guid.NewGuid(), Ct));
        Assert.Null(await store.GetAsync(Guid.NewGuid(), Ct));
    }

    [RequiresS3Fact]
    public async Task Preparing_the_bucket_twice_is_not_an_error()
    {
        // Called on every start and on every readiness probe.
        using var store = Store();
        await store.EnsureReadyAsync(Ct);
        await store.EnsureReadyAsync(Ct);
    }

    [RequiresS3Fact]
    public async Task A_document_larger_than_one_buffer_comes_back_byte_for_byte()
    {
        // A كراسة الشروط with site plans in it is tens of megabytes. Small enough
        // here to be quick, large enough to cross the copy buffer several times.
        using var store = Store();
        await store.EnsureReadyAsync(Ct);

        var body = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);
        var sha = Convert.ToHexString(SHA256.HashData(body));
        var metadata = Metadata("plans.pdf", body.Length, sha, DocumentAccess.Public, Guid.NewGuid());

        await store.PutAsync(metadata, new MemoryStream(body), Ct);

        await using var got = await store.GetAsync(metadata.Id, Ct);
        using var read = new MemoryStream();
        await got!.Bytes.CopyToAsync(read, Ct);

        Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(read.ToArray())));
    }

    [Theory]
    [InlineData("Public", DocumentAccess.Public)]
    [InlineData("public", DocumentAccess.Public)]
    [InlineData("Private", DocumentAccess.Private)]
    [InlineData("Restricted", DocumentAccess.Restricted)]
    // Anything else, including a value written by a version this build has never
    // seen: Restricted. Failing open on an unknown classification is how a bank
    // guarantee becomes anonymously readable after a deployment that renamed an
    // enum member — and the only sign would be the absence of a complaint.
    [InlineData("SomethingFromTheFuture", DocumentAccess.Restricted)]
    [InlineData("", DocumentAccess.Restricted)]
    [InlineData(null, DocumentAccess.Restricted)]
    // Numbers, because Enum.TryParse accepts them: "99" came back as
    // (DocumentAccess)99, and "0" came back as Public — an unauthenticated read of
    // anything stored with a numeric classification. Stored values are always
    // names, so a number is never something to honour.
    [InlineData("99", DocumentAccess.Restricted)]
    [InlineData("0", DocumentAccess.Restricted)]
    [InlineData("1", DocumentAccess.Restricted)]
    [InlineData("-1", DocumentAccess.Restricted)]
    public void A_stored_classification_is_never_read_more_openly_than_it_was_written(
        string? stored, DocumentAccess expected)
    {
        Assert.Equal(expected, DocumentAccessValues.Parse(stored));
    }
}

/// <summary>
/// Skips without <c>S3_ENDPOINT</c>, the same arrangement as the Kafka tests, so
/// the suite still runs on a machine with no object store.
/// </summary>
public sealed class RequiresS3FactAttribute : FactAttribute
{
    public RequiresS3FactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("S3_ENDPOINT")))
            Skip = "S3_ENDPOINT is not set.";
    }
}
