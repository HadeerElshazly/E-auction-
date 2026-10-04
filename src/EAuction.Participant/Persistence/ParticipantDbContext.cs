using EAuction.Outbox;
using EAuction.Participant.Domain;
using Microsoft.EntityFrameworkCore;

namespace EAuction.Participant.Persistence;

public sealed class ParticipantDbContext(DbContextOptions<ParticipantDbContext> options)
    : DbContext(options)
{
    public DbSet<Bidder> Bidders => Set<Bidder>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<AuctionTerms> AuctionTerms => Set<AuctionTerms>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Bidder>(e =>
        {
            e.ToTable("bidder");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.NationalId).HasMaxLength(20).IsRequired();
            e.Property(x => x.NameAr).HasMaxLength(300);
            e.Property(x => x.NameEn).HasMaxLength(300);
            e.Property(x => x.Phone).HasMaxLength(30);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Ignore(x => x.IsVerified);
            e.Ignore(x => x.IsProfileComplete);

            // One person, one account. A second registration against the same
            // national ID would let someone bid twice in an auction that caps
            // them at once.
            e.HasIndex(x => x.NationalId).IsUnique();
        });

        b.Entity<Subscription>(e =>
        {
            e.ToTable("subscription");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.DepositMethod).HasConversion<int?>();
            e.Property(x => x.BookletPaymentRef).HasMaxLength(200);
            e.Property(x => x.DepositPaymentRef).HasMaxLength(200);
            e.Property(x => x.RevocationReason).HasMaxLength(2000);
            e.Ignore(x => x.Events);

            // One subscription per bidder per auction.
            e.HasIndex(x => new { x.AuctionId, x.BidderId }).IsUnique();
            e.HasIndex(x => x.Status);
        });

        b.Entity<AuctionTerms>(e =>
        {
            e.ToTable("auction_terms");
            e.HasKey(x => x.AuctionId);
            e.Property(x => x.AuctionId).ValueGeneratedNever();
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox");
            e.HasKey(x => x.Id);
            // Lower-case to match Debezium EventRouter's default column names
            // exactly; EF would otherwise emit "Id" and the SMT would not find it.
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.AggregateType).HasColumnName("aggregatetype").HasMaxLength(100).IsRequired();
            e.Property(x => x.AggregateId).HasColumnName("aggregateid").HasMaxLength(100).IsRequired();
            e.Property(x => x.Type).HasColumnName("type").HasMaxLength(200).IsRequired();
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.RelayedAt).HasColumnName("relayed_at");
            e.HasIndex(x => new { x.RelayedAt, x.CreatedAt });
        });
    }

    /// <summary>
    /// Drains domain events into outbox rows, then saves. Both happen in one
    /// SaveChanges and therefore one transaction: a bidder cannot be made
    /// eligible without the catcher being told, and cannot be told without
    /// actually being eligible.
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var aggregates = ChangeTracker.Entries<Subscription>()
            .Select(entry => entry.Entity)
            .Where(subscription => subscription.Events.Count > 0)
            .ToList();

        foreach (var aggregate in aggregates)
        {
            foreach (var domainEvent in aggregate.Events)
                Outbox.Add(OutboxMessage.From(domainEvent));
            aggregate.ClearEvents();
        }

        return await base.SaveChangesAsync(ct);
    }
}

/// <summary>Used by `dotnet ef` at design time only.</summary>
public sealed class DesignTimeFactory
    : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<ParticipantDbContext>
{
    public ParticipantDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ParticipantDbContext>()
            .UseNpgsql("Host=localhost;Database=eauction_participant;Username=eauction;Password=eauction")
            .Options);
}
