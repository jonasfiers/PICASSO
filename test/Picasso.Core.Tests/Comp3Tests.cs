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
        // digits=5, scale=0 -> (5+1)=6 nibbles, even, no pad.
        var bytes = Comp3.Encode(12345m, digits: 5, scale: 0, signed: false);
        Assert.Equal(new byte[] { 0x01, 0x23, 0x45, 0xF0 | 0 }, SwapLastNibbleCheck(bytes));
        Assert.Equal(new byte[] { 0x01, 0x23, 0x45 }, bytes.Take(3));
        Assert.Equal(0xF, bytes[3] & 0xF); // sign nibble is F, unsigned
    }

    [Fact]
    public void EncodesOddNibbleCount_PadsWithLeadingZeroNibble()
    {
        // digits=9, scale=2, signed -> (9+1)=10 nibbles... even actually.
        // Use digits=8 -> (8+1)=9 nibbles, odd -> one pad nibble.
        var bytes = Comp3.Encode(123.45m, digits: 8, scale: 2, signed: true);
        Assert.Equal(5, bytes.Length); // ceil(9/2) = 5 bytes = 10 nibbles (1 pad + 8 digits + 1 sign)
        // nibbles: [0(pad), 0,0,1,2,3,4,5, C]
        Assert.Equal(new byte[] { 0x00, 0x01, 0x23, 0x45, 0xC0 | 0 }, PadCheck(bytes));
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

    private static byte[] PadCheck(byte[] bytes) => bytes;
    private static byte[] SwapLastNibbleCheck(byte[] bytes) => bytes;
}

internal static class ByteArrayExtensions
{
    public static byte[] Take(this byte[] bytes, int count)
    {
        var result = new byte[count];
        Array.Copy(bytes, result, count);
        return result;
    }
}
