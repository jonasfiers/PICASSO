using System;
using System.Collections.Generic;
using System.Linq;
using Picasso.Core;
using Picasso.Core.Models;
using Xunit;

namespace Picasso.Core.Tests;

/// <summary>
/// Multiple OCCURS ... DEPENDING ON tables in one record, and lower bounds of 0
/// (OCCURS 0 TO n). The catch a single-ODO codec never faces: a later table's
/// depending field sits at an offset that depends on the EARLIER table's count,
/// so the counts are resolved left-to-right per record. As with single-ODO,
/// there is no static offset oracle for a variable record, so round-trip at
/// several count combinations IS the validation — plus explicit cascaded-offset
/// and total-length assertions cross-checked by hand against GnuCOBOL
/// (cobc 3.2.0, -std=mvs, DISPLAY LENGTH OF R).
/// </summary>
public class OdoMultiTableTests
{
    // TAB1 X(3) 1..5 on CNT1; TAB2 X(4) 1..4 on CNT2; TAIL X(2).
    // Total bytes = 6 + 3*CNT1 + 4*CNT2.
    private const string TwoTable = @"
        01  R.
            05  CNT1 PIC 9(2).
            05  TAB1 PIC X(3) OCCURS 1 TO 5 DEPENDING ON CNT1.
            05  CNT2 PIC 9(2).
            05  TAB2 PIC X(4) OCCURS 1 TO 4 DEPENDING ON CNT2.
            05  TAIL PIC X(2).
    ";

    [Fact]
    public void TwoTablesAreParsedAsTwoOdosInSourceOrder()
    {
        var parsed = CopybookParser.Parse(TwoTable);
        Assert.True(parsed.IsVariableLength);
        Assert.Equal(2, parsed.Odos.Count);
        Assert.Equal(new[] { "TAB1", "TAB2" }, parsed.Odos.Select(o => o.TableName));
        Assert.Equal(new[] { "CNT1", "CNT2" }, parsed.Odos.Select(o => o.DependsOn));
        // Odo convenience returns the first table.
        Assert.Equal("TAB1", parsed.Odo!.TableName);
    }

    [Theory]
    // (cnt1, cnt2, expected total length) — the length column is GnuCOBOL's.
    [InlineData(1, 1, 13)]
    [InlineData(3, 2, 23)]
    [InlineData(5, 4, 37)]
    [InlineData(2, 3, 24)]
    public void ConcreteLayoutCascadesOffsetsAndMatchesGnuCobolLength(int cnt1, int cnt2, int expectedLen)
    {
        var parsed = CopybookParser.Parse(TwoTable);
        var spec = CopybookParser.BuildConcreteLayout(parsed, new[] { cnt1, cnt2 });

        // Names: CNT1, TAB1(1..cnt1), CNT2, TAB2(1..cnt2), TAIL.
        var expectedNames = new List<string> { "CNT1" };
        for (var k = 1; k <= cnt1; k++) expectedNames.Add($"TAB1({k})");
        expectedNames.Add("CNT2");
        for (var k = 1; k <= cnt2; k++) expectedNames.Add($"TAB2({k})");
        expectedNames.Add("TAIL");
        Assert.Equal(expectedNames, spec.Select(f => f.Name));

        // CNT2 lands right after CNT1 + all TAB1 copies.
        Assert.Equal(2 + cnt1 * 3, spec.Single(f => f.Name == "CNT2").Start);
        // TAB2 copies start after CNT2, 4 bytes each.
        for (var k = 1; k <= cnt2; k++)
            Assert.Equal(2 + cnt1 * 3 + 2 + (k - 1) * 4, spec.Single(f => f.Name == $"TAB2({k})").Start);
        // TAIL cascades past both tables.
        Assert.Equal(2 + cnt1 * 3 + 2 + cnt2 * 4, spec.Single(f => f.Name == "TAIL").Start);

        // Total length agrees with GnuCOBOL.
        Assert.Equal(expectedLen, spec.Sum(f => f.Len));
    }

