using EAuction.Audit.Domain;
using Microsoft.EntityFrameworkCore;

namespace EAuction.Audit.Persistence;

/// <summary>
/// The trail, and nothing else.
///
/// One table, no outbox, and no aggregate that raises events: this service is a
/// sink. It also has no <c>SaveChangesAsync</c> override, which is the visible
/// difference from the two services that produce audit entries — there is nothing
/// to drain, because nothing here has anywhere to send.
/// </summary>
public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public DbSet<AuditEntry> Entries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_entry");

            // The topic offset, assigned by Kafka and never by us.
            e.HasKey(x => x.Offset);
            e.Property(x => x.Offset).ValueGeneratedNever();

            // Every string is unbounded text, and none of them is jsonb.
            //
            // Not jsonb, because jsonb normalises: it reorders keys and rewrites
            // numbers, and the hash is over these exact bytes. A column that
            // quietly rewrote the evidence would make every entry fail
            // verification for a reason no auditor could be expected to guess.
            //
            // Unbounded, because a length this service chose could be exceeded by a
            // producer this service does not control. A rejection reason is 2,000
            // characters in the auction service, and a cap of 2,000 here plus a
            // prefix at the call site is a 22001 on insert — which stops the
            // consumer, because it must stop rather than write the chain out of
            // order. Anyone who can produce to staff.actions can already put
            // anything in Payload, which is unbounded of necessity, so capping the
            // projection beside it buys nothing and risks the one failure mode
            // that costs the trail.
            e.Property(x => x.EventType).HasColumnType("text").IsRequired();
            e.Property(x => x.Key).HasColumnType("text").IsRequired();
            e.Property(x => x.Payload).HasColumnType("text").IsRequired();

            e.Property(x => x.ActorRoles).HasColumnType("text");
            e.Property(x => x.Action).HasColumnType("text");
            e.Property(x => x.Subject).HasColumnType("text");
            e.Property(x => x.Details).HasColumnType("text");
            e.Property(x => x.SourceAddress).HasColumnType("text");

            e.Property(x => x.Hash).HasMaxLength(32).IsRequired();
            e.Property(x => x.PreviousHash).HasMaxLength(32).IsRequired();

            // The three questions an auditor actually asks: what did this person
            // do, who touched this auction, and what happened that week.
            e.HasIndex(x => new { x.ActorSubject, x.At });
            e.HasIndex(x => new { x.Subject, x.At });
            e.HasIndex(x => x.At);
            e.HasIndex(x => x.Action);
        });
    }
}

/// <summary>Used by `dotnet ef` at design time only.</summary>
public sealed class DesignTimeFactory
    : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql("Host=localhost;Database=eauction_audit;Username=eauction;Password=eauction")
            .Options);
}
