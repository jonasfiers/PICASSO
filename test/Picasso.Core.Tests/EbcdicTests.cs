using System;
using System.Linq;
using Picasso.Core;
using Xunit;

namespace Picasso.Core.Tests;

/// <summary>
/// The cp037 table was generated from .NET's own Encoding.GetEncoding(37)
/// rather than transcribed from a spec by hand, but it's inlined in the
/// assembly, so it needs pinning: a wrong entry would be a silent wrong
/// answer of exactly the kind this project exists to avoid. The real-data
/// check lives in Dtar020RealWorldTests; these are the invariants.
/// </summary>
public class EbcdicTests
{
    [Fact]
    public void EveryByteRoundTripsThroughTheTable()
    {
        // A bijection over all 256 values — so Encode(Decode(x)) == x for any
        // byte, and no byte is quietly mapped onto a shared '?' replacement.
        var all = new string(Enumerable.Range(0, 256).Select(i => (char)i).ToArray());
        Assert.Equal(all, Ebcdic.Encode(Ebcdic.Decode(all)));

        var distinctTargets = Enumerable.Range(0, 256).Select(i => Ebcdic.ToChar((byte)i)).Distinct().Count();
        Assert.Equal(256, distinctTargets);
    }

    [Theory]
    // The facts the whole feature rests on: EBCDIC's digits and letters sit
    // nowhere near their ASCII positions, and its pad byte is 0x40, not 0x20.
    [InlineData(0xF0, '0')]
    [InlineData(0xF9, '9')]
    [InlineData(0xC1, 'A')]
    [InlineData(0xE9, 'Z')]
    [InlineData(0x81, 'a')]
    [InlineData(0x40, ' ')]
    [InlineData(0x60, '-')]
    [InlineData(0x4E, '+')]
    public void MapsTheAnchorCodePointsOfCp037(int ebcdicByte, char expected)
    {
        Assert.Equal(expected, Ebcdic.ToChar((byte)ebcdicByte));
        Assert.Equal((byte)ebcdicByte, Ebcdic.ToByte(expected));
    }

    [Fact]
    public void DigitsAreContiguousFromF0()
    {
        for (var d = 0; d < 10; d++)
            Assert.Equal((char)('0' + d), Ebcdic.ToChar((byte)(0xF0 + d)));
    }

    [Fact]
    public void EncodingAnUnrepresentableCharThrowsRatherThanSubstituting()
    {
        // '€' has no cp037 byte. Substituting '?' would write a wrong byte into
        // a fixed-width record and call it success.
        var ex = Assert.Throws<ArgumentException>(() => Ebcdic.Encode("€"));
        Assert.Contains("cp037", ex.Message);
    }

    [Fact]
    public void DecodeAndEncodeAreInversesForOrdinaryText()
    {
        const string text = "ABC-123 xyz";
        Assert.Equal(text, Ebcdic.Decode(Ebcdic.Encode(text)));
    }
}
