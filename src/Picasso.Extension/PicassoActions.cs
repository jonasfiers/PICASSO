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
    /// <see cref="ParseCopybook"/>. Assumes ASCII/Latin-1 text; use the
    /// <paramref name="textEncoding"/> overload for an EBCDIC mainframe extract.
    /// </summary>
    public bool DecodeRecords(
        string flatSpecJson,
        string fixedWidthText,
        out string recordsJson,
        out string errorMessage) =>
        DecodeRecords(flatSpecJson, fixedWidthText, "", out recordsJson, out errorMessage);

    /// <summary>
    /// Decodes fixed-width text into records. <paramref name="textEncoding"/>
    /// names the encoding of the file's *text* bytes: "" or "LATIN1"/"ASCII"
    /// for the default, "EBCDIC" (equivalently "CP037"/"EBCDIC037") for an
    /// EBCDIC cp037 mainframe extract. COMP-3 fields are never affected by it.
    /// An unrecognized name is an error, not a fallback to the default —
    /// quietly decoding a mainframe file as ASCII is the failure this avoids.
    /// </summary>
    public bool DecodeRecords(
        string flatSpecJson,
        string fixedWidthText,
        string textEncoding,
        out string recordsJson,
        out string errorMessage) =>
        DecodeRecords(flatSpecJson, fixedWidthText, textEncoding, "", out recordsJson, out errorMessage);

    /// <summary>
    /// Decodes fixed-width text into records. <paramref name="recordFormat"/>
    /// says how records are separated: "" or "DELIMITED" for newline-separated
    /// records, "FIXED" for a bare concatenation of fixed-length records with
    /// no delimiters at all — the mainframe's usual RECFM=F shape. The record
    /// width always comes from the layout, never from this.
    /// </summary>
    public bool DecodeRecords(
        string flatSpecJson,
        string fixedWidthText,
        string textEncoding,
        string recordFormat,
        out string recordsJson,
        out string errorMessage)
    {
        recordsJson = "";
        errorMessage = "";

        try
        {
            var spec = ReadSpec(flatSpecJson);
            var records = FlatFileCodec.Decode(
                spec, fixedWidthText ?? "", ParseEncoding(textEncoding), ParseRecordFormat(recordFormat));

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
    /// Encodes records back to fixed-width text, given the same layout. Writes
    /// ASCII/Latin-1 text; use the <paramref name="textEncoding"/> overload to
    /// write an EBCDIC mainframe extract.
    /// </summary>
    public bool EncodeRecords(
        string flatSpecJson,
        string recordsJson,
        out string fixedWidthText,
        out string errorMessage) =>
        EncodeRecords(flatSpecJson, recordsJson, "", out fixedWidthText, out errorMessage);

    /// <summary>
    /// Encodes records back to fixed-width text. <paramref name="textEncoding"/>
    /// takes the same names as <see cref="DecodeRecords(string,string,string,out string,out string)"/>.
    /// </summary>
    public bool EncodeRecords(
        string flatSpecJson,
        string recordsJson,
        string textEncoding,
        out string fixedWidthText,
        out string errorMessage) =>
        EncodeRecords(flatSpecJson, recordsJson, textEncoding, "", out fixedWidthText, out errorMessage);

    /// <summary>
    /// Encodes records back to fixed-width text. <paramref name="textEncoding"/>
    /// and <paramref name="recordFormat"/> take the same names as
    /// <see cref="DecodeRecords(string,string,string,string,out string,out string)"/>;
    /// "FIXED" writes the records back to back with no delimiters.
    /// </summary>
    public bool EncodeRecords(
        string flatSpecJson,
        string recordsJson,
        string textEncoding,
        string recordFormat,
        out string fixedWidthText,
        out string errorMessage)
    {
        fixedWidthText = "";
        errorMessage = "";

        try
        {
            var spec = ReadSpec(flatSpecJson);
            var records = ReadRecords(recordsJson, spec);
            fixedWidthText = FlatFileCodec.Encode(
                spec, records, ParseEncoding(textEncoding), ParseRecordFormat(recordFormat));
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Maps the action surface's record-format name onto <see cref="RecordFormat"/>.
    /// Unknown names throw, for the same reason as <see cref="ParseEncoding"/>:
    /// this boundary turns that into a named failure rather than a guess.
    /// </summary>
    private static RecordFormat ParseRecordFormat(string name)
    {
        switch ((name ?? "").Trim().ToUpperInvariant())
        {
            case "":
            case "DELIMITED":
            case "NEWLINE":
                return RecordFormat.NewlineDelimited;

            case "FIXED":
            case "FIXEDLENGTH":
                return RecordFormat.FixedLength;

            default:
                throw new ArgumentException(
                    $"Unknown record format '{name}'. Use '' or 'DELIMITED' for newline-separated " +
                    "records, or 'FIXED' for an undelimited fixed-length file.");
        }
    }

    /// <summary>
    /// The names meaning "the default" on the way out. Blank means the same on
    /// the way in, but the actions report these instead: a caller showing a
    /// sample's settings in a UI should see a word, not an empty string.
    /// </summary>
    private const string DefaultEncodingName = "LATIN1";
    private const string DefaultRecordFormatName = "DELIMITED";

    /// <summary>
    /// <see cref="ParseEncoding"/>'s inverse — every name it emits parses back
    /// to the same value, so a sample's reported settings can be handed
    /// straight to DecodeRecords. Asserted by test.
    /// </summary>
    private static string NameOf(CharacterEncoding encoding)
    {
        switch (encoding)
        {
            case CharacterEncoding.Latin1: return DefaultEncodingName;
            case CharacterEncoding.Ebcdic037: return "EBCDIC";
            default: throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Unmapped encoding.");
        }
    }

    /// <summary><see cref="ParseRecordFormat"/>'s inverse. Asserted by test.</summary>
    private static string NameOf(RecordFormat format)
    {
        switch (format)
        {
            case RecordFormat.NewlineDelimited: return DefaultRecordFormatName;
            case RecordFormat.FixedLength: return "FIXED";
            default: throw new ArgumentOutOfRangeException(nameof(format), format, "Unmapped record format.");
        }
    }

    /// <summary>
    /// Maps the action surface's encoding name onto <see cref="CharacterEncoding"/>.
    /// Unknown names throw: this boundary turns exceptions into a false result
    /// plus errorMessage, so a typo surfaces as a named failure rather than a
    /// silent ASCII decode of an EBCDIC file.
    /// </summary>
    private static CharacterEncoding ParseEncoding(string name)
    {
        switch ((name ?? "").Trim().ToUpperInvariant())
        {
            case "":
            case "ASCII":
            case "LATIN1":
                return CharacterEncoding.Latin1;

            case "EBCDIC":
            case "CP037":
            case "EBCDIC037":
                return CharacterEncoding.Ebcdic037;

            default:
                throw new ArgumentException(
                    $"Unknown text encoding '{name}'. Use '' or 'LATIN1'/'ASCII' for ASCII text, " +
                    "or 'EBCDIC' (cp037) for a mainframe extract.");
        }
    }

    // ---- Action 4: GetSampleCopybook ----

    /// <summary>
    /// Loads a bundled copybook and its data by id, assuming the data uses the
    /// default ASCII, newline-delimited settings.
    ///
    /// Fails for a sample whose data doesn't — it has no way to report the
    /// settings, and handing back bytes that decode to garbage under the
    /// defaults, unmarked, is the silent wrong answer this project exists to
    /// avoid. Use the overload with the TextEncoding/RecordFormat outputs;
    /// it works for every sample.
    /// </summary>
    public bool GetSampleCopybook(
        string sampleId,
        out string copybookSource,
        out string sampleDataText,
        out string errorMessage)
    {
        if (!GetSampleCopybook(
                sampleId, out copybookSource, out sampleDataText,
                out var textEncoding, out var recordFormat, out errorMessage))
            return false;

        if (textEncoding == DefaultEncodingName && recordFormat == DefaultRecordFormatName)
            return true;

        copybookSource = "";
        sampleDataText = "";
        errorMessage =
            $"Sample '{sampleId}' needs TextEncoding='{textEncoding}' and RecordFormat='{recordFormat}' " +
            "to decode correctly, which this action cannot report. Use the GetSampleCopybook overload " +
            "that also returns TextEncoding and RecordFormat, and pass them to DecodeRecords.";
        return false;
    }

    /// <summary>
    /// Loads a bundled copybook and its data by id, along with the
    /// <paramref name="textEncoding"/> and <paramref name="recordFormat"/> that
    /// data must be decoded with — pass both straight to
    /// <see cref="DecodeRecords(string,string,string,string,out string,out string)"/>.
    /// They're outputs rather than something the caller guesses because neither
    /// is detectable from the bytes.
    ///
    /// Every sample currently bundled has data; if one ever ships without,
    /// <paramref name="sampleDataText"/> comes back empty and the action still
    /// returns true — absent data is not a failure.
    /// </summary>
    public bool GetSampleCopybook(
        string sampleId,
        out string copybookSource,
        out string sampleDataText,
        out string textEncoding,
        out string recordFormat,
        out string errorMessage)
    {
        copybookSource = "";
        sampleDataText = "";
        textEncoding = DefaultEncodingName;
        recordFormat = DefaultRecordFormatName;
        errorMessage = "";

        try
        {
            if (SampleLibrary.TryGet(
                    sampleId ?? "", out copybookSource, out sampleDataText,
                    out var encoding, out var format))
            {
                textEncoding = NameOf(encoding);
                recordFormat = NameOf(format);
                return true;
            }

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
                    TextEncoding = NameOf(s.TextEncoding),
                    RecordFormat = NameOf(s.RecordFormat),
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

        /// <summary>
        /// The settings this sample's data needs, in the exact vocabulary
        /// DecodeRecords accepts — so an app can pass them through untouched
        /// rather than mapping or guessing.
        /// </summary>
        [JsonPropertyName("textEncoding")] public string TextEncoding { get; set; } = "";
        [JsonPropertyName("recordFormat")] public string RecordFormat { get; set; } = "";
    }
}
