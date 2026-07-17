using System.Text.Json;
using Picasso.Extension;

namespace Picasso.Extension.Tests;

/// <summary>
/// Validates the JSON contract the Service Studio app will consume, by calling
/// PicassoActions exactly the way Integration Studio will — plain C#, no
/// OutSystems runtime involved. Everything here is checkable before the user
/// ever opens Integration Studio.
/// </summary>
public class PicassoActionsTests
{
    private readonly PicassoActions _actions = new();

    /// <summary>Every bundled sample — all eleven ship with data attached.</summary>
    public static TheoryData<string> SamplesWithData => new()
    {
        "user-rec", "user-auth-rec", "group-rec", "member-rec",
        "expense-rec", "share-rec", "balance-rec", "portrait-rec",
        "amount-owed-rec", "amount-paid-rec", "dtar020-rec",
    };

    // ---- The end-to-end contract ----

    [Theory]
    [MemberData(nameof(SamplesWithData))]
    public void SampleSurvivesTheFullActionRoundtrip(string sampleId)
    {
        // The whole surface in the order the demo app calls it, with each
        // sample decoded using the settings it reports rather than assumed
        // ones. If the JSON contract loses anything — a scale, a sign flag, a
        // COMP-3 byte — the re-encoded text stops matching and this fails.
        //
        // dtar020-rec is the real proof: a genuine mainframe extract, EBCDIC
        // and undelimited, surviving the same trip as everything else purely
        // on the strength of what its descriptor says about it.
        Assert.True(_actions.GetSampleCopybook(
            sampleId, out var copybook, out var data,
            out var textEncoding, out var recordFormat, out var error), error);

        Assert.True(_actions.ParseCopybook(copybook, out var specJson, out _, out error), error);
        Assert.True(_actions.DecodeRecords(
            specJson, data, textEncoding, recordFormat, out var recordsJson, out error), error);
        Assert.True(_actions.EncodeRecords(
            specJson, recordsJson, textEncoding, recordFormat, out var reencoded, out error), error);

        Assert.Equal(data, reencoded);
    }

    // ---- Sample metadata ----

    [Fact]
    public void TheMainframeSampleReportsSettingsThatAreNotTheDefaults()
    {
        // The one sample that isn't ASCII/newline-delimited. If these ever came
        // back as the defaults, the roundtrip above would be decoding garbage.
        Assert.True(_actions.GetSampleCopybook(
            "dtar020-rec", out _, out var data,
            out var textEncoding, out var recordFormat, out var error), error);

        Assert.Equal("EBCDIC", textEncoding);
        Assert.Equal("FIXED", recordFormat);
        Assert.Equal(10233, data.Length); // the real 379 x 27-byte extract
    }

    [Theory]
    [MemberData(nameof(SamplesWithData))]
    public void ReportedSettingsAreAcceptedVerbatimByDecodeRecords(string sampleId)
    {
        // The names a sample reports must be names DecodeRecords takes, or the
        // metadata is decoration. This is the contract that makes an app able
        // to pass them straight through without mapping.
        Assert.True(_actions.GetSampleCopybook(
            sampleId, out var copybook, out var data,
            out var textEncoding, out var recordFormat, out var error), error);
        Assert.True(_actions.ParseCopybook(copybook, out var specJson, out _, out error), error);

        Assert.True(_actions.DecodeRecords(
            specJson, data, textEncoding, recordFormat, out _, out error), error);
    }

    [Fact]
    public void ListedSamplesReportTheSameSettingsAsGetSampleCopybook()
    {
        // ListSampleIds is what an app builds its dropdown from, so its
        // settings have to agree with the ones the loader hands back.
        var listed = JsonDocument.Parse(_actions.ListSampleIds()).RootElement;

        foreach (var sample in listed.EnumerateArray())
        {
            var id = sample.GetProperty("id").GetString()!;
            Assert.True(_actions.GetSampleCopybook(
                id, out _, out _, out var textEncoding, out var recordFormat, out var error), error);

            Assert.Equal(textEncoding, sample.GetProperty("textEncoding").GetString());
            Assert.Equal(recordFormat, sample.GetProperty("recordFormat").GetString());
        }
    }

