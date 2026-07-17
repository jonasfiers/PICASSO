using System;
using System.IO;
using System.Linq;
using System.Text;
using Picasso.Core;
using Picasso.Core.Models;
using Xunit;

namespace Picasso.Core.Tests;

public class CopybookParserTests
{
    private static ParsedCopybook ParsePortrait() =>
        CopybookParser.Parse(File.ReadAllText(SamplePaths.Root("PORTRAIT-REC.cpy"), Encoding.Latin1));

    // ---- StripComments ----

    [Fact]
    public void StripsFreeFormatCommentsToEndOfLine()
    {
        var stripped = CopybookParser.StripComments("01  A-REC. *> trailing note\n    05  B PIC X(3).\n");
        Assert.DoesNotContain("trailing note", stripped);
        Assert.Contains("01  A-REC.", stripped);
        Assert.Contains("05  B PIC X(3).", stripped);
    }

    [Fact]
    public void StripsFixedFormatColumn7CommentLines()
    {
        // Column 7 (0-indexed 6) is '*', and not the start of a '*>' —
        // the whole line is a traditional fixed-format comment.
        var stripped = CopybookParser.StripComments("      * legacy comment line\n       01  A-REC.\n");
        Assert.DoesNotContain("legacy comment", stripped);
        Assert.Contains("01  A-REC.", stripped);
    }

    // ---- Fixed-format source (columns 1-6 sequence number, column 7 indicator) ----