    [Theory]
    [InlineData(1, 1, CharacterEncoding.Latin1)]
    [InlineData(3, 2, CharacterEncoding.Latin1)]
    [InlineData(5, 4, CharacterEncoding.Latin1)]
    [InlineData(2, 3, CharacterEncoding.Latin1)]
    [InlineData(1, 1, CharacterEncoding.Ebcdic037)]
    [InlineData(3, 2, CharacterEncoding.Ebcdic037)]
    [InlineData(5, 4, CharacterEncoding.Ebcdic037)]
    [InlineData(2, 3, CharacterEncoding.Ebcdic037)]
    public void TwoTablesRoundTripAtEachCombination(int cnt1, int cnt2, CharacterEncoding encoding)
    {
        var parsed = CopybookParser.Parse(TwoTable);

        var record = new Dictionary<string, object>
        {
            ["CNT1"] = (decimal)cnt1,
            ["CNT2"] = (decimal)cnt2,
            ["TAIL"] = "ZZ",
        };
        for (var k = 1; k <= cnt1; k++) record[$"TAB1({k})"] = $"a{k}".PadRight(3);
        for (var k = 1; k <= cnt2; k++) record[$"TAB2({k})"] = $"b{k}".PadRight(4);

        var encoded = FlatFileCodec.Encode(parsed, new[] { record }, encoding);
        var line = encoded.TrimEnd('\n');
        Assert.Equal(6 + 3 * cnt1 + 4 * cnt2, line.Length);

        var decoded = FlatFileCodec.Decode(parsed, encoded, encoding);
        var got = Assert.Single(decoded);

        Assert.Equal((decimal)cnt1, got["CNT1"]);
        Assert.Equal((decimal)cnt2, got["CNT2"]);
        Assert.Equal("ZZ", got["TAIL"]);
        // Text decode trims trailing pad spaces, so compare against the unpadded value.
        for (var k = 1; k <= cnt1; k++) Assert.Equal($"a{k}", got[$"TAB1({k})"]);
        for (var k = 1; k <= cnt2; k++) Assert.Equal($"b{k}", got[$"TAB2({k})"]);

        // No stray entries beyond either count.
        Assert.False(got.ContainsKey($"TAB1({cnt1 + 1})"));
        Assert.False(got.ContainsKey($"TAB2({cnt2 + 1})"));
    }

    [Fact]
    public void TwoTableRecordsOfDifferentCombosRoundTripTogether_Delimited()
    {
        var parsed = CopybookParser.Parse(TwoTable);
        var combos = new[] { (1, 1), (5, 4), (2, 3), (3, 1) };

        var records = combos.Select(c =>
        {
            var (c1, c2) = c;
            var r = new Dictionary<string, object>
            { ["CNT1"] = (decimal)c1, ["CNT2"] = (decimal)c2, ["TAIL"] = "TL" };
            for (var k = 1; k <= c1; k++) r[$"TAB1({k})"] = "aaa";
            for (var k = 1; k <= c2; k++) r[$"TAB2({k})"] = "bbbb";
            return r;
        }).ToList();

        var encoded = FlatFileCodec.Encode(parsed, records);
        var decoded = FlatFileCodec.Decode(parsed, encoded);

        Assert.Equal(combos.Length, decoded.Count);
        for (var i = 0; i < combos.Length; i++)
        {
            var (c1, c2) = combos[i];
            Assert.Equal((decimal)c1, decoded[i]["CNT1"]);
            Assert.Equal((decimal)c2, decoded[i]["CNT2"]);
            Assert.Equal(c1, decoded[i].Keys.Count(k => k.StartsWith("TAB1(")));
            Assert.Equal(c2, decoded[i].Keys.Count(k => k.StartsWith("TAB2(")));
        }
    }

    [Fact]
    public void TwoTableRecordsRoundTripUndelimited()
    {
        var parsed = CopybookParser.Parse(TwoTable);
        var combos = new[] { (2, 1), (5, 4), (1, 2) };

        var records = combos.Select(c =>
        {
            var (c1, c2) = c;
            var r = new Dictionary<string, object>
            { ["CNT1"] = (decimal)c1, ["CNT2"] = (decimal)c2, ["TAIL"] = "ZZ" };
            for (var k = 1; k <= c1; k++) r[$"TAB1({k})"] = "xxx";
            for (var k = 1; k <= c2; k++) r[$"TAB2({k})"] = "yyyy";
            return r;
        }).ToList();

        var encoded = FlatFileCodec.Encode(parsed, records, CharacterEncoding.Latin1, RecordFormat.FixedLength);
        Assert.DoesNotContain('\n', encoded);

        var decoded = FlatFileCodec.Decode(parsed, encoded, CharacterEncoding.Latin1, RecordFormat.FixedLength);
        Assert.Equal(3, decoded.Count);
        Assert.Equal(new object[] { 2m, 5m, 1m }, decoded.Select(d => d["CNT1"]));
        Assert.Equal(new object[] { 1m, 4m, 2m }, decoded.Select(d => d["CNT2"]));
    }

