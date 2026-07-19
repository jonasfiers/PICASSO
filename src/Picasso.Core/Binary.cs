using System;

namespace Picasso.Core;

/// <summary>
/// Binary (USAGE COMP / COMPUTATIONAL / COMP-4 / COMP-5 / BINARY) integers as
/// stored by an IBM mainframe: big-endian (network byte order), two's-complement
/// for a signed picture, plain magnitude for an unsigned one. The physical width
/// is fixed by the PIC digit count, not the value: 1–4 digits occupy a 2-byte
/// halfword, 5–9 a 4-byte fullword, 10–18 an 8-byte doubleword. An implied decimal
/// (<c>V</c>) is carried the same way COMP-3 and DISPLAY carry it — the stored
/// integer is value×10^scale, so decode divides and encode multiplies.
///
/// Big-endian only, deliberately: PICASSO decodes mainframe extracts, the same
/// assumption its EBCDIC and COMP-3 support already make. There is no little-endian
/// option. Like COMP-3, these bytes are NOT characters and must never be run
/// through an encoding table.
/// </summary>
public static class Binary
{
    /// <summary>
    /// Physical byte width for a binary field of <paramref name="digits"/> declared
    /// decimal digits: 2 bytes for 1–4, 4 for 5–9, 8 for 10–18. IBM's standard COMP
    /// sizing. More than 18 digits is beyond a COBOL numeric item and is rejected.
    /// </summary>
    public static int ByteLength(int digits)
    {
        if (digits <= 0)
            throw new ArgumentOutOfRangeException(nameof(digits));
        if (digits <= 4) return 2;
        if (digits <= 9) return 4;
        if (digits <= 18) return 8;
        throw new ArgumentOutOfRangeException(
            nameof(digits), $"Binary (COMP) fields hold at most 18 digits; got {digits}.");
    }

    public static byte[] Encode(decimal value, int digits, int scale, bool signed)
    {
        var scaled = Math.Truncate(value * Pow10(scale));
        var negative = scaled < 0;
        if (negative && !signed)
            throw new ArgumentException("Negative value for an unsigned binary (COMP) field.");

        // Capacity is bounded by the declared digit count, exactly as COMP-3 does:
        // a value with more digits than the picture allows overflows loudly rather
        // than silently truncating or wrapping. The digit cap for each width sits
        // strictly inside that width's signed two's-complement range (9999 < 2^15,
        // 999999999 < 2^31, 10^18-1 < 2^63), so this single check also guarantees
        // the value fits the halfword/fullword/doubleword with no separate range test.
        var digitString = Math.Abs(scaled).ToString("F0");
        if (digitString.Length > digits)
            throw new OverflowException(
                $"Value has more than {digits} digits of precision for a binary (COMP) field.");

        var len = ByteLength(digits);
        var modulus = Pow256(len);

        // Two's complement: a negative value is stored as modulus + value, landing
        // it in [modulus/2, modulus). A non-negative value is itself.
        var encoded = negative ? modulus + scaled : scaled;
        var u = (ulong)encoded;

        var bytes = new byte[len];
        for (var i = len - 1; i >= 0; i--)
        {
            bytes[i] = (byte)(u & 0xFF);
            u >>= 8;
        }
        return bytes;
    }

    /// <summary>
    /// Width-explicit encode for the named binary usages: the byte
    /// <paramref name="width"/> (1/2/4/8) is fixed by the usage, not a digit count.
    /// Big-endian, two's-complement when <paramref name="signed"/>. The value is
    /// range-checked against the width's representable range — signed
    /// [-2^(8w-1), 2^(8w-1)), unsigned [0, 2^(8w)) — and a value outside it
    /// overflows loudly rather than wrapping or truncating.
    /// </summary>
    public static byte[] EncodeWidth(decimal value, int width, int scale, bool signed)
    {
        if (width is not (1 or 2 or 4 or 8))
            throw new ArgumentException($"Binary field width must be 1, 2, 4 or 8 bytes; got {width}.");

        var scaled = Math.Truncate(value * Pow10(scale));
        var negative = scaled < 0;
        if (negative && !signed)
            throw new ArgumentException("Negative value for an unsigned binary field.");

        var modulus = Pow256(width);
        if (signed)
        {
            var half = modulus / 2m;
            if (scaled >= half || scaled < -half)
                throw new OverflowException(
                    $"Value {scaled} does not fit a signed {width}-byte binary field.");
        }
        else if (scaled >= modulus)
        {
            throw new OverflowException(
                $"Value {scaled} does not fit an unsigned {width}-byte binary field.");
        }

        var encoded = negative ? modulus + scaled : scaled;
        var u = (ulong)encoded;

        var bytes = new byte[width];
        for (var i = width - 1; i >= 0; i--)
        {
            bytes[i] = (byte)(u & 0xFF);
            u >>= 8;
        }
        return bytes;
    }

    public static decimal Decode(byte[] data, int digits, int scale, bool signed)
    {
        var expectedLength = ByteLength(digits);
        if (data.Length != expectedLength)
            throw new ArgumentException(
                $"Expected {expectedLength} binary bytes for {digits} digits, got {data.Length}.");

        return Decode(data, scale, signed);
    }

    /// <summary>
    /// Width-explicit decode: the caller has already fixed the byte width, so the
    /// value is read straight off <paramref name="data"/>'s length rather than a
    /// digit count. This is the path for the named binary usages
    /// (BINARY-CHAR/SHORT/LONG/DOUBLE), whose width — 1/2/4/8 bytes — is intrinsic
    /// to the usage name and carries no PICTURE. Big-endian, two's-complement when
    /// <paramref name="signed"/>, plain magnitude otherwise; identical arithmetic
    /// to the digit-derived overload, which now delegates here.
    /// </summary>
    public static decimal Decode(byte[] data, int scale, bool signed)
    {
        if (data.Length is not (1 or 2 or 4 or 8))
            throw new ArgumentException(
                $"Binary field width must be 1, 2, 4 or 8 bytes; got {data.Length}.");

        // Assemble the big-endian magnitude. A doubleword's full unsigned range
        // reaches 2^64-1, which overflows a long but fits a ulong and then a decimal.
        ulong u = 0;
        foreach (var b in data)
            u = (u << 8) | b;

        decimal raw = u;
        if (signed && (data[0] & 0x80) != 0)
            raw -= Pow256(data.Length); // two's complement: subtract the modulus

        return raw / Pow10(scale);
    }

    private static decimal Pow10(int exponent)
    {
        decimal result = 1m;
        for (var i = 0; i < exponent; i++) result *= 10m;
        return result;
    }

    private static decimal Pow256(int byteCount)
    {
        decimal result = 1m;
        for (var i = 0; i < byteCount; i++) result *= 256m;
        return result;
    }
}
