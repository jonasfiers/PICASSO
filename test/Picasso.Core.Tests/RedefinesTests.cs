using System;
using System.Collections.Generic;
using System.Linq;
using Picasso.Core;
using Picasso.Core.Models;
using Xunit;

namespace Picasso.Core.Tests;

/// <summary>
/// REDEFINES support: a redefining item overlays a prior sibling's bytes rather
/// than being appended at the running offset. Decode of an overlapping layout is
/// fully supported (each field is an independent reading of the same bytes);
/// ENCODE of one is rejected loudly, because writing overlapping fields would
/// silently clobber shared bytes — the core "never silently miscompute" rule.
///
/// Offsets are the whole game here, so every case asserts concrete numbers.
/// </summary>
public class RedefinesTests
{
    private static FieldSpec Field(ParsedCopybook p, string name) =>
        p.Flat.Single(f => f.Name == name);

    // ---- Offset model ----

    [Fact]
    public void ElementaryOverElementary()
    {
        // 05 A PIC X(4). 05 B REDEFINES A PIC 9(4). -> A@0+4, B@0+4, record 4.
        var p = CopybookParser.Parse(
            "01  R.\n" +
            "    05  A PIC X(4).\n" +
            "    05  B REDEFINES A PIC 9(4).\n");

        Assert.Equal(0, Field(p, "A").Start);
        Assert.Equal(4, Field(p, "A").Len);
        Assert.Equal(0, Field(p, "B").Start);   // overlays A
        Assert.Equal(4, Field(p, "B").Len);
        Assert.Equal(4, p.Root.Len);            // one field's worth of storage, not two
    }

    [Fact]
    public void GroupOverGroup()
    {
        // A group redefining a group: sub-fields lay out from the target's offset.
        var p = CopybookParser.Parse(
            "01  R.\n" +
            "    05  STATIC-DETAILS.\n" +
            "        10  S-A PIC X(3).\n" +
            "        10  S-B PIC X(2).\n" +
            "    05  CONTACTS REDEFINES STATIC-DETAILS.\n" +
            "        10  C-A PIC X(1).\n" +
            "        10  C-B PIC X(4).\n" +
            "    05  TRAILER PIC X(2).\n");

        Assert.Equal(0, Field(p, "S-A").Start);
        Assert.Equal(3, Field(p, "S-B").Start);
        Assert.Equal(0, Field(p, "C-A").Start);   // CONTACTS starts where STATIC-DETAILS did
        Assert.Equal(1, Field(p, "C-B").Start);
        Assert.Equal(5, Field(p, "TRAILER").Start); // continues past the 5-byte group, not 10
        Assert.Equal(7, p.Root.Len);
    }

    [Fact]
    public void FillerRedefines()
    {
        // 02 FILLER REDEFINES <field> — the common screen-map shape. FILLER is a
        // real (named) field in PICASSO's flat layout, overlaid at the target.
        var p = CopybookParser.Parse(
            "01  R.\n" +
            "    05  NAME-FIELD PIC X(8).\n" +
            "    05  FILLER REDEFINES NAME-FIELD PIC X(8).\n");

        Assert.Equal(0, Field(p, "NAME-FIELD").Start);
        Assert.Equal(0, Field(p, "FILLER").Start);
        Assert.Equal(8, Field(p, "FILLER").Len);
        Assert.Equal(8, p.Root.Len);
    }

    [Fact]
    public void TwoItemsRedefineOneTarget()
    {
        // Multiple redefinitions of the same target: each starts at the target's
        // offset, including a second redefiner that names the ORIGINAL (not the
        // first redefiner) even though a redefiner sits between them.
        var p = CopybookParser.Parse(
            "01  R.\n" +
            "    05  RAW PIC X(6).\n" +
            "    05  AS-NUM REDEFINES RAW PIC 9(6).\n" +
            "    05  AS-PARTS REDEFINES RAW.\n" +
            "        10  P1 PIC X(2).\n" +
            "        10  P2 PIC X(4).\n");

        Assert.Equal(0, Field(p, "RAW").Start);
        Assert.Equal(0, Field(p, "AS-NUM").Start);
        Assert.Equal(0, Field(p, "P1").Start);
        Assert.Equal(2, Field(p, "P2").Start);
        Assert.Equal(6, p.Root.Len);
    }

