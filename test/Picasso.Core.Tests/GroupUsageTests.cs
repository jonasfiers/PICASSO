using System;
using System.Collections.Generic;
using System.Linq;
using Picasso.Core;
using Picasso.Core.Models;
using Xunit;

namespace Picasso.Core.Tests;

/// <summary>
/// Group-level USAGE inheritance. A USAGE clause on a GROUP item applies to
/// every subordinate elementary item that states no USAGE of its own — standard
/// COBOL. PICASSO used to ignore it and silently default the children to
/// DISPLAY, producing a wrong byte layout with no error (a silent miscompute —
/// the worst class of bug for this project). These tests pin the correct
/// inherited layouts, the override rules (nearest ancestor wins, a child's own
/// usage wins, an inner group overrides an outer), that non-group-USAGE layouts
/// are untouched, that an inherited-yet-unsupported/illegal usage still fails
/// loud, and that a group-COMP-3 record round-trips byte-for-byte.
///
/// Per-field lengths are cross-checked against GnuCOBOL (cobc -std=mvs,
/// LENGTH OF) in tools/layout-oracle — see the group-usage fixtures there.
/// </summary>
public class GroupUsageTests
{
    private static FieldSpec Field(ParsedCopybook p, string name) =>
        p.Flat.Single(f => f.Name == name);

    // ---- The minimal repro from the task brief ----

    /// <summary>
    /// 01 REC. 05 GRP COMP-3. 10 A PIC 9(5). 10 B PIC 9(7).
    /// A and B state no usage, so both inherit COMP-3 from GRP: A is 3 bytes
    /// (ceil((5+1)/2)), B is 4 bytes (ceil((7+1)/2)), record 7 — NOT the 5+7=12
    /// DISPLAY bytes PICASSO used to compute.
    /// </summary>
    [Fact]
    public void GroupComp3IsInheritedBySubordinateElementaries()
    {
        var parsed = CopybookParser.Parse(
            "01 REC.\n" +
            "   05 GRP COMP-3.\n" +
            "      10 A PIC 9(5).\n" +
            "      10 B PIC 9(7).\n");

        var a = Field(parsed, "A");
        var b = Field(parsed, "B");

        Assert.Equal(FieldType.Comp3, a.Type);
        Assert.Equal(0, a.Start);
        Assert.Equal(3, a.Len);
        Assert.Equal(5, a.Digits);

        Assert.Equal(FieldType.Comp3, b.Type);
        Assert.Equal(3, b.Start);
        Assert.Equal(4, b.Len);
        Assert.Equal(7, b.Digits);

        Assert.Equal(7, parsed.Root.Len);
    }

    /// <summary>USAGE IS COMP-3 (the verbose spelling) at group level inherits identically.</summary>
    [Fact]
    public void GroupUsageIsComp3VerboseSpellingIsInherited()
    {
        var parsed = CopybookParser.Parse(
            "01 REC.\n" +
            "   05 GRP USAGE IS COMP-3.\n" +
            "      10 A PIC 9(5).\n" +
            "      10 B PIC 9(7).\n");

        Assert.Equal(FieldType.Comp3, Field(parsed, "A").Type);
        Assert.Equal(FieldType.Comp3, Field(parsed, "B").Type);
        Assert.Equal(7, parsed.Root.Len);
    }

    /// <summary>PACKED-DECIMAL at group level is COMP-3 and inherits the same way.</summary>
    [Fact]
    public void GroupPackedDecimalIsInheritedAsComp3()
    {
        var parsed = CopybookParser.Parse(
            "01 REC.\n" +
            "   05 GRP PACKED-DECIMAL.\n" +
            "      10 A PIC 9(5).\n");

        Assert.Equal(FieldType.Comp3, Field(parsed, "A").Type);
        Assert.Equal(3, Field(parsed, "A").Len);
    }

    // ---- Binary (COMP) group usage, width by digit count ----

    /// <summary>
    /// Group COMP over 2/4/8-digit numerics → binary widths 2/4/8 (IBM sizing).
    /// PIC 9(4)→2, 9(6)→4, 9(11)→8, record 14.
    /// </summary>
    [Fact]
    public void GroupCompIsInheritedWithBinaryWidths()
    {
        var parsed = CopybookParser.Parse(
            "01 REC.\n" +
            "   05 GRP COMP.\n" +
            "      10 A PIC 9(4).\n" +
            "      10 B PIC 9(6).\n" +
            "      10 C PIC 9(11).\n");

        var a = Field(parsed, "A");
        var b = Field(parsed, "B");
        var c = Field(parsed, "C");

        Assert.Equal(FieldType.Binary, a.Type);
        Assert.Equal(0, a.Start);
        Assert.Equal(2, a.Len);

        Assert.Equal(FieldType.Binary, b.Type);
        Assert.Equal(2, b.Start);
        Assert.Equal(4, b.Len);

        Assert.Equal(FieldType.Binary, c.Type);
        Assert.Equal(6, c.Start);
        Assert.Equal(8, c.Len);

        Assert.Equal(14, parsed.Root.Len);
    }

