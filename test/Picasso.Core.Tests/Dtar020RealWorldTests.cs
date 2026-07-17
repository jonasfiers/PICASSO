using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Picasso.Core;
using Picasso.Core.Models;
using Xunit;

namespace Picasso.Core.Tests;

/// <summary>
/// DTAR020 is not a fixture written for this project: it's a real COBOL
/// copybook (dated 19/12/90, credited "BRUCE ARTHUR", from an actual
/// reporting system) with a real 379-record binary extract, sourced from
/// github.com/bmTas/CobolToJson (LGPL-2.1). See Samples/dtar020/README.md
/// for the gaps running it uncovered, none of which any synthetic copybook
/// had exposed. Its fixed-format source and its EBCDIC text are both handled
/// now; two gaps remain — it's a headless copy member (no 01 level), so the
/// bundled .cpy still needs a synthetic 01 prepended, and DTAR020.bin has no
/// record delimiters, which FlatFileCodec's newline-based record splitting
/// can't chop up.
///
/// That second gap is why the whole-file assertions here still slice records
/// by offset by hand rather than calling FlatFileCodec.Decode over the file.
/// Every expected value below is one CobolToJson's own README publishes —
/// nothing here is derived from PICASSO.
/// </summary>
public class Dtar020RealWorldTests
{
    private static ParsedCopybook ParseDtar020() =>
        CopybookParser.Parse(File.ReadAllText(SamplePaths.Root("dtar020/DTAR020.cpy")));

    /// <summary>
    /// The bundled DTAR020.cpy is now nothing but the original mainframe file
    /// with a synthetic 01-level prepended: its sequence numbers and column-7
    /// comment indicators are left exactly as they arrived, and the parser
    /// handles them. This asserts the old hand-cleaning workaround stays gone —
    /// every other test here would still pass against a pre-stripped file.
    /// </summary>
    [Fact]
    public void BundledCopybookIsTheOriginalFixedFormatFile_PlusOnlyThe01Wrapper()
    {
        var original = File.ReadAllText(SamplePaths.Root("dtar020/DTAR020-ORIGINAL.cbl"));
        var bundled = File.ReadAllText(SamplePaths.Root("dtar020/DTAR020.cpy"));

        Assert.Equal("01  DTAR020-REC.\n" + original, bundled);
        Assert.Contains("000900        03  DTAR020-KCODE-STORE-KEY.", bundled);

        // ...and it parses, sequence numbers and all.
        Assert.Equal(27, CopybookParser.Parse(bundled).Flat.Sum(f => f.Len));
    }

    [Fact]
    public void DerivesTheLayoutTheCopybookItselfClaims()
    {
        var parsed = ParseDtar020();

        // The copybook's own comment says "RECORD LENGTH IS 27."
        Assert.Equal(27, parsed.Flat.Sum(f => f.Len));

        var storeNo = parsed.Flat.Single(f => f.Name == "DTAR020-STORE-NO");
        Assert.Equal(FieldType.Comp3, storeNo.Type);
        Assert.Equal(3, storeNo.Digits);
        Assert.True(storeNo.Signed);

        var salePrice = parsed.Flat.Single(f => f.Name == "DTAR020-SALE-PRICE");
        Assert.Equal(FieldType.Comp3, salePrice.Type);
        Assert.Equal(11, salePrice.Digits);
        Assert.Equal(2, salePrice.Scale);
    }

