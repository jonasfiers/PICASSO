using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Picasso.Core;
using Picasso.Core.Models;
using Xunit;

namespace Picasso.Core.Tests;

/// <summary>
/// Decode → re-encode → byte-identical, against CATALOG-74's real seed data
/// (the actual files its COBOL batch job reads and writes) plus the synthetic
/// PORTRAIT-REC sample.
///
/// This is the strongest single statement the engine can make: every byte of
/// a real mainframe-format file survives the trip through typed values and
/// back. Roundtrip catches the whole class of bugs where decode and encode
/// are individually plausible but disagree — off-by-one padding, a dropped
/// sign, a COMP-3 nibble written to the wrong half of a byte.
/// </summary>
public class RoundtripTests
{
    public static TheoryData<string, string> CopybookAndData => new()
    {
        { "USER-REC.cpy", "USER-MASTER.DAT" },
        { "USER-AUTH-REC.cpy", "USER-AUTH.DAT" },
        { "GROUP-REC.cpy", "GROUP-MASTER.DAT" },
        { "MEMBER-REC.cpy", "MEMBER-MASTER.DAT" },
        { "EXPENSE-REC.cpy", "EXPENSE-MASTER.DAT" },
        { "SHARE-REC.cpy", "SHARE-TRANS.DAT" },
        { "BALANCE-REC.cpy", "BALANCE-MASTER.DAT" },
        // Batch intermediates. Their data isn't seed input — it's real GnuCOBOL
        // output, captured by compiling CATALOG-74's own CALC-OWED/CALC-PAID and
        // running the batch. Bytes an actual COBOL runtime wrote, not ours.
        { "AMOUNT-OWED-REC.cpy", "AMOUNT-OWED.DAT" },
        { "AMOUNT-PAID-REC.cpy", "AMOUNT-PAID.DAT" },
    };

    /// <summary>
    /// Latin-1 throughout: one .NET char per raw byte. Reading these files as
    /// UTF-8 would mangle COMP-3's packed bytes, which are not valid UTF-8.
    /// </summary>
    private static string ReadLatin1(string path) => File.ReadAllText(path, Encoding.Latin1);

    private static IReadOnlyList<FieldSpec> DeriveCatalog74(string copybookFileName) =>
        CopybookParser.Parse(ReadLatin1(SamplePaths.Catalog74(copybookFileName))).Flat;

    private static void AssertByteIdentical(string expected, string actual)
    {
        // Compare as bytes, not as strings: string equality would hide an
        // encoding round-trip fault, which is the exact thing at risk here.
        Assert.Equal(Encoding.Latin1.GetBytes(expected), Encoding.Latin1.GetBytes(actual));
    }

    [Theory]
    [MemberData(nameof(CopybookAndData))]
    public void RealSeedDataSurvivesDecodeEncode(string copybookFileName, string dataFileName)
    {
        var spec = DeriveCatalog74(copybookFileName);
        var original = ReadLatin1(SamplePaths.Data(dataFileName));

        var records = FlatFileCodec.Decode(spec, original);
        var reencoded = FlatFileCodec.Encode(spec, records);

        Assert.NotEmpty(records);
        AssertByteIdentical(original, reencoded);
    }

    [Fact]
    public void PortraitSampleSurvivesDecodeEncode()
    {
        // The synthetic sample carries what the real corpus can't: COMP-3 and
        // SIGN IS LEADING SEPARATE, including negative values of both.
        var spec = CopybookParser.Parse(ReadLatin1(SamplePaths.Root("PORTRAIT-REC.cpy"))).Flat;
        var original = ReadLatin1(SamplePaths.Data("PORTRAIT-SAMPLE.DAT"));

        var records = FlatFileCodec.Decode(spec, original);
        var reencoded = FlatFileCodec.Encode(spec, records);

        Assert.Equal(4, records.Count);
        AssertByteIdentical(original, reencoded);
    }