    [Theory]
    [InlineData("BINARY")]
    [InlineData("COMP-4")]
    [InlineData("COMP-5")]
    [InlineData("COMPUTATIONAL")]
    public void GroupBinaryAliasesAllInheritAsBinary(string usage)
    {
        var parsed = CopybookParser.Parse(
            "01 REC.\n" +
            $"   05 GRP {usage}.\n" +
            "      10 A PIC 9(4).\n");

        Assert.Equal(FieldType.Binary, Field(parsed, "A").Type);
        Assert.Equal(2, Field(parsed, "A").Len);
    }

    // ---- Override rules ----

    /// <summary>A child's OWN usage overrides the group's inherited usage.</summary>
    [Fact]
    public void ChildOwnUsageOverridesInheritedGroupUsage()
    {
        var parsed = CopybookParser.Parse(
            "01 REC.\n" +
            "   05 GRP COMP-3.\n" +
            "      10 A PIC 9(5).\n" +          // inherits COMP-3 -> 3 bytes
            "      10 B PIC 9(5) DISPLAY.\n" +  // own DISPLAY -> 5 bytes
            "      10 C PIC 9(4) COMP.\n");     // own COMP    -> 2 bytes

        Assert.Equal(FieldType.Comp3, Field(parsed, "A").Type);
        Assert.Equal(3, Field(parsed, "A").Len);

        Assert.Equal(FieldType.NumericDisplay, Field(parsed, "B").Type);
        Assert.Equal(5, Field(parsed, "B").Len);

        Assert.Equal(FieldType.Binary, Field(parsed, "C").Type);
        Assert.Equal(2, Field(parsed, "C").Len);

        Assert.Equal(3 + 5 + 2, parsed.Root.Len);
    }

    /// <summary>
    /// Nested groups: an inner group's USAGE overrides an outer group's for its
    /// own subtree; a sibling elementary of the inner group still inherits the
    /// outer usage (nearest ancestor wins).
    /// </summary>
    [Fact]
    public void InnerGroupUsageOverridesOuterForItsSubtree()
    {
        var parsed = CopybookParser.Parse(
            "01 REC.\n" +
            "   05 OUTER COMP-3.\n" +
            "      10 A PIC 9(5).\n" +          // inherits OUTER COMP-3 -> 3
            "      10 INNER COMP.\n" +
            "         15 B PIC 9(4).\n" +       // inherits INNER COMP  -> 2
            "         15 C PIC 9(6).\n" +       // inherits INNER COMP  -> 4
            "      10 D PIC 9(7).\n");          // inherits OUTER COMP-3 -> 4

        Assert.Equal(FieldType.Comp3, Field(parsed, "A").Type);
        Assert.Equal(3, Field(parsed, "A").Len);

        Assert.Equal(FieldType.Binary, Field(parsed, "B").Type);
        Assert.Equal(2, Field(parsed, "B").Len);
        Assert.Equal(FieldType.Binary, Field(parsed, "C").Type);
        Assert.Equal(4, Field(parsed, "C").Len);

        Assert.Equal(FieldType.Comp3, Field(parsed, "D").Type);
        Assert.Equal(4, Field(parsed, "D").Len);

        Assert.Equal(3 + 2 + 4 + 4, parsed.Root.Len);
    }

    /// <summary>
    /// Nearest-ancestor wins across a usage-less intermediate group: an inner
    /// group that states NO usage transmits the outer group's usage unchanged to
    /// its own children.
    /// </summary>
    [Fact]
    public void UsageFlowsThroughAUsagelessInnerGroup()
    {
        var parsed = CopybookParser.Parse(
            "01 REC.\n" +
            "   05 OUTER COMP-3.\n" +
            "      10 INNER.\n" +               // no usage of its own
            "         15 A PIC 9(5).\n");       // still inherits OUTER COMP-3

        Assert.Equal(FieldType.Comp3, Field(parsed, "A").Type);
        Assert.Equal(3, Field(parsed, "A").Len);
    }

