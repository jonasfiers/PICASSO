using System.Collections.Generic;
using System.Linq;
using Picasso.Core;
using Picasso.Core.Models;
using Xunit;

namespace Picasso.Core.Tests;

/// <summary>
/// Overpunched (zoned) signed DISPLAY numerics — <c>PIC S9(n)</c> without
/// <c>SIGN IS ... SEPARATE</c>. The sign rides in the zone nibble of the
/// trailing digit (default) or leading digit (<c>SIGN IS LEADING</c>); the
/// field is exactly <c>n</c> bytes.
///
/// Two things are tested that unit-testing the helper alone would miss:
///   1. Byte-level fixtures pinned to the IBM table (0xC4 for +4, 0xD4 for -4
///      in EBCDIC; the letters 'D'/'M' in Latin-1), so a table typo can't hide.
///   2. Round-trip (encode → bytes → decode) reproduces the original value in
///      BOTH encodings, across sign, scale, magnitude, and sign position —
///      because the overpunch table is char-level identical in cp037 and
///      Latin-1, one implementation must serve both.
/// </summary>
public class OverpunchTests
{
    private static IReadOnlyList<FieldSpec> Spec(string pic) =>
        CopybookParser.Parse($"01  R.\n    05  A PIC {pic}.\n").Flat;

    /// <summary>A single-field spec with an explicit SIGN IS LEADING (no SEPARATE).</summary>
    private static IReadOnlyList<FieldSpec> LeadingSpec(string pic) =>
        CopybookParser.Parse($"01  R.\n    05  A PIC {pic} SIGN IS LEADING.\n").Flat;

    private static string RawBytes(params int[] bytes) => new string(bytes.Select(b => (char)b).ToArray());

    private static string Hex(string s) => string.Join(" ", s.Select(c => ((int)c).ToString("X2")));

    private static string EncodeOne(IReadOnlyList<FieldSpec> spec, decimal value, CharacterEncoding enc) =>
        FlatFileCodec.Encode(
            spec,
            new[] { new Dictionary<string, object> { ["A"] = value } },
            enc,
            RecordFormat.FixedLength);

    private static decimal DecodeOne(IReadOnlyList<FieldSpec> spec, string bytes, CharacterEncoding enc) =>
        (decimal)FlatFileCodec.Decode(spec, bytes, enc, RecordFormat.FixedLength)[0]["A"];

    // ---- Byte-level fixtures (the table must be exactly IBM's) ------------

    [Theory]
    [InlineData(4, 0xC4)]    // positive digit 4 -> zone C -> 'D'
    [InlineData(-4, 0xD4)]   // negative digit 4 -> zone D -> 'M'
    [InlineData(0, 0xC0)]    // positive 0 -> '{'
    [InlineData(-7, 0xD7)]   // negative 7 -> 'P'
    public void EncodesSingleDigitToExpectedEbcdicByte(int value, int expectedByte)
    {
        var encoded = EncodeOne(Spec("S9"), value, CharacterEncoding.Ebcdic037);
        Assert.Single(encoded);
        Assert.Equal(expectedByte, (int)encoded[0]);
    }

    [Theory]
    [InlineData(4, 'D')]     // Latin-1 stores the overpunch letter directly
    [InlineData(-4, 'M')]
    [InlineData(0, '{')]
    [InlineData(-1, 'J')]
    public void EncodesSingleDigitToExpectedLatin1Char(int value, char expected)
    {
        var encoded = EncodeOne(Spec("S9"), value, CharacterEncoding.Latin1);
        Assert.Equal(expected.ToString(), encoded);
    }

    [Theory]
    [InlineData(new byte[] { 0xF1, 0xF2, 0xD3 }, -123)]   // trailing zone D -> negative
    [InlineData(new byte[] { 0xF1, 0xF2, 0xC3 }, 123)]    // trailing zone C -> positive
    [InlineData(new byte[] { 0xF1, 0xF2, 0xF3 }, 123)]    // alternate zone F -> positive
    public void DecodesTrailingOverpunchEbcdicBytes(byte[] bytes, int expected)
    {
        var raw = new string(bytes.Select(b => (char)b).ToArray());
        Assert.Equal(expected, DecodeOne(Spec("S9(3)"), raw, CharacterEncoding.Ebcdic037));
    }