    [Fact]
    public void DecodesRealComp3RecordsCorrectly_IncludingNegatives()
    {
        var parsed = ParseDtar020();
        var recordLen = parsed.Flat.Sum(f => f.Len);

        var rawBytes = File.ReadAllBytes(SamplePaths.Root("dtar020/DTAR020.bin"));
        Assert.Equal(379 * recordLen, rawBytes.Length); // no newline delimiters: pure fixed-length concatenation

        var text = new string(rawBytes.Select(b => (char)b).ToArray());

        decimal DecodeComp3Field(string record, string fieldName)
        {
            var f = parsed.Flat.Single(x => x.Name == fieldName);
            var raw = record.Substring(f.Start, f.Len);
            var bytes = raw.Select(c => (byte)c).ToArray();
            return Comp3.Decode(bytes, f.Digits, f.Scale, f.Signed);
        }

        string Record(int index) => text.Substring(index * recordLen, recordLen);

        // Expected values independently published in CobolToJson's own README
        // (github.com/bmTas/CobolToJson), not derived from PICASSO.
        Assert.Equal(20m, DecodeComp3Field(Record(0), "DTAR020-STORE-NO"));
        Assert.Equal(40118m, DecodeComp3Field(Record(0), "DTAR020-DATE"));
        Assert.Equal(280m, DecodeComp3Field(Record(0), "DTAR020-DEPT-NO"));
        Assert.Equal(1m, DecodeComp3Field(Record(0), "DTAR020-QTY-SOLD"));
        Assert.Equal(19.00m, DecodeComp3Field(Record(0), "DTAR020-SALE-PRICE"));

        // Record 1: the negative-quantity/negative-price pair.
        Assert.Equal(-1m, DecodeComp3Field(Record(1), "DTAR020-QTY-SOLD"));
        Assert.Equal(-19.00m, DecodeComp3Field(Record(1), "DTAR020-SALE-PRICE"));

        // Record 2: a non-integer sale price.
        Assert.Equal(1m, DecodeComp3Field(Record(2), "DTAR020-QTY-SOLD"));
        Assert.Equal(5.01m, DecodeComp3Field(Record(2), "DTAR020-SALE-PRICE"));
    }

    /// <summary>
    /// The EBCDIC text field, which used to decode to garbage. "69684558" is
    /// the value CobolToJson's own README publishes for record 0 — this is the
    /// gap closing against real data, not against a fixture we wrote.
    /// </summary>
    [Fact]
    public void DecodesTheEbcdicTextField()
    {
        var parsed = ParseDtar020();
        var recordLen = parsed.Flat.Sum(f => f.Len);
        var rawBytes = File.ReadAllBytes(SamplePaths.Root("dtar020/DTAR020.bin"));
        var text = new string(rawBytes.Select(b => (char)b).ToArray());

        var keycode = parsed.Flat.Single(f => f.Name == "DTAR020-KEYCODE-NO");
        var raw = text.Substring(0, recordLen).Substring(keycode.Start, keycode.Len);

        Assert.Equal("69684558", Ebcdic.Decode(raw).TrimEnd(' '));

        // Latin-1 — the default, and what this used to do — still reads the
        // same bytes as mojibake. The encoding is a real choice, not a guess.
        Assert.Equal("öùöøôõõø", raw.TrimEnd(' '));
    }

    /// <summary>
    /// The whole point of doing this per field: a blanket EBCDIC->ASCII pass
    /// over the record (the FTP-ASCII-mode mistake) would corrupt all four
    /// COMP-3 fields. Decoding one real record through the public codec with
    /// Ebcdic037 must get the text field AND the packed fields right at once.
    /// </summary>
    [Fact]
    public void DecodesARealRecordThroughThePublicCodec_TextAndComp3Together()
    {
        var parsed = ParseDtar020();
        var recordLen = parsed.Flat.Sum(f => f.Len);
        var rawBytes = File.ReadAllBytes(SamplePaths.Root("dtar020/DTAR020.bin"));

        // One record only: DTAR020.bin has no delimiters, so FlatFileCodec's
        // newline-based record splitting can't chop the whole file up. Feeding
        // it exactly one record's bytes sidesteps that (still-open) gap.
        var oneRecord = new string(rawBytes.Take(recordLen).Select(b => (char)b).ToArray());
        Assert.DoesNotContain('\n', oneRecord); // else the split would tear it

        var record = Assert.Single(
            FlatFileCodec.Decode(parsed.Flat, oneRecord, CharacterEncoding.Ebcdic037));

        Assert.Equal("69684558", record["DTAR020-KEYCODE-NO"]);
        Assert.Equal(20m, record["DTAR020-STORE-NO"]);
        Assert.Equal(40118m, record["DTAR020-DATE"]);
        Assert.Equal(280m, record["DTAR020-DEPT-NO"]);
        Assert.Equal(1m, record["DTAR020-QTY-SOLD"]);
        Assert.Equal(19.00m, record["DTAR020-SALE-PRICE"]);
    }