    /// <summary>
    /// An inner group DISPLAY overrides an outer group COMP-3: the inner subtree
    /// is DISPLAY-sized even though the outer group is packed.
    /// </summary>
    [Fact]
    public void InnerGroupDisplayOverridesOuterComp3()
    {
        var parsed = CopybookParser.Parse(
            "01 REC.\n" +
            "   05 OUTER COMP-3.\n" +
            "      10 INNER DISPLAY.\n" +
            "         15 A PIC 9(5).\n");       // inner DISPLAY -> 5 bytes, not 3

        Assert.Equal(FieldType.NumericDisplay, Field(parsed, "A").Type);
        Assert.Equal(5, Field(parsed, "A").Len);
    }

    // ---- No-op / unchanged cases ----

    /// <summary>Group DISPLAY is a no-op: children keep their DISPLAY sizing.</summary>
    [Fact]
    public void GroupDisplayIsANoOp()
    {
        var parsed = CopybookParser.Parse(
            "01 REC.\n" +
            "   05 GRP DISPLAY.\n" +
            "      10 A PIC 9(5).\n" +
            "      10 B PIC X(3).\n");

        Assert.Equal(FieldType.NumericDisplay, Field(parsed, "A").Type);
        Assert.Equal(5, Field(parsed, "A").Len);
        Assert.Equal(FieldType.Text, Field(parsed, "B").Type);
        Assert.Equal(3, Field(parsed, "B").Len);
        Assert.Equal(8, parsed.Root.Len);
    }

    /// <summary>
    /// A group with NO usage contributes nothing: a mix of children keep their
    /// own usages (or the DISPLAY default), byte-identical to pre-feature output.
    /// </summary>
    [Fact]
    public void GroupWithNoUsageLeavesChildrenUntouched()
    {
        var parsed = CopybookParser.Parse(
            "01 REC.\n" +
            "   05 GRP.\n" +
            "      10 A PIC 9(5).\n" +           // DISPLAY default -> 5
            "      10 B PIC 9(5) COMP-3.\n" +    // own COMP-3      -> 3
            "      10 C PIC X(4).\n");           // text            -> 4

        Assert.Equal(FieldType.NumericDisplay, Field(parsed, "A").Type);
        Assert.Equal(5, Field(parsed, "A").Len);
        Assert.Equal(FieldType.Comp3, Field(parsed, "B").Type);
        Assert.Equal(3, Field(parsed, "B").Len);
        Assert.Equal(FieldType.Text, Field(parsed, "C").Type);
        Assert.Equal(4, Field(parsed, "C").Len);
        Assert.Equal(12, parsed.Root.Len);
    }

    /// <summary>
    /// A per-elementary-usage copybook with NO group usage anywhere is byte-for-
    /// byte identical to what the parser produced before this feature — the same
    /// field types, starts and lengths. Guards the "don't change already-correct
    /// cases" boundary.
    /// </summary>
    [Fact]
    public void PerElementaryUsageWithoutGroupUsageIsByteIdentical()
    {
        var parsed = CopybookParser.Parse(
            "01 REC.\n" +
            "   05 A PIC 9(5).\n" +
            "   05 B PIC S9(5) COMP-3.\n" +
            "   05 C PIC 9(4) COMP.\n" +
            "   05 D PIC X(10).\n" +
            "   05 E PIC S9(3) SIGN IS LEADING SEPARATE.\n");

        var a = Field(parsed, "A");
        Assert.Equal(FieldType.NumericDisplay, a.Type);
        Assert.Equal(0, a.Start);
        Assert.Equal(5, a.Len);

        var b = Field(parsed, "B");
        Assert.Equal(FieldType.Comp3, b.Type);
        Assert.Equal(5, b.Start);
        Assert.Equal(3, b.Len);

        var c = Field(parsed, "C");
        Assert.Equal(FieldType.Binary, c.Type);
        Assert.Equal(8, c.Start);
        Assert.Equal(2, c.Len);

        var d = Field(parsed, "D");
        Assert.Equal(FieldType.Text, d.Type);
        Assert.Equal(10, d.Start);
        Assert.Equal(10, d.Len);

        var e = Field(parsed, "E");
        Assert.Equal(FieldType.NumericDisplay, e.Type);
        Assert.Equal(20, e.Start);
        Assert.Equal(4, e.Len);           // 3 digits + 1 separate sign byte
        Assert.True(e.SignSeparate);
        Assert.True(e.SignLeading);

        Assert.Equal(24, parsed.Root.Len);
    }

    // ---- Sign flags preserved through inheritance ----

    /// <summary>
    /// An inherited-COMP-3 field preserves its own Signed flag (from the PIC S),
    /// so the packed sign nibble is still written/read.
    /// </summary>
    [Fact]
    public void InheritedComp3PreservesSignedFromPicture()
    {
        var parsed = CopybookParser.Parse(
            "01 REC.\n" +
            "   05 GRP COMP-3.\n" +
            "      10 A PIC S9(5).\n" +
            "      10 B PIC 9(5).\n");

        Assert.True(Field(parsed, "A").Signed);
        Assert.False(Field(parsed, "B").Signed);
    }

