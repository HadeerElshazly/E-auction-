using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EAuction.Documents;
using EAuction.Security;
using EAuction.TestSupport;
using Xunit;

namespace EAuction.Documents.Tests;

/// <summary>
/// Who may read which document.
///
/// This is the whole risk in a document service. The files are a bank guarantee
/// naming a citizen's account, a signed خطاب ترسية, and the terms booklet a bidder
/// paid for — so the interesting cases are all refusals, and a test suite that only
/// proved uploads work would prove nothing worth proving.
/// </summary>
public class DocumentAccessTests : IDisposable
{
    private static readonly byte[] GrantKey = DocumentGrants.NewKey();

    private readonly AuthenticatedFactory<Program> _factory = new()
    {
        Settings = new Dictionary<string, string?>
        {
            ["Documents:GrantKeyHex"] = Convert.ToHexString(GrantKey),
        },
    };

    public void Dispose() => _factory.Dispose();

    private static readonly Guid Sara = Guid.NewGuid();
    private static readonly Guid Khalid = Guid.NewGuid();
    private static readonly Guid AdminUser = Guid.NewGuid();
    private static readonly Guid CommitteeUser = Guid.NewGuid();

    private HttpClient As(Guid subject, params string[] roles) =>
        _factory.CreateClient().WithToken(TestJwt.For(subject, roles));

    private HttpClient Anonymous() => _factory.CreateClient();

    /// <summary>
    /// The service writes enums as names, so a client that reads them as numbers
    /// cannot parse its own responses. Matching the server here rather than
    /// loosening the server, because "Public" in a payload is readable by a person
    /// looking at a log and 0 is not.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Uploads a file and returns what the service said about it.</summary>
    private async Task<DocumentMetadata> UploadAsync(
        HttpClient client, string access, string fileName = "booklet.pdf",
        string body = "the terms of sale")
    {
        using var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");

        form.Add(bytes, "file", fileName);
        form.Add(new StringContent(access), "access");

        var response = await client.PostAsync("/documents", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<DocumentMetadata>(Json))!;
    }

    // --- public ------------------------------------------------------------