    /// <summary>
    /// The whole real file, start to finish, through nothing but the public
    /// API: a genuine 1990s mainframe extract — fixed-format copybook, EBCDIC
    /// text, packed decimals, no record delimiters — decoded to typed values
    /// and re-encoded to the same 10,233 bytes it arrived as.
    /// </summary>
    [Fact]
    public void RoundtripsTheEntireRealFile_ByteForByte()
    {
        var parsed = ParseDtar020();
        var rawBytes = File.ReadAllBytes(SamplePaths.Root("dtar020/DTAR020.bin"));
        var text = new string(rawBytes.Select(b => (char)b).ToArray());

        var records = FlatFileCodec.Decode(
            parsed.Flat, text, CharacterEncoding.Ebcdic037, RecordFormat.FixedLength);
        Assert.Equal(379, records.Count);

        var reencoded = FlatFileCodec.Encode(
            parsed.Flat, records, CharacterEncoding.Ebcdic037, RecordFormat.FixedLength);

        Assert.Equal(rawBytes, reencoded.Select(c => (byte)c).ToArray());
    }

    /// <summary>
    /// Why the fixed-length path must not normalize newlines: this file has
    /// 0x0D bytes inside its COMP-3 fields (0x0D is the digit 0 with a
    /// negative sign nibble). They're data. A \r\n normalization pass over an
    /// undelimited file would eat one and shift every record after it.
    /// </summary>
    [Fact]
    public void CarriesControlCodeBytesInsidePackedFields()
    {
        var rawBytes = File.ReadAllBytes(SamplePaths.Root("dtar020/DTAR020.bin"));

        Assert.Equal(21, rawBytes.Count(b => b == 0x0D));
        Assert.Equal(0, rawBytes.Count(b => b == 0x0A));
    }

    /// <summary>
    /// The old behaviour, pinned: the delimited splitter hands the layout the
    /// entire 10,233-byte file as one oversized record. It fails loudly rather
    /// than wrongly — but it fails, which is why the format is a caller choice.
    /// </summary>
    [Fact]
    public void TheNewlineSplitterCannotReadThisFile()
    {
        var parsed = ParseDtar020();
        var rawBytes = File.ReadAllBytes(SamplePaths.Root("dtar020/DTAR020.bin"));
        var text = new string(rawBytes.Select(b => (char)b).ToArray());

        var ex = Assert.Throws<FormatException>(
            () => FlatFileCodec.Decode(parsed.Flat, text, CharacterEncoding.Ebcdic037));
        Assert.Contains("10233", ex.Message);
    }

    /// <summary>
    /// The strongest check available here: re-encoding the published values
    /// must reproduce a genuine mainframe record byte for byte. Nothing in
    /// this assertion comes from PICASSO — the input values are CobolToJson's
    /// published ones and the expected bytes are the real file's.
    /// </summary>
    [Fact]
    public void ReEncodesToByteIdenticalRealMainframeBytes()
    {
        var parsed = ParseDtar020();
        var recordLen = parsed.Flat.Sum(f => f.Len);
        var rawBytes = File.ReadAllBytes(SamplePaths.Root("dtar020/DTAR020.bin"));

        var values = new Dictionary<string, object>
        {
            ["DTAR020-KEYCODE-NO"] = "69684558",
            ["DTAR020-STORE-NO"] = 20m,
            ["DTAR020-DATE"] = 40118m,
            ["DTAR020-DEPT-NO"] = 280m,
            ["DTAR020-QTY-SOLD"] = 1m,
            ["DTAR020-SALE-PRICE"] = 19.00m,
        };

        var encoded = FlatFileCodec.Encode(
            parsed.Flat, new[] { values }, CharacterEncoding.Ebcdic037).TrimEnd('\n');

        Assert.Equal(rawBytes.Take(recordLen).ToArray(), encoded.Select(c => (byte)c).ToArray());
    }
}
