using EAuction.Reporting.Domain;
using Microsoft.EntityFrameworkCore;

namespace EAuction.Reporting.Persistence;

/// <summary>
/// The read model التقارير are computed from.
///
/// Four tables and no outbox: this service consumes and never produces. Note also
/// what it has that the notification service needs and this one does not — a
/// watermark. A notice is not idempotent to a person, so that service has to know
/// what it has already sent; a report is pure state, so replaying every topic from
/// the start is not merely safe here, it is how the model is built.
/// </summary>
public sealed class ReportingDbContext(DbContextOptions<ReportingDbContext> options)
    : DbContext(options)
{
    public DbSet<AuctionRecord> Auctions => Set<AuctionRecord>();
    public DbSet<PlotRecord> Plots => Set<PlotRecord>();
    public DbSet<BidderRecord> Bidders => Set<BidderRecord>();
    public DbSet<SettlementRecord> Settlements => Set<SettlementRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AuctionRecord>(e =>
        {
            e.ToTable("auction");
            e.HasKey(x => x.AuctionId);
            e.Property(x => x.AuctionId).ValueGeneratedNever();

            e.Property(x => x.NameAr).HasColumnType("text").IsRequired();
            e.Property(x => x.NameEn).HasColumnType("text").IsRequired();
            e.Property(x => x.Phase).HasColumnType("text");
            e.Property(x => x.Channel).HasMaxLength(20).IsRequired();
            e.Property(x => x.BidderVisibility).HasMaxLength(20).IsRequired();
            e.Property(x => x.RejectionReason).HasColumnType("text");

            // As a name, not an ordinal. A report is read by people, and a CSV
            // column of 0..7 is a column somebody has to look up.
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(20);

            e.Property(x => x.TotalAreaSqm).HasPrecision(18, 2);
            e.Property(x => x.BrokerageFeePercent).HasPrecision(5, 2);

            e.Ignore(x => x.PricePerSqmMinorUnits);

            // What every report filters and groups by.
            e.HasIndex(x => x.Phase);
            e.HasIndex(x => x.Outcome);
            e.HasIndex(x => x.SettledAt);
            e.HasIndex(x => x.ClosedAt);
        });

        b.Entity<PlotRecord>(e =>
        {
            e.ToTable("plot");
            e.HasKey(x => x.PlotId);
            e.Property(x => x.PlotId).ValueGeneratedNever();
            e.Property(x => x.PlotNumber).HasColumnType("text").IsRequired();
            e.Property(x => x.AreaSqm).HasPrecision(18, 2);
            e.Property(x => x.StreetWidthMeters).HasPrecision(8, 2);
            e.Property(x => x.FrontageMeters).HasPrecision(8, 2);
            e.Property(x => x.Latitude).HasMaxLength(50);
            e.Property(x => x.Longitude).HasMaxLength(50);
            e.Property(x => x.DescriptionAr).HasColumnType("text");

            e.HasIndex(x => x.AuctionId);

            // A plot number is unique within an auction in the auction service, and
            // across the inventory it had better be too — two auctions selling the
            // same parcel is a data problem a report should surface rather than sum.
            e.HasIndex(x => x.PlotNumber);
        });

        b.Entity<BidderRecord>(e =>
        {
            e.ToTable("auction_bidder");
            e.HasKey(x => new { x.AuctionId, x.BidderId });
            e.Property(x => x.DisplayNameAr).HasColumnType("text");
            e.Property(x => x.PaymentRefusedReason).HasColumnType("text");
            e.Property(x => x.DisqualificationReason).HasColumnType("text");

            e.HasIndex(x => x.BidderId);
            e.HasIndex(x => x.EligibleAt);
        });

        b.Entity<SettlementRecord>(e =>
        {
            e.ToTable("settlement");

            // The topic's own offset. See the note on the property.
            e.HasKey(x => x.Offset);
            e.Property(x => x.Offset).ValueGeneratedNever();

            e.Property(x => x.Purpose).HasMaxLength(40).IsRequired();
            e.Property(x => x.Outcome).HasMaxLength(40).IsRequired();
            e.Property(x => x.FailureReason).HasColumnType("text");

            e.HasIndex(x => new { x.AuctionId, x.BidderId });
            e.HasIndex(x => new { x.Purpose, x.Outcome, x.At });
            e.HasIndex(x => x.At);
        });
    }
}

/// <summary>Used by `dotnet ef` at design time only.</summary>
public sealed class DesignTimeFactory
    : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<ReportingDbContext>
{
    public ReportingDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ReportingDbContext>()
            .UseNpgsql("Host=localhost;Database=eauction_reporting;Username=eauction;Password=eauction")
            .Options);
}
