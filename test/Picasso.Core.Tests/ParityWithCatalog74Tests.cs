using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Picasso.Core;
using Picasso.Core.Models;
using Xunit;

namespace Picasso.Core.Tests;

/// <summary>
/// The golden test, and the whole reason PICASSO exists.
///
/// CATALOG-74's api-cobol/lib/specs.js hand-transcribes each copybook's PIC
/// clauses into byte offsets — a human read the COBOL and typed the numbers
/// out. PICASSO derives those same numbers from the .cpy source. If the
/// parser is right, the two must agree byte-for-byte.
///
/// specs.js is vendored verbatim (Samples/catalog74/specs.js) and parsed at
/// test time rather than re-typed into C# literals here. Re-typing it would
/// repeat the exact hand-transcription this project exists to eliminate, and
/// a typo would make the test agree with the wrong thing.
/// </summary>
public class ParityWithCatalog74Tests
{
    /// <summary>One entry from a specs.js SPEC array.</summary>
    private sealed record JsField(string Name, int Start, int Len, string Type);

    private static readonly Dictionary<string, List<JsField>> JsSpecs = LoadSpecsJs();

    /// <summary>
    /// specs.js const name -> the copybook it was hand-transcribed from.
    /// AMOUNT-OWED-REC and AMOUNT-PAID-REC are intentionally absent: they are
    /// batch-intermediate files the JS API never reads, so specs.js has no
    /// counterpart to compare against. See NonTranscribedCopybooks below.
    /// </summary>
    public static TheoryData<string, string> SpecToCopybook => new()
    {
        { "USER_SPEC", "USER-REC.cpy" },
        { "USER_AUTH_SPEC", "USER-AUTH-REC.cpy" },
        { "GROUP_SPEC", "GROUP-REC.cpy" },
        { "MEMBER_SPEC", "MEMBER-REC.cpy" },
        { "EXPENSE_SPEC", "EXPENSE-REC.cpy" },
        { "SHARE_SPEC", "SHARE-REC.cpy" },
        // BALANCE_SPEC is excluded here — it has the one deliberate
        // discrepancy, asserted explicitly below.
    };

    private static IReadOnlyList<FieldSpec> Derive(string copybookFileName) =>
        CopybookParser.Parse(File.ReadAllText(SamplePaths.Catalog74(copybookFileName), Encoding.Latin1)).Flat;

    [Theory]
    [MemberData(nameof(SpecToCopybook))]
    public void DerivedOffsetsMatchHandTranscribedSpecsJs(string specName, string copybookFileName)
    {
        var expected = JsSpecs[specName];
        var derived = Derive(copybookFileName);

        // Compared positionally: specs.js uses JS camelCase names ('groupId'),
        // the copybooks use COBOL names ('GROUP-ID'). The bytes are the
        // contract; the names are each side's own business.
        Assert.Equal(
            expected.Select(f => (f.Start, f.Len)),
            derived.Select(f => (f.Start, f.Len)));
    }

    [Fact]
    public void EveryTranscribedSpecIsCovered()
    {
        // Guards against this test quietly going stale if specs.js grows a
        // spec: every SPEC in the file must either be mapped above or be the
        // documented BALANCE exception.
        var mapped = SpecToCopybook.Select(row => (string)row[0]).ToHashSet();
        mapped.Add("BALANCE_SPEC");
        Assert.Equal(mapped.OrderBy(n => n), JsSpecs.Keys.OrderBy(n => n));
    }

    [Theory]
    [InlineData("AMOUNT-OWED-REC.cpy")]
    [InlineData("AMOUNT-PAID-REC.cpy")]
    public void NonTranscribedCopybooksStillParse(string copybookFileName)
    {
        // 7 of the 9 vendored copybooks have a specs.js counterpart. These two
        // are batch intermediates with no JS-side transcription, so there is no
        // golden to compare — but they are still part of the real grammar
        // corpus and must parse.
        var derived = Derive(copybookFileName);
        Assert.Equal(3, derived.Count);
        Assert.Equal(new[] { 0, 6, 12 }, derived.Select(f => f.Start));
        Assert.Equal(new[] { 6, 6, 9 }, derived.Select(f => f.Len));
    }

    // ---- The one deliberate discrepancy ----