    [Fact]
    public void TheShorthandLoaderRefusesTheSampleItCannotDescribe()
    {
        // The four-output overload can't report settings, so for the one sample
        // that needs non-default ones it fails loudly instead of handing back
        // EBCDIC bytes that would decode to plausible garbage.
        Assert.False(_actions.GetSampleCopybook("dtar020-rec", out var copybook, out var data, out var error));
        Assert.Contains("EBCDIC", error);
        Assert.Contains("FIXED", error);
        Assert.Equal("", copybook);
        Assert.Equal("", data);

        // ...and still works for the ten that do use the defaults.
        Assert.True(_actions.GetSampleCopybook("portrait-rec", out copybook, out data, out error), error);
        Assert.NotEqual("", copybook);
        Assert.NotEqual("", data);
    }

    [Fact]
    public void SignSeparateSurvivesTheJsonSpecRoundtrip()
    {
        // flatSpecJson is an *input* to Decode/Encode, so the sign-separate
        // flags have to survive serialization. If they didn't, NET-BALANCE's
        // trailing '+' would be read as a digit and decoding would blow up.
        Assert.True(_actions.GetSampleCopybook("balance-rec", out var copybook, out var data, out var error), error);
        Assert.True(_actions.ParseCopybook(copybook, out var specJson, out _, out error), error);

        var netBalance = JsonDocument.Parse(specJson).RootElement
            .EnumerateArray()
            .Single(f => f.GetProperty("Name").GetString() == "NET-BALANCE");

        Assert.True(netBalance.GetProperty("SignSeparate").GetBoolean());
        Assert.False(netBalance.GetProperty("SignLeading").GetBoolean()); // TRAILING
        Assert.Equal(10, netBalance.GetProperty("Len").GetInt32());
        Assert.Equal(2, netBalance.GetProperty("Scale").GetInt32());

        // And the spec JSON alone is enough to decode correctly.
        Assert.True(_actions.DecodeRecords(specJson, data, out var recordsJson, out error), error);
        var first = JsonDocument.Parse(recordsJson).RootElement[0];
        Assert.Equal(72.00m, first.GetProperty("NET-BALANCE").GetDecimal());
    }

    // ---- OCCURS across the action surface ----

    private const string OccursCopybook = @"
01  ORDER-REC.
    05  ORDER-ID    PIC 9(5).
    05  LINE-ITEM OCCURS 3 TIMES.
        10  ITEM-CODE  PIC X(4).
        10  ITEM-QTY   PIC 9(3).
    05  ORDER-NOTE  PIC X(6).
";

    [Fact]
    public void OccursExpandedNamesAppearInBothJsonContracts()
    {
        // The expanded, indexed field names have to reach the external caller —
        // in the flat spec it decodes with, and in the structure preview it
        // builds its UI from. If OCCURS only worked internally, neither JSON
        // would carry the copies and the app would see one LINE-ITEM, not three.
        Assert.True(_actions.ParseCopybook(OccursCopybook, out var specJson, out var previewJson, out var error), error);

        var specNames = JsonDocument.Parse(specJson).RootElement
            .EnumerateArray().Select(f => f.GetProperty("Name").GetString()).ToList();
        var previewNames = JsonDocument.Parse(previewJson).RootElement
            .EnumerateArray().Select(f => f.GetProperty("attributeName").GetString()).ToList();

        var expected = new[]
        {
            "ORDER-ID",
            "LINE-ITEM(1)-ITEM-CODE", "LINE-ITEM(1)-ITEM-QTY",
            "LINE-ITEM(2)-ITEM-CODE", "LINE-ITEM(2)-ITEM-QTY",
            "LINE-ITEM(3)-ITEM-CODE", "LINE-ITEM(3)-ITEM-QTY",
            "ORDER-NOTE",
        };
        Assert.Equal(expected, specNames);
        Assert.Equal(expected, previewNames);

        // The preview types map per iteration just like any other field.
        var qty2 = JsonDocument.Parse(previewJson).RootElement.EnumerateArray()
            .Single(f => f.GetProperty("attributeName").GetString() == "LINE-ITEM(2)-ITEM-QTY");
        Assert.Equal("Integer", qty2.GetProperty("dataType").GetString());
    }

