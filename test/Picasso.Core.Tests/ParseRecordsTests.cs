using System;
using System.IO;
using System.Linq;
using System.Text;
using Picasso.Core;
using Picasso.Core.Models;
using Xunit;

namespace Picasso.Core.Tests;

/// <summary>
/// Covers <see cref="CopybookParser.ParseRecords"/> — the additive multi-record
/// API. Multiple 01-level records in one copybook are ALTERNATIVE layouts (a
/// header type and a detail type sharing one file), each starting at byte offset
/// 0 and decoded independently — never concatenated. The single-record
/// <see cref="CopybookParser.Parse"/> is unchanged and still rejects a multi-01
/// copybook loudly; these tests pin both halves of that contract.
/// </summary>
public class ParseRecordsTests
{
    // ---- Multiple alternative 01 records ----

    /// <summary>
    /// The brief's worked example: two records, each independent and offset from 0.
    /// record 0 = A@0 len 4 (root len 4); record 1 = B@0 len 6, C@6 len 2 (root len 8).
    /// </summary>
    [Fact]
    public void TwoRecordsEachStartAtOffsetZeroWithOwnLayout()
    {
        var source =
            "01  CLIDATA-HEADER.\n" +
            "    05  A PIC X(4).\n" +
            "01  CLIDATA-DETAIL.\n" +
            "    05  B PIC 9(6).\n" +
            "    05  C PIC X(2).\n";

        var records = CopybookParser.ParseRecords(source);

        Assert.Equal(2, records.Count);

        // Record 0
        Assert.Equal("CLIDATA-HEADER", records[0].Root.Name);
        Assert.False(records[0].RootIsSynthetic);
        var a = Assert.Single(records[0].Flat);
        Assert.Equal("A", a.Name);
        Assert.Equal(0, a.Start);
        Assert.Equal(4, a.Len);
        Assert.Equal(4, records[0].Root.Len);

        // Record 1 — starts at offset 0 again, NOT after record 0.
        Assert.Equal("CLIDATA-DETAIL", records[1].Root.Name);
        Assert.False(records[1].RootIsSynthetic);
        Assert.Equal(2, records[1].Flat.Count);

        var b = records[1].Flat[0];
        Assert.Equal("B", b.Name);
        Assert.Equal(0, b.Start);
        Assert.Equal(6, b.Len);

        var c = records[1].Flat[1];
        Assert.Equal("C", c.Name);
        Assert.Equal(6, c.Start);
        Assert.Equal(2, c.Len);

        Assert.Equal(8, records[1].Root.Len);
    }

    /// <summary>
    /// Three records, in source order, each independent and offset from 0. Guards
    /// against any accumulation of offsets across records.
    /// </summary>
    [Fact]
    public void ThreeRecordsAllIndependentAndInSourceOrder()
    {
        var source =
            "01  REC-ONE.\n" +
            "    05  F1 PIC X(2).\n" +
            "01  REC-TWO.\n" +
            "    05  F2 PIC X(3).\n" +
            "01  REC-THREE.\n" +
            "    05  F3 PIC X(5).\n";

        var records = CopybookParser.ParseRecords(source);

        Assert.Equal(3, records.Count);
        Assert.Equal(new[] { "REC-ONE", "REC-TWO", "REC-THREE" },
            records.Select(r => r.Root.Name).ToArray());

        // Every record's sole field starts at 0 — no cross-record offsetting.
        Assert.All(records, r => Assert.Equal(0, r.Flat.Single().Start));
        Assert.Equal(new[] { 2, 3, 5 }, records.Select(r => r.Root.Len).ToArray());
    }

    /// <summary>
    /// The independence must hold for byte-width-bearing usages too: a COMP-3 field
    /// in the second record computes its own packed length from offset 0, unaffected
    /// by the first record.
    /// </summary>
    [Fact]
    public void SecondRecordComputesItsOwnComp3OffsetsFromZero()
    {
        var source =
            "01  HDR.\n" +
            "    05  H PIC X(10).\n" +
            "01  DTL.\n" +
            "    05  D1 PIC 9(5) COMP-3.\n" +   // packed: ceil((5+1)/2) = 3 bytes
            "    05  D2 PIC X(4).\n";

        var records = CopybookParser.ParseRecords(source);

        Assert.Equal(2, records.Count);
        var d1 = records[1].Flat[0];
        var d2 = records[1].Flat[1];
        Assert.Equal(FieldType.Comp3, d1.Type);
        Assert.Equal(0, d1.Start);
        Assert.Equal(3, d1.Len);
        Assert.Equal(3, d2.Start);
        Assert.Equal(4, d2.Len);
        Assert.Equal(7, records[1].Root.Len);
    }

    // ---- Single 01: one-element list, identical to Parse ----

    [Fact]
    public void SingleRecordReturnsOneElementIdenticalToParse()
    {
        var source =
            "01  R.\n" +
            "    05  A PIC X(3).\n" +
            "    05  B PIC 9(4).\n";

        var viaParse = CopybookParser.Parse(source);
        var records = CopybookParser.ParseRecords(source);

        var only = Assert.Single(records);
        AssertLayoutsIdentical(viaParse, only);
    }

    // ---- Headless copy member: one synthetic 01, identical to Parse ----

    [Fact]
    public void HeadlessMemberReturnsOneSyntheticRecordIdenticalToParse()
    {
        var source = "05  A PIC X(3).\n";

        var viaParse = CopybookParser.Parse(source);
        var records = CopybookParser.ParseRecords(source);

        var only = Assert.Single(records);
        Assert.True(only.RootIsSynthetic);
        Assert.Equal(CopybookParser.SyntheticRecordName, only.Root.Name);
        AssertLayoutsIdentical(viaParse, only);
    }