    [Fact]
    public void BalanceRec_PicassoMergesNetBalanceAndSignIntoOneField()
    {
        // SIGN IS TRAILING SEPARATE reserves an extra byte belonging to the
        // SAME data item. specs.js splits it into two synthetic fields
        // (netBalance + sign) because its paired JS codec has no signed-type
        // concept — hence the extra signedNetBalance() helper in flatfile.js.
        //
        // PICASSO models it as COBOL defines it: ONE field spanning the full
        // byte range, decoding straight to a signed number. This is the parser
        // getting right what the hand transcription didn't bother with, so it
        // is asserted as intended behaviour — not tolerated as a failure.
        var js = JsSpecs["BALANCE_SPEC"];
        var derived = Derive("BALANCE-REC.cpy");

        var jsNetBalance = js.Single(f => f.Name == "netBalance");
        var jsSign = js.Single(f => f.Name == "sign");

        // specs.js: two adjacent fields, the second being a 1-byte 'str' sign.
        Assert.Equal(9, jsNetBalance.Len);
        Assert.Equal(1, jsSign.Len);
        Assert.Equal(jsNetBalance.Start + jsNetBalance.Len, jsSign.Start);
        Assert.Equal("str", jsSign.Type);

        // PICASSO: one field, spanning exactly the union of those two.
        var netBalance = derived.Single(f => f.Name == "NET-BALANCE");
        Assert.Equal(jsNetBalance.Start, netBalance.Start);
        Assert.Equal(jsNetBalance.Len + jsSign.Len, netBalance.Len);

        // ...and it carries the sign as a typed property rather than as bytes
        // the caller has to reinterpret themselves.
        Assert.Equal(FieldType.NumericDisplay, netBalance.Type);
        Assert.True(netBalance.Signed);
        Assert.True(netBalance.SignSeparate);
        Assert.False(netBalance.SignLeading); // TRAILING
        Assert.Equal(2, netBalance.Scale);

        // No synthetic sign field exists on the PICASSO side.
        Assert.DoesNotContain(derived, f => f.Name.Equals("SIGN", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(js.Count - 1, derived.Count);
    }

    [Fact]
    public void BalanceRec_EveryFieldOutsideTheMergeMatchesExactly()
    {
        // The merge is the ONLY divergence: everything before it and after it
        // must still line up byte-for-byte, including asOf's start, which only
        // lands correctly if the merged field is sized 9+1.
        var js = JsSpecs["BALANCE_SPEC"];
        var derived = Derive("BALANCE-REC.cpy");

        var jsBeforeMerge = js.TakeWhile(f => f.Name != "netBalance").ToList();
        Assert.Equal(
            jsBeforeMerge.Select(f => (f.Start, f.Len)),
            derived.Take(jsBeforeMerge.Count).Select(f => (f.Start, f.Len)));

        var jsAfterMerge = js.SkipWhile(f => f.Name != "sign").Skip(1).ToList();
        Assert.Equal(
            jsAfterMerge.Select(f => (f.Start, f.Len)),
            derived.Skip(jsBeforeMerge.Count + 1).Select(f => (f.Start, f.Len)));

        // Both sides describe a record of identical total width.
        Assert.Equal(js.Sum(f => f.Len), derived.Sum(f => f.Len));
    }

    // ---- specs.js reader ----

    private static Dictionary<string, List<JsField>> LoadSpecsJs()
    {
        var source = File.ReadAllText(SamplePaths.Catalog74("specs.js"), Encoding.Latin1);

        var specBlock = new Regex(@"const\s+(\w+_SPEC)\s*=\s*\[(.*?)\]", RegexOptions.Singleline);
        var entry = new Regex(
            @"\{\s*name:\s*'(?<name>[^']*)'\s*,\s*start:\s*(?<start>\d+)\s*,\s*len:\s*(?<len>\d+)\s*,\s*type:\s*'(?<type>[^']*)'\s*\}");

        var specs = new Dictionary<string, List<JsField>>();
        foreach (Match block in specBlock.Matches(source))
        {
            var fields = entry.Matches(block.Groups[2].Value)
                .Cast<Match>()
                .Select(m => new JsField(
                    m.Groups["name"].Value,
                    int.Parse(m.Groups["start"].Value),
                    int.Parse(m.Groups["len"].Value),
                    m.Groups["type"].Value))
                .ToList();

            if (fields.Count == 0)
                throw new InvalidOperationException($"Parsed no fields out of {block.Groups[1].Value} in specs.js.");

            specs[block.Groups[1].Value] = fields;
        }

        if (specs.Count == 0)
            throw new InvalidOperationException("Parsed no SPEC arrays out of specs.js — has its format changed?");

        return specs;
    }

    [Fact]
    public void SpecsJsReaderActuallyReadsTheFile()
    {
        // If the regex silently matched nothing, every parity assertion above
        // would vacuously pass. Pin one known value from the vendored file.
        Assert.Equal(7, JsSpecs.Count);
        var user = JsSpecs["USER_SPEC"];
        Assert.Equal(3, user.Count);
        Assert.Equal(new JsField("email", 36, 40, "str"), user[2]);
    }
}
