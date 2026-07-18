using System.Linq;
using Picasso.Core;
using Xunit;

namespace Picasso.Core.Tests;

/// <summary>
/// Fixed-format column-7 '-' continuation of a source line. The breaking case is
/// a VALUE literal too long for one line, split across two, the continuation line
/// carrying '-' in column 7 and reopening the literal with a quote in area B — the
/// K1WKA.CPY / K1WKY.CPY shape from the NIST cobol85 suite. StripComments now joins
/// such lines (reconstructing one properly-closed literal) before SplitStatements
/// sees them, so the copybook is no longer falsely rejected as "unterminated".
/// The existing loud rejection of a genuinely unterminated literal (no continuation
/// line) must survive.
/// </summary>
public class ContinuationTests
{
    /// <summary>Builds one fixed-format line: 6-char sequence area, column-7
    /// indicator, then code content starting at column 8.</summary>
    private static string F(string seq, char indicator, string content) => seq + indicator + content;

    private const string Blank6 = "      ";

    [Fact]
    public void K1wkaRepro_ContinuedLiteral_ParsesToCorrectLayout()
    {
        var source =
            F("000100", ' ', "   01 REC.") + "\n" +
            F("000100", ' ', "   02 WSTR-2A PICTURE X(3) VALUE                   \"A") + "\n" +
            F("000200", '-', "    \"BC\".") + "\n" +
            F("000300", ' ', "   02 WSTR-2B PICTURE X(5).") + "\n";

        var parsed = CopybookParser.Parse(source);

        Assert.Equal(new[] { "WSTR-2A", "WSTR-2B" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(0, parsed.Flat[0].Start);
        Assert.Equal(3, parsed.Flat[0].Len);
        Assert.Equal(3, parsed.Flat[1].Start);
        Assert.Equal(5, parsed.Flat[1].Len);
        Assert.Equal(8, parsed.Root.Len);
    }

    [Fact]
    public void StripComments_ContinuedLiteral_ReconstructsSingleClosedLiteral()
    {
        var source =
            F("000100", ' ', "   02 A PIC X(3) VALUE \"A") + "\n" +
            F("000200", '-', "    \"BC\".") + "\n";

        var stripped = CopybookParser.StripComments(source);

        // The two physical lines fold into one logical statement carrying the whole,
        // properly-closed literal; the continuation's own physical slot is now blank.
        var statements = CopybookParser.SplitStatements(stripped);
        Assert.Single(statements);
        Assert.Equal("02 A PIC X(3) VALUE \"ABC\"", statements[0]);
    }

    [Fact]
    public void ContinuedLiteral_ContainingPeriod_StillParses()
    {
        // The joined literal contains a period; the decimal-point-aware splitter must
        // treat it as literal data, not a terminator.
        var source =
            F("000100", ' ', "   01 REC.") + "\n" +
            F("000100", ' ', "   02 A PIC X(5) VALUE \"AB.") + "\n" +
            F("000200", '-', "    \"CD\".") + "\n" +
            F("000300", ' ', "   02 B PIC X(3).") + "\n";

        var parsed = CopybookParser.Parse(source);

        Assert.Equal(new[] { "A", "B" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(5, parsed.Flat[0].Len);
        Assert.Equal(5, parsed.Flat[1].Start);
        Assert.Equal(8, parsed.Root.Len);
    }

    [Fact]
    public void ContinuedNonLiteralWord_JoinsDirectly()
    {
        // A data-name split across two lines: the continuation resumes the word with
        // no separating space.
        var source =
            F("000100", ' ', "   01 REC.") + "\n" +
            F("000100", ' ', "   02 SOME-LONG-NA") + "\n" +
            F("000200", '-', "ME PIC X(4).") + "\n" +
            F("000300", ' ', "   02 B PIC X(2).") + "\n";

        var parsed = CopybookParser.Parse(source);

        Assert.Equal(new[] { "SOME-LONG-NAME", "B" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(4, parsed.Flat[0].Len);
        Assert.Equal(4, parsed.Flat[1].Start);
        Assert.Equal(6, parsed.Root.Len);
    }

    [Fact]
    public void BlankSequenceArea_ContinuedLiteral_Parses()
    {
        // Same continuation, but columns 1-6 are blank rather than numbered.
        var source =
            F(Blank6, ' ', "   01 REC.") + "\n" +
            F(Blank6, ' ', "   02 A PIC X(3) VALUE \"A") + "\n" +
            F(Blank6, '-', "    \"BC\".") + "\n" +
            F(Blank6, ' ', "   02 B PIC X(5).") + "\n";

        var parsed = CopybookParser.Parse(source);

        Assert.Equal(new[] { "A", "B" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(3, parsed.Flat[0].Len);
        Assert.Equal(5, parsed.Flat[1].Len);
        Assert.Equal(8, parsed.Root.Len);
    }

    [Fact]
    public void ThreeLineContinuation_OfOneLiteral_Parses()
    {
        var source =
            F("000100", ' ', "   01 REC.") + "\n" +
            F("000100", ' ', "   02 A PIC X(6) VALUE \"AB") + "\n" +
            F("000200", '-', "    \"CD") + "\n" +
            F("000300", '-', "    \"EF\".") + "\n" +
            F("000400", ' ', "   02 B PIC X(2).") + "\n";

        var parsed = CopybookParser.Parse(source);

        Assert.Equal(new[] { "A", "B" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(6, parsed.Flat[0].Len);
        Assert.Equal(6, parsed.Flat[1].Start);
        Assert.Equal(8, parsed.Root.Len);
    }

    // ---- Loud-failure guards ----

    [Fact]
    public void GenuinelyUnterminatedLiteral_NoContinuationLine_StillFailsLoud()
    {
        // No column-7 '-' anywhere: the open literal is a real error and must still
        // be rejected loudly by SplitStatements (never a silent miscompute).
        var source =
            F("000100", ' ', "   01 REC.") + "\n" +
            F("000100", ' ', "   02 A PIC X(5) VALUE \"oops no close.") + "\n" +
            F("000300", ' ', "   02 B PIC X(3).") + "\n";

        Assert.Throws<System.FormatException>(() => CopybookParser.Parse(source));
    }

    [Fact]
    public void OpenLiteral_ContinuationDoesNotReopen_FailsLoud()
    {
        // Preceding line leaves a literal open, but the continuation's area B does not
        // reopen it with a matching delimiter — unreconstructable, must fail loud.
        var source =
            F("000100", ' ', "   01 REC.") + "\n" +
            F("000100", ' ', "   02 A PIC X(3) VALUE \"A") + "\n" +
            F("000200", '-', "    BC\".") + "\n";

        Assert.Throws<System.FormatException>(() => CopybookParser.StripComments(source));
    }

    [Fact]
    public void ContinuationLine_WithNoPrecedingCode_FailsLoud()
    {
        var source = F("000100", '-', "    \"BC\".") + "\n";

        Assert.Throws<System.FormatException>(() => CopybookParser.StripComments(source));
    }

    // ---- Regression: non-continuation input is untouched ----

    [Fact]
    public void StripComments_NoContinuation_ByteIdenticalToPerLinePassthrough()
    {
        // A normal fixed-format copybook (no column-7 '-') must survive StripComments
        // with exactly the historical shape: sequence area dropped, code kept, one
        // '\n' per physical line, trailing '\n'.
        var source =
            F("000100", ' ', "   01 REC.") + "\n" +
            F("000200", ' ', "   02 A PIC X(3).") + "\n" +
            F("000300", ' ', "   02 B PIC 9(4).") + "\n";

        var stripped = CopybookParser.StripComments(source);

        // Trailing "\n\n": the source's final '\n' yields an empty trailing split
        // element that becomes its own blank output line — the historical shape.
        Assert.Equal(
            "    01 REC.\n    02 A PIC X(3).\n    02 B PIC 9(4).\n\n",
            stripped);
    }
}