    [Fact]
    public void OccursRecordDecodesAndEncodesThroughTheActionSurface()
    {
        Assert.True(_actions.ParseCopybook(OccursCopybook, out var specJson, out _, out var error), error);

        // A 3-item order laid out by hand at the expected offsets:
        // ORDER-ID(5) + 3 * (ITEM-CODE X(4) + ITEM-QTY 9(3)) + ORDER-NOTE X(6).
        const string record = "42007" + "AB12007" + "CD34012" + "EF56000" + "RUSH  ";
        Assert.True(_actions.DecodeRecords(specJson, record, out var recordsJson, out error), error);

        var first = JsonDocument.Parse(recordsJson).RootElement[0];
        Assert.Equal("AB12", first.GetProperty("LINE-ITEM(1)-ITEM-CODE").GetString());
        Assert.Equal(12m, first.GetProperty("LINE-ITEM(2)-ITEM-QTY").GetDecimal());
        Assert.Equal("EF56", first.GetProperty("LINE-ITEM(3)-ITEM-CODE").GetString());
        Assert.Equal("RUSH", first.GetProperty("ORDER-NOTE").GetString());

        Assert.True(_actions.EncodeRecords(specJson, recordsJson, out var reencoded, out error), error);
        Assert.Equal(record + "\n", reencoded);
    }

    // ---- ParseCopybook ----

    [Fact]
    public void ParseCopybookEmitsTheDocumentedFlatSpecShape()
    {
        Assert.True(_actions.GetSampleCopybook("portrait-rec", out var copybook, out _, out var error), error);
        Assert.True(_actions.ParseCopybook(copybook, out var specJson, out _, out error), error);

        var fields = JsonDocument.Parse(specJson).RootElement.EnumerateArray().ToList();
        Assert.Equal(8, fields.Count);

        var totalValue = fields.Single(f => f.GetProperty("Name").GetString() == "TOTAL-VALUE");
        Assert.Equal(95, totalValue.GetProperty("Start").GetInt32());
        Assert.Equal(6, totalValue.GetProperty("Len").GetInt32());
        Assert.Equal("Comp3", totalValue.GetProperty("Type").GetString());
        Assert.Equal(11, totalValue.GetProperty("Digits").GetInt32());
        Assert.Equal(2, totalValue.GetProperty("Scale").GetInt32());
        Assert.True(totalValue.GetProperty("Signed").GetBoolean());
        Assert.True(totalValue.GetProperty("Comp3").GetBoolean());
    }

    [Fact]
    public void ParseCopybookEmitsTheDocumentedStructurePreviewShape()
    {
        Assert.True(_actions.GetSampleCopybook("portrait-rec", out var copybook, out _, out var error), error);
        Assert.True(_actions.ParseCopybook(copybook, out _, out var previewJson, out error), error);

        var rows = JsonDocument.Parse(previewJson).RootElement.EnumerateArray().ToList();

        // camelCase, per the documented contract the OutSystems Structure mirrors.
        var artistName = rows.Single(r => r.GetProperty("attributeName").GetString() == "ARTIST-NAME");
        Assert.Equal("Text", artistName.GetProperty("dataType").GetString());
        Assert.Equal(30, artistName.GetProperty("length").GetInt32());
        Assert.Equal(0, artistName.GetProperty("decimals").GetInt32());

        var totalValue = rows.Single(r => r.GetProperty("attributeName").GetString() == "TOTAL-VALUE");
        Assert.Equal("Decimal", totalValue.GetProperty("dataType").GetString());
        Assert.Equal(11, totalValue.GetProperty("length").GetInt32());
        Assert.Equal(2, totalValue.GetProperty("decimals").GetInt32());

        var artistId = rows.Single(r => r.GetProperty("attributeName").GetString() == "ARTIST-ID");
        Assert.Equal("Integer", artistId.GetProperty("dataType").GetString());
    }

