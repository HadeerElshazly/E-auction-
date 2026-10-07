using System.Text.RegularExpressions;

namespace EAuction.TestSupport;

/// <summary>
/// The one check between a test fixture and <c>DROP DATABASE</c>.
///
/// Fixtures delete and recreate their database for a clean slate, which is right
/// for a test database and catastrophic for anything else. A connection string
/// pointed at the wrong database — an environment variable set for the running
/// stack, a test runner that inherited one — once dropped the platform's own
/// database mid-demo. A fixture now refuses to delete any database whose name does
/// not say it is a test one.
/// </summary>
public static partial class TestDatabases
{
    [GeneratedRegex(@"(test|_t[0-9a-f]{12})$", RegexOptions.IgnoreCase)]
    private static partial Regex Disposable();

    /// <summary>Throws unless <paramref name="database"/> is named as a test database.</summary>
    public static void EnsureDisposable(string? database)
    {
        if (string.IsNullOrWhiteSpace(database) || !Disposable().IsMatch(database))
            throw new InvalidOperationException(
                $"Refusing to drop database '{database}': its name does not mark it as a test database "
                + "(it must end in 'test' or be a generated '_t<hex>' name). Check the *_TEST_DB variable.");
    }
}