    [Fact]
    public void HeadlessMemberHonoursCustomRecordName()
    {
        var records = CopybookParser.ParseRecords("05  A PIC X(3).\n", "MY-REC");
        var only = Assert.Single(records);
        Assert.True(only.RootIsSynthetic);
        Assert.Equal("MY-REC", only.Root.Name);
    }

    // ---- Backward compatibility: Parse is UNCHANGED ----

    /// <summary>
    /// The whole point of the additive design: <see cref="CopybookParser.Parse"/>
    /// still rejects a multi-01 copybook with its existing, named error. If this
    /// ever passes, the additive contract has been broken.
    /// </summary>
    [Fact]
    public void ParseStillRejectsMultipleTopLevelRecords()
    {
        var source =
            "01  FIRST-REC.\n" +
            "    05  A PIC X(3).\n" +
            "01  SECOND-REC.\n" +
            "    05  B PIC X(3).\n";

        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(source));
        Assert.Contains("FIRST-REC", ex.Message);
        Assert.Contains("SECOND-REC", ex.Message);
        Assert.Contains("alternative layouts", ex.Message);
    }

    // ---- Fail-loud guards, applied per record ----

    /// <summary>
    /// A record that carries no storage — here its only child is a level-88
    /// condition-name, which is dropped — must throw the empty-record error even
    /// when a perfectly good record precedes it. No silent zero-byte layout.
    /// </summary>
    [Fact]
    public void EmptyRecordAmongGoodOnesThrows()
    {
        var source =
            "01  GOOD-REC.\n" +
            "    05  A PIC X(3).\n" +
            "01  EMPTY-REC.\n" +
            "    88  FLAG VALUE 'X'.\n";

        var ex = Assert.Throws<FormatException>(() => CopybookParser.ParseRecords(source));
        Assert.Contains("no elementary fields", ex.Message);
        Assert.Contains("zero bytes", ex.Message);
        // The message names the offending record specifically.
        Assert.Contains("EMPTY-REC", ex.Message);
    }

    [Fact]
    public void BlankRecordNameThrows()
    {
        Assert.Throws<ArgumentException>(() => CopybookParser.ParseRecords("05 A PIC X(3).\n", "   "));
    }

    [Fact]
    public void NoDataItemEntriesThrows()
    {
        var ex = Assert.Throws<FormatException>(() => CopybookParser.ParseRecords("      *> only a comment\n"));
        Assert.Contains("no data-item entries", ex.Message);
    }

    /// <summary>
    /// A lower-level entry BEFORE the first 01 becomes a top-level sibling of the
    /// 01 records (a level-03 after an 01 would nest under it, but nothing precedes
    /// this one). It belongs to no record — not nested under an 01, and not itself
    /// an 01 — so ParseRecords rejects it loudly rather than inventing a record for
    /// the orphan or silently attaching it to a following one.
    /// </summary>
    [Fact]
    public void RejectsOrphanTopLevelEntryMixedWith01()
    {
        var source =
            "05  ORPHAN PIC X(2).\n" +  // no 01 precedes it: a top-level orphan
            "01  REC-A.\n" +
            "    05  A PIC X(3).\n" +
            "01  REC-B.\n" +
            "    05  B PIC X(3).\n";

        var ex = Assert.Throws<FormatException>(() => CopybookParser.ParseRecords(source));
        Assert.Contains("ORPHAN", ex.Message);
        Assert.Contains("belongs to no record", ex.Message);
    }

    // ---- Regression: real copybooks round-trip through ParseRecords ----

    /// <summary>
    /// DTAR020 (a genuine headless mainframe copy member) yields exactly one record
    /// through ParseRecords, byte-identical to what Parse produces.
    /// </summary>
    [Fact]
    public void Dtar020ThroughParseRecordsMatchesParse()
    {
        var source = File.ReadAllText(SamplePaths.Root("dtar020/DTAR020.cbl"));

        var viaParse = CopybookParser.Parse(source);
        var records = CopybookParser.ParseRecords(source);

        var only = Assert.Single(records);
        AssertLayoutsIdentical(viaParse, only);
    }

    [Theory]
    [InlineData("USER-REC.cpy")]
    [InlineData("SHARE-REC.cpy")]
    [InlineData("BALANCE-REC.cpy")]
    public void SingleRecordCatalog74CopybooksMatchParse(string fileName)
    {
        var source = File.ReadAllText(SamplePaths.Catalog74(fileName), Encoding.Latin1);

        var viaParse = CopybookParser.Parse(source);
        var records = CopybookParser.ParseRecords(source);

        var only = Assert.Single(records);
        AssertLayoutsIdentical(viaParse, only);
    }

    /// <summary>
    /// Field-by-field structural equality of two layouts: same synthetic flag, same
    /// root name/length, and the same flat field list (name, type, start, length).
    /// </summary>
    private static void AssertLayoutsIdentical(ParsedCopybook expected, ParsedCopybook actual)
    {
        Assert.Equal(expected.RootIsSynthetic, actual.RootIsSynthetic);
        Assert.Equal(expected.Root.Name, actual.Root.Name);
        Assert.Equal(expected.Root.Len, actual.Root.Len);
        Assert.Equal(expected.Flat.Count, actual.Flat.Count);

        for (var i = 0; i < expected.Flat.Count; i++)
        {
            var e = expected.Flat[i];
            var a = actual.Flat[i];
            Assert.Equal(e.Name, a.Name);
            Assert.Equal(e.Type, a.Type);
            Assert.Equal(e.Start, a.Start);
            Assert.Equal(e.Len, a.Len);
            Assert.Equal(e.Digits, a.Digits);
            Assert.Equal(e.Scale, a.Scale);
            Assert.Equal(e.Signed, a.Signed);
        }
    }
}
