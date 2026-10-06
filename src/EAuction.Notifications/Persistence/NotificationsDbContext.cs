using EAuction.Notifications.Domain;
using Microsoft.EntityFrameworkCore;

namespace EAuction.Notifications.Persistence;

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options)
    : DbContext(options)
{
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Audience> Audience => Set<Audience>();
    public DbSet<AuctionName> AuctionNames => Set<AuctionName>();
    public DbSet<TopicWatermark> Watermarks => Set<TopicWatermark>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Notification>(e =>
        {
            e.ToTable("notification");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.Dedup).HasMaxLength(100);
            e.Property(x => x.TitleAr).HasMaxLength(200);
            e.Property(x => x.BodyAr).HasMaxLength(1000);

            // The whole defence against a replay storm.
            //
            // Every topic here is at-least-once and the compacted ones are read
            // from offset 0 on every start, so "insert if absent" is not a nicety:
            // without this index a restart tells every bidder again that they won.
            // The consumer inserts and swallows the unique violation, which is the
            // only version of this that is also safe between two replicas.
            e.HasIndex(x => new { x.BidderId, x.AuctionId, x.Kind, x.Dedup }).IsUnique();

            // The portal's query: this bidder's, newest first, unread first.
            e.HasIndex(x => new { x.BidderId, x.ReadAt, x.CreatedAt });
        });

        b.Entity<Audience>(e =>
        {
            e.ToTable("audience");
            e.HasKey(x => new { x.AuctionId, x.BidderId });
        });

        b.Entity<TopicWatermark>(e =>
        {
            e.ToTable("topic_watermark");
            e.HasKey(x => x.Topic);
            e.Property(x => x.Topic).HasMaxLength(200);
        });

        b.Entity<AuctionName>(e =>
        {
            e.ToTable("auction_name");
            e.HasKey(x => x.AuctionId);
            e.Property(x => x.AuctionId).ValueGeneratedNever();
            e.Property(x => x.NameAr).HasMaxLength(300);
        });
    }
}

/// <summary>Used by `dotnet ef` at design time only.</summary>
public sealed class DesignTimeFactory
    : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql("Host=localhost;Database=eauction_notifications;Username=eauction;Password=eauction")
            .Options);
}
