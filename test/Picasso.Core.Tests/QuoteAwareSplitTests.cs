using System.Linq;
using Picasso.Core;
using Xunit;

namespace Picasso.Core.Tests;

/// <summary>
/// SplitStatements must treat a '.' inside a quoted VALUE literal as data, not
/// as the COBOL statement terminator. Before the quote-aware scanner, a literal
/// such as 'Thank you... ' shattered on its embedded periods and the parser
/// falsely rejected an otherwise valid record layout (found against AWS
/// CardDemo's COTTL01Y.cpy / CSMSG01Y.cpy).
/// </summary>
public class QuoteAwareSplitTests
{
    [Fact]
    public void MinimalRepro_ValueLiteralWithPeriod_ParsesToCorrectLayout()
    {
        var source =
            "01 REC.\n" +
            "   05 A PIC X(20) VALUE 'app... done'.\n" +
            "   05 B PIC X(3).\n";

        var parsed = CopybookParser.Parse(source);

        Assert.Equal(new[] { "A", "B" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(0, parsed.Flat[0].Start);
        Assert.Equal(20, parsed.Flat[0].Len);
        Assert.Equal(20, parsed.Flat[1].Start);
        Assert.Equal(3, parsed.Flat[1].Len);
        Assert.Equal(23, parsed.Root.Len);
    }

    [Fact]
    public void ThreeConsecutiveValueLiteralsWithPeriods_CottlShape_Parses()
    {
        // The COTTL01Y.cpy shape: three PIC X(40) fields each carrying a VALUE
        // literal that contains periods.
        var source =
            "01 CCDA-TITLE.\n" +
            "   05 CCDA-LINE-1 PIC X(40) VALUE\n" +
            "      'Thank you for using CCDA application... '.\n" +
            "   05 CCDA-LINE-2 PIC X(40) VALUE\n" +
            "      'Please come back again. Goodbye.'.\n" +
            "   05 CCDA-LINE-3 PIC X(40) VALUE\n" +
            "      'End of message. Ver 1.0.'.\n";

        var parsed = CopybookParser.Parse(source);

        Assert.Equal(
            new[] { "CCDA-LINE-1", "CCDA-LINE-2", "CCDA-LINE-3" },
            parsed.Flat.Select(f => f.Name));
        Assert.Equal(new[] { 0, 40, 80 }, parsed.Flat.Select(f => f.Start));
        Assert.All(parsed.Flat, f => Assert.Equal(40, f.Len));
        Assert.Equal(120, parsed.Root.Len);
    }

    [Fact]
    public void DoubleQuoteDelimitedValueLiteralWithPeriod_Parses()
    {
        var source =
            "01 REC.\n" +
            "   05 A PIC X(20) VALUE \"app... done\".\n" +
            "   05 B PIC X(3).\n";

        var parsed = CopybookParser.Parse(source);

        Assert.Equal(new[] { "A", "B" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(20, parsed.Flat[1].Start);
        Assert.Equal(23, parsed.Root.Len);
    }

    [Fact]
    public void DoubledQuoteEscapeInsideLiteral_Parses()
    {
        // COBOL doubled-delimiter escape: '' inside a '-literal is one literal
        // quote and does NOT close the literal, so the trailing '.' inside stays
        // data and only the terminating '.' splits.
        var source =
            "01 REC.\n" +
            "   05 A PIC X(20) VALUE 'it''s fine.'.\n" +
            "   05 B PIC X(3).\n";

        var parsed = CopybookParser.Parse(source);

        Assert.Equal(new[] { "A", "B" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(20, parsed.Flat[1].Start);
        Assert.Equal(23, parsed.Root.Len);
    }

    [Fact]
    public void DoubledDoubleQuoteEscapeInsideLiteral_Parses()
    {
        var source =
            "01 REC.\n" +
            "   05 A PIC X(20) VALUE \"say \"\"hi.\"\" now\".\n" +
            "   05 B PIC X(3).\n";

        var parsed = CopybookParser.Parse(source);

        Assert.Equal(new[] { "A", "B" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(23, parsed.Root.Len);
    }

    [Fact]
    public void ValueLiteralEndingInPeriod_ThenTerminatingPeriod_SplitsCorrectly()
    {
        // 'x.' immediately followed by the terminating period: the literal's own
        // period is data, the following one terminates.
        var source =
            "01 REC.\n" +
            "   05 A PIC X(5) VALUE 'x.'.\n" +
            "   05 B PIC X(3).\n";

        var parsed = CopybookParser.Parse(source);

        Assert.Equal(new[] { "A", "B" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(5, parsed.Flat[0].Len);
        Assert.Equal(5, parsed.Flat[1].Start);
        Assert.Equal(8, parsed.Root.Len);
    }

    // ---- Regression guards: non-literal splitting must be unchanged ----

    [Fact]
    public void OrdinaryLayoutWithNoLiterals_SplitsUnchanged()
    {
        var source =
            "01 REC.\n" +
            "   05 A PIC X(3).\n" +
            "   05 C PIC 9(4).\n";

        var parsed = CopybookParser.Parse(source);

        Assert.Equal(new[] { "A", "C" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(0, parsed.Flat[0].Start);
        Assert.Equal(3, parsed.Flat[1].Start);
        Assert.Equal(7, parsed.Root.Len);
    }

    [Fact]
    public void SplitStatements_NoLiterals_MatchesNaiveBehaviour()
    {
        var statements = CopybookParser.SplitStatements("01 REC.\n   05 A PIC X(3).\n   05 B PIC 9(2).\n");

        Assert.Equal(new[] { "01 REC", "05 A PIC X(3)", "05 B PIC 9(2)" }, statements);
    }

    [Fact]
    public void SplitStatements_PeriodInsideLiteral_IsNotATerminator()
    {
        var statements = CopybookParser.SplitStatements("05 A PIC X(20) VALUE 'a.b.c'.");

        Assert.Single(statements);
        Assert.Equal("05 A PIC X(20) VALUE 'a.b.c'", statements[0]);
    }
}