    [Fact]
    public void RedefinitionShorterThanTarget()
    {
        // B redefines A but is shorter: the next ordinary field still continues
        // after A (the target), never after the shorter redefinition.
        var p = CopybookParser.Parse(
            "01  R.\n" +
            "    05  A PIC X(4).\n" +
            "    05  B REDEFINES A PIC X(2).\n" +
            "    05  C PIC X(3).\n");

        Assert.Equal(0, Field(p, "A").Start);
        Assert.Equal(0, Field(p, "B").Start);
        Assert.Equal(2, Field(p, "B").Len);
        Assert.Equal(4, Field(p, "C").Start);   // after A, not after B
        Assert.Equal(7, p.Root.Len);
    }

    [Fact]
    public void RedefinitionLongerThanTarget()
    {
        // B redefines A but is longer: the group extends to cover it, and the next
        // ordinary field continues past the longer redefinition.
        var p = CopybookParser.Parse(
            "01  R.\n" +
            "    05  A PIC X(4).\n" +
            "    05  B REDEFINES A PIC X(6).\n" +
            "    05  C PIC X(2).\n");

        Assert.Equal(0, Field(p, "A").Start);
        Assert.Equal(0, Field(p, "B").Start);
        Assert.Equal(6, Field(p, "B").Len);
        Assert.Equal(6, Field(p, "C").Start);   // after the longer redefinition
        Assert.Equal(8, p.Root.Len);            // record extended to 8
    }

    [Fact]
    public void RedefinesInsideNestedGroup()
    {
        // The overlay works at any level, not just directly under the 01, and a
        // following field in the outer group still continues correctly.
        var p = CopybookParser.Parse(
            "01  R.\n" +
            "    05  OUTER.\n" +
            "        10  X PIC X(5).\n" +
            "        10  Y REDEFINES X PIC X(5).\n" +
            "    05  Z PIC X(2).\n");

        Assert.Equal(0, Field(p, "X").Start);
        Assert.Equal(0, Field(p, "Y").Start);
        Assert.Equal(5, Field(p, "Z").Start);
        Assert.Equal(7, p.Root.Len);
    }

    // ---- Nameless (FILLER) REDEFINES ----
    //
    // The nonstandard shape "<level> REDEFINES <target> ..." omits the data-name
    // (or FILLER) before REDEFINES. GnuCOBOL tolerates this, treating the elided
    // name as FILLER. PICASSO used to take the token "REDEFINES" itself as the
    // field name and lay the item out as fresh storage AFTER the target — a silent
    // miscompute. These lock in the corrected overlay behavior.

    [Fact]
    public void NamelessGroupRedefinesOverlaysTarget()
    {
        // The brief's repro: a nameless group redefines a prior 10-byte field; its
        // sub-fields must overlay the target at offset 10, not append at 20.
        var p = CopybookParser.Parse(
            "01  R.\n" +
            "    05  FLD1 PIC X(10).\n" +
            "    05  FLD2 PIC X(10).\n" +
            "    05  REDEFINES FLD2.\n" +
            "        10  P1 PIC X(4).\n" +
            "        10  P2 PIC X(6).\n");

        Assert.Equal(0, Field(p, "FLD1").Start);
        Assert.Equal(10, Field(p, "FLD2").Start);
        Assert.Equal(10, Field(p, "P1").Start);  // overlays FLD2, NOT appended at 20
        Assert.Equal(4, Field(p, "P1").Len);
        Assert.Equal(14, Field(p, "P2").Start);
        Assert.Equal(6, Field(p, "P2").Len);
        Assert.Equal(20, p.Root.Len);            // max end, NOT 30
    }

    [Fact]
    public void NamelessElementaryRedefinesOverlaysTarget()
    {
        // Nameless elementary redefine: "10 REDEFINES X PIC 9(4)." overlays X.
        var p = CopybookParser.Parse(
            "01  R.\n" +
            "    05  X PIC X(4).\n" +
            "    05  REDEFINES X PIC 9(4).\n");

        Assert.Equal(0, Field(p, "X").Start);
        // The redefining item is elided to FILLER; it overlays X at offset 0.
        var filler = p.Flat.Single(f => f.Name == "FILLER");
        Assert.Equal(0, filler.Start);
        Assert.Equal(4, filler.Len);
        Assert.Equal(4, p.Root.Len);
    }

