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
    public void StripsCols73IdentificationAreaWhenCols1To6SequenceAreaIsBlank()
    {
        // Real fixed-format copybooks often leave columns 1-6 blank while still
        // carrying an 8-digit sequence number in columns 73-80. With cols 1-6
        // blank the numeric-detection path never fires; without this rule the
        // trailing "00030000" leaks into the tokenizer and falsely rejects the
        // layout with "Malformed copybook entry".
        var source =
            "       01 REC.".PadRight(72) + "00010000\n" +
            "          05 A PIC X(3).".PadRight(72) + "00020000\n" +
            "          05 B PIC 9(4).".PadRight(72) + "00030000\n";

        var parsed = CopybookParser.Parse(source);

        Assert.Equal(new[] { "A", "B" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(0, parsed.Flat[0].Start);
        Assert.Equal(3, parsed.Flat[0].Len);
        Assert.Equal(3, parsed.Flat[1].Start);
        Assert.Equal(4, parsed.Flat[1].Len);
        Assert.Equal(7, parsed.Root.Len);

        var stripped = CopybookParser.StripComments(source);
        Assert.DoesNotContain("00030000", stripped);
    }

    [Fact]
    public void FreeFormatLineUnderColumn72IsUntouched()
    {
        // A short free-format line never reaches the cols-73 rule.
        var line = "    05  B PIC X(3).";
        Assert.True(line.Length <= 72);
        var stripped = CopybookParser.StripComments(line + "\n");
        Assert.Contains("05  B PIC X(3).", stripped);
    }

    [Fact]
    public void FreeFormatLineWithNonNumericTailPastColumn72IsPreserved()
    {
        // Content that legitimately runs past column 72 but is NOT an all-numeric
        // identification area (here a text VALUE literal) must be preserved: the
        // digit/space test (a) fails, so nothing is stripped.
        var line = "    05  MSG PIC X(80) VALUE 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaLONGLITERALtail'.";
        Assert.True(line.Length > 72);

        var stripped = CopybookParser.StripComments(line + "\n");

        Assert.Contains("LONGLITERALtail", stripped);
        Assert.Contains("VALUE 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaLONGLITERALtail'.", stripped);
    }

    [Fact]
    public void FreeFormatNumericLiteralRunningContinuouslyPastColumn72IsPreserved()
    {
        // A numeric VALUE literal that runs continuously past column 72 has NO
        // space immediately before the digits at column 73, so the space-gap test
        // (b) fails and the tail is preserved. Constructed so the character at
        // column 72 (0-indexed 71) is a digit that is part of the literal.
        var prefix = "    05  BIGNUM PIC 9(40) VALUE ";
        var digits = new string('7', 60);
        var line = prefix + digits + ".";
        // Column 72 (index 71) must sit inside the run of digits.
        Assert.True(line.Length > 72);
        Assert.True(char.IsDigit(line[71]));

        var stripped = CopybookParser.StripComments(line + "\n");

        Assert.Contains(prefix.TrimStart() + digits + ".", stripped);
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

    // ---- Level-66 RENAMES: tolerated and ignored (zero storage) ----

    [Fact]
    public void Level66RenamesIsToleratedAndIgnored()
    {
        // A 66 RENAMES alias regroups existing bytes; it adds no storage, so it is
        // dropped and the layout is unaffected.
        var source =
            "01  R.\n" +
            "    05  A PIC X(3).\n" +
            "    05  B PIC 9(4).\n" +
            "    66  A-AND-B RENAMES A THRU B.\n";
        var parsed = CopybookParser.Parse(source);
        Assert.Equal(new[] { "A", "B" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(0, parsed.Flat[0].Start);
        Assert.Equal(3, parsed.Flat[1].Start);
        Assert.Equal(7, parsed.Flat.Max(f => f.Start + f.Len));
    }

    // ---- Level-88 condition-names: tolerated and ignored ----

    /// <summary>
    /// A level-88 condition-name is metadata on the preceding data item and
    /// occupies zero storage. It must be silently dropped: only the real field
    /// appears, and every offset/length matches the same copybook with the 88
    /// lines removed.
    /// </summary>
    [Fact]
    public void ElementaryFieldFollowedByCondition88sIsUnaffected()
    {
        var with = CopybookParser.Parse(
            "01  R.\n" +
            "    05  STATUS-CODE PIC X(1).\n" +
            "        88  STATUS-OK    VALUE 'A'.\n" +
            "        88  STATUS-ERROR VALUE 'E'.\n" +
            "    05  AMOUNT PIC 9(4).\n");

        var without = CopybookParser.Parse(
            "01  R.\n" +
            "    05  STATUS-CODE PIC X(1).\n" +
            "    05  AMOUNT PIC 9(4).\n");

        Assert.Equal(new[] { "STATUS-CODE", "AMOUNT" }, with.Flat.Select(f => f.Name));
        Assert.Equal(
            without.Flat.Select(f => (f.Name, f.Start, f.Len)),
            with.Flat.Select(f => (f.Name, f.Start, f.Len)));
        Assert.Equal(without.Root.Len, with.Root.Len);
        Assert.Equal(5, with.Root.Len);
    }

    /// <summary>
    /// The 88 body is never interpreted — multiple values, a THRU range, a
    /// VALUES ARE form, and a literal containing a period (which the quote-aware
    /// splitter keeps in one statement) are all tolerated with the layout intact.
    /// </summary>
    [Fact]
    public void Condition88BodyFormsAreAllToleratedWithoutAffectingLayout()
    {
        var parsed = CopybookParser.Parse(
            "01  R.\n" +
            "    05  GRADE PIC X(1).\n" +
            "        88  GOOD       VALUE 'A' 'B' 'C'.\n" +
            "        88  RANGE      VALUE 'A' THRU 'Z'.\n" +
            "        88  RANGE-WORD VALUE 'A' THROUGH 'Z'.\n" +
            "        88  SET-FORM   VALUES ARE 'X' 'Y'.\n" +
            "        88  MSG        VALUE 'a.b'.\n" +
            "    05  TAIL PIC 9(2).\n");

        Assert.Equal(new[] { "GRADE", "TAIL" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(new[] { 0, 1 }, parsed.Flat.Select(f => f.Start));
        Assert.Equal(3, parsed.Root.Len);
    }

    /// <summary>
    /// 88s nested under a group item are dropped without disturbing the group's
    /// children or their offsets.
    /// </summary>
    [Fact]
    public void Condition88sUnderAGroupItemAreIgnored()
    {
        var parsed = CopybookParser.Parse(
            "01  R.\n" +
            "    05  FLAGS.\n" +
            "        10  FLAG-A PIC X(1).\n" +
            "            88  FLAG-A-ON VALUE '1'.\n" +
            "        10  FLAG-B PIC X(1).\n" +
            "            88  FLAG-B-ON VALUE '1'.\n" +
            "    05  TRAILER PIC X(3).\n");

        var flags = Assert.Single(parsed.Root.Children, c => c.Name == "FLAGS");
        Assert.True(flags.IsGroup);
        Assert.Equal(new[] { "FLAG-A", "FLAG-B" }, flags.Children.Select(c => c.Name));

        Assert.Equal(
            new[] { ("FLAG-A", 0, 1), ("FLAG-B", 1, 1), ("TRAILER", 2, 3) },
            parsed.Flat.Select(f => (f.Name, f.Start, f.Len)));
        Assert.Equal(5, parsed.Root.Len);
    }

    /// <summary>
    /// An 88 as the very last line of the copybook must not leave a dangling
    /// node or otherwise disturb the record.
    /// </summary>
    [Fact]
    public void Condition88AsFinalLineIsIgnored()
    {
        var parsed = CopybookParser.Parse(
            "01  R.\n" +
            "    05  A PIC X(3).\n" +
            "    05  B PIC X(2).\n" +
            "        88  B-SET VALUE 'ZZ'.\n");

        Assert.Equal(new[] { "A", "B" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(new[] { 0, 3 }, parsed.Flat.Select(f => f.Start));
        Assert.Equal(5, parsed.Root.Len);
    }

    // ---- No storage-bearing field: fail loudly, never a silent zero-byte record ----

    /// <summary>
    /// A bare 01 with no children defines no storage. Decoding against it would
    /// silently yield a zero-byte record, so parsing must fail loudly instead.
    /// </summary>
    [Fact]
    public void RejectsBare01WithNoFields()
    {
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse("01  R.\n"));
        Assert.Contains("no elementary fields", ex.Message);
        Assert.Contains("zero bytes", ex.Message);
    }

    /// <summary>
    /// A group whose subtree contains no PIC field carries no storage; the
    /// record would be zero bytes, so it must be rejected.
    /// </summary>
    [Fact]
    public void RejectsGroupWithNoChildren()
    {
        var ex = Assert.Throws<FormatException>(
            () => CopybookParser.Parse("01  R.\n    05  G.\n"));
        Assert.Contains("defines no storage", ex.Message);
        Assert.Contains("'G'", ex.Message);
    }

    /// <summary>
    /// A group whose only child is a level-88 condition-name has that 88 dropped,
    /// leaving the group childless and the record empty — reject, don't produce a
    /// silent zero-byte layout.
    /// </summary>
    [Fact]
    public void RejectsGroupWhoseOnlyChildIsAn88()
    {
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(
            "01  R.\n" +
            "    05  G.\n" +
            "        88  C VALUE 'X'.\n"));
        Assert.Contains("defines no storage", ex.Message);
        Assert.Contains("'G'", ex.Message);
    }

    /// <summary>
    /// A pic-less, childless item sitting BETWEEN real fields ("05 B.") used to be
    /// silently dropped — sliding every following field forward and under-sizing the
    /// record with no error, a silent miscompute. It must fail loud, naming B.
    /// </summary>
    [Fact]
    public void RejectsPicLessItemAmongRealFields()
    {
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(
            "01  R.\n    05  A PIC X(3).\n    05  B.\n    05  C PIC X(2).\n"));
        Assert.Contains("defines no storage", ex.Message);
        Assert.Contains("'B'", ex.Message);
    }

    /// <summary>
    /// A level-78 named constant carries no storage (like an 88): it is tolerated,
    /// dropped, and never affects the layout — so a copybook mixing a 78 constant
    /// with data fields parses, with the fields at their unshifted offsets.
    /// </summary>
    [Fact]
    public void Level78NamedConstantIsIgnored()
    {
        var parsed = CopybookParser.Parse(
            "01  R.\n    05  A PIC X(2).\n    78  MAX-ROWS VALUE 100.\n    05  B PIC X(3).\n");
        Assert.Equal(2, parsed.Flat.Count);
        Assert.Equal(("A", 0, 2), (parsed.Flat[0].Name, parsed.Flat[0].Start, parsed.Flat[0].Len));
        Assert.Equal(("B", 2, 3), (parsed.Flat[1].Name, parsed.Flat[1].Start, parsed.Flat[1].Len));
    }

    /// <summary>
    /// OCCURS is illegal on a level-01 (a record) or level-77 (a standalone item):
    /// neither may repeat. PICASSO used to silently expand it (a 12-byte record for
    /// `01 X PIC 9(3) OCCURS 4`), disagreeing with a real compiler; now it rejects.
    /// A repeating item belongs under a group (an 05 OCCURS inside an 01).
    /// </summary>
    [Theory]
    [InlineData("01  X PIC 9(3) OCCURS 4 TIMES.")]
    [InlineData("77  Y PIC 9 OCCURS 3 TIMES.")]
    [InlineData("01  Z PIC 9(2) OCCURS 0 TO 5 DEPENDING ON N.")]
    public void OccursOnAnO1OrO77LevelIsRejected(string line)
    {
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(line + "\n"));
        Assert.Contains("OCCURS is not allowed on a level-", ex.Message);
    }

    /// <summary>
    /// The empty-record guard must not misfire when a real field is present
    /// alongside an 88 that carries no storage: field A survives, the 88 is
    /// ignored, and the layout parses.
    /// </summary>
    [Fact]
    public void FieldPresentWithOnly88StillParses()
    {
        var parsed = CopybookParser.Parse(
            "01  R.\n" +
            "    05  A PIC X(3).\n" +
            "        88  C VALUE 'X'.\n");

        var a = parsed.Flat.Single();
        Assert.Equal("A", a.Name);
        Assert.Equal(3, a.Len);
        Assert.Equal(3, parsed.Root.Len);
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
    public void ParsesSignedDisplayNumericWithoutSeparateSignClauseAsOverpunch()
    {
        // A signed DISPLAY numeric without SIGN IS ... SEPARATE carries its sign
        // as an overpunch in the trailing digit's zone nibble — no extra byte, so
        // the field's width equals its digit count.
        var spec = CopybookParser.Parse("01  R.\n    05  A PIC S9(5).\n");
        var a = spec.Flat.Single();
        Assert.Equal(FieldType.NumericDisplay, a.Type);
        Assert.True(a.Signed);
        Assert.False(a.SignSeparate);
        Assert.False(a.SignLeading);   // trailing is the default
        Assert.Equal(5, a.Digits);
        Assert.Equal(5, a.Len);
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
    public void RedefinesOverlaysTargetOffset()
    {
        // REDEFINES is now supported: the redefining field overlays the target's
        // bytes rather than being placed at the next free offset. Full coverage
        // (groups, FILLER, longer/shorter redefinitions, decode round-trip, and
        // the encode-overlap rejection) lives in RedefinesTests; this asserts the
        // headline offset invariant right where the old rejection test stood.
        var parsed = CopybookParser.Parse(
            "01  R.\n    05  ORIGINAL PIC X(10).\n    05  ALIASED REDEFINES ORIGINAL PIC 9(10).\n");

        var original = parsed.Flat.Single(f => f.Name == "ORIGINAL");
        var aliased = parsed.Flat.Single(f => f.Name == "ALIASED");
        Assert.Equal(0, original.Start);
        Assert.Equal(0, aliased.Start);          // overlays ORIGINAL, not appended
        Assert.Equal(10, original.Len);
        Assert.Equal(10, aliased.Len);
        Assert.Equal(10, parsed.Root.Len);        // record is 10 bytes, not 20
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

    [Fact]
    public void TrailingDosEofByteIsIgnored()
    {
        // A 0x1A (DOS ^Z EOF marker) frequently trails a transferred copybook; it
        // must not become a stray token and reject the layout.
        var source = "01  R.\n    05  A PIC X(3).\n    05  B PIC 9(4).\n" + (char)0x1A;
        var parsed = CopybookParser.Parse(source);
        Assert.Equal(new[] { "A", "B" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(7, parsed.Flat.Max(f => f.Start + f.Len));
    }

    [Fact]
    public void ExecSqlDclgenBlockIsSkipped()
    {
        // A DB2 DCLGEN copybook: an EXEC SQL DECLARE ... TABLE ... END-EXEC block
        // (no COBOL storage) sits above the 01 host-variable structure. The block is
        // skipped; the structure parses normally.
        var source =
            "       EXEC SQL DECLARE MY.TBL TABLE\n" +
            "       ( C1 DECIMAL(9,0) NOT NULL,\n" +
            "         C2 CHAR(50)\n" +
            "       ) END-EXEC.\n" +
            "       01  DCLTBL.\n" +
            "           10 F1 PIC S9(9) USAGE COMP-4.\n" +
            "           10 F2 PIC X(50).\n";
        var parsed = CopybookParser.Parse(source);
        Assert.Equal(new[] { "F1", "F2" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(0, parsed.Flat[0].Start);
        Assert.Equal(4, parsed.Flat[0].Len);   // S9(9) COMP-4 = fullword
        Assert.Equal(4, parsed.Flat[1].Start);
        Assert.Equal(54, parsed.Flat.Max(f => f.Start + f.Len));
    }

    [Fact]
    public void ListingControlDirectivesAreSkipped()
    {
        // EJECT / SKIP1-3 / TITLE format the compiler's listing and carry no
        // storage; they must be ignored, not treated as data entries.
        var source =
            "       01  R.\n" +
            "       SKIP1\n" +
            "           05  A PIC X(3).\n" +
            "       EJECT\n" +
            "           05  B PIC 9(4).\n" +
            "       TITLE 'MY LISTING TITLE'.\n" +
            "           05  C PIC X(2).\n";
        var parsed = CopybookParser.Parse(source);
        Assert.Equal(new[] { "A", "B", "C" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(9, parsed.Flat.Max(f => f.Start + f.Len));
    }

    [Fact]
    public void AlphanumericSequenceNumberInCols1To6IsStripped()
    {
        // Real copybooks put programmer change-tags (letters+digits) in the cols-1-6
        // sequence area. The 6-digit-only rule missed them and the tag leaked as a
        // stray token; now a 6-char alphanumeric sequence area (with a level number
        // in the code area) is recognized and stripped.
        var source =
            "JL0001 01  REC.\n" +
            "JL0001     05 FLD-A PIC X(5).\n" +
            "JL0001     05 FLD-B PIC 9(3).\n";
        var parsed = CopybookParser.Parse(source);
        Assert.Equal(new[] { "FLD-A", "FLD-B" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(0, parsed.Flat[0].Start);
        Assert.Equal(5, parsed.Flat[1].Start);
        Assert.Equal(8, parsed.Flat.Max(f => f.Start + f.Len));
    }

    [Fact]
    public void OffColumnStarCommentBannerIsStripped()
    {
        // Fixed-format comments live in column 7, but many exported copybooks leave
        // column 7 blank and start a '*' banner in Area A (column 8). Such a banner
        // must still be recognized as a comment; otherwise it glues onto the first
        // real data item. Column layout below: 7 spaces, then '*' at column 8.
        var source =
            "       ****************************\n" +
            "       * STORE DETAILS EXTRACT     \n" +
            "       ****************************\n" +
            "        03  DTAR-REC.\n" +
            "            10 DTAR-STORE-NO PIC X(5).\n" +
            "            10 DTAR-REGION-NO PIC 9(3).\n";
        var parsed = CopybookParser.Parse(source);
        Assert.Equal(new[] { "DTAR-STORE-NO", "DTAR-REGION-NO" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(0, parsed.Flat[0].Start);
        Assert.Equal(5, parsed.Flat[1].Start);
        Assert.Equal(8, parsed.Flat.Max(f => f.Start + f.Len));
    }

    [Fact]
    public void ElementaryItemWithSubordinateFailsLoudly()
    {
        // An item with a PICTURE is elementary and cannot contain sub-items. A
        // following higher level number under it (here 10 under a 05-with-PIC) is
        // malformed COBOL — a compiler rejects it. Left unchecked, the subordinate
        // was silently attached then dropped by the flattener, so the field vanished
        // and every following offset shifted (a silent miscompute). Must fail loudly.
        var source =
            "       01 REC.\n" +
            "          05 A PIC X(2).\n" +
            "             10 B PIC X(3).\n" +
            "          03 C PIC X(4).\n";
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(source));
        Assert.Contains("PICTURE", ex.Message);
        Assert.Contains("'A'", ex.Message);
        Assert.Contains("'B'", ex.Message);
    }

    [Theory]
    [InlineData("COMPUTATIONAL3")] // dropped hyphen
    [InlineData("COMP-0")]         // no such usage
    [InlineData("COMP-7")]         // no such usage
    public void UnrecognizedComputationalUsageFailsLoudly(string usage)
    {
        // A mistyped or unsupported COMPUTATIONAL usage changes a field's physical
        // width. Silently skipping it (the old default) left the field mis-sized as
        // DISPLAY with no error — a silent miscompute. It must fail loudly.
        var source = $"       01 REC.\n          05 A PIC 9(4) {usage}.\n";
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(source));
        Assert.Contains("COMPUTATIONAL usage", ex.Message);
        Assert.Contains(usage, ex.Message);
    }

    [Fact]
    public void KnownUsagesAndValueLiteralsAreNotFalselyRejected()
    {
        // Guard against over-rejection: a valid COMP-4 must still parse, and a
        // multi-word VALUE string literal — even one that contains the bare word
        // "COMP" — must not trip the computational-usage check (it requires trailing
        // digits, so a plain word in a literal never matches).
        var source =
            "       01 REC.\n" +
            "          05 A PIC S9(4) COMP-4.\n" +
            "          05 B PIC X(20) VALUE 'HELLO WORLD FROM COMP'.\n";
        var parsed = CopybookParser.Parse(source);
        Assert.Equal(new[] { "A", "B" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(2, parsed.Flat[0].Len);   // COMP-4, 4 digits → 2 bytes
        Assert.Equal(20, parsed.Flat[1].Len);
    }
}
