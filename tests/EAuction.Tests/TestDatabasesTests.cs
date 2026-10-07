using EAuction.TestSupport;
using Xunit;

namespace EAuction.Tests;

/// <summary>
/// A fixture once dropped the platform's live database because its connection
/// string named no database and Npgsql fell back to the user's. These are the
/// names a fixture may drop, and the ones it must not.
/// </summary>
public class TestDatabasesTests
{
    [Theory]
    [InlineData("eauction_test")]
    [InlineData("eauction_participant_test")]
    [InlineData("eauction_notifications_t0424fe7d91d0")]
    public void Test_databases_may_be_dropped(string name) => TestDatabases.EnsureDisposable(name);

    [Theory]
    [InlineData("eauction")]
    [InlineData("eauction_participant")]
    [InlineData("eauction_audit")]
    [InlineData("postgres")]
    [InlineData("")]
    [InlineData(null)]
    public void Live_or_unnamed_databases_may_not(string? name) =>
        Assert.Throws<InvalidOperationException>(() => TestDatabases.EnsureDisposable(name));
}
