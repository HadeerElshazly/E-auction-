using System.Text;
using EAuction.Reporting.Reports;
using Xunit;

namespace EAuction.Reporting.Tests;

/// <summary>
/// The CSV writer, which is the half of this service that leaves the building.
///
/// A report is a file that gets emailed around a municipality and opened in Excel,
/// so the failures here are not cosmetic: mojibake instead of مخطط السعيد, a column
/// of halalas nobody divides correctly, or a formula that runs when the file opens.
/// </summary>
public class CsvTests
{
    private static string Text(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes, Encoding.UTF8.GetPreamble().Length,
            bytes.Length - Encoding.UTF8.GetPreamble().Length);

    [Fact]
    public void It_starts_with_a_byte_order_mark()
    {
        // Without it Excel on Windows reads UTF-8 in the system code page, and every
        // Arabic name in the file arrives as mojibake. In a report whose every name
        // is Arabic that is the whole file ruined.
        var bytes = Csv.Write(["name"], [["مخطط السعيد"]]);

        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        Assert.Contains("مخطط السعيد", Text(bytes));
    }

    [Fact]
    public void Rows_end_with_crlf()
    {
        var text = Text(Csv.Write(["a"], [["1"], ["2"]]));
        Assert.Equal("a\r\n1\r\n2\r\n", text);
    }

    [Fact]
    public void Minor_units_are_rendered_as_the_major_unit()
    {
        // 120,000,000 halalas is 1,200,000.00 SAR. A column of halalas is a column
        // every reader divides by a hundred in their head and a quarter get wrong.
        var text = Text(Csv.Write(["price"], [[1_200_000_00L]]));
        Assert.Equal("price\r\n1200000.00\r\n", text);
    }

    [Fact]
    public void A_negative_figure_stays_a_number()
    {
        // The bug the formula guard caused when it was applied to everything: `-` is
        // both a formula lead-in and a minus sign, so every negative figure in the
        // revenue report became the text '-5000.00 and broke the column.
        var text = Text(Csv.Write(["net"], [[-5_000_00L]]));
        Assert.Equal("net\r\n-5000.00\r\n", text);
        Assert.DoesNotContain("'", text);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\",\"click\")")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    public void Text_that_a_spreadsheet_would_execute_is_defused(string hostile)
    {
        // Every text field in these reports is staff-entered — an auction name, a
        // rejection reason, a disqualification reason. This is the one place the
        // platform hands that text to a spreadsheet, and Excel and LibreOffice both
        // execute a field beginning =, +, - or @ when the file is opened.
        var text = Text(Csv.Write(["reason"], [[hostile]]));

        Assert.DoesNotContain($"\n{hostile[0]}", text);
        Assert.Contains("'" + hostile[0], text);
    }

    [Fact]
    public void A_field_with_a_comma_or_a_quote_is_quoted_and_escaped()
    {
        var text = Text(Csv.Write(
            ["reason"], [["did not pay, then withdrew"], ["قال \"لا\""]]));

        Assert.Contains("\"did not pay, then withdrew\"", text);
        Assert.Contains("\"قال \"\"لا\"\"\"", text);
    }

    [Fact]
    public void An_arabic_comma_is_not_a_separator_and_is_left_alone()
    {
        // ، (U+060C) appears in the middle of rejection reasons constantly. The
        // field delimiter is an ASCII character, so quoting every field containing
        // an Arabic comma would quote half the file for nothing. Written down
        // because the first version of the test above assumed otherwise.
        var text = Text(Csv.Write(["reason"], [["لم يسدّد، وانسحب"]]));

        Assert.Equal("reason\r\nلم يسدّد، وانسحب\r\n", text);
    }

    [Fact]
    public void A_semicolon_separator_is_available_for_excel_in_an_arabic_locale()
    {
        // A Windows machine set to Arabic (Saudi Arabia) splits on the locale's list
        // separator, which is a semicolon — so it opens a comma-separated file with
        // every row in one column. The most common complaint about any CSV export,
        // and the one this client hits first.
        var text = Text(Csv.Write(
            ["a", "b"], [["x", "y"]], separator: ';'));

        Assert.Equal("a;b\r\nx;y\r\n", text);
    }

    [Fact]
    public void The_quoting_follows_the_separator_that_was_chosen()
    {
        // A field containing a semicolon needs no quoting in a comma-separated file
        // and does need it in a semicolon-separated one. Quoting against a fixed
        // comma would have produced a file that reparses wrongly in exactly the
        // locale the option exists for.
        Assert.Equal(
            "a\r\nx;y\r\n",
            Text(Csv.Write(["a"], [["x;y"]])));

        Assert.Equal(
            "a\r\n\"x;y\"\r\n",
            Text(Csv.Write(["a"], [["x;y"]], separator: ';')));
    }

    [Fact]
    public void A_field_with_a_newline_does_not_break_the_row_count()
    {
        // A rejection reason is free text in a textarea, so it genuinely contains
        // newlines. An unquoted one would turn one row into two and shift every
        // column after it.
        var bytes = Csv.Write(["a", "b"], [["one\ntwo", "x"]]);
        var text = Text(bytes);

        Assert.Equal(2, text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("\"one\ntwo\"", text);
    }

    [Fact]
    public void Dates_go_out_as_utc_iso8601()
    {
        // One unambiguous column, so a report read in Riyadh and a report read
        // anywhere else are the same report.
        var at = new DateTimeOffset(2026, 10, 6, 11, 30, 0, TimeSpan.FromHours(3));
        var text = Text(Csv.Write(["at"], [[at]]));

        Assert.Equal("at\r\n2026-10-06T08:30:00Z\r\n", text);
    }

    [Fact]
    public void Null_is_empty_and_booleans_are_words()
    {
        var text = Text(Csv.Write(["a", "b", "c"], [[null, true, false]]));
        Assert.Equal("a,b,c\r\n,true,false\r\n", text);
    }

    [Fact]
    public void An_area_keeps_its_decimals_without_trailing_zeroes()
    {
        var text = Text(Csv.Write(["area"], [[950.50m], [1000m]]));
        Assert.Equal("area\r\n950.5\r\n1000\r\n", text);
    }
}
