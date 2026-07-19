using System;
using System.Collections.Generic;
using System.Linq;
using Picasso.Core;
using Picasso.Core.Models;
using Xunit;

namespace Picasso.Core.Tests;

/// <summary>
/// The named binary USAGE forms — BINARY-CHAR / BINARY-SHORT / BINARY-LONG /
/// BINARY-DOUBLE and their SIGNED / UNSIGNED qualifiers. Unlike COMP/BINARY,
/// these carry a FIXED byte width in the usage name (1/2/4/8) and no PICTURE,
/// stored big-endian, two's-complement (SIGNED, the default) or plain magnitude
/// (UNSIGNED). Before this feature a pic-less named-binary item was silently
/// dropped, shifting every following offset — a silent miscompute this suite
/// guards against. Totals here are cross-checked against GnuCOBOL (-std=mvs):
/// the four-field record below is 12 bytes, DB2 SQLCA is 157.
/// </summary>
public class NamedBinaryTests
{
    private static string L1(params byte[] b) => new string(b.Select(x => (char)x).ToArray());

    private static List<Dictionary<string, object>> DecodeFixed(ParsedCopybook p, string bytes) =>
        FlatFileCodec.Decode(p.Flat, bytes, CharacterEncoding.Latin1, RecordFormat.FixedLength);

    private static string EncodeFixed(ParsedCopybook p, Dictionary<string, object> rec) =>
        FlatFileCodec.Encode(p.Flat, new[] { rec }, CharacterEncoding.Latin1, RecordFormat.FixedLength);

    // ---- Width by usage name ----

    [Theory]
    [InlineData("BINARY-CHAR", 1)]
    [InlineData("BINARY-SHORT", 2)]
    [InlineData("BINARY-LONG", 4)]
    [InlineData("BINARY-DOUBLE", 8)]
    public void WidthComesFromTheUsageName(string usage, int width)
    {
        var parsed = CopybookParser.Parse($"01  R.\n    05  A USAGE {usage}.\n");
        var f = Assert.Single(parsed.Flat);
        Assert.Equal(FieldType.Binary, f.Type);
        Assert.Equal(width, f.Len);
        Assert.Equal(0, f.Digits);   // width-explicit, not digit-derived
        Assert.True(f.Signed);       // SIGNED is the default
    }

    [Fact]
    public void NamedBinaryFieldIsNotDroppedAndFollowingOffsetsAreCorrect()
    {
        // The exact silent-miscompute reproduction: without B and C being sized,
        // D would slide to offset 6 and the record would be 6 bytes, not 12.
        var parsed = CopybookParser.Parse(
            "01  R.\n    05  A PIC X(3).\n    05  B USAGE BINARY-LONG.\n" +
            "    05  C USAGE BINARY-SHORT.\n    05  D PIC X(3).\n");
        var flat = parsed.Flat;
        Assert.Equal(4, flat.Count);
        Assert.Equal(("A", 0, 3), (flat[0].Name, flat[0].Start, flat[0].Len));
        Assert.Equal(("B", 3, 4), (flat[1].Name, flat[1].Start, flat[1].Len));
        Assert.Equal(("C", 7, 2), (flat[2].Name, flat[2].Start, flat[2].Len));
        Assert.Equal(("D", 9, 3), (flat[3].Name, flat[3].Start, flat[3].Len));
        Assert.Equal(12, flat.Max(f => f.Start + f.Len));
    }

    // ---- SIGNED vs UNSIGNED changes decode of a high-bit value ----

    [Fact]
    public void SignedVsUnsignedByteDecodesDifferently()
    {
        var s = CopybookParser.Parse("01  R.\n    05  A BINARY-CHAR SIGNED.\n");
        var u = CopybookParser.Parse("01  R.\n    05  A BINARY-CHAR UNSIGNED.\n");
        Assert.Equal(-1m, DecodeFixed(s, L1(0xFF))[0]["A"]);
        Assert.Equal(255m, DecodeFixed(u, L1(0xFF))[0]["A"]);
    }

    [Fact]
    public void SignedVsUnsignedFullwordDecodesDifferently()
    {
        var s = CopybookParser.Parse("01  R.\n    05  A BINARY-LONG SIGNED.\n");
        var u = CopybookParser.Parse("01  R.\n    05  A BINARY-LONG UNSIGNED.\n");
        Assert.Equal(-1m, DecodeFixed(s, L1(0xFF, 0xFF, 0xFF, 0xFF))[0]["A"]);
        Assert.Equal(4294967295m, DecodeFixed(u, L1(0xFF, 0xFF, 0xFF, 0xFF))[0]["A"]);
    }