    // ---- Three ODO tables ----

    // TA X(2) 1..3 on A; TB X(3) 1..3 on B; TC X(4) 1..3 on C; Z X(2).
    private const string ThreeTable = @"
        01  R.
            05  A  PIC 9(1).
            05  TA PIC X(2) OCCURS 1 TO 3 DEPENDING ON A.
            05  B  PIC 9(1).
            05  TB PIC X(3) OCCURS 1 TO 3 DEPENDING ON B.
            05  C  PIC 9(1).
            05  TC PIC X(4) OCCURS 1 TO 3 DEPENDING ON C.
            05  Z  PIC X(2).
    ";

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(3, 2, 1)]
    [InlineData(2, 3, 3)]
    [InlineData(3, 3, 3)]
    public void ThreeTablesRoundTripAndCascade(int a, int b, int c)
    {
        var parsed = CopybookParser.Parse(ThreeTable);
        Assert.Equal(3, parsed.Odos.Count);

        var record = new Dictionary<string, object>
        { ["A"] = (decimal)a, ["B"] = (decimal)b, ["C"] = (decimal)c, ["Z"] = "ZZ" };
        for (var k = 1; k <= a; k++) record[$"TA({k})"] = $"A{k}";
        for (var k = 1; k <= b; k++) record[$"TB({k})"] = $"B{k}".PadRight(3);
        for (var k = 1; k <= c; k++) record[$"TC({k})"] = $"C{k}".PadRight(4);

        var encoded = FlatFileCodec.Encode(parsed, new[] { record });
        var line = encoded.TrimEnd('\n');
        // 1 + 2a + 1 + 3b + 1 + 4c + 2
        var expectedLen = 5 + 2 * a + 3 * b + 4 * c;
        Assert.Equal(expectedLen, line.Length);

        var decoded = FlatFileCodec.Decode(parsed, encoded);
        var got = Assert.Single(decoded);
        Assert.Equal((decimal)a, got["A"]);
        Assert.Equal((decimal)b, got["B"]);
        Assert.Equal((decimal)c, got["C"]);
        Assert.Equal("ZZ", got["Z"]);
        for (var k = 1; k <= a; k++) Assert.Equal($"A{k}", got[$"TA({k})"]);
        for (var k = 1; k <= b; k++) Assert.Equal($"B{k}", got[$"TB({k})"]);
        for (var k = 1; k <= c; k++) Assert.Equal($"C{k}", got[$"TC({k})"]);
    }

    // ---- min = 0 (OCCURS 0 TO n) ----

    // GnuCOBOL (cobc -std=mvs) accepts OCCURS 0 TO n; LENGTH OF R at C=0 is 4,
    // at C=2 is 10 for this shape. PICASSO matches.
    private const string ZeroLowerBound = @"
        01  R.
            05  C    PIC 9(2).
            05  T    PIC X(3) OCCURS 0 TO 4 DEPENDING ON C.
            05  TAIL PIC X(2).
    ";

    [Fact]
    public void ZeroLowerBoundIsAcceptedAndBoundsAreZeroToN()
    {
        var parsed = CopybookParser.Parse(ZeroLowerBound);
        Assert.True(parsed.IsVariableLength);
        Assert.Equal(0, parsed.Odo!.Min);
        Assert.Equal(4, parsed.Odo.Max);
    }

    [Fact]
    public void RepresentativeFlatAtZeroMinOmitsTheTable()
    {
        // Min is 0, so the representative (min-count) flat has no T copies.
        var parsed = CopybookParser.Parse(ZeroLowerBound);
        Assert.Equal(new[] { "C", "TAIL" }, parsed.Flat.Select(f => f.Name));
    }

    [Theory]
    [InlineData(0, CharacterEncoding.Latin1)]
    [InlineData(1, CharacterEncoding.Latin1)]
    [InlineData(4, CharacterEncoding.Latin1)]
    [InlineData(0, CharacterEncoding.Ebcdic037)]
    [InlineData(2, CharacterEncoding.Ebcdic037)]
    public void ZeroLowerBoundRoundTripsIncludingTheEmptyTable(int count, CharacterEncoding encoding)
    {
        var parsed = CopybookParser.Parse(ZeroLowerBound);

        var record = new Dictionary<string, object> { ["C"] = (decimal)count, ["TAIL"] = "QQ" };
        for (var k = 1; k <= count; k++) record[$"T({k})"] = $"t{k}".PadRight(3);

        var encoded = FlatFileCodec.Encode(parsed, new[] { record }, encoding);
        var line = encoded.TrimEnd('\n');
        // 2 + 3*count + 2; at count 0 the table vanishes and TAIL shifts to offset 2.
        Assert.Equal(2 + 3 * count + 2, line.Length);

        var spec = CopybookParser.BuildConcreteLayout(parsed, new[] { count });
        // TAIL cascades past the table's copies; at count 0 it sits at the table's own start (offset 2).
        Assert.Equal(2 + 3 * count, spec.Single(f => f.Name == "TAIL").Start);
        if (count == 0)
            Assert.DoesNotContain(spec, f => f.Name.StartsWith("T("));

        var decoded = FlatFileCodec.Decode(parsed, encoded, encoding);
        var got = Assert.Single(decoded);
        Assert.Equal((decimal)count, got["C"]);
        Assert.Equal("QQ", got["TAIL"]);
        Assert.False(got.ContainsKey("T(1)") && count == 0);
        for (var k = 1; k <= count; k++) Assert.Equal($"t{k}", got[$"T({k})"]);
    }

    [Fact]
    public void ZeroLowerBoundAtCountZeroTotalMatchesGnuCobol()
    {
        // GnuCOBOL LEN@0 = 4 (C=2 + TAIL=2, table empty).
        var parsed = CopybookParser.Parse(ZeroLowerBound);
        var spec = CopybookParser.BuildConcreteLayout(parsed, new[] { 0 });
        Assert.Equal(4, spec.Sum(f => f.Len));
    }

    [Fact]
    public void MultipleZeroBoundRecordsIncludingZeroCountRoundTripUndelimited()
    {
        var parsed = CopybookParser.Parse(ZeroLowerBound);
        var counts = new[] { 0, 2, 0, 4, 1 };
        var records = counts.Select(c =>
        {
            var r = new Dictionary<string, object> { ["C"] = (decimal)c, ["TAIL"] = "ZZ" };
            for (var k = 1; k <= c; k++) r[$"T({k})"] = "abc";
            return r;
        }).ToList();

        var encoded = FlatFileCodec.Encode(parsed, records, CharacterEncoding.Latin1, RecordFormat.FixedLength);
        var decoded = FlatFileCodec.Decode(parsed, encoded, CharacterEncoding.Latin1, RecordFormat.FixedLength);
        Assert.Equal(counts.Select(c => (decimal)c).Cast<object>(), decoded.Select(d => d["C"]));
    }

    // ---- A negative lower bound and min>max still fail loud ----

    [Fact]
    public void NegativeLowerBoundIsRejected()
    {
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(
            "01 R.\n 05 C PIC 9(2).\n 05 T PIC X(3) OCCURS -1 TO 4 DEPENDING ON C.\n"));
        // A negative bound is not a valid OCCURS clause; the message names the field.
        Assert.Contains("T", ex.Message);
    }

    [Fact]
    public void OutOfBoundsCountOnASecondTableFailsLoud()
    {
        var parsed = CopybookParser.Parse(TwoTable); // CNT2 is 1 TO 4
        var record = new Dictionary<string, object>
        { ["CNT1"] = 1m, ["CNT2"] = 5m, ["TAIL"] = "ZZ", ["TAB1(1)"] = "aaa" };
        for (var k = 1; k <= 5; k++) record[$"TAB2({k})"] = "bbbb";

        var ex = Assert.Throws<FormatException>(() => FlatFileCodec.Encode(parsed, new[] { record }));
        Assert.Contains("outside the OCCURS bounds", ex.Message);
        Assert.Contains("TAB2", ex.Message);
    }

    [Fact]
    public void OverProvidedEntriesOnASecondTableFailLoud()
    {
        var parsed = CopybookParser.Parse(TwoTable);
        var record = new Dictionary<string, object>
        {
            ["CNT1"] = 1m, ["CNT2"] = 2m, ["TAIL"] = "ZZ",
            ["TAB1(1)"] = "aaa",
            ["TAB2(1)"] = "bbbb", ["TAB2(2)"] = "cccc", ["TAB2(3)"] = "dddd",
        };
        var ex = Assert.Throws<FormatException>(() => FlatFileCodec.Encode(parsed, new[] { record }));
        Assert.Contains("TAB2(3)", ex.Message);
    }
}
