using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EAuction.Core;
using EAuction.Documents;
using EAuction.Outbox;
using EAuction.Security;
using EAuction.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EAuction.Documents.Tests;

/// <summary>
/// What gets recorded when somebody opens a file that is not theirs (D-44).
///
/// The narrow rule, and the reason it is narrow: a trail with every cover-image
/// fetch in it is a trail nobody reads, and the one entry that mattered — an
/// administrator opening a citizen's bank guarantee — arrives in the same list as
/// ten thousand catalogue hits.
/// </summary>
public class DocumentAuditTests : IDisposable
{
    private static readonly byte[] GrantKey = DocumentGrants.NewKey();

    /// <summary>
    /// The seam. In deployment this is Kafka; here it collects what the service
    /// published so the test can read it back.
    /// </summary>
    private readonly InMemoryEventStream _events = new();

    private readonly AuthenticatedFactory<Program> _factory;

    public DocumentAuditTests()
    {
        _factory = new AuthenticatedFactory<Program>
        {
            Settings = new Dictionary<string, string?>
            {
                ["Documents:GrantKeyHex"] = Convert.ToHexString(GrantKey),
            },
            ConfigureServices = services =>
                services.AddSingleton<IEventStream>(_events),
        };
    }

    public void Dispose() => _factory.Dispose();

    private static readonly Guid Sara = Guid.NewGuid();
    private static readonly Guid AdminUser = Guid.NewGuid();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private HttpClient As(Guid subject, params string[] roles) =>
        _factory.CreateClient().WithToken(TestJwt.For(subject, roles));

    private async Task<DocumentMetadata> UploadAsync(
        HttpClient client, string access, string fileName = "guarantee.pdf")
    {
        using var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(Encoding.UTF8.GetBytes("a bank guarantee"));
        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");

        form.Add(bytes, "file", fileName);
        form.Add(new StringContent(access), "access");

        var response = await client.PostAsync("/documents", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<DocumentMetadata>(Json))!;
    }

    /// <summary>
    /// What reached the topic. Bounded, because an empty topic and a publish that
    /// has not landed yet are indistinguishable by waiting — the assertions that
    /// expect nothing cannot be written as a timeout.
    /// </summary>
    private async Task<List<StaffActionRecorded>> RecordedAsync(int expected)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var entries = new List<StaffActionRecorded>();

        if (expected == 0)
        {
            // The in-memory stream assigns offsets synchronously on publish, so by
            // the time the request has returned anything it was going to say is
            // already there. -1 is an empty topic.
            Assert.Equal(-1, await _events.LatestOffsetAsync(Topics.StaffActions, cts.Token));
            return entries;
        }

        await foreach (var record in _events.ReadAsync(Topics.StaffActions, cts.Token))
        {
            entries.Add(JsonSerializer.Deserialize<StaffActionRecorded>(
                record.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);

            if (entries.Count >= expected) break;
        }

        return entries;
    }

