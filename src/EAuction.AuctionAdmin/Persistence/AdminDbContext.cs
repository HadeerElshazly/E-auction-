using EAuction.AuctionAdmin.Domain;
using EAuction.Core;
using EAuction.Outbox;
using Microsoft.EntityFrameworkCore;

namespace EAuction.AuctionAdmin.Persistence;

public sealed class AdminDbContext(DbContextOptions<AdminDbContext> options) : DbContext(options)
{
    public DbSet<Auction> Auctions => Set<Auction>();
    public DbSet<Plot> Plots => Set<Plot>();
    public DbSet<Award> Awards => Set<Award>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Auction>(e =>
        {
            e.ToTable("auction");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.Channel).HasConversion<int>();
            e.Property(x => x.NameAr).HasMaxLength(300).IsRequired();
            e.Property(x => x.NameEn).HasMaxLength(300).IsRequired();
            e.Property(x => x.Phase).HasMaxLength(100);
            e.Property(x => x.RejectionReason).HasMaxLength(2000);
            e.Property(x => x.BrokerageFeePercent).HasPrecision(5, 2);

            e.HasMany(x => x.Plots).WithOne()
                .HasForeignKey(p => p.AuctionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Awards).WithOne()
                .HasForeignKey(a => a.AuctionId).OnDelete(DeleteBehavior.Cascade);

            e.Navigation(x => x.Plots).UsePropertyAccessMode(PropertyAccessMode.Field);
            e.Navigation(x => x.Awards).UsePropertyAccessMode(PropertyAccessMode.Field);

            e.Ignore(x => x.Events);
            e.Ignore(x => x.CurrentAward);
            e.Ignore(x => x.TotalAreaSqm);

            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.Phase);
        });

        b.Entity<Plot>(e =>
        {
            e.ToTable("plot");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.DeedNumber).HasMaxLength(100).IsRequired();
            e.Property(x => x.AreaSqm).HasPrecision(18, 2);
            e.Property(x => x.Latitude).HasMaxLength(50);
            e.Property(x => x.Longitude).HasMaxLength(50);
            e.Property(x => x.DescriptionAr).HasMaxLength(2000);
            e.Property(x => x.DescriptionEn).HasMaxLength(2000);
            e.HasIndex(x => new { x.AuctionId, x.DeedNumber }).IsUnique();
        });

        b.Entity<Award>(e =>
        {
            e.ToTable("award");
            e.HasKey(x => x.Id);
            // Domain-assigned, not store-generated. Without this EF reads the
            // pre-set Guid on a new Award reached through Auction.Awards as
            // proof the row already exists and emits an UPDATE, which affects
            // zero rows — every cascade step would fail.
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.DisqualificationReason).HasMaxLength(2000);
            e.Ignore(x => x.IsOpen);

            // One open award at a time: a cascade must close the previous
            // award before the next can be confirmed.
            e.HasIndex(x => new { x.AuctionId, x.CascadeStep }).IsUnique();
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            // Lower-case to match the EventRouter SMT's default column names
            // exactly; EF would otherwise emit "Id" and the SMT would not find it.
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.AggregateType).HasColumnName("aggregatetype").HasMaxLength(100).IsRequired();
            e.Property(x => x.AggregateId).HasColumnName("aggregateid").HasMaxLength(100).IsRequired();
            e.Property(x => x.Type).HasColumnName("type").HasMaxLength(200).IsRequired();
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.RelayedAt).HasColumnName("relayed_at");

            // The relay's work queue. Debezium does not use this index; the
            // polling relay does.
            e.HasIndex(x => new { x.RelayedAt, x.CreatedAt });
        });
    }

    /// <summary>
    /// Drains domain events into outbox rows, then saves. Both happen in one
    /// SaveChanges and therefore one transaction: the event cannot be
    /// published without the state change, and the state change cannot be
    /// committed without the event queued.
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var aggregates = ChangeTracker.Entries<Auction>()
            .Select(entry => entry.Entity)
            .Where(auction => auction.Events.Count > 0)
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
