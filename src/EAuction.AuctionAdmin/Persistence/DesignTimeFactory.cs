using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EAuction.AuctionAdmin.Persistence;

/// <summary>Used by `dotnet ef` at design time only.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AdminDbContext>
{
    public AdminDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AdminDbContext>()
            .UseNpgsql("Host=localhost;Database=eauction;Username=eauction;Password=eauction")
            .Options);
}