    [Fact]
    public async Task An_administrator_reading_a_citizens_guarantee_is_recorded()
    {
        var guarantee = await UploadAsync(As(Sara, Roles.Bidder), "Private");

        var response = await As(AdminUser, Roles.AuctionAdmin)
            .GetAsync($"/documents/{guarantee.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = Assert.Single(await RecordedAsync(1));

        Assert.Equal("ReadDocument", entry.Action);
        Assert.Equal(AuditSubject.Document(guarantee.Id), entry.Subject);
        Assert.Equal(AdminUser, entry.ActorSubject);
        Assert.Contains(Roles.AuctionAdmin, entry.ActorRoles);

        // Whose file it was, so the entry is readable without a second lookup
        // against a service this one knows nothing about.
        Assert.Contains(Sara.ToString(), entry.Details);
        Assert.Contains("guarantee.pdf", entry.Details);
    }

    [Fact]
    public async Task Metadata_is_recorded_too()
    {
        // Less than the bytes and not nothing: it names the uploader, the file and
        // its size, which is enough to confirm that a particular citizen filed a
        // bank guarantee at all.
        var guarantee = await UploadAsync(As(Sara, Roles.Bidder), "Private");

        var response = await As(AdminUser, Roles.AuctionAdmin)
            .GetAsync($"/documents/{guarantee.Id}/metadata");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = Assert.Single(await RecordedAsync(1));
        Assert.Equal("ReadDocumentMetadata", entry.Action);
    }

    [Fact]
    public async Task A_bidder_reading_their_own_file_is_not_recorded()
    {
        // A citizen using the product. Recording it would bury the entries that
        // matter under the ones that do not.
        var guarantee = await UploadAsync(As(Sara, Roles.Bidder), "Private");

        var response = await As(Sara, Roles.Bidder).GetAsync($"/documents/{guarantee.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Empty(await RecordedAsync(0));
    }

    [Fact]
    public async Task A_public_document_is_not_recorded()
    {
        // The auction's cover image, fetched by every visitor to the catalogue.
        var cover = await UploadAsync(As(AdminUser, Roles.AuctionAdmin), "Public", "cover.jpg");

        Assert.Equal(
            HttpStatusCode.OK,
            (await _factory.CreateClient().GetAsync($"/documents/{cover.Id}")).StatusCode);

        Assert.Empty(await RecordedAsync(0));
    }

    [Fact]
    public async Task A_refused_read_is_not_recorded_as_a_read()
    {
        // An award letter is Restricted: no role opens it without a grant. The 404
        // comes before any record, so a refusal cannot be mistaken for a staff
        // member having seen the file.
        var letter = await UploadAsync(
            As(AdminUser, Roles.AwardCommittee), "Restricted", "letter.pdf");

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await As(Guid.NewGuid(), Roles.AuctionAdmin)
                .GetAsync($"/documents/{letter.Id}")).StatusCode);

        Assert.Empty(await RecordedAsync(0));
    }

    [Fact]
    public async Task A_read_with_a_grant_is_recorded()
    {
        // The winner opening their own award letter — which the service that minted
        // the grant says they are entitled to, and which this service has no way of
        // judging. Recorded because the file is not theirs by ownership: it was
        // uploaded by the committee, and a letter read by someone other than the
        // uploader is exactly what should leave a trace.
        var letter = await UploadAsync(
            As(AdminUser, Roles.AwardCommittee), "Restricted", "letter.pdf");

        var grant = DocumentGrants.Mint(GrantKey, letter.Id, Sara, DateTimeOffset.UtcNow);

        var response = await As(Sara, Roles.Bidder)
            .GetAsync($"/documents/{letter.Id}?grant={Uri.EscapeDataString(grant)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = Assert.Single(await RecordedAsync(1));
        Assert.Equal(Sara, entry.ActorSubject);
        Assert.Contains("Restricted", entry.Details);
    }

    [Fact]
    public async Task The_read_still_succeeds_when_the_audit_topic_will_not_take_it()
    {
        // The asymmetry this service accepts on purpose. The other two producers
        // write their audit row in the transaction that makes the change, so
        // neither can exist alone; here there is no transaction to join, and
        // refusing a winner their award letter because a broker is down would be a
        // worse service and a worse audit story — the pressure would be to turn the
        // auditing off.
        using var factory = new AuthenticatedFactory<Program>
        {
            Settings = new Dictionary<string, string?>
            {
                ["Documents:GrantKeyHex"] = Convert.ToHexString(GrantKey),
            },
            ConfigureServices = services =>
                services.AddSingleton<IEventStream>(new BrokenEventStream()),
        };

        var uploader = factory.CreateClient().WithToken(TestJwt.For(Sara, Roles.Bidder));

        using var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(Encoding.UTF8.GetBytes("a bank guarantee"));
        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(bytes, "file", "guarantee.pdf");
        form.Add(new StringContent("Private"), "access");

        var created = await uploader.PostAsync("/documents", form);
        var guarantee = (await created.Content.ReadFromJsonAsync<DocumentMetadata>(Json))!;

        var response = await factory.CreateClient()
            .WithToken(TestJwt.For(AdminUser, Roles.AuctionAdmin))
            .GetAsync($"/documents/{guarantee.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("a bank guarantee", await response.Content.ReadAsStringAsync());
    }

    /// <summary>A stream that refuses everything, for the degradation test above.</summary>
    private sealed class BrokenEventStream : IEventStream
    {
        public Task PublishAsync(
            string topic, string key, string payload, string eventType, CancellationToken ct) =>
            Task.FromException(new InvalidOperationException("No broker."));

        public IAsyncEnumerable<StreamEvent> ReadAsync(string topic, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<long> LatestOffsetAsync(string topic, CancellationToken ct) =>
            Task.FromException<long>(new InvalidOperationException("No broker."));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