    [Fact]
    public void DecodesTrailingOverpunchLatin1Letters()
    {
        // Latin-1: the sign char is the letter itself. "12L" = 12 with trailing
        // negative 3 -> -123; "12C" = +123.
        Assert.Equal(-123m, DecodeOne(Spec("S9(3)"), "12L", CharacterEncoding.Latin1));
        Assert.Equal(123m, DecodeOne(Spec("S9(3)"), "12C", CharacterEncoding.Latin1));
        Assert.Equal(123m, DecodeOne(Spec("S9(3)"), "123", CharacterEncoding.Latin1)); // alternate
    }

    [Fact]
    public void LeadingSignOverpunchesTheFirstDigitEbcdic()
    {
        var spec = LeadingSpec("S9(3)");
        var encoded = EncodeOne(spec, -123m, CharacterEncoding.Ebcdic037);
        Assert.Equal("D1 F2 F3", Hex(encoded));   // 0xD1 = 'J' = negative 1 (leading)
        Assert.Equal(-123m, DecodeOne(spec, encoded, CharacterEncoding.Ebcdic037));
    }

    [Fact]
    public void LeadingSignFieldHasNoExtraByte()
    {
        var a = LeadingSpec("S9(4)").Single();
        Assert.True(a.Signed);
        Assert.False(a.SignSeparate);
        Assert.True(a.SignLeading);
        Assert.Equal(4, a.Len);
    }

    // ---- Round-trip battery (value survives encode -> bytes -> decode) -----

    public static TheoryData<string, decimal> Cases => new()
    {
        // -0m equals 0m as a decimal, so it is not a distinct theory row; the
        // negative-zero BYTE form (zone D over digit 0) is pinned separately in
        // PlusZeroAndMinusZeroAreBothZero.
        { "S9", 0m }, { "S9", 7m }, { "S9", -7m },
        { "S9(5)", 12345m }, { "S9(5)", -12345m }, { "S9(5)", 0m },
        { "S9(5)V99", 123.45m }, { "S9(5)V99", -123.45m }, { "S9(5)V99", 0m },
        { "S9(9)", 987654321m }, { "S9(9)", -987654321m },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void RoundTripsTrailingSignLatin1(string pic, decimal value)
    {
        var spec = Spec(pic);
        var encoded = EncodeOne(spec, value, CharacterEncoding.Latin1);
        Assert.Equal(value, DecodeOne(spec, encoded, CharacterEncoding.Latin1));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RoundTripsTrailingSignEbcdic(string pic, decimal value)
    {
        var spec = Spec(pic);
        var encoded = EncodeOne(spec, value, CharacterEncoding.Ebcdic037);
        Assert.Equal(value, DecodeOne(spec, encoded, CharacterEncoding.Ebcdic037));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RoundTripsLeadingSignBothEncodings(string pic, decimal value)
    {
        var spec = LeadingSpec(pic);
        foreach (var enc in new[] { CharacterEncoding.Latin1, CharacterEncoding.Ebcdic037 })
        {
            var encoded = EncodeOne(spec, value, enc);
            Assert.Equal(value, DecodeOne(spec, encoded, enc));
        }
    }

    [Fact]
    public void PlusZeroAndMinusZeroAreBothZero()
    {
        var spec = Spec("S9(3)");
        Assert.Equal(0m, DecodeOne(spec, RawBytes(0xF0, 0xF0, 0xC0), CharacterEncoding.Ebcdic037)); // +0
        Assert.Equal(0m, DecodeOne(spec, RawBytes(0xF0, 0xF0, 0xD0), CharacterEncoding.Ebcdic037)); // -0
    }

    [Fact]
    public void AlternateZoneFPositiveReEncodesToPreferredZoneC()
    {
        // Documented round-trip exception: a positive value stored in the plain
        // zone-F form decodes correctly but re-encodes to the zone-C letter —
        // same value, different byte. Standard data uses zone C/D, so this only
        // bites the non-standard alternate positive.
        var spec = Spec("S9(3)");
        var decoded = DecodeOne(spec, RawBytes(0xF1, 0xF2, 0xF3), CharacterEncoding.Ebcdic037);
        Assert.Equal(123m, decoded);

        var reencoded = EncodeOne(spec, decoded, CharacterEncoding.Ebcdic037);
        Assert.Equal("F1 F2 C3", Hex(reencoded));   // zone F trailing digit becomes zone C
    }
}
