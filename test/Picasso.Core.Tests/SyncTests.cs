using System;
using System.Linq;
using Picasso.Core;
using Picasso.Core.Models;
using Xunit;

namespace Picasso.Core.Tests;

/// <summary>
/// SYNCHRONIZED / SYNC alignment. A SYNC <b>binary</b> (COMP/COMP-4/COMP-5/BINARY)
/// item is aligned to its natural byte boundary — 2 for a 2-byte halfword, 4 for a
/// fullword, 8 for a doubleword — by inserting slack (padding) bytes BEFORE it,
/// which shifts every following field. The slack is surfaced as a synthetic
/// FILLER-SYNC-&lt;offset&gt; Text field so the flat layout stays contiguous and a
/// SYNC record round-trips byte-for-byte with no codec change. SYNC on a non-binary
/// item (DISPLAY, COMP-3, Text) is a documented no-op — no padding. Every byte
/// offset asserted here was cross-checked against GnuCOBOL (cobc -std=mvs) via the
/// layout-oracle: all cases agree exactly.
/// </summary>
public class SyncTests
{
    // ---- Alignment offsets (hand-derived, GnuCOBOL-confirmed) ----

    [Fact]
    public void TwoAndFourByteBinariesAlignAfterAOneBytePrefix()
    {
        // 01 R. 05 A PIC X. 05 B PIC S9(4) COMP SYNC. 05 C PIC S9(8) COMP SYNC.
        // A@0(1); B is a 2-byte halfword, offset 1 is odd -> 1 slack byte -> B@2;
        // C is a 4-byte fullword, offset 4 is already aligned -> C@4. Record = 8.
        // GnuCOBOL LENGTH OF R = 8 (without SYNC it is 7).
        var parsed = CopybookParser.Parse(
            "01  R.\n" +
            "    05  A PIC X.\n" +
            "    05  B PIC S9(4) COMP SYNC.\n" +
            "    05  C PIC S9(8) COMP SYNC.\n");

        var flat = parsed.Flat;
        Assert.Equal(new[] { "A", "FILLER-SYNC-1", "B", "C" }, flat.Select(f => f.Name).ToArray());

        Assert.Equal((0, 1), (flat[0].Start, flat[0].Len)); // A
        Assert.Equal((1, 1), (flat[1].Start, flat[1].Len)); // slack
        Assert.Equal((2, 2), (flat[2].Start, flat[2].Len)); // B @ 2
        Assert.Equal((4, 4), (flat[3].Start, flat[3].Len)); // C @ 4
        Assert.Equal(FieldType.Text, flat[1].Type);         // slack is passthrough Text
        Assert.Equal(FieldType.Binary, flat[2].Type);
        Assert.Equal(8, flat.Max(f => f.Start + f.Len));    // record length
    }

    [Fact]
    public void FourByteBinaryAlignsToOffsetFourAfterOneByte()
    {
        // A lone PIC S9(8) COMP SYNC (4-byte fullword) after a single PIC X:
        // offset 1 -> aligned to 4 with 3 slack bytes -> B@4. Record = 8.
        var parsed = CopybookParser.Parse(
            "01  R.\n" +
            "    05  A PIC X.\n" +
            "    05  B PIC S9(8) COMP SYNC.\n");

        var flat = parsed.Flat;
        Assert.Equal(new[] { "A", "FILLER-SYNC-1", "B" }, flat.Select(f => f.Name).ToArray());
        Assert.Equal((1, 3), (flat[1].Start, flat[1].Len)); // 3 slack bytes
        Assert.Equal(4, flat[2].Start);                     // B @ 4
        Assert.Equal(8, flat.Max(f => f.Start + f.Len));
    }

    [Fact]
    public void EightByteBinaryAlignsToDoublewordBoundary()
    {
        // PIC S9(10) COMP SYNC is an 8-byte doubleword: offset 1 -> 7 slack -> B@8.
        // C PIC S9(4) COMP SYNC (halfword) at offset 16 is already aligned.
        var parsed = CopybookParser.Parse(
            "01  R.\n" +
            "    05  A PIC X.\n" +
            "    05  B PIC S9(10) COMP SYNC.\n" +
            "    05  C PIC S9(4) COMP SYNC.\n");

        var flat = parsed.Flat;
        Assert.Equal((1, 7), (flat[1].Start, flat[1].Len)); // 7 slack bytes
        Assert.Equal(8, flat[2].Start);                     // B @ 8 (doubleword)
        Assert.Equal(16, flat[3].Start);                    // C @ 16, no slack
        Assert.Equal(18, flat.Max(f => f.Start + f.Len));
    }