    [Theory]
    [MemberData(nameof(CopybookAndData))]
    public void EveryRealRecordIsExactlyTheDerivedRecordWidth(string copybookFileName, string dataFileName)
    {
        // If the derived layout were the wrong width, Decode would throw — but
        // assert the arithmetic outright so a failure names the real problem
        // instead of surfacing as an opaque decode error.
        var spec = DeriveCatalog74(copybookFileName);
        var recordWidth = spec.Sum(f => f.Len);
        var lines = ReadLatin1(SamplePaths.Data(dataFileName))
            .Split('\n')
            .Where(l => l.Length > 0)
            .ToList();

        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.Equal(recordWidth, line.Length));
    }

    // ---- Decoded values, not just byte equality ----

    [Fact]
    public void BalanceNetBalanceDecodesToASignedNumberWithNoHelper()
    {
        // CATALOG-74's flatfile.js needs a bolted-on signedNetBalance() helper
        // to reassemble netBalance + sign into a number. PICASSO returns the
        // signed decimal directly, because the field was never split.
        var spec = DeriveCatalog74("BALANCE-REC.cpy");
        var records = FlatFileCodec.Decode(spec, ReadLatin1(SamplePaths.Data("BALANCE-MASTER.DAT")));

        Assert.All(records, r => Assert.IsType<decimal>(r["NET-BALANCE"]));

        // First seed record: '000007200+' at bytes 30..39 -> +72.00
        Assert.Equal(72.00m, records[0]["NET-BALANCE"]);
        Assert.Equal(96.00m, records[0]["TOTAL-PAID"]);
        Assert.Equal(24.00m, records[0]["TOTAL-OWED"]);

        // The whole point of the record: paid - owed == net.
        Assert.All(records, r => Assert.Equal(
            (decimal)r["TOTAL-PAID"] - (decimal)r["TOTAL-OWED"],
            (decimal)r["NET-BALANCE"]));

        // No synthetic sign field leaks into the decoded record.
        Assert.DoesNotContain("sign", records[0].Keys);
        Assert.DoesNotContain("SIGN", records[0].Keys);
    }

    [Fact]
    public void NegativeBalancesRoundtripThroughTheTrailingSignByte()
    {
        // The seed data must actually exercise the negative path, or the
        // roundtrip above only ever proves '+' works.
        var spec = DeriveCatalog74("BALANCE-REC.cpy");
        var original = ReadLatin1(SamplePaths.Data("BALANCE-MASTER.DAT"));
        var records = FlatFileCodec.Decode(spec, original);

        Assert.Contains(records, r => (decimal)r["NET-BALANCE"] < 0);
        AssertByteIdentical(original, FlatFileCodec.Encode(spec, records));
    }

    [Fact]
    public void PortraitComp3AndLeadingSignDecodeToExpectedValues()
    {
        var spec = CopybookParser.Parse(ReadLatin1(SamplePaths.Root("PORTRAIT-REC.cpy"))).Flat;
        var records = FlatFileCodec.Decode(spec, ReadLatin1(SamplePaths.Data("PORTRAIT-SAMPLE.DAT")));

        // Positive COMP-3, and text padding trimmed on the way out.
        Assert.Equal(139428500.00m, records[0]["TOTAL-VALUE"]);
        Assert.Equal("PABLO RUIZ PICASSO", records[0]["ARTIST-NAME"]);
        Assert.Equal("PARIS", records[0]["CITY"]);

        // Negative COMP-3 (0xD sign nibble) and negative SIGN IS LEADING SEPARATE.
        Assert.Equal(-2750.55m, records[1]["TOTAL-VALUE"]);
        Assert.Equal(-18400.75m, records[1]["NET-WORTH"]);

        // Zero, and the S9(9)V99 ceiling in both directions.
        Assert.Equal(0.00m, records[2]["TOTAL-VALUE"]);
        Assert.Equal(999999999.99m, records[3]["TOTAL-VALUE"]);
        Assert.Equal(-999999999.99m, records[3]["NET-WORTH"]);
    }

    [Fact]
    public void EncodeThenDecodeReturnsTheOriginalValues()
    {
        // The mirror direction: start from values rather than from bytes, so a
        // codec that is self-consistently wrong in both directions still fails.
        var spec = CopybookParser.Parse(ReadLatin1(SamplePaths.Root("PORTRAIT-REC.cpy"))).Flat;
        var record = new Dictionary<string, object>
        {
            ["ARTIST-ID"] = 424242m,
            ["ARTIST-NAME"] = "DORA MAAR",
            ["STREET"] = "6 RUE DE SAVOIE",
            ["CITY"] = "PARIS",
            ["POSTAL-CODE"] = "75006",
            ["CANVAS-COUNT"] = 77m,
            ["TOTAL-VALUE"] = -1.05m,
            ["NET-WORTH"] = 250000.00m,
        };

        var encoded = FlatFileCodec.Encode(spec, new[] { record });
        Assert.Equal(spec.Sum(f => f.Len) + 1, encoded.Length); // + trailing newline

        var decoded = FlatFileCodec.Decode(spec, encoded).Single();
        foreach (var key in record.Keys)
            Assert.Equal(record[key], decoded[key]);
    }
}

