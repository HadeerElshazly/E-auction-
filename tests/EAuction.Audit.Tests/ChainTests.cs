using System.Security.Cryptography;
using System.Text.Json;
using EAuction.Audit.Domain;
using EAuction.Core;
using EAuction.Outbox;
using Xunit;

namespace EAuction.Audit.Tests;

/// <summary>
/// The hash chain, without a database or a broker in the way.
///
/// These are the properties the whole service rests on: that an entry's hash
/// follows from its predecessor and its own bytes, that two instances reading the
/// same topic agree, and that two different records cannot produce one frame.
/// </summary>
public class ChainTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static StreamEvent Record(long offset, string action, string subject) =>
        new(Topics.StaffActions, subject,
            JsonSerializer.Serialize(
                StaffActionRecorded.By(
                    Guid.NewGuid(), "auction-admin", "10.0.0.1", action, subject),
                Json),
            nameof(StaffActionRecorded), offset);

    [Fact]
    public void The_first_entry_chains_onto_the_zero_head()
    {
        var chain = new LedgerChain();
        var entry = AuditEntry.From(Record(0, "ApproveAuction", "auction/a"), chain, DateTimeOffset.UtcNow);

        Assert.Equal(new byte[32], entry.PreviousHash);
        Assert.Equal(32, entry.Hash.Length);
        Assert.NotEqual(new byte[32], entry.Hash);
    }

    [Fact]
    public void Each_entry_carries_the_previous_ones_hash()
    {
        var chain = new LedgerChain();

        var first = AuditEntry.From(Record(0, "ApproveAuction", "auction/a"), chain, DateTimeOffset.UtcNow);
        var second = AuditEntry.From(Record(1, "RejectAuction", "auction/b"), chain, DateTimeOffset.UtcNow);

        Assert.Equal(first.Hash, second.PreviousHash);
        Assert.Equal(second.Hash, chain.Head.ToArray());
    }

    [Fact]
    public void An_entrys_hash_is_recomputable_from_what_is_stored()
    {
        var chain = new LedgerChain();
        var entry = AuditEntry.From(Record(7, "ConfirmAward", "auction/a"), chain, DateTimeOffset.UtcNow);

        // This is what GET /audit/verify does, and the reason the payload is stored
        // verbatim: the evidence has to be enough to rebuild the hash from.
        var recomputed = new LedgerChain(entry.PreviousHash).Append(entry.Frame());

        Assert.True(CryptographicOperations.FixedTimeEquals(recomputed, entry.Hash));
    }

    [Fact]
    public void Two_instances_reading_the_same_records_compute_the_same_hashes()
    {
        // The property that lets two replicas write the same table. Nothing hashed
        // comes from a clock or from the process, so the chain is a function of the
        // topic alone — which is why the consumer can adopt another replica's row
        // on a unique violation instead of having to reconcile with it.
        var records = new[]
        {
            Record(0, "CreateAuctionDraft", "auction/a"),
            Record(1, "AddPlot", "auction/a"),
            Record(2, "ApproveAuction", "auction/a"),
        };

        var one = new LedgerChain();
        var two = new LedgerChain();

        var left = records.Select(r => AuditEntry.From(r, one, DateTimeOffset.UtcNow)).ToList();

        // A different clock, deliberately: RecordedAt differs and nothing else may.
        var right = records
            .Select(r => AuditEntry.From(r, two, DateTimeOffset.UtcNow.AddHours(3)))
            .ToList();

        Assert.Equal(
            left.Select(e => Convert.ToHexString(e.Hash)),
            right.Select(e => Convert.ToHexString(e.Hash)));
    }

    [Fact]
    public void Changing_one_byte_of_a_payload_changes_the_hash()
    {
        var record = Record(0, "ApproveAuction", "auction/a");
        var tampered = record with { Payload = record.Payload.Replace("ApproveAuction", "RejectAuction") };

        var original = AuditEntry.From(record, new LedgerChain(), DateTimeOffset.UtcNow);
        var altered = AuditEntry.From(tampered, new LedgerChain(), DateTimeOffset.UtcNow);

        Assert.NotEqual(Convert.ToHexString(original.Hash), Convert.ToHexString(altered.Hash));
    }

    [Fact]
    public void The_offset_is_part_of_the_hash()
    {
        // So an entry cannot be moved to a different place in the trail with its
        // hash intact.
        var record = Record(5, "ApproveAuction", "auction/a");

        var here = AuditEntry.From(record, new LedgerChain(), DateTimeOffset.UtcNow);
        var there = AuditEntry.From(record with { Offset = 6 }, new LedgerChain(), DateTimeOffset.UtcNow);

        Assert.NotEqual(Convert.ToHexString(here.Hash), Convert.ToHexString(there.Hash));
    }

    [Fact]
    public void Two_records_that_differ_only_in_where_the_fields_divide_hash_differently()
    {
        // The reason the frame is length-prefixed rather than delimited. Details is
        // free text from a call site: with any separator, a value containing it
        // could be split into the same bytes as a different pair of fields, and two
        // different records hashing alike is the one thing a chain must not permit.
        var left = new StreamEvent(
            Topics.StaffActions, "auction/a|b", "{}", nameof(StaffActionRecorded), 0);
        var right = new StreamEvent(
            Topics.StaffActions, "auction/a", "|b{}", nameof(StaffActionRecorded), 0);

        var one = AuditEntry.From(left, new LedgerChain(), DateTimeOffset.UtcNow);
        var two = AuditEntry.From(right, new LedgerChain(), DateTimeOffset.UtcNow);

        Assert.NotEqual(Convert.ToHexString(one.Hash), Convert.ToHexString(two.Hash));
    }

    [Fact]
    public void A_payload_that_will_not_parse_is_still_recorded_and_still_chains()
    {
        var chain = new LedgerChain();
        var good = AuditEntry.From(Record(0, "ApproveAuction", "auction/a"), chain, DateTimeOffset.UtcNow);

        var garbage = AuditEntry.From(
            new StreamEvent(Topics.StaffActions, "auction/b", "not json at all",
                nameof(StaffActionRecorded), 1),
            chain, DateTimeOffset.UtcNow);

        Assert.True(garbage.Malformed);
        Assert.Null(garbage.Action);
        Assert.Null(garbage.ActorSubject);

        // The point: the link holds. Dropping the record instead would leave a hole
        // in the offsets and break every hash after it, so an unreadable payload
        // must never cost the chain.
        Assert.Equal(good.Hash, garbage.PreviousHash);
        Assert.Equal("not json at all", garbage.Payload);
    }

    [Fact]
    public void Json_that_parses_but_is_missing_a_required_field_counts_as_malformed()
    {
        // A half-written payload is not a half-written entry. StaffActionRecorded's
        // fields are required, so this throws inside the deserialiser rather than
        // yielding an entry with an empty actor — which would read as an action
        // nobody performed.
        var entry = AuditEntry.From(
            new StreamEvent(Topics.StaffActions, "auction/a", """{"action":"ApproveAuction"}""",
                nameof(StaffActionRecorded), 0),
            new LedgerChain(), DateTimeOffset.UtcNow);

        Assert.True(entry.Malformed);
        Assert.Null(entry.Action);
    }

    [Fact]
    public void The_projection_carries_what_an_auditor_filters_on()
    {
        var actor = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(
            StaffActionRecorded.By(
                actor, "auction-admin,award-committee", "10.1.2.3",
                "DisqualifyWinner", "auction/a", "Deposit forfeited. Did not pay."),
            Json);

        var entry = AuditEntry.From(
            new StreamEvent(Topics.StaffActions, "auction/a", payload,
                nameof(StaffActionRecorded), 0),
            new LedgerChain(), DateTimeOffset.UtcNow);

        Assert.False(entry.Malformed);
        Assert.Equal(actor, entry.ActorSubject);
        Assert.Equal("auction-admin,award-committee", entry.ActorRoles);
        Assert.Equal("DisqualifyWinner", entry.Action);
        Assert.Equal("auction/a", entry.Subject);
        Assert.Equal("Deposit forfeited. Did not pay.", entry.Details);
        Assert.Equal("10.1.2.3", entry.SourceAddress);
        Assert.NotNull(entry.At);
    }

    [Fact]
    public void A_resumed_chain_continues_where_the_stored_one_stopped()
    {
        // What the consumer does on restart: it reads the last entry's hash out of
        // the table rather than recomputing a trail that may be years long.
        //
        // The records are built once and reused, because Record() mints a fresh
        // actor and timestamp on each call — two calls with the same arguments are
        // two different staff actions, not the same one twice.
        var approval = Record(0, "ApproveAuction", "auction/a");
        var notice = Record(1, "NotifyWinner", "auction/a");

        var live = new LedgerChain();
        var first = AuditEntry.From(approval, live, DateTimeOffset.UtcNow);
        var second = AuditEntry.From(notice, live, DateTimeOffset.UtcNow);

        var resumed = new LedgerChain(first.Hash);
        var again = AuditEntry.From(notice, resumed, DateTimeOffset.UtcNow);

        Assert.Equal(Convert.ToHexString(second.Hash), Convert.ToHexString(again.Hash));
    }

    [Fact]
    public void A_head_of_the_wrong_length_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new LedgerChain(new byte[16]));
    }
}