    [Fact]
    public void StripsSequenceNumbersAndColumn7CommentsFromFixedFormatSource()
    {
        var source =
            "000100*   A COMMENT LINE, INDICATED BY THE '*' IN COLUMN 7\n" +
            "000200 01  A-REC.\n" +
            "000300     05  B PIC X(3).\n" +
            "000400     05  C PIC 9(4).\n";

        var parsed = CopybookParser.Parse(source);

        Assert.Equal(new[] { "B", "C" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(0, parsed.Flat[0].Start);
        Assert.Equal(3, parsed.Flat[1].Start);
        Assert.Equal(7, parsed.Root.Len);
    }

    [Fact]
    public void FixedAndFreeFormatLinesCanMixInOneCopybook()
    {
        // Detection is per line, so a free-format 01 and a fixed-format copy
        // member can sit in one file — the shape DTAR020.cbl needed before the
        // parser learned to supply the 01 itself.
        var source =
            "01  A-REC.\n" +
            "000100*   FIXED-FORMAT COMMENT\n" +
            "000200     05  B PIC X(3).\n" +
            "    05  C PIC X(2). *> free-format comment\n";

        var parsed = CopybookParser.Parse(source);

        Assert.Equal(new[] { "B", "C" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(5, parsed.Root.Len);
    }

    [Fact]
    public void IgnoresFixedFormatContentBeyondColumn72()
    {
        // Columns 73-80 are an identification area the compiler ignores. Here it
        // holds junk that would otherwise tokenize into the PIC clause's entry.
        var line = "000200     05  B PIC X(3).";
        var padded = line.PadRight(72) + "PROGID99";
        Assert.Equal(80, padded.Length);

        var parsed = CopybookParser.Parse("000100 01  A-REC.\n" + padded + "\n");

        Assert.Equal("B", Assert.Single(parsed.Flat).Name);
        Assert.Equal(3, parsed.Root.Len);
        Assert.DoesNotContain("PROGID99", CopybookParser.StripComments(padded));
    }

    [Fact]
    public void FreeFormatLevelNumbersAreNotMistakenForSequenceNumbers()
    {
        // Regression guard for the detection rule: only six leading digits mark a
        // line as fixed-format, and free-format code never opens a line that way.
        var stripped = CopybookParser.StripComments("01  A-REC.\n    05  B PIC 9(6).\n");

        Assert.Contains("01  A-REC.", stripped);
        Assert.Contains("05  B PIC 9(6).", stripped);
    }

    [Fact]
    public void RealCopybookCommentBannerIsStrippedEntirely()
    {
        // The vendored copybooks lead with a '      *>' banner: column 7 is
        // '*' but it is a free-format '*>' marker, so it must strip via the
        // '*>' path rather than the column-7 path. Same result either way.
        var stripped = CopybookParser.StripComments(
            File.ReadAllText(SamplePaths.Catalog74("BALANCE-REC.cpy"), Encoding.Latin1));
        Assert.DoesNotContain("derived output", stripped);
        Assert.Contains("BALANCE-REC", stripped);
    }

    // ---- SplitStatements ----

    [Fact]
    public void SplitsOnPeriodsAndCollapsesWhitespace()
    {
        var statements = CopybookParser.SplitStatements("01  A-REC.\n    05  B    PIC   X(3).\n");
        Assert.Equal(new[] { "01 A-REC", "05 B PIC X(3)" }, statements);
    }

    [Fact]
    public void IgnoresTrailingWhitespaceOnlyChunks()
    {
        Assert.Equal(new[] { "01 A-REC" }, CopybookParser.SplitStatements("01  A-REC.\n\n   \n"));
    }

    // ---- BuildTree ----

    [Fact]
    public void BuildsThreeLevelTree()
    {
        var root = ParsePortrait().Root;

        Assert.Equal("PORTRAIT-REC", root.Name);
        Assert.Equal(1, root.LevelNumber);
        Assert.True(root.IsGroup);

        Assert.Equal(
            new[] { "ARTIST-ID", "ARTIST-NAME", "STUDIO-ADDRESS", "CANVAS-COUNT", "TOTAL-VALUE", "NET-WORTH" },
            root.Children.Select(c => c.Name));

        var address = root.Children.Single(c => c.Name == "STUDIO-ADDRESS");
        Assert.True(address.IsGroup);
        Assert.Equal(5, address.LevelNumber);
        Assert.Equal(new[] { "STREET", "CITY", "POSTAL-CODE" }, address.Children.Select(c => c.Name));
        Assert.All(address.Children, c => Assert.Equal(10, c.LevelNumber));
    }

    [Fact]
    public void SiblingsAtSameLevelAttachToTheSameParent()
    {
        // A 10-level following a nested 10-level group must pop back to the
        // group, not chain ever-deeper.
        var root = CopybookParser.BuildTree(CopybookParser.SplitStatements(@"
            01  R.
                05  G.
                    10  A PIC X(1).
                    10  B PIC X(1).
                05  C PIC X(1).
        "))!;

        Assert.Equal(new[] { "G", "C" }, root.Children.Select(c => c.Name));
        Assert.Equal(new[] { "A", "B" }, root.Children[0].Children.Select(c => c.Name));
    }

    [Fact]
    public void PicBearingEntriesAreLeaves()
    {
        var root = ParsePortrait().Root;
        var artistId = root.Children.Single(c => c.Name == "ARTIST-ID");
        Assert.False(artistId.IsGroup);
        Assert.NotNull(artistId.Field);
        Assert.Empty(artistId.Children);
    }

    // ---- Headless copy members (no 01 of their own) ----

    /// <summary>
    /// A copy member written to be COPY'd into a record the including program
    /// names — the ordinary shape for an FD record layout. The parser supplies
    /// the 01 that COBOL would have gotten from the program.
    /// </summary>
    [Fact]
    public void SuppliesAnO1ForAHeadlessCopyMember()
    {
        var parsed = CopybookParser.Parse(@"
            03  A-GROUP.
                05  B PIC X(3).
                05  C PIC X(2).
            03  D PIC X(4).
        ");

        Assert.True(parsed.RootIsSynthetic);
        Assert.Equal(1, parsed.Root.LevelNumber);
        Assert.Equal(CopybookParser.SyntheticRecordName, parsed.Root.Name);
        Assert.Equal(new[] { "A-GROUP", "D" }, parsed.Root.Children.Select(c => c.Name));

        // The wrapper is structural only — it moves nothing.
        Assert.Equal(new[] { "B", "C", "D" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(new[] { 0, 3, 5 }, parsed.Flat.Select(f => f.Start));
        Assert.Equal(9, parsed.Root.Len);
    }

    /// <summary>
    /// The discriminator that matters. Two 01 records and a headless fragment
    /// both used to hit one error, but they are not the same thing: two 01s are
    /// alternative record layouts, and wrapping them would silently concatenate
    /// them into a single record that describes neither.
    /// </summary>
    [Fact]
    public void TwoO1RecordsAreStillRejectedRatherThanMergedIntoOne()
    {
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(@"
            01  FIRST-REC.
                05  A PIC X(3).
            01  SECOND-REC.
                05  B PIC X(4).
        "));

        Assert.Contains("FIRST-REC", ex.Message);
        Assert.Contains("SECOND-REC", ex.Message);
        Assert.Contains("alternative layouts", ex.Message);
    }

    [Fact]
    public void ACopybookWithItsOwnO1IsUntouched()
    {
        var parsed = CopybookParser.Parse("01  R.\n    05  A PIC X(3).\n");

        Assert.False(parsed.RootIsSynthetic);
        Assert.Equal("R", parsed.Root.Name);
    }

    [Fact]
    public void ASingleHeadlessGroupIsAlsoWrapped()
    {
        // This one already produced the right layout before, rooted at 03. It
        // gets the same 01 as any other headless member, so "the root is the
        // record" holds for every copybook rather than most of them.
        var parsed = CopybookParser.Parse("03  A-GROUP.\n    05  B PIC X(3).\n");

        Assert.True(parsed.RootIsSynthetic);
        Assert.Equal(1, parsed.Root.LevelNumber);
        Assert.Equal("A-GROUP", Assert.Single(parsed.Root.Children).Name);
    }

    [Fact]
    public void TheSyntheticRecordNameIsTheCallersToChoose()
    {
        // The copybook genuinely doesn't say what the record is called — that
        // name lives in the program that COPY's it, which PICASSO never sees.
        var parsed = CopybookParser.Parse("03  A PIC X(3).\n", "DTAR020-REC");

        Assert.True(parsed.RootIsSynthetic);
        Assert.Equal("DTAR020-REC", parsed.Root.Name);

        Assert.Throws<ArgumentException>(() => CopybookParser.Parse("03  A PIC X(3).\n", "  "));
    }

    [Fact]
    public void HeadlessEntriesDisagreeingOnLevelNumberAreRejected()
    {
        // Siblings of one record share a level number. Anything else is
        // malformed, and which record it was meant to be isn't guessable.
        var ex = Assert.Throws<FormatException>(
            () => CopybookParser.Parse("05  A PIC X(3).\n03  B PIC X(2).\n"));

        Assert.Contains("disagree on level number", ex.Message);
    }

    // ---- ComputeOffsets ----

    [Fact]
    public void ComputesLeafOffsetsDepthFirst()
    {
        var flat = ParsePortrait().Flat;

        Assert.Equal(
            new[]
            {
                ("ARTIST-ID", 0, 6),
                ("ARTIST-NAME", 6, 30),
                ("STREET", 36, 25),
                ("CITY", 61, 20),
                ("POSTAL-CODE", 81, 10),
                ("CANVAS-COUNT", 91, 4),
                ("TOTAL-VALUE", 95, 6),
                ("NET-WORTH", 101, 12),
            },
            flat.Select(f => (f.Name, f.Start, f.Len)));
    }

    [Fact]
    public void GroupRollsUpChildSizes()
    {
        var root = ParsePortrait().Root;
        var address = root.Children.Single(c => c.Name == "STUDIO-ADDRESS");

        Assert.Equal(36, address.Start);
        Assert.Equal(25 + 20 + 10, address.Len);

        // The 01 level rolls up the whole record.
        Assert.Equal(0, root.Start);
        Assert.Equal(113, root.Len);
        Assert.Equal(root.Len, ParsePortrait().Flat.Sum(f => f.Len));
    }

    [Fact]
    public void FieldStartMatchesOwningNodeStart()
    {
        var root = ParsePortrait().Root;
        var netWorth = root.Children.Single(c => c.Name == "NET-WORTH");
        Assert.Equal(netWorth.Start, netWorth.Field!.Start);
    }

    // ---- Flatten ----

    [Fact]
    public void FlattenReturnsLeavesOnlyInByteOrder()
    {
        var flat = ParsePortrait().Flat;
        Assert.DoesNotContain(flat, f => f.Name == "STUDIO-ADDRESS");
        Assert.DoesNotContain(flat, f => f.Name == "PORTRAIT-REC");
        Assert.Equal(8, flat.Count);
        Assert.Equal(flat.OrderBy(f => f.Start).Select(f => f.Name), flat.Select(f => f.Name));
    }

    // ---- Field sizing ----

    [Fact]
    public void Comp3FieldUsesPackedByteLengthNotDigitCount()
    {
        var totalValue = ParsePortrait().Flat.Single(f => f.Name == "TOTAL-VALUE");
        Assert.Equal(FieldType.Comp3, totalValue.Type);
        Assert.Equal(11, totalValue.Digits);
        Assert.Equal(2, totalValue.Scale);
        Assert.True(totalValue.Signed);
        Assert.Equal(6, totalValue.Len); // ceil((11+1)/2), not 11
    }

    [Fact]
    public void SignLeadingSeparateReservesOneExtraByteWithinTheSameField()
    {
        var netWorth = ParsePortrait().Flat.Single(f => f.Name == "NET-WORTH");
        Assert.Equal(FieldType.NumericDisplay, netWorth.Type);
        Assert.Equal(11, netWorth.Digits);
        Assert.True(netWorth.Signed);
        Assert.True(netWorth.SignSeparate);
        Assert.True(netWorth.SignLeading);
        Assert.Equal(12, netWorth.Len); // 11 digits + 1 sign byte, one field
    }

    [Fact]
    public void SignTrailingSeparateIsDistinguishedFromLeading()
    {
        var parsed = CopybookParser.Parse(
            File.ReadAllText(SamplePaths.Catalog74("BALANCE-REC.cpy"), Encoding.Latin1));
        var netBalance = parsed.Flat.Single(f => f.Name == "NET-BALANCE");

        Assert.True(netBalance.SignSeparate);
        Assert.False(netBalance.SignLeading);
        Assert.Equal(10, netBalance.Len); // 9 digits + 1 trailing sign byte
    }

    [Fact]
    public void UnsignedDisplayNumericHasNoSignByte()
    {
        var canvasCount = ParsePortrait().Flat.Single(f => f.Name == "CANVAS-COUNT");
        Assert.False(canvasCount.Signed);
        Assert.False(canvasCount.SignSeparate);
        Assert.Equal(4, canvasCount.Len);
    }

    // ---- Rejections ----

    [Theory]
    [InlineData(66, "RENAMES")]
    [InlineData(88, "condition-name")]
    public void RejectsUnsupportedLevelNumbers(int level, string expectedInMessage)
    {
        var source = $"01  R.\n    05  A PIC X(3).\n    {level}  B-COND VALUE 'X'.\n";
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(source));
        Assert.Contains(expectedInMessage, ex.Message);
        Assert.Contains(level.ToString(), ex.Message);
    }

    [Fact]
    public void RejectsMultipleTopLevelRecords()
    {
        var source = "01  FIRST-REC.\n    05  A PIC X(3).\n01  SECOND-REC.\n    05  B PIC X(3).\n";
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(source));
        Assert.Contains("FIRST-REC", ex.Message);
        Assert.Contains("SECOND-REC", ex.Message);
    }

    [Fact]
    public void RejectsSignedDisplayNumericWithoutSeparateSignClause()
    {
        // Overpunched sign encoding is out of scope; failing loudly beats
        // silently mis-sizing the field by one byte.
        var ex = Assert.Throws<FormatException>(
            () => CopybookParser.Parse("01  R.\n    05  A PIC S9(5).\n"));
        Assert.Contains("SIGN IS LEADING/TRAILING SEPARATE", ex.Message);
    }

    [Fact]
    public void RejectsComp3OnAlphanumericPicture()
    {
        Assert.Throws<FormatException>(
            () => CopybookParser.Parse("01  R.\n    05  A PIC X(5) COMP-3.\n"));
    }

    [Fact]
    public void RejectsEmptySource()
    {
        Assert.Throws<FormatException>(() => CopybookParser.Parse("      *> only a comment\n"));
    }

    // Fixed-count OCCURS is now a supported feature; its full behaviour lives in
    // OccursTests. The rejection cases that must survive alongside it — the
    // variable-length (ODO) form and the table-of-tables form — are asserted
    // there too, so the "easier case didn't swallow the harder ones" guarantee
    // is regression-tested next to the feature it guards.

    [Fact]
    public void RejectsRedefines()
    {
        // Previously silently ignored the same way: REDEFINES was skipped, so
        // the redefining field was placed at the next free offset instead of
        // overlapping the field it redefines, corrupting every offset after it.
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(
            "01  R.\n    05  ORIGINAL PIC X(10).\n    05  ALIASED REDEFINES ORIGINAL PIC 9(10).\n"));
        Assert.Contains("REDEFINES", ex.Message);
        Assert.Contains("ALIASED", ex.Message);
    }

    // ---- Corpus smoke test ----

    [Theory]
    [InlineData("AMOUNT-OWED-REC.cpy")]
    [InlineData("AMOUNT-PAID-REC.cpy")]
    [InlineData("BALANCE-REC.cpy")]
    [InlineData("EXPENSE-REC.cpy")]
    [InlineData("GROUP-REC.cpy")]
    [InlineData("MEMBER-REC.cpy")]
    [InlineData("SHARE-REC.cpy")]
    [InlineData("USER-AUTH-REC.cpy")]
    [InlineData("USER-REC.cpy")]
    public void EveryVendoredCopybookParsesToAContiguousLayout(string fileName)
    {
        var parsed = CopybookParser.Parse(File.ReadAllText(SamplePaths.Catalog74(fileName), Encoding.Latin1));

        Assert.NotEmpty(parsed.Flat);

        // Fields must tile the record with no gaps and no overlaps.
        var cursor = 0;
        foreach (var field in parsed.Flat)
        {
            Assert.Equal(cursor, field.Start);
            Assert.True(field.Len > 0, $"Field '{field.Name}' has non-positive length.");
            cursor += field.Len;
        }
        Assert.Equal(parsed.Root.Len, cursor);
    }
}
