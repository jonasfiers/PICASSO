using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Picasso.Core;
using Picasso.Core.Models;

namespace Picasso.Extension;

/// <summary>
/// The Integration Studio action surface. Every method is shaped the way
/// OutSystems expects an extension action to look: a Boolean success result,
/// input parameters first, output parameters as <c>out</c> arguments, and no
/// exceptions crossing the boundary — a failure comes back as
/// <c>false</c> plus <c>errorMessage</c>, never as a thrown exception.
///
/// Complex results travel as JSON strings because Integration Studio actions
/// map cleanly onto Text/Integer/Decimal/Boolean but not onto arbitrary nested
/// objects. The OutSystems app deserializes them with its own built-in
/// JSONDeserialize against Structures defined in Service Studio — the same
/// pattern real Forge components use when the data shape is dynamic. The
/// Structures to define are documented in docs/outsystems-app-spec.md.
///
/// This is deliberately a plain class with no OutSystems base type: the
/// generated base class name is Integration Studio-version specific, so the
/// wiring step (README-IntegrationStudio.md) delegates to this from whatever
/// IS generates rather than guessing at it here.
/// </summary>
public sealed class PicassoActions
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // ---- Action 1: ParseCopybook ----

    /// <summary>
    /// Parses copybook source into a flat field layout plus an OutSystems
    /// structure preview.
    /// </summary>
    public bool ParseCopybook(
        string copybookSource,
        out string flatSpecJson,
        out string structurePreviewJson,
        out string errorMessage)
    {
        flatSpecJson = "";
        structurePreviewJson = "";
        errorMessage = "";

        try
        {
            if (string.IsNullOrWhiteSpace(copybookSource))
            {
                errorMessage = "Copybook source is empty.";
                return false;
            }

            var parsed = CopybookParser.Parse(copybookSource);

            flatSpecJson = JsonSerializer.Serialize(
                parsed.Flat.Select(FieldSpecDto.From).ToList(), Json);

            structurePreviewJson = JsonSerializer.Serialize(
                OutSystemsPreview.Build(parsed.Flat)
                    .Select(r => new AttributePreviewDto
                    {
                        AttributeName = r.AttributeName,
                        DataType = r.DataType,
                        Length = r.Length,
                        Decimals = r.Decimals,
                    })
                    .ToList(),
                Json);

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    // ---- Action 2: DecodeRecords ----

    /// <summary>
    /// Decodes fixed-width text into records, given a layout from
    /// <see cref="ParseCopybook"/>.
    /// </summary>
    public bool DecodeRecords(
        string flatSpecJson,
        string fixedWidthText,
        out string recordsJson,
        out string errorMessage)
    {
        recordsJson = "";
        errorMessage = "";

        try
        {
            var spec = ReadSpec(flatSpecJson);
            var records = FlatFileCodec.Decode(spec, fixedWidthText ?? "");

            var payload = records
                .Select(r => r.ToDictionary(kv => kv.Key, kv => kv.Value))
                .ToList();

            recordsJson = JsonSerializer.Serialize(payload, Json);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    // ---- Action 3: EncodeRecords ----

    /// <summary>
    /// Encodes records back to fixed-width text, given the same layout.
    /// </summary>
    public bool EncodeRecords(
        string flatSpecJson,
        string recordsJson,
        out string fixedWidthText,
        out string errorMessage)
    {
        fixedWidthText = "";
        errorMessage = "";

        try
        {
            var spec = ReadSpec(flatSpecJson);
            var records = ReadRecords(recordsJson, spec);
            fixedWidthText = FlatFileCodec.Encode(spec, records);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    // ---- Action 4: GetSampleCopybook ----

    /// <summary>
    /// Loads a bundled copybook and its seed data by id.
    /// <paramref name="sampleDataText"/> comes back empty for the two
    /// batch-intermediate copybooks that ship without data — that is not a
    /// failure, and the action still returns true.
    /// </summary>
    public bool GetSampleCopybook(
        string sampleId,
        out string copybookSource,
        out string sampleDataText,
        out string errorMessage)
    {
        copybookSource = "";
        sampleDataText = "";
        errorMessage = "";

        try
        {
            if (SampleLibrary.TryGet(sampleId ?? "", out copybookSource, out sampleDataText))
                return true;

            errorMessage =
                $"Unknown sample id '{sampleId}'. Known ids: {string.Join(", ", SampleLibrary.Ids())}.";
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    // ---- Action 5: ListSampleIds ----

    /// <summary>
    /// The bundled samples, as a JSON array. No failure mode worth a Boolean:
    /// the list is compiled into the assembly.
    /// </summary>
    public string ListSampleIds() =>
        JsonSerializer.Serialize(
            SampleLibrary.List()
                .Select(s => new SampleDto
                {
                    Id = s.Id,
                    FileName = s.FileName,
                    Description = s.Description,
                    HasSampleData = s.HasSampleData,
                })
                .ToList(),
            Json);

    // ---- JSON <-> engine ----

    private static List<FieldSpec> ReadSpec(string flatSpecJson)
    {
        if (string.IsNullOrWhiteSpace(flatSpecJson))
            throw new FormatException("Field layout JSON is empty. Call ParseCopybook first.");

        var dtos = JsonSerializer.Deserialize<List<FieldSpecDto>>(flatSpecJson, Json);
        if (dtos is null || dtos.Count == 0)
            throw new FormatException("Field layout JSON contained no fields.");

        return dtos.Select(d => d.ToFieldSpec()).ToList();
    }

    private static List<Dictionary<string, object>> ReadRecords(
        string recordsJson, IReadOnlyList<FieldSpec> spec)
    {
        if (string.IsNullOrWhiteSpace(recordsJson))
            throw new FormatException("Records JSON is empty.");

        var raw = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(recordsJson, Json);
        if (raw is null)
            throw new FormatException("Records JSON could not be read as a list of records.");

        var byName = spec.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var records = new List<Dictionary<string, object>>(raw.Count);

        foreach (var row in raw)
        {
            var record = new Dictionary<string, object>();
            foreach (var kv in row)
            {
                // Unknown keys are ignored rather than rejected: the OutSystems
                // side may round-trip extra presentation fields, and the codec
                // only ever reads what the layout names.
                if (!byName.TryGetValue(kv.Key, out var field))
                    continue;

                record[kv.Key] = ToClrValue(kv.Value, field);
            }
            records.Add(record);
        }

        return records;
    }

    /// <summary>
    /// JSON is looser than the layout: OutSystems may hand back a number as a
    /// string or vice versa, so coerce toward what the field actually is
    /// rather than trusting the JSON value kind.
    /// </summary>
    private static object ToClrValue(JsonElement element, FieldSpec field)
    {
        if (field.Type == FieldType.Text)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? "",
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                _ => element.ToString(),
            };
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.GetDecimal();

            case JsonValueKind.String:
                var text = element.GetString();
                if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
                throw new FormatException(
                    $"Field '{field.Name}' expects a number but got \"{text}\".");

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return 0m;

            default:
                throw new FormatException(
                    $"Field '{field.Name}' expects a number but got {element.ValueKind}.");
        }
    }

    // ---- DTOs: the JSON contract the OutSystems Structures mirror ----

    /// <summary>
    /// Mirrors the FieldSpec Structure in Service Studio. Carries
    /// SignSeparate/SignLeading in addition to the obvious fields: this JSON is
    /// an *input* to Decode/EncodeRecords, so dropping them would silently
    /// mis-decode any SIGN IS SEPARATE field (BALANCE-REC's NET-BALANCE) by
    /// reading its sign byte as a digit.
    /// </summary>
    private sealed class FieldSpecDto
    {
        public string Name { get; set; } = "";
        public int Start { get; set; }
        public int Len { get; set; }

        /// <summary>"Text", "NumericDisplay" or "Comp3".</summary>
        public string Type { get; set; } = "";

        public int Digits { get; set; }
        public int Scale { get; set; }
        public bool Signed { get; set; }
        public bool SignSeparate { get; set; }
        public bool SignLeading { get; set; }

        /// <summary>Redundant with Type == "Comp3"; present because the documented Structure includes it.</summary>
        public bool Comp3 { get; set; }

        public static FieldSpecDto From(FieldSpec f) => new()
        {
            Name = f.Name,
            Start = f.Start,
            Len = f.Len,
            Type = f.Type.ToString(),
            Digits = f.Digits,
            Scale = f.Scale,
            Signed = f.Signed,
            SignSeparate = f.SignSeparate,
            SignLeading = f.SignLeading,
            Comp3 = f.Type == FieldType.Comp3,
        };

        public FieldSpec ToFieldSpec()
        {
            if (!Enum.TryParse<FieldType>(Type, ignoreCase: true, out var type))
                throw new FormatException(
                    $"Field '{Name}' has unknown type \"{Type}\". Expected Text, NumericDisplay or Comp3.");

            return new FieldSpec
            {
                Name = Name,
                Start = Start,
                Len = Len,
                Type = type,
                Digits = Digits,
                Scale = Scale,
                Signed = Signed,
                SignSeparate = SignSeparate,
                SignLeading = SignLeading,
            };
        }
    }

    /// <summary>Mirrors the StructurePreview Structure. camelCase per the documented contract.</summary>
    private sealed class AttributePreviewDto
    {
        [JsonPropertyName("attributeName")] public string AttributeName { get; set; } = "";
        [JsonPropertyName("dataType")] public string DataType { get; set; } = "";
        [JsonPropertyName("length")] public int Length { get; set; }
        [JsonPropertyName("decimals")] public int Decimals { get; set; }
    }

    /// <summary>Mirrors the Sample Structure. camelCase per the documented contract.</summary>
    private sealed class SampleDto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("filename")] public string FileName { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("hasSampleData")] public bool HasSampleData { get; set; }
    }
}
