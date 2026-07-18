using System;
using System.Collections.Generic;
using System.Linq;
using Picasso.Core;
using Picasso.Core.Models;
using Xunit;

namespace Picasso.Core.Tests;

/// <summary>
/// Fixed-count OCCURS support. An OCCURS n [TIMES] item expands into n
/// 1-indexed leaf fields with sequential offsets, so the flat {Name, Start,
/// Len, Type} model the rest of PICASSO runs on never has to learn about
/// repetition. Elementary OCCURS becomes NAME(1)..NAME(n); a group OCCURS
/// propagates the parenthesized index into every descendant, e.g.
/// LINE-ITEM(2)-ITEM-QTY. Nested FIXED-count OCCURS (a table of tables) is now
/// flattened recursively — see NestedOccursTests. The variable-length shapes
/// that stay rejected — OCCURS ... DEPENDING ON nested in an OCCURS, and any
/// OCCURS nested in an ODO — are asserted here next to the feature so no
/// variable-length nesting can slip through as a side effect of the fixed path.
/// </summary>
public class OccursTests
{
    private static IReadOnlyList<FieldSpec> Flat(string copybook) => CopybookParser.Parse(copybook).Flat;

    // ---- Elementary OCCURS ----

    [Fact]
    public void ElementaryOccursExpandsIntoIndexedFieldsWithSequentialOffsets()
    {
        var flat = Flat(@"
            01  R.
                05  HEADER    PIC X(4).
                05  SLOT      PIC X(3) OCCURS 5 TIMES.
                05  TRAILER   PIC 9(2).
        ");

        Assert.Equal(
            new[] { "HEADER", "SLOT(1)", "SLOT(2)", "SLOT(3)", "SLOT(4)", "SLOT(5)", "TRAILER" },
            flat.Select(f => f.Name));

        // One iteration's worth of bytes (3) apart, starting right after HEADER.
        Assert.Equal(
            new[]
            {
                ("HEADER", 0, 4),
                ("SLOT(1)", 4, 3),
                ("SLOT(2)", 7, 3),
                ("SLOT(3)", 10, 3),
                ("SLOT(4)", 13, 3),
                ("SLOT(5)", 16, 3),
                ("TRAILER", 19, 2),
            },
            flat.Select(f => (f.Name, f.Start, f.Len)));

        // Total length accounts for every copy: 4 + 5*3 + 2 = 21.
        Assert.Equal(21, flat.Sum(f => f.Len));
        Assert.Equal(21, CopybookParser.Parse(@"
            01  R.
                05  HEADER    PIC X(4).
                05  SLOT      PIC X(3) OCCURS 5 TIMES.
                05  TRAILER   PIC 9(2).
        ").Root.Len);
    }

    [Fact]
    public void ExpandedElementaryCopiesKeepTheOriginalFieldTypeAndSizing()
    {
        // COMP-3 packed decimal repeated: every copy must carry the packed byte
        // length and scale, not just a name and offset.
        var flat = Flat("01  R.\n    05  AMT PIC S9(5)V99 COMP-3 OCCURS 3 TIMES.\n");

        Assert.Equal(3, flat.Count);
        Assert.All(flat, f =>
        {
            Assert.Equal(FieldType.Comp3, f.Type);
            Assert.Equal(7, f.Digits);
            Assert.Equal(2, f.Scale);
            Assert.True(f.Signed);
            Assert.Equal(4, f.Len); // ceil((7+1)/2)
        });
        Assert.Equal(new[] { 0, 4, 8 }, flat.Select(f => f.Start));
    }

    // ---- Group OCCURS ----

    [Fact]
    public void GroupOccursPropagatesTheIndexIntoEveryDescendant()
    {
        // The example from the task: an OCCURS on a group, flattened with the
        // index riding on the group name for each descendant leaf.
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
            new[]
            {
                ("ORDER-ID", 0, 5),
                ("LINE-ITEM(1)-ITEM-CODE", 5, 6), ("LINE-ITEM(1)-ITEM-QTY", 11, 3),
                ("LINE-ITEM(2)-ITEM-CODE", 14, 6), ("LINE-ITEM(2)-ITEM-QTY", 20, 3),
                ("LINE-ITEM(3)-ITEM-CODE", 23, 6), ("LINE-ITEM(3)-ITEM-QTY", 29, 3),
                ("ORDER-TOTAL", 32, 7),
            },
            flat.Select(f => (f.Name, f.Start, f.Len)));

        // Per-iteration stride is one whole group (6 + 3 = 9 bytes).
        Assert.Equal(9, flat.Single(f => f.Name == "LINE-ITEM(2)-ITEM-CODE").Start
                       - flat.Single(f => f.Name == "LINE-ITEM(1)-ITEM-CODE").Start);
        Assert.Equal(5 + 3 * 9 + 7, flat.Sum(f => f.Len));
    }

    [Fact]
    public void OccursGroupNodeSpansAllIterationsInTheTree()
    {
        // The tree keeps one iteration under the OCCURS node, but the node's own
        // Len must roll up all iterations so a following field starts past them.
        var parsed = CopybookParser.Parse(@"
            01  R.
                05  G OCCURS 4 TIMES.
                    10  A PIC X(2).
                    10  B PIC X(1).
                05  AFTER PIC X(5).
        ");

        var g = parsed.Root.Children.Single(c => c.Name == "G");
        Assert.Equal(4, g.OccursCount);
        Assert.Equal(0, g.Start);
        Assert.Equal(4 * 3, g.Len); // 4 iterations of a 3-byte group

        var after = parsed.Root.Children.Single(c => c.Name == "AFTER");
        Assert.Equal(12, after.Start);
    }

    // ---- Clause tolerance ----

    [Fact]
    public void OccursWithoutTheWordTimesIsSupported()
    {
        // Decision: both "OCCURS n" and "OCCURS n TIMES" are accepted — TIMES is
        // optional in real dialects and carries no extra meaning.
        var flat = Flat("01  R.\n    05  SLOT PIC X(2) OCCURS 4.\n");
        Assert.Equal(new[] { "SLOT(1)", "SLOT(2)", "SLOT(3)", "SLOT(4)" }, flat.Select(f => f.Name));
        Assert.Equal(new[] { 0, 2, 4, 6 }, flat.Select(f => f.Start));
    }

    [Fact]
    public void IndexedByClauseIsToleratedInTheSameStatement()
    {
        // INDEXED BY is a PROCEDURE-DIVISION index-access concern PICASSO doesn't
        // model; it must be skipped without disturbing the count or a later field.
        var flat = Flat(@"
            01  R.
                05  SLOT PIC X(3) OCCURS 5 TIMES INDEXED BY SLOT-IDX.
                05  AFTER PIC X(2).
        ");

        Assert.Equal(6, flat.Count);
        Assert.Equal(5, flat.Count(f => f.Name.StartsWith("SLOT(")));
        var after = flat.Single(f => f.Name == "AFTER");
        Assert.Equal(15, after.Start); // 5 * 3, INDEXED BY consumed no data bytes
        Assert.Equal(2, after.Len);
    }

    [Fact]
    public void AscendingKeyAndIndexedByTogetherAreTolerated()
    {
        var flat = Flat(@"
            01  R.
                05  SLOT PIC 9(4) OCCURS 3 TIMES ASCENDING KEY IS SLOT INDEXED BY I.
                05  AFTER PIC X(1).
        ");

        Assert.Equal(new[] { "SLOT(1)", "SLOT(2)", "SLOT(3)", "AFTER" }, flat.Select(f => f.Name));
        Assert.Equal(12, flat.Single(f => f.Name == "AFTER").Start);
    }

    [Fact]
    public void OccursCanPrecedeThePicClauseInTheStatement()
    {
        // COBOL allows the clauses in either order; parsing must not depend on
        // OCCURS trailing PIC.
        var flat = Flat("01  R.\n    05  SLOT OCCURS 3 TIMES PIC X(2).\n");
        Assert.Equal(new[] { "SLOT(1)", "SLOT(2)", "SLOT(3)" }, flat.Select(f => f.Name));
        Assert.All(flat, f => Assert.Equal(2, f.Len));
    }

    // ---- Rejections: the harder shapes stay out, distinctly ----

    [Fact]
    public void DependingOnIsNowRecognizedAsAVariableLengthTableNotAFixedCount()
    {
        // "OCCURS m TO n TIMES DEPENDING ON f" is the single, common ODO case,
        // now supported. It must be recognized as variable-length — never
        // silently expanded as a fixed count of m or n — and its bounds and
        // depending field captured. Full round-trip behaviour lives in OdoTests.
        var parsed = CopybookParser.Parse(@"
            01  R.
                05  N            PIC 9(2).
                05  ENTRY OCCURS 1 TO 10 TIMES DEPENDING ON N.
                    10  ENTRY-VAL PIC X(4).
        ");

        Assert.True(parsed.IsVariableLength);
        Assert.NotNull(parsed.Odo);
        Assert.Equal("ENTRY", parsed.Odo!.TableName);
        Assert.Equal("N", parsed.Odo.DependsOn);
        Assert.Equal(1, parsed.Odo.Min);
        Assert.Equal(10, parsed.Odo.Max);
    }

    [Fact]
    public void DependingOnWithoutAnUpperBoundGetsAnImpliedMinimumOfOne()
    {
        // "OCCURS n TIMES DEPENDING ON f" with no TO: n is the maximum, and the
        // minimum is implied to be 1 (COBOL leaves it unstated in this form).
        var parsed = CopybookParser.Parse(
            "01  R.\n    05  N PIC 9(2).\n    05  E PIC X(3) OCCURS 9 TIMES DEPENDING ON N.\n");

        Assert.True(parsed.IsVariableLength);
        Assert.Equal(1, parsed.Odo!.Min);
        Assert.Equal(9, parsed.Odo.Max);
        Assert.Equal("N", parsed.Odo.DependsOn);
    }

    [Fact]
    public void OdoNestedInsideAFixedOccursIsRejected()
    {
        // A variable-length (DEPENDING ON) table nested inside a fixed OCCURS is
        // still out of scope: its offset would itself be index-dependent. Only
        // all-FIXED-count nesting is supported.
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(@"
            01  R.
                05  N PIC 9(2).
                05  OUTER OCCURS 3 TIMES.
                    10  INNER OCCURS 1 TO 5 TIMES DEPENDING ON N.
                        15  V PIC X(2).
        "));
        Assert.Contains("DEPENDING ON", ex.Message);
        Assert.Contains("nested inside another OCCURS", ex.Message);
    }

    [Fact]
    public void FixedOccursNestedInsideAnOdoTableIsRejected()
    {
        // The inverse: a fixed OCCURS inside an ODO table. The outer table being
        // variable makes the whole shape a variable-length table-of-tables, still
        // rejected loudly.
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(@"
            01  R.
                05  N PIC 9(2).
                05  OUTER OCCURS 1 TO 5 TIMES DEPENDING ON N.
                    10  INNER OCCURS 3 TIMES.
                        15  V PIC X(2).
        "));
        Assert.Contains("OCCURS", ex.Message);
        Assert.Contains("INNER", ex.Message);
    }

    [Fact]
    public void OccursWithoutACountIsRejected()
    {
        Assert.Throws<FormatException>(() => CopybookParser.Parse("01  R.\n    05  SLOT PIC X(2) OCCURS TIMES.\n"));
    }

    [Fact]
    public void PathologicallyLargeOccursCountFailsLoudlyRatherThanOverflowing()
    {
        // 200,000,000 copies of a 20-byte group overflows a 32-bit byte length
        // (4,000,000,000 > int.MaxValue). Unchecked multiplication would wrap to
        // a wrong (likely negative) Len silently -- exactly the "silently wrong
        // offsets" failure mode this project fails loudly on everywhere else.
        // No real copybook needs a count this large; this is a guard against
        // pathological input, not a realistic file.
        var ex = Assert.Throws<OverflowException>(() => CopybookParser.Parse(
            "01  R.\n    05  BIG-GROUP OCCURS 200000000.\n        10  A PIC X(20).\n"));
        Assert.Contains("BIG-GROUP", ex.Message);
        Assert.Contains("overflows", ex.Message);
    }
}
