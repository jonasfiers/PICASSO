using System;

namespace Picasso.Core;

/// <summary>
/// COMP-3 packed decimal: two digits per byte, plus one trailing nibble
/// reserved for the sign on every field regardless of whether the PIC
/// clause declared S. 0xC/0xF = positive (signed/unsigned), 0xD = negative.
/// </summary>
public static class Comp3
{
    public static int ByteLength(int digits)
    {
        if (digits <= 0)
            throw new ArgumentOutOfRangeException(nameof(digits));
        return (digits + 1 + 1) / 2; // ceil((digits + 1) / 2)
    }

    public static byte[] Encode(decimal value, int digits, int scale, bool signed)
    {
        var scaled = Math.Truncate(value * Pow10(scale));
        var negative = scaled < 0;
        if (negative && !signed)
            throw new ArgumentException("Negative value for an unsigned COMP-3 field.");

        var digitString = Math.Abs(scaled).ToString("F0");
        if (digitString.Length > digits)
            throw new OverflowException($"Value has more than {digits} digits of precision.");
        digitString = digitString.PadLeft(digits, '0');

        var signNibble = signed ? (negative ? 0xD : 0xC) : 0xF;

        // digitString.Length nibbles + 1 sign nibble; pad with a leading
        // zero nibble if that total is odd so nibble-pairs fill whole bytes.
        var nibbles = new System.Collections.Generic.List<int>(digits + 2);
        if ((digits + 1) % 2 != 0)
            nibbles.Add(0);
        foreach (var ch in digitString)
            nibbles.Add(ch - '0');
        nibbles.Add(signNibble);

        var bytes = new byte[nibbles.Count / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            var high = nibbles[i * 2];
            var low = nibbles[i * 2 + 1];
            bytes[i] = (byte)((high << 4) | low);
        }
        return bytes;
    }

    public static decimal Decode(byte[] data, int digits, int scale, bool signed)
    {
        var expectedLength = ByteLength(digits);
        if (data.Length != expectedLength)
            throw new ArgumentException($"Expected {expectedLength} packed bytes for {digits} digits, got {data.Length}.");

        var nibbles = new System.Collections.Generic.List<int>(data.Length * 2);
        foreach (var b in data)
        {
            nibbles.Add((b >> 4) & 0xF);
            nibbles.Add(b & 0xF);
        }

        var signNibble = nibbles[nibbles.Count - 1];
        bool negative;
        switch (signNibble)
        {
            case 0xC:
            case 0xF:
            case 0xA:
            case 0xE:
                negative = false;
                break;
            case 0xD:
            case 0xB:
                negative = true;
                break;
            default:
                throw new FormatException($"Invalid COMP-3 sign nibble: 0x{signNibble:X}.");
        }

        // Drop the sign nibble, then keep only the last `digits` nibbles
        // (there may be one leading zero-pad nibble ahead of them).
        var digitNibbles = nibbles.GetRange(0, nibbles.Count - 1);
        var start = digitNibbles.Count - digits;
        if (start < 0)
            throw new FormatException("Packed decimal has fewer digit nibbles than expected.");

        var digitChars = new char[digits];
        for (var i = 0; i < digits; i++)
            digitChars[i] = (char)('0' + digitNibbles[start + i]);

        var magnitude = decimal.Parse(new string(digitChars)) / Pow10(scale);
        return negative ? -magnitude : magnitude;
    }

    private static decimal Pow10(int exponent)
    {
        decimal result = 1m;
        for (var i = 0; i < exponent; i++) result *= 10m;
        return result;
    }
}
