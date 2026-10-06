using System.Text.RegularExpressions;
using Xunit;

namespace EAuction.AuctionAdmin.Tests;

/// <summary>
/// Every validation message a person can be shown is in Arabic.
///
/// Not a style rule. These strings are rendered verbatim by both portals —
/// `e.problems.join(' · ')` — into a right-to-left Arabic interface used by
/// municipal staff and by citizens, and they spent the project in English: a clerk
/// filling in an auction was told "Start must be in the future.". Two of them were
/// even half-translated, which is how it survived review: the file looked
/// localised at a glance.
///
/// Asserted by reading the source rather than by calling each validator, because
/// the failure is the <em>literal</em>. A test that exercised the paths would cover
/// whichever ones somebody remembered to write a case for, and the next message
/// added in English would pass it.
/// </summary>
public class ValidationLanguageTests
{
    /// <summary>The domain files that compose text for a person to read.</summary>
    private static readonly string[] Sources =
    [
        "src/EAuction.AuctionAdmin/Domain/Auction.cs",
        "src/EAuction.Participant/Domain/Bidder.cs",
        "src/EAuction.Participant/Domain/Subscription.cs",
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EAuction.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void No_validation_message_is_written_in_English()
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var relative in Sources)
        {
            var path = Path.Combine(root, relative);
            Assert.True(File.Exists(path), $"{relative} has moved; this test needs its new home.");

            foreach (Match match in Regex.Matches(File.ReadAllText(path), """problems\.Add\("([^"]+)"\)"""))
            {
                var message = match.Groups[1].Value;

                // Arabic, Arabic-Indic digits and Arabic punctuation.
                if (!Regex.IsMatch(message, @"[؀-ۿ]"))
                    offenders.Add($"{relative}: {message}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These reach a person's screen as written, in a right-to-left Arabic "
            + "interface:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_messages_are_actually_being_found()
    {
        // Guards the guard. If the regex above stops matching — a reformat putting
        // the literal on its own line, a rename of the local — the test would pass
        // by examining nothing at all, which is the one way a rule like this fails
        // silently.
        var root = RepoRoot();

        var found = Sources.Sum(relative => Regex.Matches(
            File.ReadAllText(Path.Combine(root, relative)),
            """problems\.Add\("([^"]+)"\)""").Count);

        Assert.True(found >= 20, $"Only {found} validation messages found; expected the full set.");
    }
}
