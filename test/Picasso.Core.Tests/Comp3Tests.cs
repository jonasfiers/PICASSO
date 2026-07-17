using System;
using Picasso.Core;
using Xunit;

namespace Picasso.Core.Tests;

public class Comp3Tests
{
    [Fact]
    public void ByteLengthIsCeilingOfDigitsPlusOneOverTwo()
    {
        Assert.Equal(4, Comp3.ByteLength(7));  // (7+1)/2 = 4, even
        Assert.Equal(6, Comp3.ByteLength(11)); // (11+1)/2 = 6, even
        Assert.Equal(3, Comp3.ByteLength(5));  // (5+1)/2 = 3, even
        Assert.Equal(2, Comp3.ByteLength(3));  // (3+1)/2 = 2, even
    }

    [Fact]
    public void EncodesSignedPositiveValue_EvenNibbleCount()
    {
        // digits=7, scale=2, value=1234.56 -> digit string "0123456", sign 0xC.
        // (7+1)=8 nibbles, even, no pad nibble.
        var bytes = Comp3.Encode(1234.56m, digits: 7, scale: 2, signed: true);
        Assert.Equal(4, bytes.Length);
        Assert.Equal(new byte[] { 0x01, 0x23, 0x45, 0x6C }, bytes);
    }

    [Fact]
    public void EncodesSignedNegativeValue()
    {
        var bytes = Comp3.Encode(-1234.56m, digits: 7, scale: 2, signed: true);
        Assert.Equal(new byte[] { 0x01, 0x23, 0x45, 0x6D }, bytes);
    }

    [Fact]
    public void EncodesUnsignedValue_AlwaysUsesFSignNibble()
    {
        // digits=5, scale=0, value=12345 -> (5+1)=6 nibbles, even, no pad.
        // nibble stream: 1 2 3 4 5 F  ->  0x12 0x34 0x5F
        var bytes = Comp3.Encode(12345m, digits: 5, scale: 0, signed: false);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x5F }, bytes);
        Assert.Equal(0xF, bytes[bytes.Length - 1] & 0xF);
    }

    [Fact]
    public void EncodesOddNibbleCount_PadsWithLeadingZeroNibble()
    {
        // digits=8, scale=2, value=123.45 -> digit string "00012345".
        // (8+1)=9 nibbles is odd, so a leading zero pad nibble is prepended.
        // nibble stream: 0(pad) 0 0 0 1 2 3 4 5 C  ->  0x00 0x00 0x12 0x34 0x5C
        var bytes = Comp3.Encode(123.45m, digits: 8, scale: 2, signed: true);
        Assert.Equal(5, bytes.Length); // ceil((8+1)/2)
        Assert.Equal(new byte[] { 0x00, 0x00, 0x12, 0x34, 0x5C }, bytes);
    }

    [Fact]
    public void RoundTripsAcrossManyValues()
    {
        var cases = new (decimal value, int digits, int scale, bool signed)[]
        {
            (0m, 5, 0, false),
            (42m, 5, 0, false),
            (-42m, 5, 0, true),
            (1234.56m, 7, 2, true),
            (-1234.56m, 7, 2, true),
            (123.45m, 8, 2, true),
            (999999999.99m, 11, 2, true),
            (-999999999.99m, 11, 2, true),
        };

        foreach (var (value, digits, scale, signed) in cases)
        {
            var bytes = Comp3.Encode(value, digits, scale, signed);
            var decoded = Comp3.Decode(bytes, digits, scale, signed);
            Assert.Equal(value, decoded);
        }
    }

    [Fact]
    public void DecodeRejectsInvalidSignNibble()
    {
        var bytes = new byte[] { 0x01, 0x23, 0x45, 0x62 }; // sign nibble 0x2 is invalid
        Assert.Throws<FormatException>(() => Comp3.Decode(bytes, 7, 2, true));
    }
}
