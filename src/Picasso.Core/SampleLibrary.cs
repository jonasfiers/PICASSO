using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Picasso.Core;

/// <summary>One bundled copybook, as offered to a caller picking a sample.</summary>
public sealed class SampleDescriptor
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>
    /// True for every sample currently bundled. Kept as an explicit flag rather
    /// than assumed: a copybook can legitimately ship without data, and callers
    /// should branch on this rather than on an empty string.
    /// </summary>
    public bool HasSampleData { get; set; }
}

/// <summary>
/// Serves the bundled copybooks and seed data out of the assembly's embedded
/// resources rather than off disk: once this DLL is referenced from an
/// Integration Studio extension, there is no repo tree beside it to read.
/// </summary>
public static class SampleLibrary
{
    private sealed class Sample
    {
        public string Id = "";
        public string CopybookResource = "";
        public string? DataResource;
        public string Description = "";
    }

    private static readonly List<Sample> Samples = new()
    {
        new Sample
        {
            Id = "user-rec",
            CopybookResource = "Samples.catalog74.USER-REC.cpy",
            DataResource = "Samples.data.USER-MASTER.DAT",
            Description = "One record per person. The simplest real layout: an ID and two text fields.",
        },
        new Sample
        {
            Id = "user-auth-rec",
            CopybookResource = "Samples.catalog74.USER-AUTH-REC.cpy",
            DataResource = "Samples.data.USER-AUTH.DAT",
            Description = "Credential record — an ID plus a 60-byte password hash.",
        },
        new Sample
        {
            Id = "group-rec",
            CopybookResource = "Samples.catalog74.GROUP-REC.cpy",
            DataResource = "Samples.data.GROUP-MASTER.DAT",
            Description = "One record per expense-sharing group.",
        },
        new Sample
        {
            Id = "member-rec",
            CopybookResource = "Samples.catalog74.MEMBER-REC.cpy",
            DataResource = "Samples.data.MEMBER-MASTER.DAT",
            Description = "Group/user join record. Two numeric fields, 12 bytes total.",
        },
        new Sample
        {
            Id = "expense-rec",
            CopybookResource = "Samples.catalog74.EXPENSE-REC.cpy",
            DataResource = "Samples.data.EXPENSE-MASTER.DAT",
            Description = "One record per expense, including an implied-decimal amount (PIC 9(7)V99).",
        },
        new Sample
        {
            Id = "share-rec",
            CopybookResource = "Samples.catalog74.SHARE-REC.cpy",
            DataResource = "Samples.data.SHARE-TRANS.DAT",
            Description = "Per-person share of an expense. Implied decimal, no sign.",
        },
        new Sample
        {
            Id = "balance-rec",
            CopybookResource = "Samples.catalog74.BALANCE-REC.cpy",
            DataResource = "Samples.data.BALANCE-MASTER.DAT",
            Description =
                "Derived net balance per person. Carries SIGN IS TRAILING SEPARATE, so NET-BALANCE " +
                "spans 10 bytes — 9 digits plus a sign character — and decodes to a signed number.",
        },
        new Sample
        {
            Id = "amount-owed-rec",
            CopybookResource = "Samples.catalog74.AMOUNT-OWED-REC.cpy",
            DataResource = "Samples.data.AMOUNT-OWED.DAT",
            Description =
                "Per-person owed totals, produced mid-batch by CALC-OWED's control-break aggregation. " +
                "The bundled data is real GnuCOBOL output, not synthesized.",
        },
        new Sample
        {
            Id = "amount-paid-rec",
            CopybookResource = "Samples.catalog74.AMOUNT-PAID-REC.cpy",
            DataResource = "Samples.data.AMOUNT-PAID.DAT",
            Description =
                "Per-person paid totals, produced mid-batch by CALC-PAID. The bundled data is real " +
                "GnuCOBOL output, not synthesized.",
        },
        new Sample
        {
            Id = "portrait-rec",
            CopybookResource = "Samples.PORTRAIT-REC.cpy",
            DataResource = "Samples.data.PORTRAIT-SAMPLE.DAT",
            Description =
                "Synthetic demo record. The only sample with 3-level nesting, COMP-3 packed decimal, " +
                "and SIGN IS LEADING SEPARATE — none of the real copybooks combine all three.",
        },
        new Sample
        {
            Id = "dtar020-rec",
            CopybookResource = "Samples.dtar020.DTAR020.cpy",
            DataResource = null,
            Description =
                "A genuine mainframe copybook (1990, credited 'BRUCE ARTHUR', from a real reporting " +
                "system) — not written for this project, and bundled in its original fixed-format form: " +
                "sequence numbers intact, parsed as-is. See Samples/dtar020/README.md for provenance and " +
                "the limitations it surfaced that no synthetic copybook exposed — fixed-format source and " +
                "EBCDIC text (both now handled), headless copy members and undelimited record files (not). " +
                "No bundled sample data: the real DTAR020.bin has no delimiters between records, so this " +
                "action surface can't split it into records yet — its EBCDIC text now decodes correctly " +
                "via TextEncoding='EBCDIC'. See Dtar020RealWorldTests.cs for how it's actually decoded.",
        },
    };

    public static IReadOnlyList<SampleDescriptor> List() =>
        Samples.Select(s => new SampleDescriptor
        {
            Id = s.Id,
            FileName = FileNameOf(s.CopybookResource),
            Description = s.Description,
            HasSampleData = s.DataResource != null,
        }).ToList();

    /// <summary>
    /// Looks a sample up by id. <paramref name="sampleDataText"/> is empty for a
    /// sample bundled without data — not an error. Every sample currently
    /// bundled has data.
    /// </summary>
    public static bool TryGet(string id, out string copybookSource, out string sampleDataText)
    {
        copybookSource = "";
        sampleDataText = "";

        var sample = Samples.FirstOrDefault(
            s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        if (sample is null)
            return false;

        copybookSource = ReadResource(sample.CopybookResource);
        sampleDataText = sample.DataResource is null ? "" : ReadResource(sample.DataResource);
        return true;
    }

    public static IReadOnlyList<string> Ids() => Samples.Select(s => s.Id).ToList();

    private static string FileNameOf(string resourceSuffix) =>
        resourceSuffix.Substring(resourceSuffix.LastIndexOf('.', resourceSuffix.LastIndexOf('.') - 1) + 1);

    private static string ReadResource(string suffix)
    {
        var assembly = typeof(SampleLibrary).GetTypeInfo().Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal));

        if (name is null)
            throw new InvalidOperationException(
                $"Embedded sample resource '{suffix}' is missing from {assembly.GetName().Name}.");

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Latin1(buffer.ToArray());
    }

    /// <summary>
    /// One char per raw byte. The seed data contains COMP-3 packed bytes that
    /// are not valid UTF-8, and Encoding.Latin1 does not exist on
    /// netstandard2.0 — so the mapping is spelled out rather than assumed.
    /// </summary>
    private static string Latin1(byte[] bytes)
    {
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
            chars[i] = (char)bytes[i];
        return new string(chars);
    }
}