    [Fact]
    public void MultipleNamelessRedefinersOfOneTarget()
    {
        // Two nameless group redefiners of the same target both start at its offset.
        var p = CopybookParser.Parse(
            "01  R.\n" +
            "    05  RAW PIC X(6).\n" +
            "    05  REDEFINES RAW.\n" +
            "        10  A1 PIC X(2).\n" +
            "        10  A2 PIC X(4).\n" +
            "    05  REDEFINES RAW.\n" +
            "        10  B1 PIC X(3).\n" +
            "        10  B2 PIC X(3).\n");

        Assert.Equal(0, Field(p, "RAW").Start);
        Assert.Equal(0, Field(p, "A1").Start);
        Assert.Equal(2, Field(p, "A2").Start);
        Assert.Equal(0, Field(p, "B1").Start);  // second redefiner, also at RAW
        Assert.Equal(3, Field(p, "B2").Start);
        Assert.Equal(6, p.Root.Len);
    }

    [Fact]
    public void NamelessRedefinesLongerThanTargetExtendsRecord()
    {
        // A nameless redefiner longer than its target still extends the record and
        // the following ordinary field continues past it.
        var p = CopybookParser.Parse(
            "01  R.\n" +
            "    05  A PIC X(4).\n" +
            "    05  REDEFINES A.\n" +
            "        10  L1 PIC X(6).\n" +
            "    05  C PIC X(2).\n");

        Assert.Equal(0, Field(p, "L1").Start);
        Assert.Equal(6, Field(p, "L1").Len);
        Assert.Equal(6, Field(p, "C").Start);   // after the longer redefinition
        Assert.Equal(8, p.Root.Len);
    }

    [Fact]
    public void NamelessRedefinesDecodesBothViews()
    {
        // Round-trip: both the target and the nameless overlay's sub-fields decode
        // from the same bytes.
        var p = CopybookParser.Parse(
            "01  R.\n" +
            "    05  RAW PIC X(6).\n" +
            "    05  REDEFINES RAW.\n" +
            "        10  P1 PIC X(2).\n" +
            "        10  P2 PIC X(4).\n");

        var records = FlatFileCodec.Decode(p.Flat, "ABCDEF\n");
        var rec = Assert.Single(records);
        Assert.Equal("ABCDEF", rec["RAW"]);
        Assert.Equal("AB", rec["P1"]);
        Assert.Equal("CDEF", rec["P2"]);
    }