    [Fact]
    public async Task A_public_document_opens_with_no_token_at_all()
    {
        // The auction's cover image. An anonymous citizen reads the catalogue
        // before they register, so this one cannot need a token.
        var cover = await UploadAsync(
            As(AdminUser, Roles.AuctionAdmin), "Public", "cover.jpg");

        var response = await Anonymous().GetAsync($"/documents/{cover.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("the terms of sale", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_bidder_cannot_publish()
    {
        // A bidder uploads one thing: their bank guarantee. If they could ask for
        // it to be world-readable, the mistake would be invisible — the upload
        // succeeds either way and nothing complains until someone finds the file.
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("my bank details")), "file", "g.pdf");
        form.Add(new StringContent("Public"), "access");

        var response = await As(Sara, Roles.Bidder).PostAsync("/documents", form);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- private -----------------------------------------------------------

    [Fact]
    public async Task A_private_document_opens_to_its_owner_and_to_staff_and_to_nobody_else()
    {
        // The bank guarantee: Sara uploads it, an administrator verifies it.
        var guarantee = await UploadAsync(As(Sara, Roles.Bidder), "Private", "guarantee.pdf");

        Assert.Equal(Sara, guarantee.OwnerSubject);

        Assert.Equal(HttpStatusCode.OK,
            (await As(Sara, Roles.Bidder).GetAsync($"/documents/{guarantee.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await As(AdminUser, Roles.AuctionAdmin)
                .GetAsync($"/documents/{guarantee.Id}")).StatusCode);

        // Another bidder, and an anonymous caller.
        Assert.Equal(HttpStatusCode.NotFound,
            (await As(Khalid, Roles.Bidder).GetAsync($"/documents/{guarantee.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Anonymous().GetAsync($"/documents/{guarantee.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_refusal_is_a_404_so_it_does_not_confirm_the_document_exists()
    {
        // These ids travel: they are in the auction's public event, in award
        // letters, in support tickets. A 403 would answer "yes, that document is
        // real and it is not yours", which is a fact worth not confirming.
        var guarantee = await UploadAsync(As(Sara, Roles.Bidder), "Private");

        var refused = await As(Khalid, Roles.Bidder).GetAsync($"/documents/{guarantee.Id}");
        var absent = await As(Khalid, Roles.Bidder).GetAsync($"/documents/{Guid.NewGuid()}");

        Assert.Equal(absent.StatusCode, refused.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
    }

    // --- restricted, and the grant -----------------------------------------

    [Fact]
    public async Task A_restricted_document_opens_to_nobody_by_role_not_even_an_administrator()
    {
        // The award letter. "Staff can read it" is a far larger set of people than
        // anyone would choose if asked, and a signed خطاب ترسية is the winner's
        // instrument — so no role opens this, including the committee's own.
        var letter = await UploadAsync(
            As(CommitteeUser, Roles.AwardCommittee), "Restricted", "letter.pdf");

        foreach (var caller in new[]
                 {
                     As(AdminUser, Roles.AuctionAdmin),
                     As(CommitteeUser, Roles.AwardCommittee),
                     As(Sara, Roles.Bidder),
                     Anonymous(),
                 })
        {
            Assert.Equal(HttpStatusCode.NotFound,
                (await caller.GetAsync($"/documents/{letter.Id}")).StatusCode);
        }
    }

    [Fact]
    public async Task A_grant_opens_a_restricted_document_for_the_one_person_it_names()
    {
        var letter = await UploadAsync(As(CommitteeUser, Roles.AwardCommittee), "Restricted");

        var forSara = DocumentGrants.Mint(GrantKey, letter.Id, Sara, DateTimeOffset.UtcNow);

        Assert.Equal(HttpStatusCode.OK,
            (await As(Sara, Roles.Bidder)
                .GetAsync($"/documents/{letter.Id}?grant={forSara}")).StatusCode);

        // The same grant, presented by someone else. It names Sara in the signed
        // part, so a forwarded link is useless to whoever it reaches.
        Assert.Equal(HttpStatusCode.NotFound,
            (await As(Khalid, Roles.Bidder)
                .GetAsync($"/documents/{letter.Id}?grant={forSara}")).StatusCode);

        // And with no token at all, since a grant is bound to a subject and an
        // anonymous caller has none to bind it to.
        Assert.Equal(HttpStatusCode.NotFound,
            (await Anonymous().GetAsync($"/documents/{letter.Id}?grant={forSara}")).StatusCode);
    }

    [Fact]
    public async Task A_grant_for_one_document_does_not_open_another()
    {
        var letter = await UploadAsync(As(CommitteeUser, Roles.AwardCommittee), "Restricted");
        var booklet = await UploadAsync(As(AdminUser, Roles.AuctionAdmin), "Restricted");

        var forLetter = DocumentGrants.Mint(GrantKey, letter.Id, Sara, DateTimeOffset.UtcNow);

        Assert.Equal(HttpStatusCode.NotFound,
            (await As(Sara, Roles.Bidder)
                .GetAsync($"/documents/{booklet.Id}?grant={forLetter}")).StatusCode);
    }

    [Fact]
    public async Task A_grant_signed_with_the_wrong_key_opens_nothing()
    {
        var letter = await UploadAsync(As(CommitteeUser, Roles.AwardCommittee), "Restricted");

        var forged = DocumentGrants.Mint(
            DocumentGrants.NewKey(), letter.Id, Sara, DateTimeOffset.UtcNow);

        Assert.Equal(HttpStatusCode.NotFound,
            (await As(Sara, Roles.Bidder)
                .GetAsync($"/documents/{letter.Id}?grant={forged}")).StatusCode);
    }

    // --- the bytes, and what is said about them ----------------------------

    [Fact]
    public async Task The_hash_is_of_what_was_stored_and_comes_back_on_every_read()
    {
        // A signed award letter and a bank guarantee are both evidence, and
        // evidence that cannot be shown to be unaltered since upload is weaker
        // evidence.
        const string body = "خطاب ترسية";
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

        var letter = await UploadAsync(
            As(CommitteeUser, Roles.AwardCommittee), "Public", "letter.pdf", body);

        Assert.Equal(expected, letter.Sha256);

        var response = await Anonymous().GetAsync($"/documents/{letter.Id}");
        Assert.Equal(expected, response.Headers.GetValues("X-Document-SHA256").Single());
    }

    [Fact]
    public async Task An_arabic_filename_survives_the_round_trip()
    {
        // كراسة الشروط is what the file is called. Renaming it to document.pdf is
        // not a security measure, it is a worse product — and S3 user metadata is
        // ASCII-only, so this is a real thing to get wrong rather than a formality.
        var booklet = await UploadAsync(
            As(AdminUser, Roles.AuctionAdmin), "Public", "كراسة الشروط.pdf");

        Assert.Equal("كراسة الشروط.pdf", booklet.FileName);

        var metadata = await Anonymous()
            .GetFromJsonAsync<JsonElement>($"/documents/{booklet.Id}/metadata");

        Assert.Equal("كراسة الشروط.pdf", metadata.GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task An_uploaded_file_is_never_rendered_in_place()
    {
        // Serving an uploaded file inline from this origin means an uploaded .html
        // — or a .pdf the browser decides is really HTML — runs as a page on a
        // domain the platform's own cookies belong to. That is stored cross-site
        // scripting with an upload form for a delivery mechanism, and it is the one
        // thing a document service gets wrong that costs the whole platform.
        using var form = new MultipartFormDataContent();
        var html = new ByteArrayContent(
            Encoding.UTF8.GetBytes("<script>alert(document.cookie)</script>"));
        html.Headers.ContentType = new MediaTypeHeaderValue("text/html");

        form.Add(html, "file", "innocent.html");
        form.Add(new StringContent("Public"), "access");

        var created = await As(AdminUser, Roles.AuctionAdmin).PostAsync("/documents", form);
        var uploaded = (await created.Content.ReadFromJsonAsync<DocumentMetadata>(Json))!;

        var response = await Anonymous().GetAsync($"/documents/{uploaded.Id}");

        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Theory]
    // The name reaches a Content-Disposition header, and a CR/LF in it would let
    // the uploader append headers of their own to the download response.
    [InlineData("ok.pdf\r\nX-Injected: yes", "ok.pdfX-Injected: yes")]
    [InlineData("ok\u0000.pdf", "ok.pdf")]
    // A path, so a traversal attempt loses its path before anything else sees it.
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Windows\\system32\\x.dll", "x.dll")]
    // A quote would close the header's own quoting early.
    [InlineData("a\"b.pdf", "ab.pdf")]
    // Nothing usable left, and nothing supplied: a name either way.
    [InlineData("///", "document")]
    [InlineData("", "document")]
    [InlineData(null, "document")]
    // Arabic survives, because that is what these files are actually called.
    [InlineData("كراسة الشروط.pdf", "كراسة الشروط.pdf")]
    public void A_filename_cannot_write_response_headers(string? supplied, string expected)
    {
        // Called directly rather than over HTTP: .NET's own multipart client
        // refuses to send most of these, and an attacker uses curl.
        Assert.Equal(expected, DocumentNames.Safe(supplied));
    }

    [Fact]
    public void A_very_long_filename_is_cut_rather_than_rejected()
    {
        // Rejecting it would fail an upload over something cosmetic. The limit is
        // there so a name cannot become a header on its own.
        var name = new string('ا', 5_000) + ".pdf";

        Assert.Equal(DocumentNames.MaxLength, DocumentNames.Safe(name).Length);
    }

    [Fact]
    public async Task A_sanitised_filename_is_what_reaches_the_download_header()
    {
        // The unit cases above prove the function; this proves the service calls it.
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("x")), "file", "../../etc/passwd");
        form.Add(new StringContent("Public"), "access");

        var created = await As(AdminUser, Roles.AuctionAdmin).PostAsync("/documents", form);
        var uploaded = (await created.Content.ReadFromJsonAsync<DocumentMetadata>(Json))!;

        Assert.Equal("passwd", uploaded.FileName);

        var response = await Anonymous().GetAsync($"/documents/{uploaded.Id}");
        Assert.Equal("passwd", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
    }

    [Fact]
    public async Task An_upload_with_no_classification_is_restricted_not_public()
    {
        // Failing open on a missing field would mean a caller that forgot it
        // published a bank guarantee, and the upload succeeds either way so
        // nothing would say so.
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("secret")), "file", "x.pdf");

        var created = await As(AdminUser, Roles.AuctionAdmin).PostAsync("/documents", form);
        var uploaded = (await created.Content.ReadFromJsonAsync<DocumentMetadata>(Json))!;

        Assert.Equal(DocumentAccess.Restricted, uploaded.Access);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Anonymous().GetAsync($"/documents/{uploaded.Id}")).StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_cannot_upload()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("x")), "file", "x.pdf");

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Anonymous().PostAsync("/documents", form)).StatusCode);
    }
}