/// <summary>
/// The same roundtrip guarantee, in EBCDIC cp037. The interesting case isn't
/// the text — it's that text and COMP-3 have to be handled differently within
/// one record: translate everything and the packed decimals corrupt, translate
/// nothing and the text is mojibake.
/// </summary>
public class EbcdicCodecTests
{
    private const string Copybook = @"
01  MIXED-REC.
    05  NAME       PIC X(6).
    05  QTY        PIC S9(5)V99 COMP-3.
    05  CODE       PIC X(2).
    05  COUNT      PIC 9(3).
";

    private static IReadOnlyList<FieldSpec> Spec() => CopybookParser.Parse(Copybook).Flat;

    private static Dictionary<string, object> Sample() => new()
    {
        ["NAME"] = "ABC",       // shorter than the field: exercises pad translation
        ["QTY"] = -12.34m,      // negative COMP-3: nibbles must survive untouched
        ["CODE"] = "Z9",
        ["COUNT"] = 7m,
    };

    [Fact]
    public void RoundtripsThroughEbcdic()
    {
        var spec = Spec();
        var encoded = FlatFileCodec.Encode(spec, new[] { Sample() }, CharacterEncoding.Ebcdic037);
        var decoded = Assert.Single(FlatFileCodec.Decode(spec, encoded, CharacterEncoding.Ebcdic037));

        Assert.Equal("ABC", decoded["NAME"]);
        Assert.Equal(-12.34m, decoded["QTY"]);
        Assert.Equal("Z9", decoded["CODE"]);
        Assert.Equal(7m, decoded["COUNT"]);
    }

    /// <summary>
    /// The load-bearing assertion for the whole design: within one record, the
    /// text bytes must change under EBCDIC and the COMP-3 bytes must not. A
    /// blanket record-level translation would fail this — and would be the
    /// FTP-ASCII-mode bug that silently destroys packed decimals.
    /// </summary>
    [Fact]
    public void TranslatesTextAndDisplayNumericsButNeverComp3()
    {
        var spec = Spec();
        var latin1 = FlatFileCodec.Encode(spec, new[] { Sample() });
        var ebcdic = FlatFileCodec.Encode(spec, new[] { Sample() }, CharacterEncoding.Ebcdic037);

        Assert.Equal(latin1.Length, ebcdic.Length); // encoding never changes field widths

        string Slice(string s, string fieldName)
        {
            var f = spec.Single(x => x.Name == fieldName);
            return s.Substring(f.Start, f.Len);
        }

        // COMP-3: byte-for-byte identical under both encodings — untouched.
        Assert.Equal(Slice(latin1, "QTY"), Slice(ebcdic, "QTY"));

        // Text: "ABC" + 3 pad bytes, as cp037 — not ASCII, and padded with
        // 0x40 rather than 0x20.
        Assert.Equal(
            new byte[] { 0xC1, 0xC2, 0xC3, 0x40, 0x40, 0x40 },
            Slice(ebcdic, "NAME").Select(c => (byte)c).ToArray());
        Assert.Equal("ABC   ", Slice(latin1, "NAME"));

        // DISPLAY numerics are text too: "007" as EBCDIC digits, not 0x30-0x32.
        Assert.Equal(
            new byte[] { 0xF0, 0xF0, 0xF7 },
            Slice(ebcdic, "COUNT").Select(c => (byte)c).ToArray());
        Assert.Equal("007", Slice(latin1, "COUNT"));
    }