    [Theory]
    [InlineData("BINARY-CHAR", 1)]
    [InlineData("BINARY-SHORT", 2)]
    [InlineData("BINARY-LONG", 4)]
    [InlineData("BINARY-DOUBLE", 8)]
    public void PositiveValueDecodesAcrossEveryWidth(string usage, int width)
    {
        // Big-endian 0x...2A == 42 at any width.
        var parsed = CopybookParser.Parse($"01  R.\n    05  A USAGE {usage}.\n");
        var bytes = new byte[width];
        bytes[width - 1] = 0x2A;
        Assert.Equal(42m, DecodeFixed(parsed, L1(bytes))[0]["A"]);
    }

    // ---- OCCURS of a named binary (SQLCA's SQLERRD BINARY-LONG OCCURS 6) ----

    [Fact]
    public void OccursOfNamedBinaryFlattensToIndexedFields()
    {
        var parsed = CopybookParser.Parse("01  R.\n    05  T USAGE BINARY-LONG OCCURS 6.\n");
        Assert.Equal(6, parsed.Flat.Count);
        Assert.Equal(24, parsed.Flat.Max(f => f.Start + f.Len));
        Assert.Equal("T(1)", parsed.Flat[0].Name);
        Assert.Equal("T(6)", parsed.Flat[5].Name);
        Assert.All(parsed.Flat, f => Assert.Equal(4, f.Len));
    }

    // ---- Round-trip encode <-> decode ----

    [Theory]
    [InlineData("BINARY-CHAR", -1)]
    [InlineData("BINARY-CHAR", 127)]
    [InlineData("BINARY-SHORT", -12345)]
    [InlineData("BINARY-LONG", -1)]
    [InlineData("BINARY-LONG", 2000000000)]
    [InlineData("BINARY-DOUBLE", -1234567890123)]
    public void SignedRoundTrips(string usage, long value)
    {
        var parsed = CopybookParser.Parse($"01  R.\n    05  A USAGE {usage}.\n");
        var encoded = EncodeFixed(parsed, new Dictionary<string, object> { ["A"] = (decimal)value });
        Assert.Equal(parsed.Flat[0].Len, encoded.Length);
        Assert.Equal((decimal)value, DecodeFixed(parsed, encoded)[0]["A"]);
    }

    [Fact]
    public void UnsignedHighValueRoundTrips()
    {
        var parsed = CopybookParser.Parse("01  R.\n    05  A BINARY-LONG UNSIGNED.\n");
        var encoded = EncodeFixed(parsed, new Dictionary<string, object> { ["A"] = 4294967295m });
        Assert.Equal(L1(0xFF, 0xFF, 0xFF, 0xFF), encoded);
        Assert.Equal(4294967295m, DecodeFixed(parsed, encoded)[0]["A"]);
    }

    [Fact]
    public void UnsignedDoublewordMaxRoundTrips()
    {
        // 2^64 - 1, the largest unsigned doubleword. The riskiest boundary: the
        // magnitude is assembled into a ulong then a decimal, so a float path here
        // would lose precision. 0xFF x 8 must decode to exactly 18446744073709551615.
        const decimal max = 18446744073709551615m;
        var parsed = CopybookParser.Parse("01  R.\n    05  A BINARY-DOUBLE UNSIGNED.\n");
        var encoded = EncodeFixed(parsed, new Dictionary<string, object> { ["A"] = max });
        Assert.Equal(L1(0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF), encoded);
        Assert.Equal(max, DecodeFixed(parsed, encoded)[0]["A"]);
    }

    [Fact]
    public void VerboseUsageIsFormSizesIdentically()
    {
        // The wordy "USAGE IS <name>" spelling must size exactly like the bare form:
        // the "IS" is a noise word, BINARY-LONG is still 4 bytes, UNSIGNED still flips
        // the sign — the qualifier after the usage name is consumed either way.
        var parsed = CopybookParser.Parse("01  R.\n    05  A USAGE IS BINARY-LONG UNSIGNED.\n");
        var f = Assert.Single(parsed.Flat);
        Assert.Equal(FieldType.Binary, f.Type);
        Assert.Equal(4, f.Len);
        Assert.Equal(0, f.Digits);
        Assert.False(f.Signed);
    }

    [Fact]
    public void NegativeValueIntoUnsignedFieldFailsLoud()
    {
        var parsed = CopybookParser.Parse("01  R.\n    05  A BINARY-CHAR UNSIGNED.\n");
        Assert.Throws<ArgumentException>(
            () => EncodeFixed(parsed, new Dictionary<string, object> { ["A"] = -1m }));
    }

    [Fact]
    public void ValueTooWideForTheFieldOverflowsLoud()
    {
        var parsed = CopybookParser.Parse("01  R.\n    05  A BINARY-CHAR SIGNED.\n");
        // A signed byte holds -128..127; 200 does not fit.
        Assert.Throws<OverflowException>(
            () => EncodeFixed(parsed, new Dictionary<string, object> { ["A"] = 200m }));
    }

