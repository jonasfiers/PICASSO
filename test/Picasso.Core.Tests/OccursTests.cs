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
/// LINE-ITEM(2)-ITEM-QTY. The two harder shapes — OCCURS ... DEPENDING ON and
/// nested OCCURS — stay rejected, each with its own distinct error, and are
/// asserted here next to the feature so neither can slip through as a side
/// effect of the fixed-count path.
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
    public void DependingOnIsRejectedAsADistinctNamedCaseNotAFixedCount()
    {
        // The regression that proves the easier case didn't let ODO through:
        // "OCCURS m TO n TIMES DEPENDING ON f" must not be treated as a fixed
        // count of m or n. Its message names DEPENDING ON specifically and is
        // NOT the generic "OCCURS is not supported" text the old code used.
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(@"
            01  R.
                05  N            PIC 9(2).
                05  ENTRY OCCURS 1 TO 10 TIMES DEPENDING ON N.
                    10  ENTRY-VAL PIC X(4).
        "));

        Assert.Contains("DEPENDING ON", ex.Message);
        Assert.Contains("ENTRY", ex.Message);
        // Distinct from the fixed-count path: the bounds are never mistaken for a
        // count, so no field named ENTRY(1)/ENTRY(10) exists.
        Assert.DoesNotContain("Nested OCCURS", ex.Message);
    }

    [Fact]
    public void DependingOnWithoutAnUpperBoundIsAlsoRejected()
    {
        // Some dialects allow "OCCURS n TIMES DEPENDING ON f" with no TO. The
        // DEPENDING keyword alone still marks the variable form.
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(
            "01  R.\n    05  N PIC 9(2).\n    05  E PIC X(3) OCCURS 9 TIMES DEPENDING ON N.\n"));
        Assert.Contains("DEPENDING ON", ex.Message);
    }

    [Fact]
    public void NestedOccursIsRejectedAsASeparateNamedNonGoal()
    {
        // A table of tables is a real construct but a deliberate non-goal for this
        // pass. Its message names nested OCCURS and is distinct from the ODO one.
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(@"
            01  R.
                05  OUTER OCCURS 2 TIMES.
                    10  INNER PIC X(3) OCCURS 4 TIMES.
        "));

        // Named as nested OCCURS, not silently expanded as if the inner count
        // were 1. (The message contrasts itself against ODO by name, so its own
        // identity is "Nested OCCURS", asserted here.)
        Assert.Contains("Nested OCCURS", ex.Message);
        Assert.Contains("table of tables", ex.Message);
        Assert.Contains("INNER", ex.Message);
    }

    [Fact]
    public void NestedOccursOnGroupsIsAlsoRejected()
    {
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(@"
            01  R.
                05  OUTER OCCURS 2 TIMES.
                    10  MIDDLE OCCURS 3 TIMES.
                        15  LEAF PIC X(1).
        "));
        Assert.Contains("Nested OCCURS", ex.Message);
        Assert.Contains("MIDDLE", ex.Message);
    }

    [Fact]
    public void OccursWithoutACountIsRejected()
    {
        Assert.Throws<FormatException>(() => CopybookParser.Parse("01  R.\n    05  SLOT PIC X(2) OCCURS TIMES.\n"));
    }
}