    /// <summary>
    /// Why the encoding has to be an explicit caller choice and is never
    /// guessed: reading EBCDIC as Latin-1 is only loud when the record happens
    /// to contain a DISPLAY numeric, whose EBCDIC digits won't parse. A
    /// text-only record decodes silently wrong. There is no detecting this
    /// after the fact — the caller has to say.
    /// </summary>
    [Fact]
    public void ReadingEbcdicAsLatin1IsLoudOnlyIfTheRecordHasADisplayNumeric()
    {
        var spec = Spec();
        var ebcdic = FlatFileCodec.Encode(spec, new[] { Sample() }, CharacterEncoding.Ebcdic037);

        // COUNT (PIC 9(3)) saves us here: 0xF0 0xF0 0xF7 isn't a number.
        Assert.ThrowsAny<Exception>(() => FlatFileCodec.Decode(spec, ebcdic));

        // Text-only, and the same mistake passes silently with garbage values.
        var textOnly = CopybookParser.Parse("01  R.\n    05  NAME PIC X(6).\n").Flat;
        var encoded = FlatFileCodec.Encode(textOnly, new[] { Sample() }, CharacterEncoding.Ebcdic037);
        var decoded = Assert.Single(FlatFileCodec.Decode(textOnly, encoded));

        Assert.NotEqual("ABC", decoded["NAME"]);
        // Not even the padding survives: EBCDIC pads with 0x40, which reads as
        // '@' in Latin-1, so TrimEnd(' ') leaves it on the value.
        Assert.Equal("ÁÂÃ@@@", decoded["NAME"]);
    }
}

/// <summary>
/// Undelimited fixed-length records (the mainframe's RECFM=F shape): the file
/// is a bare concatenation and the layout's own record length is the only
/// thing that says where one record stops.
/// </summary>
public class FixedLengthRecordTests
{
    private static IReadOnlyList<FieldSpec> Spec() =>
        CopybookParser.Parse("01  R.\n    05  NAME PIC X(6).\n    05  COUNT PIC 9(3).\n").Flat;

    private static Dictionary<string, object> Rec(string name, decimal count) =>
        new() { ["NAME"] = name, ["COUNT"] = count };

    [Fact]
    public void RoundtripsWithNoDelimitersAtAll()
    {
        var spec = Spec();
        var records = new[] { Rec("AAA", 1m), Rec("BBB", 2m), Rec("CCC", 3m) };

        var encoded = FlatFileCodec.Encode(spec, records, format: RecordFormat.FixedLength);

        Assert.Equal("AAA   001BBB   002CCC   003", encoded);
        Assert.DoesNotContain('\n', encoded); // no trailing newline either

        var decoded = FlatFileCodec.Decode(
            spec, encoded, CharacterEncoding.Latin1, RecordFormat.FixedLength);

        Assert.Equal(3, decoded.Count);
        Assert.Equal("BBB", decoded[1]["NAME"]);
        Assert.Equal(2m, decoded[1]["COUNT"]);
    }

    /// <summary>
    /// The reason the two formats can't be collapsed into one: with no
    /// delimiters, 0x0A is just a byte. The newline splitter would tear this
    /// record in half; the fixed-length splitter carries it through.
    /// </summary>
    [Fact]
    public void ANewlineInsideAFieldIsDataNotAStructure()
    {
        var spec = Spec();
        var encoded = FlatFileCodec.Encode(
            spec, new[] { Rec("A\nB", 7m) }, format: RecordFormat.FixedLength);

        var decoded = Assert.Single(FlatFileCodec.Decode(
            spec, encoded, CharacterEncoding.Latin1, RecordFormat.FixedLength));
        Assert.Equal("A\nB", decoded["NAME"]);

        // Read as delimited, the same bytes tear into two malformed records.
        Assert.Throws<FormatException>(() => FlatFileCodec.Decode(spec, encoded));
    }

    [Fact]
    public void ALengthThatIsNotAWholeNumberOfRecordsFailsLoudly()
    {
        var spec = Spec(); // 9 bytes per record

        var ex = Assert.Throws<FormatException>(() => FlatFileCodec.Decode(
            spec, new string('X', 20), CharacterEncoding.Latin1, RecordFormat.FixedLength));

        Assert.Contains("20 bytes", ex.Message);
        Assert.Contains("2 records plus 2 bytes left over", ex.Message);
    }

    [Fact]
    public void AnEmptyFileIsZeroRecordsNotAnError()
    {
        var spec = Spec();
        Assert.Empty(FlatFileCodec.Decode(spec, "", CharacterEncoding.Latin1, RecordFormat.FixedLength));
        Assert.Equal("", FlatFileCodec.Encode(
            spec, new List<Dictionary<string, object>>(), format: RecordFormat.FixedLength));
    }
}
