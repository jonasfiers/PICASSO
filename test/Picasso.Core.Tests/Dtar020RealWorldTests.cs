using System;
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
/// for the two real gaps running it uncovered — fixed-format source and
/// EBCDIC text — neither of which any synthetic copybook had exposed.
///
/// Because of those two gaps, this test does not call the public
/// FlatFileCodec.Decode (it assumes newline-delimited records; DTAR020.bin
/// has none) or assert anything about DTAR020-KEYCODE-NO (it's EBCDIC;
/// PICASSO only supports ASCII/Latin-1 text). It decodes the four COMP-3
/// fields by hand, the same way FlatFileCodec does internally, and checks
/// them against values independently published in CobolToJson's own
/// README — not anything derived from PICASSO.
/// </summary>
public class Dtar020RealWorldTests
{
    private static ParsedCopybook ParseDtar020()
    {
        var source = File.ReadAllText(SamplePaths.Root("dtar020/DTAR020.cpy"));
        return CopybookParser.Parse(source);
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

    [Fact]
    public void TextFieldIsGarbledBecauseTheSourceIsEbcdic_DocumentedNotFixed()
    {
        var parsed = ParseDtar020();
        var recordLen = parsed.Flat.Sum(f => f.Len);
        var rawBytes = File.ReadAllBytes(SamplePaths.Root("dtar020/DTAR020.bin"));
        var text = new string(rawBytes.Select(b => (char)b).ToArray());

        var keycode = parsed.Flat.Single(f => f.Name == "DTAR020-KEYCODE-NO");
        var raw = text.Substring(0, recordLen).Substring(keycode.Start, keycode.Len).TrimEnd(' ');

        // Real expected value (per CobolToJson's README) is "69684558" — an
        // EBCDIC-aware decoder would produce that. PICASSO's Latin-1-only
        // text decode does not, and this test exists to make that failure
        // visible and intentional rather than silently swallowed.
        Assert.NotEqual("69684558", raw);
        Assert.Equal("öùöøôõõø", raw); // EBCDIC digit bytes read as Latin-1
    }
}