    // ---- Fail-loud cases ----

    /// <summary>
    /// A group carrying an UNSUPPORTED usage (COMP-1 float) fails loud at its own
    /// statement — never a silent skip that would inherit nothing and mis-size.
    /// </summary>
    [Fact]
    public void GroupUnsupportedUsageFailsLoud()
    {
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(
            "01 REC.\n" +
            "   05 GRP COMP-1.\n" +
            "      10 A PIC 9(5).\n"));
        Assert.Contains("COMP-1", ex.Message);
    }

    [Fact]
    public void GroupPointerUsageFailsLoud()
    {
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(
            "01 REC.\n" +
            "   05 GRP POINTER.\n" +
            "      10 A PIC 9(5).\n"));
        Assert.Contains("POINTER", ex.Message);
    }

    /// <summary>
    /// An inherited usage that is illegal for the child's picture (group COMP-3
    /// over an alphanumeric PIC X child — a contradiction COBOL itself rejects)
    /// fails loud through the same guard an explicit COMP-3 on PIC X hits, not a
    /// silent mis-size.
    /// </summary>
    [Fact]
    public void GroupComp3OverAlphanumericChildFailsLoud()
    {
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(
            "01 REC.\n" +
            "   05 GRP COMP-3.\n" +
            "      10 A PIC X(5).\n"));
        Assert.Contains("A", ex.Message);
    }

    // ---- Round-trip: a group-COMP-3 record survives decode -> encode ----

    private const string GroupComp3Copybook =
        "01 REC.\n" +
        "   05 GRP COMP-3.\n" +
        "      10 A PIC 9(3).\n" +
        "      10 B PIC S9(3).\n" +
        "   05 NAME PIC X(4).\n";

    [Theory]
    [InlineData(CharacterEncoding.Latin1)]
    [InlineData(CharacterEncoding.Ebcdic037)]
    public void GroupComp3RecordRoundTripsByteForByte(CharacterEncoding encoding)
    {
        var parsed = CopybookParser.Parse(GroupComp3Copybook);

        // A@0 len2, B@2 len2 (both COMP-3, 3 digits -> ceil(4/2)=2), NAME@4 len4.
        Assert.Equal(2, Field(parsed, "A").Len);
        Assert.Equal(2, Field(parsed, "B").Len);
        Assert.Equal(4, Field(parsed, "NAME").Len);
        Assert.Equal(8, parsed.Root.Len);

        var record = new Dictionary<string, object>
        {
            ["A"] = 123m,
            ["B"] = -45m,
            ["NAME"] = "AB",
        };

        var encoded = FlatFileCodec.Encode(
            parsed.Flat, new[] { record }, encoding, RecordFormat.FixedLength);

        var decoded = FlatFileCodec.Decode(
            parsed.Flat, encoded, encoding, RecordFormat.FixedLength).Single();

        Assert.Equal(123m, (decimal)decoded["A"]);
        Assert.Equal(-45m, (decimal)decoded["B"]);
        Assert.Equal("AB", (string)decoded["NAME"]);

        // Re-encode the decoded record: byte-for-byte identical (round-trip stable).
        var reEncoded = FlatFileCodec.Encode(
            parsed.Flat, new[] { decoded }, encoding, RecordFormat.FixedLength);
        Assert.Equal(encoded, reEncoded);
    }

    /// <summary>
    /// The COMP-3 packed bytes of an inherited-usage field are identical under
    /// Latin-1 and EBCDIC (packed nibbles are never run through an encoding table);
    /// only the trailing text field differs between the two encodings.
    /// </summary>
    [Fact]
    public void InheritedComp3BytesAreEncodingIndependent()
    {
        var parsed = CopybookParser.Parse(GroupComp3Copybook);
        var record = new Dictionary<string, object>
        {
            ["A"] = 123m,
            ["B"] = -45m,
            ["NAME"] = "AB",
        };

        var latin = FlatFileCodec.Encode(
            parsed.Flat, new[] { record }, CharacterEncoding.Latin1, RecordFormat.FixedLength);
        var ebcdic = FlatFileCodec.Encode(
            parsed.Flat, new[] { record }, CharacterEncoding.Ebcdic037, RecordFormat.FixedLength);

        // Bytes 0..3 are the two COMP-3 fields — identical under both encodings.
        Assert.Equal(latin.Substring(0, 4), ebcdic.Substring(0, 4));
        // Byte 4.. is the NAME text — differs (A/B are different code points).
        Assert.NotEqual(latin.Substring(4), ebcdic.Substring(4));
    }
}
