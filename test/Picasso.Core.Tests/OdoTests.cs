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
    public void TwoOdoTablesInOneRecordAreRejected()
    {
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(@"
            01  R.
                05  C1 PIC 9(2).
                05  T1 PIC X(3) OCCURS 1 TO 5 DEPENDING ON C1.
                05  C2 PIC 9(2).
                05  T2 PIC X(3) OCCURS 1 TO 5 DEPENDING ON C2.
        "));
        Assert.Contains("More than one OCCURS ... DEPENDING ON", ex.Message);
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
}
