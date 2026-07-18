using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Picasso.Core;
using Picasso.Core.Models;
using Xunit;

namespace Picasso.Core.Tests;

/// <summary>
/// Nested FIXED-count OCCURS — a table of tables. An OCCURS item inside another
/// OCCURS item flattens recursively: the outer table expands to N copies, each
/// containing M copies of the inner (and deeper), to arbitrary depth. Every
/// level's 1-based index rides in the leaf name (LINE(1)-ITEM(1)-CODE …), one
/// (index) segment per OCCURS level, matching the single-level parenthesized
/// style; offsets are sequential and the outer element's stride is the full
/// byte size of one fully-expanded inner element.
///
/// Only ALL-fixed-count nesting is in scope. Any nesting that involves an
/// OCCURS ... DEPENDING ON table stays rejected loudly (asserted in OccursTests
/// next to the fixed-count feature). The overflow guard multiplies across every
/// level, so a pathological product fails loud rather than wrapping.
/// </summary>
public class NestedOccursTests
{
    private static IReadOnlyList<FieldSpec> Flat(string copybook) => CopybookParser.Parse(copybook).Flat;

    [Fact]
    public void TwoLevelNestingCarriesBothIndexesWithSequentialOffsets()
    {
        // The canonical example from the task brief.
        var flat = Flat(@"
            01  R.
                05  LINE OCCURS 3 TIMES.
                    10  ITEM OCCURS 4 TIMES.
                        15  CODE PIC X(2).
        ");

        // 3 * 4 = 12 leaves, every name carrying LINE's and ITEM's index.
        Assert.Equal(12, flat.Count);
        Assert.Equal("LINE(1)-ITEM(1)-CODE", flat[0].Name);

        // Spot-check the offsets the brief calls out explicitly.
        (string Name, int Start)[] expected =
        {
            ("LINE(1)-ITEM(1)-CODE", 0),
            ("LINE(1)-ITEM(2)-CODE", 2),
            ("LINE(1)-ITEM(4)-CODE", 6),
            ("LINE(2)-ITEM(1)-CODE", 8),
            ("LINE(3)-ITEM(4)-CODE", 22),
        };
        foreach (var (name, start) in expected)
        {
            var f = flat.Single(x => x.Name == name);
            Assert.Equal(start, f.Start);
            Assert.Equal(2, f.Len);
        }

        // Total 3 * 4 * 2 = 24 bytes, contiguous, no gaps.
        Assert.Equal(24, flat.Sum(f => f.Len));
        Assert.Equal(24, CopybookParser.Parse(@"
            01  R.
                05  LINE OCCURS 3 TIMES.
                    10  ITEM OCCURS 4 TIMES.
                        15  CODE PIC X(2).
        ").Root.Len);

        // Sequential and gapless: each leaf starts exactly where the previous ended.
        var ordered = flat.OrderBy(f => f.Start).ToList();
        var running = 0;
        foreach (var f in ordered)
        {
            Assert.Equal(running, f.Start);
            running += f.Len;
        }
    }

    [Fact]
    public void TripleNestingProducesTripleIndexedNames()
    {
        // A OCCURS 2 / B OCCURS 3 / C OCCURS 2 / D PIC X  ->  2*3*2 = 12 bytes.
        var flat = Flat(@"
            01  R.
                05  A OCCURS 2 TIMES.
                    10  B OCCURS 3 TIMES.
                        15  C OCCURS 2 TIMES.
                            20  D PIC X.
        ");

        Assert.Equal(12, flat.Count);
        Assert.Equal(12, flat.Sum(f => f.Len));

        // First, a middle, and the last name all carry three (index) segments.
        Assert.Equal("A(1)-B(1)-C(1)-D", flat[0].Name);
        Assert.Equal("A(2)-B(3)-C(2)-D", flat[^1].Name);
        Assert.Equal(11, flat.Single(f => f.Name == "A(2)-B(3)-C(2)-D").Start);

        // A(2)-B(1)-C(1)-D sits one full A-element (3*2 = 6 bytes) past A(1)-B(1)-C(1)-D.
        Assert.Equal(6, flat.Single(f => f.Name == "A(2)-B(1)-C(1)-D").Start);
        // Within an A, B's stride is 2 (C count * D len).
        Assert.Equal(2, flat.Single(f => f.Name == "A(1)-B(2)-C(1)-D").Start);
        // Every leaf name is unique — no index collision across levels.
        Assert.Equal(flat.Count, flat.Select(f => f.Name).Distinct().Count());
    }

    [Fact]
    public void InnerGroupWithMultipleFieldsAndATrailingFieldAfterTheNestedTable()
    {
        // The inner OCCURS group holds two fields, and a fixed field follows the
        // whole nested table — its offset must continue correctly past every copy.
        var flat = Flat(@"
            01  R.
                05  HDR PIC X(3).
                05  OUTER OCCURS 2 TIMES.
                    10  INNER OCCURS 3 TIMES.
                        15  A PIC X(2).
                        15  B PIC 9(1).
                    10  OUTER-TEXT PIC X(4).
                05  TRAILER PIC 9(2).
        ");

        // Inner element = A(2) + B(1) = 3 bytes; inner table = 3*3 = 9; outer
        // element = 9 + OUTER-TEXT(4) = 13; outer table = 2*13 = 26.
        // Record = HDR(3) + 26 + TRAILER(2) = 31.
        Assert.Equal(31, flat.Sum(f => f.Len));

        Assert.Equal(0, flat.Single(f => f.Name == "HDR").Start);
        Assert.Equal(3, flat.Single(f => f.Name == "OUTER(1)-INNER(1)-A").Start);
        Assert.Equal(5, flat.Single(f => f.Name == "OUTER(1)-INNER(1)-B").Start);
        Assert.Equal(6, flat.Single(f => f.Name == "OUTER(1)-INNER(2)-A").Start);
        // OUTER-TEXT of copy 1 sits right after the 9-byte inner table (3 + 9 = 12).
        Assert.Equal(12, flat.Single(f => f.Name == "OUTER(1)-OUTER-TEXT").Start);
        // Copy 2 starts one 13-byte outer element later.
        Assert.Equal(16, flat.Single(f => f.Name == "OUTER(2)-INNER(1)-A").Start);
        Assert.Equal(25, flat.Single(f => f.Name == "OUTER(2)-OUTER-TEXT").Start);
        // TRAILER continues past both outer copies: 3 + 26 = 29.
        Assert.Equal(29, flat.Single(f => f.Name == "TRAILER").Start);
        Assert.Equal(2, flat.Single(f => f.Name == "TRAILER").Len);
    }

    [Fact]
    public void ElementaryOccursNestedInsideAGroupOccursKeepsBothIndexes()
    {
        // The inner repetition is on an elementary item, not a group.
        var flat = Flat(@"
            01  R.
                05  ROW OCCURS 2 TIMES.
                    10  CELL PIC X(2) OCCURS 3 TIMES.
        ");

        Assert.Equal(
            new[]
            {
                "ROW(1)-CELL(1)", "ROW(1)-CELL(2)", "ROW(1)-CELL(3)",
                "ROW(2)-CELL(1)", "ROW(2)-CELL(2)", "ROW(2)-CELL(3)",
            },
            flat.Select(f => f.Name));
        Assert.Equal(new[] { 0, 2, 4, 6, 8, 10 }, flat.Select(f => f.Start));
        Assert.Equal(12, flat.Sum(f => f.Len));
    }

    [Fact]
    public void NestedCopiesPreserveFieldTypeAndSizing()
    {
        // A COMP-3 leaf repeated at two nesting levels keeps its packed sizing on
        // every copy — not just name and offset.
        var flat = Flat(@"
            01  R.
                05  GRP OCCURS 2 TIMES.
                    10  AMT PIC S9(5)V99 COMP-3 OCCURS 2 TIMES.
        ");

        Assert.Equal(4, flat.Count);
        Assert.All(flat, f =>
        {
            Assert.Equal(FieldType.Comp3, f.Type);
            Assert.Equal(7, f.Digits);
            Assert.Equal(2, f.Scale);
            Assert.True(f.Signed);
            Assert.Equal(4, f.Len); // ceil((7+1)/2)
        });
        Assert.Equal(new[] { 0, 4, 8, 12 }, flat.Select(f => f.Start));
    }

    [Fact]
    public void IntermediateNonOccursGroupsContributeNoNameSegment()
    {
        // A plain group sitting between two OCCURS levels adds no name segment,
        // matching the single-level convention (only OCCURS names + the leaf show).
        var flat = Flat(@"
            01  R.
                05  OUTER OCCURS 2 TIMES.
                    10  MID.
                        15  INNER OCCURS 2 TIMES.
                            20  V PIC X(1).
        ");

        Assert.Equal(
            new[] { "OUTER(1)-INNER(1)-V", "OUTER(1)-INNER(2)-V", "OUTER(2)-INNER(1)-V", "OUTER(2)-INNER(2)-V" },
            flat.Select(f => f.Name));
        Assert.Equal(new[] { 0, 1, 2, 3 }, flat.Select(f => f.Start));
    }

    [Fact]
    public void NestedOccursRoundTripsThroughTheCodec()
    {
        // End-to-end: the flattened nested layout decodes and re-encodes byte-for-byte.
        var parsed = CopybookParser.Parse(@"
            01  R.
                05  LINE OCCURS 2 TIMES.
                    10  ITEM OCCURS 2 TIMES.
                        15  CODE PIC X(2).
        ");

        const string text = "ABCDEFGH"; // 2*2*2 = 8 bytes, one fixed-length record
        var records = FlatFileCodec.Decode(parsed.Flat, text, CharacterEncoding.Latin1, RecordFormat.FixedLength);
        var record = Assert.Single(records);
        Assert.Equal("AB", record["LINE(1)-ITEM(1)-CODE"]);
        Assert.Equal("CD", record["LINE(1)-ITEM(2)-CODE"]);
        Assert.Equal("EF", record["LINE(2)-ITEM(1)-CODE"]);
        Assert.Equal("GH", record["LINE(2)-ITEM(2)-CODE"]);

        var reencoded = FlatFileCodec.Encode(parsed.Flat, records, CharacterEncoding.Latin1, RecordFormat.FixedLength);
        Assert.Equal(text, reencoded);
    }

    [Fact]
    public void NestedOverflowProductFailsLoudRatherThanWrapping()
    {
        // Nested counts MULTIPLY: 100000 inside 100000 of a 1-byte field is 10^10
        // bytes, well past int.MaxValue. The checked multiplication in
        // ComputeOffsets must fail loud at whichever level overflows, never wrap to
        // a negative/short Len.
        var ex = Assert.Throws<OverflowException>(() => CopybookParser.Parse(@"
            01  R.
                05  OUTER OCCURS 100000 TIMES.
                    10  INNER OCCURS 100000 TIMES.
                        15  B PIC X(1).
        "));
        Assert.Contains("overflows", ex.Message);
    }

    [Fact]
    public void SingleLevelOccursIsByteIdenticalAfterGeneralization()
    {
        // Guard against regression: the generalized recursion must reproduce the
        // exact single-level names and offsets it replaced.
        var flat = Flat(@"
            01  ORDER-REC.
                05  ORDER-ID    PIC 9(5).
                05  LINE-ITEM OCCURS 3 TIMES.
                    10  ITEM-CODE  PIC X(6).
                    10  ITEM-QTY   PIC 9(3).
                05  ORDER-TOTAL PIC 9(7).
        ");

        Assert.Equal(
            new[]
            {
                "ORDER-ID",
                "LINE-ITEM(1)-ITEM-CODE", "LINE-ITEM(1)-ITEM-QTY",
                "LINE-ITEM(2)-ITEM-CODE", "LINE-ITEM(2)-ITEM-QTY",
                "LINE-ITEM(3)-ITEM-CODE", "LINE-ITEM(3)-ITEM-QTY",
                "ORDER-TOTAL",
            },
            flat.Select(f => f.Name));
        Assert.Equal(
            new[] { 0, 5, 11, 14, 20, 23, 29, 32 },
            flat.Select(f => f.Start));
    }
}
