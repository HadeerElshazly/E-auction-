using System.Globalization;
using System.Text;

namespace EAuction.Reporting.Reports;

/// <summary>
/// Writes a report as a CSV somebody can open in Excel without it going wrong.
///
/// Four details, none of them optional for this client:
///
/// <list type="bullet">
/// <item>
/// A UTF-8 byte-order mark. Excel on Windows reads a BOM-less UTF-8 file in the
/// system code page, so مخطط السعيد arrives as mojibake — in a report whose every
/// name is Arabic, that is the whole file ruined.
/// </item>
/// <item>
/// CRLF line endings, per RFC 4180.
/// </item>
/// <item>
/// Quoting and doubled quotes for any field containing a comma, a quote or a
/// newline. Rejection reasons and auction names are free text typed by staff.
/// </item>
/// <item>
/// A formula guard, which is the one that actually matters for safety. A field
/// beginning <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a carriage return is
/// executed as a formula by Excel and LibreOffice when the file is opened —
/// <c>=HYPERLINK(...)</c> or a DDE call. Every text field in these reports is
/// staff-entered: an auction name, a rejection reason, a disqualification reason.
/// A report is a file that gets emailed around a municipality, so the one place
/// this platform hands attacker-influenced text to a spreadsheet is here.
/// </item>
/// <item>
/// A choice of separator. The comma is the default because RFC 4180 says so and
/// every tool reads it — but Excel splits on the <em>locale's</em> list separator,
/// which on a Windows machine set to Arabic (Saudi Arabia) is a semicolon. Such a
/// machine opens a comma-separated file with every row in one column, which is the
/// single most common complaint about any CSV export and the one this client will
/// hit first. <c>?separator=semicolon</c> is the answer, rather than the usual
/// <c>sep=,</c> preamble, which Excel honours and every RFC 4180 parser treats as a
/// first row of data.
/// </item>
/// </list>
///
/// <para>
/// Note what is <em>not</em> a separator: the Arabic comma ،. It appears in the
/// middle of rejection reasons constantly and needs no quoting, which a test asserts
/// — the field delimiter is an ASCII character and treating a U+060C as one would
/// quote half the file for nothing.
/// </para>
/// </summary>
public static class Csv
{
    public const string ContentType = "text/csv; charset=utf-8";

    public static byte[] Write(
        IEnumerable<string> headers, IEnumerable<IEnumerable<object?>> rows,
        char separator = ',')
    {
        var text = new StringBuilder();

        text.Append(string.Join(separator, headers.Select(h => Quote(h, separator))))
            .Append("\r\n");

        foreach (var row in rows)
            text.Append(string.Join(separator, row.Select(cell => Cell(cell, separator))))
                .Append("\r\n");

        // Encoding with the BOM written explicitly rather than through
        // `new UTF8Encoding(true)`, because GetBytes never emits the preamble —
        // only a StreamWriter does, and this returns bytes.
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    /// <summary>
    /// One field, rendered and escaped.
    ///
    /// The formula guard applies to text and nothing else, and that distinction is
    /// not cosmetic: <c>-</c> is both a formula lead-in and a minus sign, so
    /// guarding a rendered number would turn every negative figure in the revenue
    /// report into the text <c>'-5000.00</c> and break the column. Numbers, dates
    /// and ids are produced by this file from typed values and cannot carry a
    /// payload; only the strings came from a person.
    /// </summary>
    private static string Cell(object? value, char separator) => value switch
    {
        null => "",
        bool b => b ? "true" : "false",

        // Minor units are rendered as the major unit with two places — 120000000
        // halalas as 1200000.00 — because a column of halalas is a column every
        // reader divides by a hundred in their head and a quarter of them get wrong.
        // The JSON API hands back the integer; this is the human-facing edge.
        long l => (l / 100m).ToString("0.00", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        decimal d => d.ToString("0.##", CultureInfo.InvariantCulture),

        // ISO-8601, UTC. A report crossing time zones needs one unambiguous column,
        // and the portal can render Riyadh time from it.
        DateTimeOffset at => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),

        Guid g => g.ToString(),

        // Everything else is text: a name, a phase, a reason, an enum's name.
        _ => Quote(Defuse(value.ToString() ?? ""), separator),
    };

    private static readonly char[] Dangerous = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>
    /// Stops a spreadsheet executing staff-entered text. The apostrophe goes on
    /// first so it ends up inside the quotes if the field also needs quoting.
    /// </summary>
    private static string Defuse(string value) =>
        value.Length > 0 && Dangerous.Contains(value[0]) ? "'" + value : value;

    private static string Quote(string value, char separator) =>
        value.Contains(separator) || value.Contains('"')
            || value.Contains('\n') || value.Contains('\r')
                ? '"' + value.Replace("\"", "\"\"") + '"'
                : value;
}
