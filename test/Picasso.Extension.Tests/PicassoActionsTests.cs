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

    /// <summary>Every bundled sample — all ten now ship with data attached.</summary>
    public static TheoryData<string> SamplesWithData => new()
    {
        "user-rec", "user-auth-rec", "group-rec", "member-rec",
        "expense-rec", "share-rec", "balance-rec", "portrait-rec",
        "amount-owed-rec", "amount-paid-rec",
    };

    // ---- The end-to-end contract ----

    [Theory]
    [MemberData(nameof(SamplesWithData))]
    public void SampleSurvivesTheFullActionRoundtrip(string sampleId)
    {
        // The whole surface in the order the demo app calls it. If the JSON
        // contract loses anything — a scale, a sign flag, a COMP-3 byte — the
        // re-encoded text stops matching and this fails.
        Assert.True(_actions.GetSampleCopybook(sampleId, out var copybook, out var data, out var error), error);
        Assert.True(_actions.ParseCopybook(copybook, out var specJson, out _, out error), error);
        Assert.True(_actions.DecodeRecords(specJson, data, out var recordsJson, out error), error);
        Assert.True(_actions.EncodeRecords(specJson, recordsJson, out var reencoded, out error), error);

        Assert.Equal(data, reencoded);
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
    public void ParseCopybookReportsUnsupportedLevel88()
    {
        var source = "01  R.\n    05  A PIC X(3).\n    88  A-IS-X VALUE 'X'.\n";
        Assert.False(_actions.ParseCopybook(source, out _, out _, out var error));
        Assert.Contains("88", error);
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
            Assert.True(_actions.GetSampleCopybook(id, out var copybook, out _, out var error), $"{id}: {error}");
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
    public void EveryBundledSampleShipsWithDataExceptDtar020()
    {
        // dtar020-rec is the one deliberate exception: its real data file is
        // EBCDIC with no newline delimiters, which this action surface has no
        // way to decode correctly yet — see Samples/dtar020/README.md.
        var rows = JsonDocument.Parse(_actions.ListSampleIds()).RootElement.EnumerateArray().ToList();
        foreach (var r in rows)
        {
            var id = r.GetProperty("id").GetString();
            var hasSampleData = r.GetProperty("hasSampleData").GetBoolean();
            if (id == "dtar020-rec")
                Assert.False(hasSampleData, "dtar020-rec should not report bundled sample data");
            else
                Assert.True(hasSampleData, $"{id} reports no sample data");
        }
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
        Assert.Contains(names, n => n.EndsWith("Samples.dtar020.DTAR020.cpy", StringComparison.Ordinal));
        Assert.Equal(21, names.Length); // 11 copybooks (DTAR020 has no bundled data) + 10 data files
        Assert.DoesNotContain(names, n => n.EndsWith("specs.js", StringComparison.Ordinal));
    }
}
