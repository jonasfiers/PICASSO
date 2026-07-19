using System;
using System.Collections.Generic;
using System.Linq;
using Picasso.Core;
using Picasso.Core.Models;
using Xunit;

namespace Picasso.Core.Tests;

/// <summary>
/// OCCURS ... DEPENDING ON (variable-length / ODO) — the single, common case:
/// exactly one OCCURS m TO n DEPENDING ON dep per record, dep defined before
/// the table. There is no clean static offset oracle for a variable-length
/// record, so round-trip correctness at several counts IS the validation:
/// encode a record → decode it → the values (and the record's byte geometry)
/// must come back identical. Everything harder — two ODO tables, nested ODO,
/// an OCCURS inside the ODO, a dep after/absent — is rejected loudly, asserted
/// here next to the feature.
/// </summary>
public class OdoTests
{
    private const string ElementaryOdo = @"
        01  R.
            05  CNT  PIC 9(2).
            05  TAB  PIC X(3) OCCURS 1 TO 5 DEPENDING ON CNT.
            05  TAIL PIC X(2).
    ";

    // REDEFINES overlay in a record that also has an ODO tail — the classic
    // mainframe shape (a raw key redefined as numeric, then a variable table).
    // The parser lays KEY-NUM over KEY-RAW (both @0), so the record is overlap-
    // aware: 4 (shared) + 1 (CNT) + 2*count (T). The codec must measure it the
    // same overlap-aware way, and must refuse to ENCODE the ambiguous overlay.
    private const string RedefinesOdo = @"
        01  R.
            05  KEY-RAW PIC X(4).
            05  KEY-NUM REDEFINES KEY-RAW PIC 9(4).
            05  CNT     PIC 9.
            05  T OCCURS 0 TO 3 DEPENDING ON CNT.
                10  V PIC X(2).
    ";

    [Fact]
    public void RedefinesInsideOdoRecordDecodesAtOverlaidLength()
    {
        // A genuine 9-byte record (CNT=2): "1234" + "2" + "xx" + "yy". Both overlay
        // readings come from bytes 0-3; the record is 9 bytes, not 13 (the Sum of
        // all field lengths, which double-counts the overlap). Must NOT be rejected.
        var parsed = CopybookParser.Parse(RedefinesOdo);
        var decoded = FlatFileCodec.Decode(parsed, "12342xxyy");
        var rec = Assert.Single(decoded);
        Assert.Equal("1234", rec["KEY-RAW"]);
        Assert.Equal(1234m, rec["KEY-NUM"]);
        Assert.Equal(2m, rec["CNT"]);
        Assert.Equal("xx", rec["T(1)-V"]);
        Assert.Equal("yy", rec["T(2)-V"]);
    }

