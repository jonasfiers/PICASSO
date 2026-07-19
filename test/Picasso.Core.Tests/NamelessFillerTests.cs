using System.Linq;
using Picasso.Core;
using Picasso.Core.Models;
using Xunit;

namespace Picasso.Core.Tests;

/// <summary>
/// Omitted data-name → unnamed FILLER. When a data description states no data-name
/// (the level number is followed directly by a clause keyword, e.g.
/// <c>02 PIC X(4) VALUE '1420'.</c>) COBOL treats the item as an unnamed FILLER and
/// a real compiler (GnuCOBOL) accepts it — the record LENGTH includes those bytes.
/// PICASSO used to take the clause keyword ("PIC") as the field NAME and reject the
/// item as having no PICTURE: a FALSE REJECTION on legal COBOL, and the general form
/// of the already-handled nameless-REDEFINES case. These tests pin the fix: the item
/// becomes a FILLER of the right width/offset, and — critically — a NAMED item is
/// still parsed with its own name unchanged.
/// </summary>
public class NamelessFillerTests
{
    private static FieldSpec[] Fillers(ParsedCopybook p) =>
        p.Flat.Where(f => f.Name == "FILLER").ToArray();

    [Fact]
    public void NamelessPicElementaryBecomesFillerOfRightWidth()
    {
        // "02 PIC X(4) VALUE '1420'." — the exact idz "Global Auto Mart" shape.
        // One FILLER, 4 bytes, at offset 0; the VALUE literal is ignored (no storage).
        var p = CopybookParser.Parse(
            "01  REC.\n" +
            "    02 PIC X(4) VALUE '1420'.\n");

        var fillers = Fillers(p);
        Assert.Single(fillers);
        Assert.Equal(0, fillers[0].Start);
        Assert.Equal(4, fillers[0].Len);
        Assert.Equal(FieldType.Text, fillers[0].Type);
        Assert.Equal(4, p.Root.Len);
    }

    [Fact]
    public void MultipleNamelessFillersLayOutSequentially()
    {
        // A run of nameless FILLERs (idz-gam shape) accumulates offsets like any
        // sequence of fields; a numeric-display FILLER keeps its own width.
        var p = CopybookParser.Parse(
            "01  REC.\n" +
            "    02 PIC X(4) VALUE '1420'.\n" +
            "    02 PIC 9(4) VALUE 2004.\n" +
            "    02 PIC X(20) VALUE 'Chrysler'.\n");

        var fillers = Fillers(p);
        Assert.Equal(3, fillers.Length);
        Assert.Equal(0, fillers[0].Start);
        Assert.Equal(4, fillers[0].Len);
        Assert.Equal(4, fillers[1].Start);
        Assert.Equal(4, fillers[1].Len);
        Assert.Equal(FieldType.NumericDisplay, fillers[1].Type);
        Assert.Equal(8, fillers[2].Start);
        Assert.Equal(20, fillers[2].Len);
        Assert.Equal(28, p.Root.Len);
    }

    [Fact]
    public void NamelessFillerWithComp3UsageIsSizedPacked()
    {
        // The clause keyword after the level can be a USAGE too. "02 PIC S9(4) COMP-3."
        // is a 3-byte packed FILLER (COMP-3 of 4 digits = ceil((4+1)/2) = 3 bytes),
        // not a 4-byte DISPLAY — proving the usage clause is honoured on a FILLER.
        var p = CopybookParser.Parse(
            "01  REC.\n" +
            "    02 CUST-NO PIC X(4).\n" +
            "    02 PIC S9(4) COMP-3.\n" +
            "    02 FLAG PIC X.\n");

        var filler = Fillers(p).Single();
        Assert.Equal(4, filler.Start);
        Assert.Equal(3, filler.Len);
        Assert.Equal(FieldType.Comp3, filler.Type);
        Assert.Equal(8, p.Root.Len);
    }

    [Fact]
    public void NamedItemAdjacentToNamelessFillerKeepsItsName()
    {
        // GUARD: the fix must NEVER FILLER-ise a NAMED item. A named field sitting
        // between two nameless FILLERs keeps its own name and offset unchanged.
        var p = CopybookParser.Parse(
            "01  REC.\n" +
            "    02 PIC X(4) VALUE '1420'.\n" +
            "    02 CUST-NO PIC X(4).\n" +
            "    02 PIC X(2) VALUE 'ZZ'.\n");

        var custNo = p.Flat.Single(f => f.Name == "CUST-NO");
        Assert.Equal(4, custNo.Start);
        Assert.Equal(4, custNo.Len);
        Assert.Equal(2, Fillers(p).Length);
        Assert.Equal(10, p.Root.Len);
    }

    [Fact]
    public void PlainNamedItemUnaffected()
    {
        // GUARD (baseline): an ordinary named record with no nameless items is
        // untouched — every field keeps its name; no spurious FILLER appears.
        var p = CopybookParser.Parse(
            "01  REC.\n" +
            "    05  A PIC X(3).\n" +
            "    05  B PIC 9(2).\n");

        Assert.Empty(Fillers(p));
        Assert.Equal(0, p.Flat.Single(f => f.Name == "A").Start);
        Assert.Equal(3, p.Flat.Single(f => f.Name == "B").Start);
        Assert.Equal(5, p.Root.Len);
    }

    [Fact]
    public void ExplicitFillerNameStillWorks()
    {
        // The literal word FILLER (an explicit, spelled-out FILLER) is NOT in the
        // clause-keyword set, so it still flows through the normal named path —
        // unchanged by the omitted-name handling.
        var p = CopybookParser.Parse(
            "01  REC.\n" +
            "    05  FILLER PIC X(3).\n" +
            "    05  A PIC X(2).\n");

        Assert.Single(Fillers(p));
        Assert.Equal(0, Fillers(p)[0].Start);
        Assert.Equal(3, Fillers(p)[0].Len);
        Assert.Equal(3, p.Flat.Single(f => f.Name == "A").Start);
    }

    [Theory]
    // Data-names that resemble a clause keyword but are NOT reserved words must keep
    // their names — the omitted-name detection matches whole reserved tokens only.
    // (POINTER-32/POINTER-64 are NOT COBOL reserved words — a regression guard.)
    [InlineData("POINTER-32")]
    [InlineData("POINTER-64")]
    [InlineData("COMP-CODE")]
    [InlineData("VALUE-DATE")]
    [InlineData("OCCURS-CTR")]
    public void KeywordLookalikeDataNameKeepsItsName(string dataName)
    {
        var p = CopybookParser.Parse($"01  REC.\n    05  {dataName} PIC X(4).\n");
        var f = Assert.Single(p.Flat);
        Assert.Equal(dataName, f.Name);   // named field, NOT turned into FILLER or rejected
        Assert.Equal(4, f.Len);
    }
}
