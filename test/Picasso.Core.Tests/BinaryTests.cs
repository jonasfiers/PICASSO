using System;
using System.Collections.Generic;
using Picasso.Core;
using Picasso.Core.Models;
using Xunit;

namespace Picasso.Core.Tests;

/// <summary>
/// Binary (USAGE COMP / COMPUTATIONAL / COMP-4 / COMP-5 / BINARY) integers:
/// big-endian, two's-complement for a signed picture, plain magnitude for an
/// unsigned one, physical width fixed by the PIC digit count (1–4 -> 2 bytes,
/// 5–9 -> 4, 10–18 -> 8). Byte fixtures are hand-verified against the IBM
/// mainframe representation; round-trips confirm encode/decode are inverses.
/// </summary>
public class BinaryTests
{
    private static byte[] Bytes(params byte[] b) => b;

    // ---- Width by digit count (Binary.ByteLength) ----

    [Theory]
    [InlineData(1, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 4)]
    [InlineData(9, 4)]
    [InlineData(10, 8)]
    [InlineData(18, 8)]
    public void ByteLengthFollowsDigitCount(int digits, int expected)
    {
        Assert.Equal(expected, Binary.ByteLength(digits));
    }

    [Fact]
    public void ByteLengthRejectsMoreThan18Digits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Binary.ByteLength(19));
    }

    // ---- Byte fixtures: decode raw bytes -> value ----

    [Fact]
    public void DecodesSigned2ByteHalfword()
    {
        // PIC S9(4) COMP, 2 bytes, big-endian two's complement.
        Assert.Equal(42m, Binary.Decode(Bytes(0x00, 0x2A), digits: 4, scale: 0, signed: true));
        Assert.Equal(-42m, Binary.Decode(Bytes(0xFF, 0xD6), digits: 4, scale: 0, signed: true));
        Assert.Equal(1m, Binary.Decode(Bytes(0x00, 0x01), digits: 4, scale: 0, signed: true));
        Assert.Equal(-1m, Binary.Decode(Bytes(0xFF, 0xFF), digits: 4, scale: 0, signed: true));
        Assert.Equal(0m, Binary.Decode(Bytes(0x00, 0x00), digits: 4, scale: 0, signed: true));
    }

    [Fact]
    public void DecodesSigned4ByteFullword()
    {
        // PIC S9(9) COMP, 4 bytes. 0x000F4240 = 1_000_000.
        Assert.Equal(1000000m, Binary.Decode(Bytes(0x00, 0x0F, 0x42, 0x40), digits: 9, scale: 0, signed: true));
        // Two's complement negative: 0xFFFFFFFF = -1.
        Assert.Equal(-1m, Binary.Decode(Bytes(0xFF, 0xFF, 0xFF, 0xFF), digits: 9, scale: 0, signed: true));
        // 0xFFF0BDC0 = 4293967296; - 2^32 = -1_000_000 (two's complement of 0x000F4240).
        Assert.Equal(-1000000m, Binary.Decode(Bytes(0xFF, 0xF0, 0xBD, 0xC0), digits: 9, scale: 0, signed: true));
    }

    [Fact]
    public void DecodesImpliedDecimalByDividingByScale()
    {
        // Stored integer 1234 (0x04D2) with scale 2 -> 12.34.
        Assert.Equal(12.34m, Binary.Decode(Bytes(0x04, 0xD2), digits: 4, scale: 2, signed: true));
    }

    [Fact]
    public void DecodesUnsignedReadsFullBinaryValue()
    {
        // PIC 9(4) COMP unsigned: 0xFFFF is 65535, NOT -1. The unsigned picture
        // means the full binary value is read; the declared 4-digit cap governs
        // encoding, not what a decode is allowed to observe in the bytes.
        Assert.Equal(65535m, Binary.Decode(Bytes(0xFF, 0xFF), digits: 4, scale: 0, signed: false));
    }

    [Fact]
    public void DecodesSigned8ByteDoubleword()
    {
        // PIC S9(18) COMP, 8 bytes. 123456789012345678 = 0x01B69B4BA630F34E.
        Assert.Equal(
            123456789012345678m,
            Binary.Decode(Bytes(0x01, 0xB6, 0x9B, 0x4B, 0xA6, 0x30, 0xF3, 0x4E), digits: 18, scale: 0, signed: true));
    }

    // ---- Encode fixtures ----

    [Fact]
    public void EncodesSigned2ByteHalfword()
    {
        Assert.Equal(Bytes(0x00, 0x2A), Binary.Encode(42m, digits: 4, scale: 0, signed: true));
        Assert.Equal(Bytes(0xFF, 0xD6), Binary.Encode(-42m, digits: 4, scale: 0, signed: true));
        Assert.Equal(Bytes(0xFF, 0xFF), Binary.Encode(-1m, digits: 4, scale: 0, signed: true));
        Assert.Equal(Bytes(0x00, 0x00), Binary.Encode(0m, digits: 4, scale: 0, signed: true));
    }

    [Fact]
    public void EncodesImpliedDecimalByMultiplyingByScale()
    {
        Assert.Equal(Bytes(0x04, 0xD2), Binary.Encode(12.34m, digits: 4, scale: 2, signed: true));
    }

    // ---- Round-trip battery ----

    [Fact]
    public void RoundTripsAcrossSignsScalesAndWidths()
    {
        var cases = new (decimal value, int digits, int scale, bool signed)[]
        {
            (0m, 4, 0, true),
            (0m, 4, 0, false),
            (42m, 4, 0, true),
            (-42m, 4, 0, true),
            (9999m, 4, 0, false),
            (12.34m, 4, 2, true),
            (-12.34m, 4, 2, true),
            (1000000m, 9, 0, true),
            (-1000000m, 9, 0, true),
            (999999999m, 9, 0, false),
            (-999999999m, 9, 0, true),
            (1234567.89m, 9, 2, true),
            (123456789012345678m, 18, 0, true),
            (-123456789012345678m, 18, 0, true),
            (999999999999999999m, 18, 0, false),
            (-1m, 18, 0, true),
        };

        foreach (var (value, digits, scale, signed) in cases)
        {
            var bytes = Binary.Encode(value, digits, scale, signed);
            Assert.Equal(Binary.ByteLength(digits), bytes.Length);
            var decoded = Binary.Decode(bytes, digits, scale, signed);
            Assert.Equal(value, decoded);
        }
    }

    // ---- Overflow / range ----

    [Fact]
    public void EncodeThrowsWhenValueExceedsDeclaredDigits()
    {
        // 100000 is 6 digits; a 4-digit field cannot hold it. Mirrors COMP-3.
        Assert.Throws<OverflowException>(() => Binary.Encode(100000m, digits: 4, scale: 0, signed: true));
    }

    [Fact]
    public void EncodeThrowsOnNegativeIntoUnsignedField()
    {
        Assert.Throws<ArgumentException>(() => Binary.Encode(-1m, digits: 4, scale: 0, signed: false));
    }

    [Fact]
    public void DecodeThrowsOnWrongByteCount()
    {
        Assert.Throws<ArgumentException>(() => Binary.Decode(Bytes(0x00), digits: 4, scale: 0, signed: true));
    }

    // ---- Parser: width boundaries via parsed layout ----

    [Theory]
    [InlineData("S9(4)", 2)]
    [InlineData("S9(5)", 4)]
    [InlineData("S9(9)", 4)]
    [InlineData("S9(10)", 8)]
    [InlineData("S9(18)", 8)]
    public void ParserSizesBinaryFieldByDigitCount(string pic, int expectedLen)
    {
        var parsed = CopybookParser.Parse($"01  R.\n    05  A PIC {pic} COMP.\n");
        Assert.Single(parsed.Flat);
        Assert.Equal(FieldType.Binary, parsed.Flat[0].Type);
        Assert.Equal(expectedLen, parsed.Flat[0].Len);
    }

    [Theory]
    [InlineData("COMP")]
    [InlineData("COMPUTATIONAL")]
    [InlineData("COMP-4")]
    [InlineData("COMPUTATIONAL-4")]
    [InlineData("COMP-5")]
    [InlineData("COMPUTATIONAL-5")]
    [InlineData("BINARY")]
    public void AllBinaryUsageSpellingsParseToBinary(string usage)
    {
        var parsed = CopybookParser.Parse($"01  R.\n    05  A PIC S9(9) {usage}.\n");
        Assert.Single(parsed.Flat);
        Assert.Equal(FieldType.Binary, parsed.Flat[0].Type);
        Assert.Equal(4, parsed.Flat[0].Len);
        Assert.True(parsed.Flat[0].Signed);
    }

    [Fact]
    public void ParserCarriesImpliedDecimalDigitsAndScale()
    {
        // PIC S9(4)V99 -> 6 total digits -> 4-byte fullword, scale 2.
        var parsed = CopybookParser.Parse("01  R.\n    05  A PIC S9(4)V99 COMP.\n");
        Assert.Equal(FieldType.Binary, parsed.Flat[0].Type);
        Assert.Equal(6, parsed.Flat[0].Digits);
        Assert.Equal(2, parsed.Flat[0].Scale);
        Assert.Equal(4, parsed.Flat[0].Len);
    }

    // ---- Parser rejections specific to binary ----

    [Fact]
    public void BinaryOnAlphanumericPicIsRejected()
    {
        var ex = Assert.Throws<FormatException>(
            () => CopybookParser.Parse("01  R.\n    05  A PIC X(4) COMP.\n"));
        Assert.Contains("Binary", ex.Message);
        Assert.Contains("'A'", ex.Message);
    }

    [Fact]
    public void BinaryOnEditedPicIsRejected()
    {
        var ex = Assert.Throws<FormatException>(
            () => CopybookParser.Parse("01  R.\n    05  A PIC ZZ9 COMP.\n"));
        Assert.Contains("Binary", ex.Message);
        Assert.Contains("'A'", ex.Message);
    }

    [Fact]
    public void BinaryWithSignSeparateIsRejected()
    {
        var ex = Assert.Throws<FormatException>(
            () => CopybookParser.Parse("01  R.\n    05  A PIC S9(4) COMP SIGN IS TRAILING SEPARATE.\n"));
        Assert.Contains("SEPARATE", ex.Message);
        Assert.Contains("'A'", ex.Message);
    }

    // ---- PACKED-DECIMAL aliases to COMP-3, byte-identical ----

    [Fact]
    public void PackedDecimalEncodesByteIdenticalToComp3()
    {
        var packed = CopybookParser.Parse("01  R.\n    05  A PIC S9(5) PACKED-DECIMAL.\n").Flat;
        var comp3 = CopybookParser.Parse("01  R.\n    05  A PIC S9(5) COMP-3.\n").Flat;

        Assert.Equal(FieldType.Comp3, packed[0].Type);
        Assert.Equal(3, packed[0].Len);

        foreach (var value in new[] { 12345m, -12345m, 0m, -1m })
        {
            var rec = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["A"] = value },
            };
            var packedBytes = FlatFileCodec.Encode(packed, rec);
            var comp3Bytes = FlatFileCodec.Encode(comp3, rec);
            Assert.Equal(comp3Bytes, packedBytes);
        }
    }

    // ---- End-to-end through FlatFileCodec (binary field in a record) ----

    [Fact]
    public void FullPipelineDecodesAndRoundTripsBinaryField()
    {
        // 01 R. 05 CODE PIC X(2). 05 QTY PIC S9(4) COMP. 05 AMT PIC S9(7)V99 COMP.
        // AMT is 9 total digits -> 4-byte fullword. Layout: 2 text + 2 + 4 = 8 bytes.
        var spec = CopybookParser.Parse(
            "01  R.\n" +
            "    05  CODE PIC X(2).\n" +
            "    05  QTY  PIC S9(4) COMP.\n" +
            "    05  AMT  PIC S9(7)V99 COMP.\n").Flat;
        Assert.Equal(8, spec[0].Len + spec[1].Len + spec[2].Len);

        // "AB" + halfword 42 (0x002A) + fullword 123.45 stored as 12345 (0x00003039).
        var record = "AB" + (char)0x00 + (char)0x2A + (char)0x00 + (char)0x00 + (char)0x30 + (char)0x39;
        var decoded = FlatFileCodec.Decode(spec, record, CharacterEncoding.Latin1, RecordFormat.FixedLength);

        Assert.Single(decoded);
        Assert.Equal("AB", decoded[0]["CODE"]);
        Assert.Equal(42m, decoded[0]["QTY"]);
        Assert.Equal(123.45m, decoded[0]["AMT"]);

        // Re-encode reproduces the exact bytes.
        var reencoded = FlatFileCodec.Encode(spec, decoded, CharacterEncoding.Latin1, RecordFormat.FixedLength);
        Assert.Equal(record, reencoded);
    }

    [Fact]
    public void BinaryBytesAreNotTranslatedByEbcdicEncoding()
    {
        // A binary field must pass through raw even when the record encoding is
        // EBCDIC — exactly like COMP-3. 0x00 0x2A would corrupt if run through the
        // cp037 table; decode must still see 42.
        var spec = CopybookParser.Parse("01  R.\n    05  QTY PIC S9(4) COMP.\n").Flat;
        var record = "" + (char)0x00 + (char)0x2A;
        var decoded = FlatFileCodec.Decode(spec, record, CharacterEncoding.Ebcdic037, RecordFormat.FixedLength);
        Assert.Equal(42m, decoded[0]["QTY"]);

        var reencoded = FlatFileCodec.Encode(spec, decoded, CharacterEncoding.Ebcdic037, RecordFormat.FixedLength);
        Assert.Equal(record, reencoded);
    }

    // ---- OutSystems preview mapping ----

    [Fact]
    public void PreviewMapsBinaryToIntegerOrDecimal()
    {
        var flat = CopybookParser.Parse(
            "01  R.\n" +
            "    05  CNT PIC S9(4) COMP.\n" +
            "    05  AMT PIC S9(9)V99 COMP.\n").Flat;
        var rows = OutSystemsPreview.Build(flat);

        Assert.Equal("Integer", rows[0].DataType);
        Assert.Equal(4, rows[0].Length);
        Assert.Equal(0, rows[0].Decimals);

        Assert.Equal("Decimal", rows[1].DataType);
        Assert.Equal(11, rows[1].Length); // S9(9)V99 = 11 total digits
        Assert.Equal(2, rows[1].Decimals);
    }

    // ---- Over-long binary picture rejects cleanly (named FormatException, not a
    //      leaked ArgumentOutOfRangeException from Binary.ByteLength) ----

    [Theory]
    [InlineData("PIC 9(19) COMP")]
    [InlineData("PIC S9(19) COMP")]
    public void BinaryOverEighteenDigitsRejectsWithNamedFieldError(string clause)
    {
        var ex = Assert.Throws<FormatException>(() =>
            CopybookParser.Parse($"01  R.\n    05  BIG {clause}.\n"));
        Assert.Contains("BIG", ex.Message);
        Assert.Contains("18 digits", ex.Message);
    }
}