    [Fact]
    public void RedefinesInsideOdoRecordEncodeRejectedAsOverlapping()
    {
        // Encoding an overlapping layout is ambiguous (which of KEY-RAW / KEY-NUM
        // owns bytes 0-3?), so it must fail loud — exactly as the non-ODO REDEFINES
        // encode path does — never silently write both readings (a 13-byte record).
        var parsed = CopybookParser.Parse(RedefinesOdo);
        var record = new Dictionary<string, object>
        {
            ["KEY-RAW"] = "1234", ["KEY-NUM"] = 1234m, ["CNT"] = 2m,
            ["T(1)-V"] = "xx", ["T(2)-V"] = "yy",
        };
        var ex = Assert.Throws<FormatException>(() => FlatFileCodec.Encode(parsed, new[] { record }));
        Assert.Contains("overlap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Recognition + representative layout ----

    [Fact]
    public void OdoCopybookIsFlaggedVariableLengthWithBoundsAndDependingField()
    {
        var parsed = CopybookParser.Parse(ElementaryOdo);

        Assert.True(parsed.IsVariableLength);
        Assert.Equal("TAB", parsed.Odo!.TableName);
        Assert.Equal("CNT", parsed.Odo.DependsOn);
        Assert.Equal(1, parsed.Odo.Min);
        Assert.Equal(5, parsed.Odo.Max);

        // The depending field is at its fixed prefix offset, ahead of the table.
        Assert.Equal(0, parsed.Odo.DependingField.Start);
        Assert.Equal(2, parsed.Odo.DependingField.Len);
    }

    [Fact]
    public void RepresentativeFlatIsTheMinimumCountLayout()
    {
        // Flat is documented as a min-count snapshot for an ODO copybook, not the
        // authoritative decode layout. Min here is 1, so one TAB copy.
        var parsed = CopybookParser.Parse(ElementaryOdo);
        Assert.Equal(new[] { "CNT", "TAB(1)", "TAIL" }, parsed.Flat.Select(f => f.Name));
    }

    // ---- Concrete layout geometry ----

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void ConcreteLayoutPlacesTailAfterAllCopiesAndSizesTheRecord(int count)
    {
        var parsed = CopybookParser.Parse(ElementaryOdo);
        var spec = CopybookParser.BuildConcreteLayout(parsed, count);

        var expectedNames = new List<string> { "CNT" };
        for (var k = 1; k <= count; k++) expectedNames.Add($"TAB({k})");
        expectedNames.Add("TAIL");
        Assert.Equal(expectedNames, spec.Select(f => f.Name));

        // TAB copies are 3 bytes each, right after the 2-byte CNT.
        for (var k = 1; k <= count; k++)
        {
            var tab = spec.Single(f => f.Name == $"TAB({k})");
            Assert.Equal(2 + (k - 1) * 3, tab.Start);
            Assert.Equal(3, tab.Len);
        }

        var tail = spec.Single(f => f.Name == "TAIL");
        Assert.Equal(2 + count * 3, tail.Start);
        Assert.Equal(2 + count * 3 + 2, spec.Sum(f => f.Len));
    }

    // ---- Round-trip: the core validation ----

    [Theory]
    [InlineData(1, CharacterEncoding.Latin1)]
    [InlineData(3, CharacterEncoding.Latin1)]
    [InlineData(5, CharacterEncoding.Latin1)]
    [InlineData(1, CharacterEncoding.Ebcdic037)]
    [InlineData(3, CharacterEncoding.Ebcdic037)]
    [InlineData(5, CharacterEncoding.Ebcdic037)]
    public void ElementaryOdoRoundTripsAtEachCount(int count, CharacterEncoding encoding)
    {
        var parsed = CopybookParser.Parse(ElementaryOdo);

        var record = new Dictionary<string, object> { ["CNT"] = (decimal)count, ["TAIL"] = "ZZ" };
        for (var k = 1; k <= count; k++)
            record[$"TAB({k})"] = "X" + k + "X"; // distinct per slot, e.g. X1X

        var encoded = FlatFileCodec.Encode(parsed, new[] { record }, encoding);

        // Geometry: one line, TAIL landing at 2 + count*3, total 2 + count*3 + 2.
        var line = encoded.TrimEnd('\n');
        Assert.Equal(2 + count * 3 + 2, line.Length);

        var decoded = FlatFileCodec.Decode(parsed, encoded, encoding);
        var got = Assert.Single(decoded);

        Assert.Equal((decimal)count, got["CNT"]);
        Assert.Equal("ZZ", got["TAIL"]);
        for (var k = 1; k <= count; k++)
            Assert.Equal("X" + k + "X", got[$"TAB({k})"]);

        // No stray keys beyond the count.
        Assert.False(got.ContainsKey($"TAB({count + 1})"));
    }

    [Fact]
    public void MultipleOdoRecordsOfDifferentCountsRoundTripTogether()
    {
        var parsed = CopybookParser.Parse(ElementaryOdo);

        var records = new List<Dictionary<string, object>>();
        foreach (var count in new[] { 2, 5, 1, 4 })
        {
            var r = new Dictionary<string, object> { ["CNT"] = (decimal)count, ["TAIL"] = "TL" };
            for (var k = 1; k <= count; k++) r[$"TAB({k})"] = $"s{k:0}".PadRight(3);
            records.Add(r);
        }

        var encoded = FlatFileCodec.Encode(parsed, records);
        var decoded = FlatFileCodec.Decode(parsed, encoded);

        Assert.Equal(4, decoded.Count);
        Assert.Equal(new object[] { 2m, 5m, 1m, 4m }, decoded.Select(d => d["CNT"]));
        Assert.Equal(5, decoded[1].Keys.Count(k => k.StartsWith("TAB(")));
        Assert.Equal(1, decoded[2].Keys.Count(k => k.StartsWith("TAB(")));
    }

    // ---- Group ODO element ----

    private const string GroupOdo = @"
        01  ORDER.
            05  LINE-COUNT PIC 9(2).
            05  LINE OCCURS 1 TO 4 DEPENDING ON LINE-COUNT.
                10  SKU   PIC X(4).
                10  QTY   PIC 9(3).
            05  FOOTER PIC X(2).
    ";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void GroupOdoRoundTripsAtEachCount(int count)
    {
        var parsed = CopybookParser.Parse(GroupOdo);
        Assert.True(parsed.IsVariableLength);

        var record = new Dictionary<string, object> { ["LINE-COUNT"] = (decimal)count, ["FOOTER"] = "OK" };
        for (var k = 1; k <= count; k++)
        {
            record[$"LINE({k})-SKU"] = $"SK{k:0}".PadRight(4);
            record[$"LINE({k})-QTY"] = (decimal)(k * 11);
        }

        var encoded = FlatFileCodec.Encode(parsed, new[] { record });
        var line = encoded.TrimEnd('\n');
        // 2 (count) + count*(4+3) + 2 (footer)
        Assert.Equal(2 + count * 7 + 2, line.Length);

        var decoded = FlatFileCodec.Decode(parsed, encoded);
        var got = Assert.Single(decoded);

        Assert.Equal((decimal)count, got["LINE-COUNT"]);
        Assert.Equal("OK", got["FOOTER"]);
        for (var k = 1; k <= count; k++)
        {
            Assert.Equal($"SK{k:0}", got[$"LINE({k})-SKU"]);
            Assert.Equal((decimal)(k * 11), got[$"LINE({k})-QTY"]);
        }
    }

    // ---- Undelimited (RECFM=F-style) walk ----

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void UndelimitedOdoRecordsRoundTripByWalkingTheStream(int count)
    {
        var parsed = CopybookParser.Parse(ElementaryOdo);

        // Three records of different counts, concatenated with no delimiters.
        var records = new List<Dictionary<string, object>>();
        foreach (var c in new[] { count, 2, 4 })
        {
            var r = new Dictionary<string, object> { ["CNT"] = (decimal)c, ["TAIL"] = "ZZ" };
            for (var k = 1; k <= c; k++) r[$"TAB({k})"] = "abc";
            records.Add(r);
        }

        var encoded = FlatFileCodec.Encode(parsed, records, CharacterEncoding.Latin1, RecordFormat.FixedLength);
        Assert.DoesNotContain('\n', encoded); // truly undelimited

        var decoded = FlatFileCodec.Decode(parsed, encoded, CharacterEncoding.Latin1, RecordFormat.FixedLength);
        Assert.Equal(3, decoded.Count);
        Assert.Equal(new object[] { (decimal)count, 2m, 4m }, decoded.Select(d => d["CNT"]));
    }

    // ---- Fail loud: count out of bounds ----

    [Fact]
    public void DecodeFailsLoudWhenCountBelowMinimum()
    {
        // Bounds 2 TO 5; a record declaring CNT=1 is out of range.
        var parsed = CopybookParser.Parse(
            "01 R.\n 05 CNT PIC 9(2).\n 05 TAB PIC X(3) OCCURS 2 TO 5 DEPENDING ON CNT.\n");

        // CNT=01 then one 3-byte entry — a well-formed line for count 1, but 1 < min.
        var ex = Assert.Throws<FormatException>(() =>
            FlatFileCodec.Decode(parsed, "01abc\n"));
        Assert.Contains("outside the OCCURS bounds", ex.Message);
        Assert.Contains("2 TO 5", ex.Message);
    }

    [Fact]
    public void DecodeFailsLoudWhenCountAboveMaximum()
    {
        var parsed = CopybookParser.Parse(ElementaryOdo); // 1 TO 5
        // CNT=06, six entries — count 6 > max 5.
        var line = "06" + string.Concat(Enumerable.Repeat("xyz", 6)) + "ZZ\n";
        var ex = Assert.Throws<FormatException>(() => FlatFileCodec.Decode(parsed, line));
        Assert.Contains("outside the OCCURS bounds", ex.Message);
    }

    [Fact]
    public void EncodeFailsLoudWhenCountOutOfBounds()
    {
        var parsed = CopybookParser.Parse(ElementaryOdo); // 1 TO 5
        var record = new Dictionary<string, object> { ["CNT"] = 6m, ["TAIL"] = "ZZ" };
        for (var k = 1; k <= 6; k++) record[$"TAB({k})"] = "xyz";

        var ex = Assert.Throws<FormatException>(() => FlatFileCodec.Encode(parsed, new[] { record }));
        Assert.Contains("outside the OCCURS bounds", ex.Message);
    }

    [Fact]
    public void EncodeFailsLoudWhenTableEntriesExceedTheCount()
    {
        // CNT says 2 but the record carries a TAB(3): encoding at count 2 would
        // silently drop TAB(3). Reject instead.
        var parsed = CopybookParser.Parse(ElementaryOdo);
        var record = new Dictionary<string, object>
        {
            ["CNT"] = 2m,
            ["TAB(1)"] = "aaa",
            ["TAB(2)"] = "bbb",
            ["TAB(3)"] = "ccc",
            ["TAIL"] = "ZZ",
        };

        var ex = Assert.Throws<FormatException>(() => FlatFileCodec.Encode(parsed, new[] { record }));
        Assert.Contains("TAB(3)", ex.Message);
    }

    [Fact]
    public void EncodeFailsLoudWhenAnExpectedTableEntryIsMissing()
    {
        // CNT says 3 but TAB(3) is absent: the layout at count 3 needs it.
        var parsed = CopybookParser.Parse(ElementaryOdo);
        var record = new Dictionary<string, object>
        {
            ["CNT"] = 3m,
            ["TAB(1)"] = "aaa",
            ["TAB(2)"] = "bbb",
            ["TAIL"] = "ZZ",
        };

        var ex = Assert.Throws<FormatException>(() => FlatFileCodec.Encode(parsed, new[] { record }));
        Assert.Contains("TAB(3)", ex.Message);
    }

    [Fact]
    public void EncodeFailsLoudWhenDependingFieldIsMissing()
    {
        var parsed = CopybookParser.Parse(ElementaryOdo);
        var record = new Dictionary<string, object> { ["TAB(1)"] = "aaa", ["TAIL"] = "ZZ" };

        var ex = Assert.Throws<FormatException>(() => FlatFileCodec.Encode(parsed, new[] { record }));
        Assert.Contains("CNT", ex.Message);
        Assert.Contains("depending field", ex.Message);
    }

    [Fact]
    public void DecodeFailsLoudWhenRecordTooShortToHoldDependingField()
    {
        var parsed = CopybookParser.Parse(ElementaryOdo); // dep CNT is 2 bytes
        var ex = Assert.Throws<FormatException>(() => FlatFileCodec.Decode(parsed, "0\n"));
        Assert.Contains("too short", ex.Message);
        Assert.Contains("CNT", ex.Message);
    }

    // ---- Fail loud: structural rejections at parse ----

    [Fact]
    public void DependingFieldDefinedAfterTheTableIsRejected()
    {
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(@"
            01  R.
                05  TAB PIC X(3) OCCURS 1 TO 5 DEPENDING ON CNT.
                05  CNT PIC 9(2).
        "));
        Assert.Contains("not defined before the table", ex.Message);
        Assert.Contains("CNT", ex.Message);
    }

    [Fact]
    public void DependingFieldThatDoesNotExistIsRejected()
    {
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(@"
            01  R.
                05  CNT PIC 9(2).
                05  TAB PIC X(3) OCCURS 1 TO 5 DEPENDING ON NOPE.
        "));
        Assert.Contains("does not exist", ex.Message);
        Assert.Contains("NOPE", ex.Message);
    }

    [Fact]
    public void OdoTableNestedInsideAFixedOccursIsRejected()
    {
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(@"
            01  R.
                05  OUTER OCCURS 3 TIMES.
                    10  CNT PIC 9(2).
                    10  TAB PIC X(3) OCCURS 1 TO 5 DEPENDING ON CNT.
        "));
        Assert.Contains("nested inside another OCCURS", ex.Message);
    }

    [Fact]
    public void FixedOccursNestedInsideTheOdoTableIsRejected()
    {
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(@"
            01  R.
                05  CNT PIC 9(2).
                05  LINE OCCURS 1 TO 5 DEPENDING ON CNT.
                    10  INNER PIC X(2) OCCURS 3 TIMES.
        "));
        Assert.Contains("inside the variable-length table", ex.Message);
    }

    [Fact]
    public void TwoNestedOdoTablesAreRejected()
    {
        // ODO inside ODO: caught either as nested-under-OCCURS or occurs-in-subtree;
        // both are loud FormatExceptions, which is the guarantee that matters.
        Assert.Throws<FormatException>(() => CopybookParser.Parse(@"
            01  R.
                05  OUTER-CNT PIC 9(2).
                05  OUTER OCCURS 1 TO 3 DEPENDING ON OUTER-CNT.
                    10  INNER-CNT PIC 9(2).
                    10  INNER PIC X(2) OCCURS 1 TO 4 DEPENDING ON INNER-CNT.
        "));
    }

    [Fact]
    public void OccursToWithoutDependingOnIsRejected()
    {
        // A range bound with no DEPENDING ON is not valid COBOL — must not be
        // silently read as a fixed count of either bound.
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(
            "01 R.\n 05 TAB PIC X(3) OCCURS 1 TO 5.\n"));
        Assert.Contains("without DEPENDING ON", ex.Message);
    }

    // ---- The non-ODO path is untouched ----

    [Fact]
    public void FixedCopybookStillDecodesThroughTheParsedCopybookOverload()
    {
        // The ParsedCopybook overload must be a transparent pass-through for a
        // non-ODO copybook — identical result to the flat-spec overload.
        var parsed = CopybookParser.Parse("01 R.\n 05 A PIC X(3).\n 05 B PIC 9(2).\n");
        Assert.False(parsed.IsVariableLength);

        var viaParsed = FlatFileCodec.Decode(parsed, "abc42\n");
        var viaFlat = FlatFileCodec.Decode(parsed.Flat, "abc42\n");

        Assert.Equal(viaFlat.Single()["A"], viaParsed.Single()["A"]);
        Assert.Equal(viaFlat.Single()["B"], viaParsed.Single()["B"]);
    }

    // ---- Multiple flat ODO tables in one record (left-to-right resolution) ----
    //
    // Two independent ODO tables per record, each with its own depending field
    // defined ahead of it. The second table's dep field (C2) sits AFTER the first
    // table, so its offset shifts with C1's count — an earlier count of 0 or 5
    // moves C2, and reading it at the wrong offset would be a silent miscompute.
    // Geometry per (C1, C2) is 6 + 3*C1 + 4*C2 bytes; every total below was
    // confirmed against GnuCOBOL (cobc -x -std=mvs) LENGTH OF the record.

    private const string TwoOdo = @"
        01  R.
            05  C1   PIC 9(2).
            05  T1   PIC X(3) OCCURS 0 TO 5 DEPENDING ON C1.
            05  C2   PIC 9(2).
            05  T2   PIC X(4) OCCURS 0 TO 5 DEPENDING ON C2.
            05  TAIL PIC X(2).
    ";

    private static Dictionary<string, object> BuildTwoOdoRecord(int c1, int c2)
    {
        var r = new Dictionary<string, object> { ["C1"] = (decimal)c1, ["C2"] = (decimal)c2, ["TAIL"] = "ZZ" };
        for (var k = 1; k <= c1; k++) r[$"T1({k})"] = ("p" + k).PadRight(3);
        for (var k = 1; k <= c2; k++) r[$"T2({k})"] = ("q" + k).PadRight(4);
        return r;
    }

    [Fact]
    public void TwoOdoTablesAreAcceptedAndListedInSourceOrder()
    {
        var parsed = CopybookParser.Parse(TwoOdo);
        Assert.True(parsed.IsVariableLength);
        Assert.Equal(new[] { "T1", "T2" }, parsed.Odos.Select(o => o.TableName));
        Assert.Equal(new[] { "C1", "C2" }, parsed.Odos.Select(o => o.DependsOn));
    }

    [Theory]
    [InlineData(0, 0, 6)]   // both empty
    [InlineData(1, 1, 13)]
    [InlineData(3, 2, 23)]
    [InlineData(5, 0, 21)]  // first full, second empty
    [InlineData(0, 5, 26)]  // first EMPTY — shifts C2's dep field to offset 2
    public void TwoOdoConcreteLayoutTotalMatchesGnuCobolLength(int c1, int c2, int total)
    {
        var parsed = CopybookParser.Parse(TwoOdo);
        var spec = CopybookParser.BuildConcreteLayout(parsed, new[] { c1, c2 });
        Assert.Equal(total, spec.Sum(f => f.Len));
    }

    [Theory]
    [InlineData(0, 0, CharacterEncoding.Latin1)]
    [InlineData(1, 1, CharacterEncoding.Latin1)]
    [InlineData(3, 2, CharacterEncoding.Latin1)]
    [InlineData(5, 0, CharacterEncoding.Latin1)]
    [InlineData(0, 5, CharacterEncoding.Latin1)]  // adversarial: first count 0
    [InlineData(0, 0, CharacterEncoding.Ebcdic037)]
    [InlineData(3, 2, CharacterEncoding.Ebcdic037)]
    [InlineData(0, 5, CharacterEncoding.Ebcdic037)]
    public void TwoOdoTablesRoundTripDelimited(int c1, int c2, CharacterEncoding encoding)
    {
        var parsed = CopybookParser.Parse(TwoOdo);
        var record = BuildTwoOdoRecord(c1, c2);

        var encoded = FlatFileCodec.Encode(parsed, new[] { record }, encoding);
        Assert.Equal(6 + 3 * c1 + 4 * c2, encoded.TrimEnd('\n').Length);

        var decoded = FlatFileCodec.Decode(parsed, encoded, encoding);
        var got = Assert.Single(decoded);

        Assert.Equal((decimal)c1, got["C1"]);
        Assert.Equal((decimal)c2, got["C2"]);
        Assert.Equal("ZZ", got["TAIL"]);
        for (var k = 1; k <= c1; k++) Assert.Equal(("p" + k).TrimEnd(), ((string)got[$"T1({k})"]).TrimEnd());
        for (var k = 1; k <= c2; k++) Assert.Equal(("q" + k).TrimEnd(), ((string)got[$"T2({k})"]).TrimEnd());
        Assert.False(got.ContainsKey($"T1({c1 + 1})"));
        Assert.False(got.ContainsKey($"T2({c2 + 1})"));
    }

    [Fact]
    public void DecodeReadsSecondDepFieldAtShiftedOffsetWhenFirstTableIsEmpty()
    {
        // C1=0 → T1 contributes nothing, so C2's dep field sits at offset 2, not
        // at its representative (min-count) offset. Hand-build the bytes (not via
        // Encode) so this exercises decode's offset-pinning independently.
        var parsed = CopybookParser.Parse(TwoOdo);
        // C1=00, C2=05, five 4-byte T2 entries, TAIL "ZZ" — 2+2+20+2 = 26 bytes.
        var line = "00" + "05" + "aaaabbbbccccddddeeee" + "ZZ" + "\n";
        Assert.Equal(26, line.TrimEnd('\n').Length);

        var got = Assert.Single(FlatFileCodec.Decode(parsed, line));
        Assert.Equal(0m, got["C1"]);
        Assert.Equal(5m, got["C2"]);
        Assert.Equal("aaaa", got["T2(1)"]);
        Assert.Equal("eeee", got["T2(5)"]);
        Assert.Equal("ZZ", got["TAIL"]);
        Assert.False(got.ContainsKey("T1(1)"));
    }

    [Fact]
    public void TwoOdoTablesRoundTripUndelimited()
    {
        var parsed = CopybookParser.Parse(TwoOdo);
        var records = new List<Dictionary<string, object>>
        {
            BuildTwoOdoRecord(3, 2),
            BuildTwoOdoRecord(0, 5),   // first table empty
            BuildTwoOdoRecord(5, 0),   // second table empty
            BuildTwoOdoRecord(0, 0),   // both empty
        };

        var encoded = FlatFileCodec.Encode(parsed, records, CharacterEncoding.Latin1, RecordFormat.FixedLength);
        Assert.DoesNotContain('\n', encoded);

        var decoded = FlatFileCodec.Decode(parsed, encoded, CharacterEncoding.Latin1, RecordFormat.FixedLength);
        Assert.Equal(4, decoded.Count);
        Assert.Equal(new object[] { 3m, 0m, 5m, 0m }, decoded.Select(d => d["C1"]));
        Assert.Equal(new object[] { 2m, 5m, 0m, 0m }, decoded.Select(d => d["C2"]));
    }

    [Fact]
    public void TwoOdoTablesRoundTripUndelimitedEbcdic()
    {
        var parsed = CopybookParser.Parse(TwoOdo);
        var records = new List<Dictionary<string, object>> { BuildTwoOdoRecord(2, 3), BuildTwoOdoRecord(0, 1) };

        var encoded = FlatFileCodec.Encode(parsed, records, CharacterEncoding.Ebcdic037, RecordFormat.FixedLength);
        var decoded = FlatFileCodec.Decode(parsed, encoded, CharacterEncoding.Ebcdic037, RecordFormat.FixedLength);

        Assert.Equal(2, decoded.Count);
        Assert.Equal("p1", ((string)decoded[0]["T1(1)"]).TrimEnd());
        Assert.Equal("q3", ((string)decoded[0]["T2(3)"]).TrimEnd());
        Assert.Equal(0m, decoded[1]["C1"]);
        Assert.Equal(1m, decoded[1]["C2"]);
    }

    [Fact]
    public void DecodeFailsLoudWhenSecondTableCountOutOfBounds()
    {
        // C1=1 (fine), C2=06 (max 5). The failure must name the SECOND table's
        // bounds, proving each table is validated independently.
        var parsed = CopybookParser.Parse(TwoOdo);
        var line = "01" + "ppp" + "06" + string.Concat(Enumerable.Repeat("qqqq", 6)) + "ZZ" + "\n";
        var ex = Assert.Throws<FormatException>(() => FlatFileCodec.Decode(parsed, line));
        Assert.Contains("outside the OCCURS bounds", ex.Message);
        Assert.Contains("C2", ex.Message);
        Assert.Contains("T2", ex.Message);
    }

    [Fact]
    public void EncodeFailsLoudWhenSecondTableCountOutOfBounds()
    {
        var parsed = CopybookParser.Parse(TwoOdo);
        var record = BuildTwoOdoRecord(1, 5);
        record["C2"] = 6m;                     // claim 6 but bounds are 0 TO 5
        record["T2(6)"] = "qqqq";
        var ex = Assert.Throws<FormatException>(() => FlatFileCodec.Encode(parsed, new[] { record }));
        Assert.Contains("outside the OCCURS bounds", ex.Message);
        Assert.Contains("C2", ex.Message);
    }

    [Fact]
    public void EncodeFailsLoudWhenSecondTableHasEntriesBeyondItsCount()
    {
        // C2=2 but a T2(3) is present: encoding at count 2 would silently drop it.
        var parsed = CopybookParser.Parse(TwoOdo);
        var record = BuildTwoOdoRecord(1, 2);
        record["T2(3)"] = "qqqq";
        var ex = Assert.Throws<FormatException>(() => FlatFileCodec.Encode(parsed, new[] { record }));
        Assert.Contains("T2(3)", ex.Message);
    }

    // ---- Three flat ODO tables (recursion of the same resolution) ----
    //
    // Length per (C1, C2, C3) is 8 + 2*C1 + 3*C2 + C3 bytes — each combination
    // below confirmed against GnuCOBOL LENGTH OF the record.

    private const string ThreeOdo = @"
        01  R3.
            05  C1 PIC 9(2).
            05  A  PIC X(2) OCCURS 0 TO 4 DEPENDING ON C1.
            05  C2 PIC 9(2).
            05  B  PIC X(3) OCCURS 0 TO 4 DEPENDING ON C2.
            05  C3 PIC 9(2).
            05  D  PIC X(1) OCCURS 0 TO 4 DEPENDING ON C3.
            05  Z  PIC X(2).
    ";

    [Theory]
    [InlineData(2, 3, 1, 22)]
    [InlineData(0, 0, 0, 8)]
    [InlineData(4, 0, 4, 20)]  // middle table empty, both ends full
    public void ThreeOdoConcreteLayoutTotalMatchesGnuCobolLength(int c1, int c2, int c3, int total)
    {
        var parsed = CopybookParser.Parse(ThreeOdo);
        Assert.Equal(new[] { "A", "B", "D" }, parsed.Odos.Select(o => o.TableName));
        var spec = CopybookParser.BuildConcreteLayout(parsed, new[] { c1, c2, c3 });
        Assert.Equal(total, spec.Sum(f => f.Len));
    }

    [Theory]
    [InlineData(2, 3, 1)]
    [InlineData(0, 0, 0)]
    [InlineData(4, 0, 4)]
    public void ThreeOdoTablesRoundTrip(int c1, int c2, int c3)
    {
        var parsed = CopybookParser.Parse(ThreeOdo);
        var r = new Dictionary<string, object>
        {
            ["C1"] = (decimal)c1, ["C2"] = (decimal)c2, ["C3"] = (decimal)c3, ["Z"] = "ZZ",
        };
        for (var k = 1; k <= c1; k++) r[$"A({k})"] = ("a" + k).PadRight(2).Substring(0, 2);
        for (var k = 1; k <= c2; k++) r[$"B({k})"] = ("b" + k).PadRight(3);
        for (var k = 1; k <= c3; k++) r[$"D({k})"] = "d";

        var encoded = FlatFileCodec.Encode(parsed, new[] { r });
        Assert.Equal(8 + 2 * c1 + 3 * c2 + c3, encoded.TrimEnd('\n').Length);

        var got = Assert.Single(FlatFileCodec.Decode(parsed, encoded));
        Assert.Equal((decimal)c1, got["C1"]);
        Assert.Equal((decimal)c2, got["C2"]);
        Assert.Equal((decimal)c3, got["C3"]);
        Assert.Equal("ZZ", got["Z"]);
        for (var k = 1; k <= c2; k++) Assert.Equal(("b" + k).TrimEnd(), ((string)got[$"B({k})"]).TrimEnd());
        for (var k = 1; k <= c3; k++) Assert.Equal("d", got[$"D({k})"]);
    }
}