    [Fact]
    public void NamelessRedefinesTargetNotFoundFailsLoud()
    {
        // Unknown target still fails loud, exactly as for the named form.
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(
            "01  R.\n" +
            "    05  A PIC X(4).\n" +
            "    05  REDEFINES NOSUCH PIC X(4).\n"));
        Assert.Contains("NOSUCH", ex.Message);
        Assert.Contains("REDEFINES", ex.Message);
    }

    [Fact]
    public void NamelessRedefinesWithNoTargetNameFailsLoud()
    {
        // "REDEFINES" with nothing after it is malformed either way.
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(
            "01  R.\n" +
            "    05  A PIC X(4).\n" +
            "    05  REDEFINES\n"));
        Assert.Contains("REDEFINES", ex.Message);
    }

    // ---- Fail-loud target resolution ----

    [Fact]
    public void TargetNotFoundFailsLoud()
    {
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(
            "01  R.\n" +
            "    05  B REDEFINES NOSUCH PIC X.\n"));
        Assert.Contains("NOSUCH", ex.Message);
        Assert.Contains("B", ex.Message);
        Assert.Contains("REDEFINES", ex.Message);
    }

    [Fact]
    public void ForwardReferenceFailsLoud()
    {
        // The target must be a PRIOR sibling: a name defined only later is not yet
        // known and must fail rather than silently guess an offset.
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(
            "01  R.\n" +
            "    05  B REDEFINES A PIC X(4).\n" +
            "    05  A PIC X(4).\n"));
        Assert.Contains("A", ex.Message);
        Assert.Contains("B", ex.Message);
    }

    [Fact]
    public void RedefinesWithNoTargetNameFailsLoud()
    {
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(
            "01  R.\n" +
            "    05  A PIC X(4).\n" +
            "    05  B REDEFINES\n"));
        Assert.Contains("REDEFINES", ex.Message);
    }

    // ---- Decode (fully supported) ----

    [Fact]
    public void DecodeReadsBothOverlappingFields()
    {
        // A (text) and B (numeric) share the same 4 bytes; decode yields both
        // interpretations of "1234".
        var p = CopybookParser.Parse(
            "01  R.\n" +
            "    05  A PIC X(4).\n" +
            "    05  B REDEFINES A PIC 9(4).\n");

        var records = FlatFileCodec.Decode(p.Flat, "1234\n");
        var rec = Assert.Single(records);
        Assert.Equal("1234", rec["A"]);
        Assert.Equal(1234m, rec["B"]);
    }

    [Fact]
    public void DecodeGroupRedefinitionReinterpretsBytes()
    {
        // Same bytes read two ways: a 6-byte raw field, and a 2+4 split over it.
        var p = CopybookParser.Parse(
            "01  R.\n" +
            "    05  RAW PIC X(6).\n" +
            "    05  PARTS REDEFINES RAW.\n" +
            "        10  P1 PIC X(2).\n" +
            "        10  P2 PIC X(4).\n");

        var records = FlatFileCodec.Decode(p.Flat, "ABCDEF\n");
        var rec = Assert.Single(records);
        Assert.Equal("ABCDEF", rec["RAW"]);
        Assert.Equal("AB", rec["P1"]);
        Assert.Equal("CDEF", rec["P2"]);
    }

    [Fact]
    public void DecodeRecordLengthIsMaxNotSum()
    {
        // The record is 4 bytes (max end-offset), not 8 (sum of both fields'
        // lengths) — a too-long expected length would reject a valid record.
        var p = CopybookParser.Parse(
            "01  R.\n" +
            "    05  A PIC X(4).\n" +
            "    05  B REDEFINES A PIC 9(4).\n");

        // A single 4-byte record decodes cleanly; it would fail if the codec
        // expected 8 bytes.
        var records = FlatFileCodec.Decode(p.Flat, "1234\n");
        Assert.Single(records);
    }

    // ---- Encode (rejected loudly on overlap) ----

    [Fact]
    public void EncodeOverlappingLayoutFailsLoud()
    {
        var p = CopybookParser.Parse(
            "01  R.\n" +
            "    05  A PIC X(4).\n" +
            "    05  B REDEFINES A PIC 9(4).\n");

        var record = new Dictionary<string, object> { ["A"] = "1234", ["B"] = 1234m };
        var ex = Assert.Throws<FormatException>(() =>
            FlatFileCodec.Encode(p.Flat, new[] { record }));

        // Names both overlapping fields so the cause is unmistakable.
        Assert.Contains("A", ex.Message);
        Assert.Contains("B", ex.Message);
        Assert.Contains("overlap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EncodeGroupOverlapFailsLoud()
    {
        var p = CopybookParser.Parse(
            "01  R.\n" +
            "    05  RAW PIC X(6).\n" +
            "    05  PARTS REDEFINES RAW.\n" +
            "        10  P1 PIC X(2).\n" +
            "        10  P2 PIC X(4).\n");

        var record = new Dictionary<string, object>
        {
            ["RAW"] = "ABCDEF",
            ["P1"] = "AB",
            ["P2"] = "CDEF",
        };
        Assert.Throws<FormatException>(() =>
            FlatFileCodec.Encode(p.Flat, new[] { record }));
    }

    [Fact]
    public void EncodeNonOverlappingLayoutStillWorks()
    {
        // A layout with no REDEFINES overlap encodes exactly as before — the
        // overlap guard must not disturb ordinary layouts.
        var p = CopybookParser.Parse(
            "01  R.\n" +
            "    05  A PIC X(3).\n" +
            "    05  B PIC 9(2).\n");

        var record = new Dictionary<string, object> { ["A"] = "XYZ", ["B"] = 7m };
        var encoded = FlatFileCodec.Encode(p.Flat, new[] { record });
        Assert.Equal("XYZ07\n", encoded);
    }
}