    [Fact]
    public void MixedWidthsEachAlignToTheirOwnBoundary()
    {
        // FLAG X@0; H1 halfword -> slack@1, H1@2; MID X@4; F1 fullword -> slack@5(3),
        // F1@8; D1 doubleword -> slack@12(4), D1@16; TAIL X@24. Record = 25.
        var parsed = CopybookParser.Parse(
            "01  R.\n" +
            "    05  FLAG PIC X.\n" +
            "    05  H1 PIC S9(4) COMP SYNC.\n" +
            "    05  MID PIC X.\n" +
            "    05  F1 PIC S9(8) COMP SYNC.\n" +
            "    05  D1 PIC S9(15) COMP SYNC.\n" +
            "    05  TAIL PIC X.\n");

        var byName = parsed.Flat.ToDictionary(f => f.Name, f => f.Start);
        Assert.Equal(2, byName["H1"]);
        Assert.Equal(8, byName["F1"]);
        Assert.Equal(16, byName["D1"]);
        Assert.Equal(24, byName["TAIL"]);
        Assert.Equal(25, parsed.Flat.Max(f => f.Start + f.Len));
    }

    [Fact]
    public void AlreadyAlignedBinaryGetsNoSlack()
    {
        // A@0(2 bytes) leaves B (halfword) starting at the aligned offset 2, so no
        // slack filler is emitted at all — the layout is identical to no-SYNC.
        var parsed = CopybookParser.Parse(
            "01  R.\n" +
            "    05  A PIC X(2).\n" +
            "    05  B PIC S9(4) COMP SYNC.\n");

        Assert.DoesNotContain(parsed.Flat, f => f.Name.StartsWith("FILLER-SYNC"));
        Assert.Equal(new[] { "A", "B" }, parsed.Flat.Select(f => f.Name).ToArray());
        Assert.Equal(2, parsed.Flat.Single(f => f.Name == "B").Start);
    }

    // ---- No-op on non-binary items (identical layout to no-SYNC) ----

    [Theory]
    [InlineData("05  B PIC S9(5) COMP-3 SYNC.\n")] // packed decimal: not aligned
    [InlineData("05  B PIC 9(5) SYNC.\n")]          // DISPLAY numeric: not aligned
    [InlineData("05  B PIC X(5) SYNC.\n")]          // alphanumeric: not aligned
    public void SyncOnNonBinaryIsANoOp(string bEntry)
    {
        // SYNC only aligns binary; on any other item it inserts no padding, so the
        // layout is byte-identical to the same copybook without SYNC.
        var withSync = CopybookParser.Parse("01  R.\n    05  A PIC X.\n    " + bEntry + "    05  C PIC X.\n");
        var withoutSync = CopybookParser.Parse(
            "01  R.\n    05  A PIC X.\n    " + bEntry.Replace(" SYNC", "") + "    05  C PIC X.\n");

        Assert.DoesNotContain(withSync.Flat, f => f.Name.StartsWith("FILLER-SYNC"));
        Assert.Equal(
            withoutSync.Flat.Select(f => (f.Name, f.Start, f.Len)).ToArray(),
            withSync.Flat.Select(f => (f.Name, f.Start, f.Len)).ToArray());
    }

    [Fact]
    public void SynchronizedFullSpellingIsAccepted()
    {
        // SYNCHRONIZED and SYNC are the same clause; both must align a binary item.
        var parsed = CopybookParser.Parse(
            "01  R.\n    05  A PIC X.\n    05  B PIC S9(4) COMP SYNCHRONIZED.\n");
        Assert.Equal(2, parsed.Flat.Single(f => f.Name == "B").Start);
    }

    // ---- Round-trip: slack bytes survive decode -> encode byte-for-byte ----