    [Fact]
    public void StructurePreviewNeverEmitsASyntheticSignAttribute()
    {
        // The OutSystems-facing view of BALANCE-REC shows one NET-BALANCE
        // Decimal attribute, not a Decimal plus a stray 1-char sign Text.
        Assert.True(_actions.GetSampleCopybook("balance-rec", out var copybook, out _, out var error), error);
        Assert.True(_actions.ParseCopybook(copybook, out _, out var previewJson, out error), error);

        var names = JsonDocument.Parse(previewJson).RootElement
            .EnumerateArray()
            .Select(r => r.GetProperty("attributeName").GetString())
            .ToList();

        Assert.Equal(6, names.Count);
        Assert.Contains("NET-BALANCE", names);
        Assert.DoesNotContain("SIGN", names);
        Assert.DoesNotContain("sign", names);
    }

    // ---- Text encoding (cp037) ----

    [Fact]
    public void EbcdicSurvivesTheFullActionRoundtrip()
    {
        // Same contract as the end-to-end test above, but with the file's text
        // bytes in EBCDIC — the shape a real mainframe extract arrives in.
        Assert.True(_actions.GetSampleCopybook("portrait-rec", out var copybook, out var data, out var error), error);
        Assert.True(_actions.ParseCopybook(copybook, out var specJson, out _, out error), error);
        Assert.True(_actions.DecodeRecords(specJson, data, out var recordsJson, out error), error);

        Assert.True(_actions.EncodeRecords(specJson, recordsJson, "EBCDIC", out var ebcdic, out error), error);
        Assert.NotEqual(data, ebcdic);            // text bytes really did change...
        Assert.Equal(data.Length, ebcdic.Length); // ...without moving any field

        Assert.True(_actions.DecodeRecords(specJson, ebcdic, "EBCDIC", out var back, out error), error);
        Assert.Equal(recordsJson, back);
    }

    [Fact]
    public void OmittingTheEncodingIsTheSameAsAskingForLatin1()
    {
        Assert.True(_actions.GetSampleCopybook("portrait-rec", out var copybook, out var data, out var error), error);
        Assert.True(_actions.ParseCopybook(copybook, out var specJson, out _, out error), error);

        Assert.True(_actions.DecodeRecords(specJson, data, out var byDefault, out error), error);
        Assert.True(_actions.DecodeRecords(specJson, data, "LATIN1", out var byName, out error), error);
        Assert.Equal(byDefault, byName);
    }

    [Fact]
    public void AnUnknownEncodingNameFailsLoudlyRatherThanDefaulting()
    {
        // Silently falling back to ASCII on a typo would decode a mainframe
        // file to garbage and report success.
        Assert.True(_actions.GetSampleCopybook("portrait-rec", out var copybook, out var data, out var error), error);
        Assert.True(_actions.ParseCopybook(copybook, out var specJson, out _, out error), error);

        Assert.False(_actions.DecodeRecords(specJson, data, "EBCDIK", out _, out error));
        Assert.Contains("EBCDIK", error);
        Assert.False(_actions.EncodeRecords(specJson, "[]", "EBCDIK", out _, out error));
    }

    // ---- Record format ----

    [Fact]
    public void UndelimitedFixedLengthFileSurvivesTheFullActionRoundtrip()
    {
        Assert.True(_actions.GetSampleCopybook("portrait-rec", out var copybook, out var data, out var error), error);
        Assert.True(_actions.ParseCopybook(copybook, out var specJson, out _, out error), error);
        Assert.True(_actions.DecodeRecords(specJson, data, out var recordsJson, out error), error);

        // Re-encode with no delimiters, then read it back the same way.
        Assert.True(_actions.EncodeRecords(specJson, recordsJson, "", "FIXED", out var packed, out error), error);
        Assert.DoesNotContain("\n", packed);
        Assert.True(_actions.DecodeRecords(specJson, packed, "", "FIXED", out var back, out error), error);
        Assert.Equal(recordsJson, back);
    }