    // ---- Loud rejections ----

    [Fact]
    public void BinaryCLongIsRejectedLoudly()
    {
        var ex = Assert.Throws<FormatException>(
            () => CopybookParser.Parse("01  R.\n    05  A USAGE BINARY-C-LONG.\n"));
        Assert.Contains("BINARY-C-LONG", ex.Message);
        Assert.Contains("'A'", ex.Message);
        Assert.Contains("not supported", ex.Message);
    }

    [Fact]
    public void PicClauseCombinedWithNamedBinaryIsRejectedLoudly()
    {
        var ex = Assert.Throws<FormatException>(
            () => CopybookParser.Parse("01  R.\n    05  A PIC 9(4) USAGE BINARY-LONG.\n"));
        Assert.Contains("named binary", ex.Message);
        Assert.Contains("PIC", ex.Message);
        Assert.Contains("'A'", ex.Message);
    }

    [Fact]
    public void SignSeparateCombinedWithNamedBinaryIsRejectedLoudly()
    {
        var ex = Assert.Throws<FormatException>(
            () => CopybookParser.Parse("01  R.\n    05  A BINARY-LONG SIGN IS TRAILING SEPARATE.\n"));
        Assert.Contains("SEPARATE", ex.Message);
        Assert.Contains("'A'", ex.Message);
    }

    [Fact]
    public void NamedBinaryUsageOnAGroupIsRejectedWithAnAccurateMessage()
    {
        // A named binary USAGE on a GROUP (it has subordinates) is not inherited to
        // children in this parser. The rejection must say so — not misname the group
        // as an "elementary item" — and point at stating the USAGE on each child.
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(
            "01  R.\n    05  GRP BINARY-LONG.\n        10  A.\n        10  B.\n"));
        Assert.Contains("not inherited to subordinate", ex.Message);
        Assert.Contains("'GRP'", ex.Message);
        Assert.DoesNotContain("elementary item", ex.Message);
    }

    // ---- Regression guard: the digit-derived COMP path is unchanged ----

    [Fact]
    public void DigitDerivedCompPathStillSizesByDigitCount()
    {
        // PIC 9(4) COMP -> 2-byte halfword (digit-derived), not touched by the
        // named-binary width-explicit path.
        var parsed = CopybookParser.Parse("01  R.\n    05  A PIC 9(4) COMP.\n");
        var f = Assert.Single(parsed.Flat);
        Assert.Equal(FieldType.Binary, f.Type);
        Assert.Equal(2, f.Len);
        Assert.Equal(4, f.Digits);   // digit-derived, NOT 0
        Assert.Equal(42m, DecodeFixed(parsed, L1(0x00, 0x2A))[0]["A"]);
    }

    // ---- The realistic payoff: DB2 SQLCA parses to 157 bytes ----

    [Fact]
    public void Db2SqlcaParsesToOneHundredFiftySevenBytes()
    {
        // SQLCA is COPY'd into essentially every DB2 program: two BINARY-LONG
        // counters, a BINARY-SHORT length, a BINARY-LONG OCCURS 6 array, a
        // REDEFINES, and DISPLAY text. GnuCOBOL LENGTH OF SQLCA = 157.
        var src =
            "       01  SQLCA.\n" +
            "           03  SQLCAID         PIC X(8).\n" +
            "           03  SQLCABC         USAGE BINARY-LONG VALUE 136.\n" +
            "           03  SQLCODE         USAGE BINARY-LONG VALUE 0.\n" +
            "           03  SQLERRM.\n" +
            "               05  SQLERRML    USAGE BINARY-SHORT.\n" +
            "               05  SQLERRMC    PIC X(70).\n" +
            "           03  SQLERRP         PIC X(8).\n" +
            "           03  SQLERRD         USAGE BINARY-LONG OCCURS 6.\n" +
            "           03  SQLWARN.\n" +
            "               05  SQLWARN0    PIC X.\n" +
            "               05  SQLWARN1    PIC X.\n" +
            "               05  SQLWARN2    PIC X.\n" +
            "               05  SQLWARN3    PIC X.\n" +
            "               05  SQLWARN4    PIC X.\n" +
            "               05  SQLWARN5    PIC X.\n" +
            "               05  SQLWARN6    PIC X.\n" +
            "               05  SQLWARN7    PIC X.\n" +
            "               05  SQLWARN8    PIC X.\n" +
            "               05  SQLWARN9    PIC X.\n" +
            "               05  SQLWARN10   PIC X.\n" +
            "               05  SQLWARNA    REDEFINES SQLWARN10 PIC X.\n" +
            "           03  SQLSTATE        PIC X(5).\n" +
            "           03  FILLER          PIC X(21).\n";
        var parsed = CopybookParser.Parse(src);
        Assert.Equal(157, parsed.Flat.Max(f => f.Start + f.Len));
    }
}
