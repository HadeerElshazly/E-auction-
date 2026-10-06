using EAuction.Security;
using Xunit;

namespace EAuction.Documents.Tests;

/// <summary>
/// The grant itself, apart from HTTP.
///
/// It is the whole of the authorisation for the two documents that matter most —
/// the terms booklet and the signed award letter — so each of the things it has to
/// refuse gets its own case. A grant that could be re-pointed at another person, or
/// another document, or used after it expired, would be a bearer token for someone
/// else's evidence.
/// </summary>
public class DocumentGrantTests
{
    private static readonly byte[] Key = DocumentGrants.NewKey();

    private static readonly Guid Document = Guid.NewGuid();
    private static readonly Guid Sara = Guid.NewGuid();
    private static readonly Guid Khalid = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_fresh_grant_verifies_for_the_person_and_document_it_names()
    {
        var grant = DocumentGrants.Mint(Key, Document, Sara, Now);

        Assert.True(DocumentGrants.Verify(Key, grant, Document, Sara, Now));
    }

    [Fact]
    public void It_does_not_verify_for_another_person()
    {
        // The subject is inside the signature, not merely written next to it, so
        // editing the part of the string that says who it is for breaks it.
        var grant = DocumentGrants.Mint(Key, Document, Sara, Now);

        Assert.False(DocumentGrants.Verify(Key, grant, Document, Khalid, Now));
    }

    [Fact]
    public void It_does_not_verify_for_another_document()
    {
        var grant = DocumentGrants.Mint(Key, Document, Sara, Now);

        Assert.False(DocumentGrants.Verify(Key, grant, Guid.NewGuid(), Sara, Now));
    }

    [Fact]
    public void It_stops_working_when_it_expires()
    {
        var grant = DocumentGrants.Mint(Key, Document, Sara, Now);

        Assert.True(DocumentGrants.Verify(
            Key, grant, Document, Sara, Now.Add(DocumentGrants.Lifetime).AddSeconds(-1)));

        Assert.False(DocumentGrants.Verify(
            Key, grant, Document, Sara, Now.Add(DocumentGrants.Lifetime).AddSeconds(1)));
    }

    [Fact]
    public void It_does_not_verify_under_a_different_key()
    {
        var grant = DocumentGrants.Mint(DocumentGrants.NewKey(), Document, Sara, Now);

        Assert.False(DocumentGrants.Verify(Key, grant, Document, Sara, Now));
    }

    [Fact]
    public void A_grant_cannot_be_given_a_later_expiry_by_editing_it()
    {
        // The obvious forgery: take a real grant, raise the number in the middle.
        var grant = DocumentGrants.Mint(Key, Document, Sara, Now);
        var parts = grant.Split('.');
        var extended = string.Join('.',
            parts[0], parts[1], long.Parse(parts[2]) + 86_400, parts[3]);

        Assert.False(DocumentGrants.Verify(
            Key, extended, Document, Sara, Now.AddHours(1)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData("a.b.c")]                 // three parts, not four
    [InlineData("a.b.c.d.e")]             // five
    [InlineData("not-a-guid.x.123.ABCD")]
    [InlineData("....")]
    public void Anything_malformed_is_refused_rather_than_thrown(string? grant)
    {
        // A grant arrives in a query string, so every one of these is something a
        // caller will eventually send. An exception here would be a 500 on a
        // public endpoint, reachable by typing.
        Assert.False(DocumentGrants.Verify(Key, grant, Document, Sara, Now));
    }

    [Fact]
    public void A_grant_with_a_non_hex_signature_is_refused_rather_than_thrown()
    {
        // Convert.FromHexString throws on odd lengths and on non-hex characters,
        // and both are one keystroke away in a URL.
        var grant = DocumentGrants.Mint(Key, Document, Sara, Now);
        var parts = grant.Split('.');

        foreach (var signature in new[] { "zz", "ABC", "" })
        {
            Assert.False(DocumentGrants.Verify(
                Key, string.Join('.', parts[0], parts[1], parts[2], signature),
                Document, Sara, Now));
        }
    }

    [Fact]
    public void Two_grants_for_the_same_pair_differ_only_by_their_expiry()
    {
        // Not a security property, a diagnostic one: a grant is deterministic given
        // its inputs, so a support ticket quoting one can be reproduced.
        var first = DocumentGrants.Mint(Key, Document, Sara, Now);
        var second = DocumentGrants.Mint(Key, Document, Sara, Now);

        Assert.Equal(first, second);
        Assert.NotEqual(first, DocumentGrants.Mint(Key, Document, Sara, Now.AddSeconds(1)));
    }
}