    [Fact]
    public void AnUnknownRecordFormatFailsLoudlyRatherThanDefaulting()
    {
        Assert.True(_actions.GetSampleCopybook("portrait-rec", out var copybook, out var data, out var error), error);
        Assert.True(_actions.ParseCopybook(copybook, out var specJson, out _, out error), error);

        Assert.False(_actions.DecodeRecords(specJson, data, "", "BLOCKED", out _, out error));
        Assert.Contains("BLOCKED", error);
    }

    [Fact]
    public void AFileThatIsNotAWholeNumberOfRecordsReportsHowManyBytesAreLeftOver()
    {
        Assert.True(_actions.GetSampleCopybook("portrait-rec", out var copybook, out _, out var error), error);
        Assert.True(_actions.ParseCopybook(copybook, out var specJson, out _, out error), error);

        Assert.False(_actions.DecodeRecords(specJson, "junk", "", "FIXED", out _, out error));
        Assert.Contains("left over", error);
    }

    // ---- Failure modes come back as errorMessage, never as exceptions ----

    [Fact]
    public void ParseCopybookReportsEmptyInput()
    {
        Assert.False(_actions.ParseCopybook("", out _, out _, out var error));
        Assert.Contains("empty", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseCopybookReportsMalformedSourceWithoutThrowing()
    {
        Assert.False(_actions.ParseCopybook("this is not a copybook", out _, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ParseCopybookToleratesAndIgnoresLevel88()
    {
        // A level-88 condition-name occupies zero storage, so it's tolerated and
        // dropped: parsing succeeds and only the real field 'A' is in the layout.
        var source = "01  R.\n    05  A PIC X(3).\n    88  A-IS-X VALUE 'X'.\n";
        Assert.True(_actions.ParseCopybook(source, out var specJson, out _, out var error), error);
        Assert.DoesNotContain("A-IS-X", specJson);
        Assert.Contains("\"A\"", specJson);
    }

    [Fact]
    public void DecodeReportsWrongRecordWidth()
    {
        Assert.True(_actions.GetSampleCopybook("user-rec", out var copybook, out _, out var error), error);
        Assert.True(_actions.ParseCopybook(copybook, out var specJson, out _, out error), error);

        Assert.False(_actions.DecodeRecords(specJson, "too short\n", out _, out error));
        Assert.Contains("bytes", error);
    }

    [Fact]
    public void DecodeReportsMissingSpec()
    {
        Assert.False(_actions.DecodeRecords("", "whatever", out _, out var error));
        Assert.Contains("ParseCopybook", error);
    }

    [Fact]
    public void EncodeReportsMissingField()
    {
        Assert.True(_actions.GetSampleCopybook("user-rec", out var copybook, out _, out var error), error);
        Assert.True(_actions.ParseCopybook(copybook, out var specJson, out _, out error), error);

        Assert.False(_actions.EncodeRecords(specJson, """[{"USER-ID": 1}]""", out _, out error));
        Assert.Contains("USER-NAME", error);
    }

    [Fact]
    public void EncodeAcceptsNumbersSentAsStrings()
    {
        // OutSystems JSON round-tripping can stringify numbers; coerce toward
        // what the layout says the field is rather than trusting the JSON kind.
        Assert.True(_actions.GetSampleCopybook("member-rec", out var copybook, out _, out var error), error);
        Assert.True(_actions.ParseCopybook(copybook, out var specJson, out _, out error), error);

        Assert.True(_actions.EncodeRecords(
            specJson, """[{"GROUP-ID": "42", "USER-ID": 7}]""", out var text, out error), error);
        Assert.Equal("000042000007\n", text);
    }

    // ---- Samples ----

    [Fact]
    public void ListSampleIdsEmitsTheDocumentedShape()
    {
        var rows = JsonDocument.Parse(_actions.ListSampleIds()).RootElement.EnumerateArray().ToList();
        Assert.Equal(11, rows.Count);

        var portrait = rows.Single(r => r.GetProperty("id").GetString() == "portrait-rec");
        Assert.Equal("PORTRAIT-REC.cpy", portrait.GetProperty("filename").GetString());
        Assert.NotEmpty(portrait.GetProperty("description").GetString()!);
        Assert.True(portrait.GetProperty("hasSampleData").GetBoolean());
    }

    [Fact]
    public void EveryListedSampleIdResolvesAndParses()
    {
        // Guards the listing against drifting out of sync with the resources.
        var ids = JsonDocument.Parse(_actions.ListSampleIds()).RootElement
            .EnumerateArray()
            .Select(r => r.GetProperty("id").GetString()!)
            .ToList();

        foreach (var id in ids)
        {
            Assert.True(_actions.GetSampleCopybook(
                id, out var copybook, out _, out _, out _, out var error), $"{id}: {error}");
            Assert.NotEmpty(copybook);
            Assert.True(_actions.ParseCopybook(copybook, out _, out _, out error), $"{id}: {error}");
        }
    }

    [Theory]
    [InlineData("amount-owed-rec", 21)]
    [InlineData("amount-paid-rec", 21)]
    public void BatchIntermediateSamplesCarryRealCobolOutput(string sampleId, int expectedWidth)
    {
        // These two have no seed data — the bundled files are what CATALOG-74's
        // own CALC-OWED/CALC-PAID actually wrote when compiled with GnuCOBOL and
        // run. Decoding them proves the derived layout against a real COBOL
        // runtime rather than against our own encoder.
        Assert.True(_actions.GetSampleCopybook(sampleId, out var copybook, out var data, out var error), error);
        Assert.NotEmpty(data);
        Assert.True(_actions.ParseCopybook(copybook, out var specJson, out _, out error), error);

        var width = JsonDocument.Parse(specJson).RootElement
            .EnumerateArray().Sum(f => f.GetProperty("Len").GetInt32());
        Assert.Equal(expectedWidth, width);

        Assert.True(_actions.DecodeRecords(specJson, data, out var recordsJson, out error), error);
        Assert.True(_actions.EncodeRecords(specJson, recordsJson, out var reencoded, out error), error);
        Assert.Equal(data, reencoded);
    }

    [Fact]
    public void EveryBundledSampleShipsWithData()
    {
        // dtar020-rec used to be the one exception — its real extract is EBCDIC
        // with no delimiters, which the surface couldn't decode. It can now, and
        // the descriptor says how, so the exception is gone.
        var rows = JsonDocument.Parse(_actions.ListSampleIds()).RootElement.EnumerateArray().ToList();

        Assert.Equal(11, rows.Count);
        foreach (var r in rows)
            Assert.True(
                r.GetProperty("hasSampleData").GetBoolean(),
                $"{r.GetProperty("id").GetString()} reports no sample data");
    }

    [Fact]
    public void UnknownSampleIdListsTheKnownOnes()
    {
        Assert.False(_actions.GetSampleCopybook("no-such-rec", out _, out _, out var error));
        Assert.Contains("no-such-rec", error);
        Assert.Contains("portrait-rec", error); // the message names what IS available
    }

    [Fact]
    public void SampleIdIsCaseInsensitive()
    {
        Assert.True(_actions.GetSampleCopybook("PORTRAIT-REC", out var copybook, out _, out var error), error);
        Assert.Contains("PORTRAIT-REC", copybook);
    }

    [Fact]
    public void SamplesAreServedFromEmbeddedResourcesNotDisk()
    {
        // The assembly must carry its own samples: in a deployed extension
        // there is no repo tree beside the DLL to read from.
        var assembly = typeof(Picasso.Core.SampleLibrary).Assembly;
        var names = assembly.GetManifestResourceNames();

        Assert.Contains(names, n => n.EndsWith("Samples.PORTRAIT-REC.cpy", StringComparison.Ordinal));
        Assert.Contains(names, n => n.EndsWith("Samples.data.PORTRAIT-SAMPLE.DAT", StringComparison.Ordinal));
        Assert.Contains(names, n => n.EndsWith("Samples.dtar020.DTAR020.cbl", StringComparison.Ordinal));
        Assert.Contains(names, n => n.EndsWith("Samples.dtar020.DTAR020.bin", StringComparison.Ordinal));
        Assert.Equal(22, names.Length); // 11 copybooks + 11 data files
        Assert.DoesNotContain(names, n => n.EndsWith("specs.js", StringComparison.Ordinal));
    }

    // ---- Binary Data actions ----

    [Fact]
    public void DecodeRecordsFromBinaryMatchesDecodeRecordsOnTheRealMainframeExtract()
    {
        // dtar020-rec's real bytes, taken as actual bytes rather than routed
        // through Text at any point -- this is what an SFTP Get would hand an
        // OutSystems flow, never a pre-converted string.
        Assert.True(_actions.GetSampleCopybook(
            "dtar020-rec", out var copybook, out var dataAsText,
            out var textEncoding, out var recordFormat, out var error), error);
        var rawBytes = Picasso.Core.Latin1.ToBytes(dataAsText);
        Assert.Equal(10233, rawBytes.Length);

        Assert.True(_actions.ParseCopybook(copybook, out var specJson, out _, out error), error);

        Assert.True(_actions.DecodeRecordsFromBinary(
            specJson, rawBytes, textEncoding, recordFormat, out var recordsJson, out error), error);
        Assert.True(_actions.DecodeRecords(
            specJson, dataAsText, textEncoding, recordFormat, out var recordsJsonFromText, out error), error);

        Assert.Equal(recordsJsonFromText, recordsJson);
    }

    [Fact]
    public void EncodeRecordsToBinaryRoundTripsToTheExactOriginalBytes()
    {
        Assert.True(_actions.GetSampleCopybook(
            "dtar020-rec", out var copybook, out var dataAsText,
            out var textEncoding, out var recordFormat, out var error), error);
        var originalBytes = Picasso.Core.Latin1.ToBytes(dataAsText);

        Assert.True(_actions.ParseCopybook(copybook, out var specJson, out _, out error), error);
        Assert.True(_actions.DecodeRecordsFromBinary(
            specJson, originalBytes, textEncoding, recordFormat, out var recordsJson, out error), error);

        Assert.True(_actions.EncodeRecordsToBinary(
            specJson, recordsJson, textEncoding, recordFormat, out var reencodedBytes, out error), error);

        Assert.Equal(originalBytes, reencodedBytes);
    }

    [Fact]
    public void DecodeRecordsFromBinaryDefaultsMatchDecodeRecordsDefaults()
    {
        // An ASCII, newline-delimited sample: the 2-arg and 4-arg overloads
        // of both the Text and Binary Data actions must agree.
        Assert.True(_actions.GetSampleCopybook("user-rec", out var copybook, out var dataAsText, out var error), error);
        Assert.True(_actions.ParseCopybook(copybook, out var specJson, out _, out error), error);

        Assert.True(_actions.DecodeRecordsFromBinary(
            specJson, Picasso.Core.Latin1.ToBytes(dataAsText), out var recordsJsonBinary, out error), error);
        Assert.True(_actions.DecodeRecords(specJson, dataAsText, out var recordsJsonText, out error), error);

        Assert.Equal(recordsJsonText, recordsJsonBinary);
    }

    [Fact]
    public void DecodeRecordsFromBinaryFailsTheSameWayAsDecodeRecordsOnAMalformedSpec()
    {
        Assert.False(_actions.DecodeRecordsFromBinary(
            "not json", new byte[] { 1, 2, 3 }, out _, out var error));
        Assert.NotEmpty(error);
    }
}