    [Fact]
    public void SyncRecordRoundTripsByteForByteInLatin1()
    {
        // abc layout: A(1) + slack(1) + B(2) + C(4) = 8 bytes. The slack byte here
        // is a non-zero, non-ASCII value (0xEE) to prove it is preserved verbatim
        // rather than regenerated as a zero pad.
        var parsed = CopybookParser.Parse(
            "01  R.\n" +
            "    05  A PIC X.\n" +
            "    05  B PIC S9(4) COMP SYNC.\n" +
            "    05  C PIC S9(8) COMP SYNC.\n");

        var record =
            "Z" +                                   // A
            (char)0xEE +                            // slack byte (arbitrary)
            (char)0x00 + (char)0x2A +               // B = 42
            (char)0x00 + (char)0x00 + (char)0x30 + (char)0x39; // C = 12345

        var decoded = FlatFileCodec.Decode(parsed.Flat, record, CharacterEncoding.Latin1, RecordFormat.FixedLength);
        Assert.Single(decoded);
        Assert.Equal("Z", decoded[0]["A"]);
        Assert.Equal(42m, decoded[0]["B"]);
        Assert.Equal(12345m, decoded[0]["C"]);

        var reencoded = FlatFileCodec.Encode(parsed.Flat, decoded, CharacterEncoding.Latin1, RecordFormat.FixedLength);
        Assert.Equal(record, reencoded); // includes the 0xEE slack byte, untouched
    }

    [Fact]
    public void SyncRecordRoundTripsByteForByteInEbcdic()
    {
        // Same layout under EBCDIC cp037: the binary and slack bytes pass through
        // raw (never run through the encoding table), only A's text is translated.
        var parsed = CopybookParser.Parse(
            "01  R.\n" +
            "    05  A PIC X.\n" +
            "    05  B PIC S9(4) COMP SYNC.\n" +
            "    05  C PIC S9(8) COMP SYNC.\n");

        var record =
            "" + (char)0xC1 +                       // A = EBCDIC 'A'
            (char)0x00 +                            // slack byte
            (char)0xFF + (char)0xD6 +               // B = -42
            (char)0x00 + (char)0x00 + (char)0x30 + (char)0x39; // C = 12345

        var decoded = FlatFileCodec.Decode(parsed.Flat, record, CharacterEncoding.Ebcdic037, RecordFormat.FixedLength);
        Assert.Equal("A", decoded[0]["A"]);
        Assert.Equal(-42m, decoded[0]["B"]);

        var reencoded = FlatFileCodec.Encode(parsed.Flat, decoded, CharacterEncoding.Ebcdic037, RecordFormat.FixedLength);
        Assert.Equal(record, reencoded);
    }

    // ---- SYNC inside an OCCURS: fail loud (scope boundary) ----

    [Fact]
    public void SyncBinaryInsideFixedOccursGroupFailsLoud()
    {
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(
            "01  R.\n" +
            "    05  TAB OCCURS 3.\n" +
            "        10  B PIC S9(4) COMP SYNC.\n"));
        Assert.Contains("SYNCHRONIZED", ex.Message);
        Assert.Contains("'B'", ex.Message);
        Assert.Contains("OCCURS", ex.Message);
    }

    [Fact]
    public void SyncBinaryThatItselfOccursFailsLoud()
    {
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(
            "01  R.\n" +
            "    05  B PIC S9(4) COMP SYNC OCCURS 3.\n"));
        Assert.Contains("SYNCHRONIZED", ex.Message);
        Assert.Contains("'B'", ex.Message);
    }

    [Fact]
    public void NonBinarySyncInsideOccursIsToleratedNotRejected()
    {
        // The OCCURS guard is specific to SYNC binary (the only aligned kind). A
        // SYNC on a DISPLAY item inside an OCCURS is a no-op and must NOT be
        // rejected — it carries no alignment, so no per-occurrence slack question.
        var parsed = CopybookParser.Parse(
            "01  R.\n" +
            "    05  TAB OCCURS 3.\n" +
            "        10  B PIC 9(4) SYNC.\n");
        Assert.Equal(12, parsed.Flat.Max(f => f.Start + f.Len)); // 3 * 4 bytes, no slack
    }
}
