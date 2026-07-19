using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Picasso.Core.Models;

namespace Picasso.Core;

/// <summary>
/// Decodes/encodes fixed-width flat-file text against a flat
/// <see cref="FieldSpec"/> list — the generic, spec-driven codec that
/// CATALOG-74's flatfile.js already proved works, minus the two things
/// that codec had to hand-wave: signed numerics (via a bolted-on
/// signedNetBalance() helper) and COMP-3.
///
/// Byte contract: every string here is treated as one .NET char per raw
/// byte (ISO-8859-1/Latin-1 semantics), not UTF-8. That's what makes
/// COMP-3's packed binary bytes round-trip safely through a string —
/// callers must read/write the underlying files using Latin-1, not the
/// platform default encoding.
///
/// That byte contract is also why <see cref="CharacterEncoding"/> is applied
/// per field rather than to the record as a whole. Translating a whole
/// EBCDIC record to ASCII up front and then running this codec over it would
/// corrupt every COMP-3 field, because packed nibbles aren't characters —
/// the same mistake as transferring a mainframe extract in FTP ASCII mode.
/// Only Text and NumericDisplay bytes are translated; Comp3 bytes are passed
/// through untouched.
/// </summary>
public static class FlatFileCodec
{
    public static List<Dictionary<string, object>> Decode(
        IReadOnlyList<FieldSpec> spec,
        string text,
        CharacterEncoding encoding = CharacterEncoding.Latin1,
        RecordFormat format = RecordFormat.NewlineDelimited)
    {
        var records = new List<Dictionary<string, object>>();
        var expectedLength = RecordLength(spec);
        foreach (var line in SplitRecords(text, expectedLength, format))
        {
            if (line.Length != expectedLength)
                throw new FormatException(
                    $"Record is {line.Length} bytes, expected {expectedLength} bytes for this layout.");

            records.Add(DecodeRecordFields(spec, line, encoding));
        }
        return records;
    }

    public static string Encode(
        IReadOnlyList<FieldSpec> spec,
        IEnumerable<Dictionary<string, object>> records,
        CharacterEncoding encoding = CharacterEncoding.Latin1,
        RecordFormat format = RecordFormat.NewlineDelimited)
    {
        // Encoding a layout whose fields overlap (a REDEFINES overlay) is
        // ambiguous by construction: two names describe the same bytes, so
        // writing one field would silently clobber the other's contribution.
        // That is exactly the silent corruption this project forbids, so reject
        // the whole encode loudly and up front (the overlap is a property of the
        // layout, not of any one record). DECODE has no such problem — each
        // overlapping field is an independent reading of the same bytes — so it
        // stays fully supported.
        RejectOverlappingLayout(spec);

        var lines = new List<string>();
        foreach (var record in records)
            lines.Add(EncodeRecordFields(spec, record, encoding));

        return JoinRecords(lines, format);
    }

    // ---- Variable-length (OCCURS ... DEPENDING ON / ODO) overloads ----
    //
    // A variable-length copybook cannot be decoded/encoded against one static
    // layout: each record's length is fixed by the runtime value of a depending
    // field read out of that record. These overloads take the whole
    // ParsedCopybook so they can see its Odo descriptor and build the concrete
    // layout per record. A fixed (non-ODO) copybook routes straight through the
    // existing flat-spec path above — the non-ODO behaviour is byte-for-byte
    // unchanged.

    /// <summary>
    /// Decodes fixed-width text against a parsed copybook, handling both fixed
    /// and variable-length (ODO) layouts. For an ODO copybook each record's
    /// occurrence count is read from its depending field, validated against the
    /// OCCURS bounds (out-of-range fails loudly), and the record decoded against
    /// the layout that count produces.
    /// </summary>
    public static List<Dictionary<string, object>> Decode(
        ParsedCopybook parsed,
        string text,
        CharacterEncoding encoding = CharacterEncoding.Latin1,
        RecordFormat format = RecordFormat.NewlineDelimited)
    {
        if (!parsed.IsVariableLength)
            return Decode(parsed.Flat, text, encoding, format);

        return format == RecordFormat.FixedLength
            ? DecodeVariableFixedLength(parsed, text, encoding)
            : DecodeVariableDelimited(parsed, text, encoding);
    }

    /// <summary>
    /// Encodes records against a parsed copybook, handling both fixed and
    /// variable-length (ODO) layouts. For an ODO copybook each record's
    /// occurrence count is taken from its depending field value, validated
    /// against the OCCURS bounds, and the record encoded against the layout that
    /// count produces. A record carrying more table entries than the count admits
    /// (a TAB(count+1) key) is rejected rather than silently truncated.
    /// </summary>
    public static string Encode(
        ParsedCopybook parsed,
        IEnumerable<Dictionary<string, object>> records,
        CharacterEncoding encoding = CharacterEncoding.Latin1,
        RecordFormat format = RecordFormat.NewlineDelimited)
    {
        if (!parsed.IsVariableLength)
            return Encode(parsed.Flat, records, encoding, format);

        var odo = parsed.Odo!;
        var lines = new List<string>();
        foreach (var record in records)
        {
            if (!record.TryGetValue(odo.DependsOn, out var depValue))
                throw new FormatException(
                    $"Record is missing the depending field '{odo.DependsOn}', which sets the occurrence " +
                    $"count for the variable-length table '{odo.TableName}'.");

            var count = ToCount(depValue, odo.DependsOn);
            ValidateCount(count, odo);
            RejectOverProvidedEntries(record, odo, count);

            var spec = CopybookParser.BuildConcreteLayout(parsed, count);
            lines.Add(EncodeRecordFields(spec, record, encoding));
        }

        return JoinRecords(lines, format);
    }

    private static List<Dictionary<string, object>> DecodeVariableDelimited(
        ParsedCopybook parsed, string text, CharacterEncoding encoding)
    {
        var odo = parsed.Odo!;
        var records = new List<Dictionary<string, object>>();

        foreach (var line in text.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0))
        {
            var count = ReadCountFromRecord(line, offset: 0, odo, encoding);
            ValidateCount(count, odo);

            var spec = CopybookParser.BuildConcreteLayout(parsed, count);
            var expected = spec.Sum(f => f.Len);
            if (line.Length != expected)
                throw new FormatException(
                    $"Variable-length record with {odo.DependsOn}={count} is {line.Length} bytes, expected " +
                    $"{expected} bytes for that occurrence count.");

            records.Add(DecodeRecordFields(spec, line, encoding));
        }
        return records;
    }

    private static List<Dictionary<string, object>> DecodeVariableFixedLength(
        ParsedCopybook parsed, string text, CharacterEncoding encoding)
    {
        var odo = parsed.Odo!;
        var records = new List<Dictionary<string, object>>();

        // Walk the undelimited stream: each record's length is only known once its
        // depending field has been read, so records are sliced one at a time,
        // advancing by the length the count implies. A record that would run past
        // the end of the file, or a partial record left over, fails loudly.
        var pos = 0;
        while (pos < text.Length)
        {
            var depEnd = pos + odo.DependingField.Start + odo.DependingField.Len;
            if (depEnd > text.Length)
                throw new FormatException(
                    $"Undelimited file ends mid-record: only {text.Length - pos} bytes remain at offset {pos}, " +
                    $"too few to read the depending field '{odo.DependsOn}'.");

            var count = ReadCountFromRecord(text, pos, odo, encoding);
            ValidateCount(count, odo);

            var spec = CopybookParser.BuildConcreteLayout(parsed, count);
            var recordLen = spec.Sum(f => f.Len);
            if (pos + recordLen > text.Length)
                throw new FormatException(
                    $"Undelimited record at offset {pos} with {odo.DependsOn}={count} needs {recordLen} bytes " +
                    $"but only {text.Length - pos} remain. Either the layout doesn't match this file or a " +
                    "record is truncated.");

            records.Add(DecodeRecordFields(spec, text.Substring(pos, recordLen), encoding));
            pos += recordLen;
        }
        return records;
    }

    /// <summary>
    /// Reads the depending field out of a record slice and returns its value as
    /// an occurrence count. <paramref name="offset"/> is the record's start in
    /// <paramref name="text"/>; the depending field's own Start is relative to it.
    /// </summary>
    private static int ReadCountFromRecord(string text, int offset, OdoInfo odo, CharacterEncoding encoding)
    {
        var dep = odo.DependingField;
        if (offset + dep.Start + dep.Len > text.Length)
            throw new FormatException(
                $"Record starting at offset {offset} is too short to contain its depending field " +
                $"'{odo.DependsOn}' (needs {dep.Start + dep.Len} bytes, has {text.Length - offset}). " +
                "The layout does not match this file.");

        var raw = text.Substring(offset + dep.Start, dep.Len);
        var value = DecodeField(raw, dep, encoding);
        return ToCount(value, odo.DependsOn);
    }

    private static int ToCount(object value, string fieldName)
    {
        var d = Convert.ToDecimal(value);
        if (d != Math.Truncate(d))
            throw new FormatException(
                $"Depending field '{fieldName}' has a fractional value ({d}); an occurrence count must be a " +
                "whole number.");
        return (int)d;
    }

    private static void ValidateCount(int count, OdoInfo odo)
    {
        if (count < odo.Min || count > odo.Max)
            throw new FormatException(
                $"Occurrence count {count} from depending field '{odo.DependsOn}' is outside the OCCURS bounds " +
                $"{odo.Min} TO {odo.Max} for table '{odo.TableName}'. A count outside the declared range means " +
                "the record cannot be laid out — refusing rather than guessing a length.");
    }

    /// <summary>
    /// Rejects a record that carries table entries beyond the occurrence count —
    /// e.g. a TAB(4) key when the count is 3. The concrete layout at that count
    /// would silently ignore the extra entry, dropping data; fail loudly instead.
    /// </summary>
    private static void RejectOverProvidedEntries(
        Dictionary<string, object> record, OdoInfo odo, int count)
    {
        var open = odo.TableName + "(";
        foreach (var key in record.Keys)
        {
            if (!key.StartsWith(open, StringComparison.Ordinal))
                continue;

            var close = key.IndexOf(')', open.Length);
            if (close < 0)
                continue;

            var indexText = key.Substring(open.Length, close - open.Length);
            if (int.TryParse(indexText, out var index) && index > count)
                throw new FormatException(
                    $"Record has table entry '{key}' but the depending field '{odo.DependsOn}' says only " +
                    $"{count} occurrence(s). Encoding at that count would silently drop the extra entry — " +
                    "make the count and the supplied entries agree.");
        }
    }

    private static Dictionary<string, object> DecodeRecordFields(
        IReadOnlyList<FieldSpec> spec, string line, CharacterEncoding encoding)
    {
        var record = new Dictionary<string, object>();
        foreach (var field in spec)
            record[field.Name] = DecodeField(line.Substring(field.Start, field.Len), field, encoding);
        return record;
    }

    private static string EncodeRecordFields(
        IReadOnlyList<FieldSpec> spec, Dictionary<string, object> record, CharacterEncoding encoding)
    {
        var sb = new StringBuilder();
        foreach (var field in spec)
        {
            if (!record.TryGetValue(field.Name, out var value))
                throw new FormatException($"Record is missing field '{field.Name}'.");
            sb.Append(EncodeField(value, field, encoding));
        }
        return sb.ToString();
    }

    private static string JoinRecords(List<string> lines, RecordFormat format)
    {
        if (format == RecordFormat.FixedLength)
            return string.Concat(lines);

        return lines.Count == 0 ? "" : string.Join("\n", lines) + "\n";
    }

    /// <summary>
    /// The byte length of one record for this layout: the farthest byte any field
    /// reaches, i.e. max(Start + Len). For an ordinary layout (fields tiling
    /// [0, total) with no gaps) this equals the plain sum of lengths, so nothing
    /// changes there. It differs only when a REDEFINES overlay makes fields share
    /// bytes — then summing lengths would over-count the shared bytes and invent a
    /// too-long record. Max-end is the correct, overlap-aware measure.
    /// </summary>
    private static int RecordLength(IReadOnlyList<FieldSpec> spec) =>
        spec.Count == 0 ? 0 : spec.Max(f => f.Start + f.Len);

    /// <summary>
    /// Fails loudly if any two fields in the layout claim overlapping bytes — the
    /// signature of a REDEFINES overlay. Called only on the ENCODE path: writing
    /// overlapping fields is ambiguous (last write silently wins, corrupting the
    /// other field's bytes), which this project refuses to do. The error names the
    /// specific overlapping pair and their byte ranges so the cause is unmistakable.
    /// </summary>
    private static void RejectOverlappingLayout(IReadOnlyList<FieldSpec> spec)
    {
        // Sort by start, then by end. Walking left to right, keep the field that
        // reaches farthest so far; the next field overlaps something iff it starts
        // before that farthest end. Zero-length fields can't overlap anything and
        // are skipped so they never falsely trip the check.
        var ordered = spec
            .Where(f => f.Len > 0)
            .OrderBy(f => f.Start)
            .ThenBy(f => f.Start + f.Len)
            .ToList();

        FieldSpec? farthest = null;
        foreach (var f in ordered)
        {
            if (farthest != null && f.Start < farthest.Start + farthest.Len)
                throw new FormatException(
                    $"Cannot encode a layout with overlapping fields: '{farthest.Name}' " +
                    $"(bytes {farthest.Start}-{farthest.Start + farthest.Len - 1}) and '{f.Name}' " +
                    $"(bytes {f.Start}-{f.Start + f.Len - 1}) share bytes — a REDEFINES overlay. " +
                    "Decoding such a layout is fine (each field is an independent reading of the same " +
                    "bytes), but encoding is ambiguous: writing one field would silently clobber the " +
                    "other. Encode from a layout without REDEFINES overlaps, or use decode only.");

            if (farthest == null || f.Start + f.Len > farthest.Start + farthest.Len)
                farthest = f;
        }
    }

    /// <summary>Text bytes -> readable text. COMP-3 never goes through here.</summary>
    private static string DecodeText(string raw, CharacterEncoding encoding) =>
        encoding == CharacterEncoding.Ebcdic037 ? Ebcdic.Decode(raw) : raw;

    /// <summary>Readable text -> text bytes. COMP-3 never goes through here.</summary>
    private static string EncodeText(string text, CharacterEncoding encoding) =>
        encoding == CharacterEncoding.Ebcdic037 ? Ebcdic.Encode(text) : text;

    private static IEnumerable<string> SplitRecords(string text, int recordLength, RecordFormat format)
    {
        if (format == RecordFormat.NewlineDelimited)
            return text.Replace("\r\n", "\n").Split('\n').Where(line => line.Length > 0);

        return SplitFixedLength(text, recordLength);
    }

    /// <summary>
    /// Slices a bare concatenation of fixed-length records.
    ///
    /// Deliberately does no newline normalization and drops no "empty" chunks:
    /// in an undelimited file 0x0D and 0x0A are data, not structure. That is
    /// not hypothetical — the real DTAR020.bin carries 21 0x0D bytes inside
    /// COMP-3 fields (0x0D is the digit 0 with a negative sign nibble), so the
    /// delimited path's Replace("\r\n", "\n") would silently eat a byte and
    /// shift every record after it, were one ever followed by an 0x0A.
    /// </summary>
    private static List<string> SplitFixedLength(string text, int recordLength)
    {
        if (recordLength <= 0)
            throw new FormatException("Layout has no bytes; cannot split a file by record length.");

        if (text.Length % recordLength != 0)
            throw new FormatException(
                $"File is {text.Length} bytes, which is not a whole number of {recordLength}-byte records " +
                $"({text.Length / recordLength} records plus {text.Length % recordLength} bytes left over). " +
                "Either the layout doesn't match this file, or its records are delimited rather than fixed-length.");

        var records = new List<string>(text.Length / recordLength);
        for (var offset = 0; offset < text.Length; offset += recordLength)
            records.Add(text.Substring(offset, recordLength));
        return records;
    }

    private static object DecodeField(string raw, FieldSpec field, CharacterEncoding encoding)
    {
        switch (field.Type)
        {
            case FieldType.Text:
                // Translate before trimming: an EBCDIC pad byte is 0x40, which
                // TrimEnd(' ') would not touch until it's been made a space.
                return DecodeText(raw, encoding).TrimEnd(' ');

            case FieldType.Comp3:
                // Deliberately not translated — see the class remarks.
                return Comp3.Decode(Latin1StringToBytes(raw), field.Digits, field.Scale, field.Signed);

            case FieldType.Binary:
                // Big-endian two's-complement integer bytes — like COMP-3, raw
                // binary that must never pass through an encoding table. Digits > 0
                // is a digit-derived COMP/COMP-4/BINARY field (width from the digit
                // count); Digits == 0 is a named binary usage (BINARY-CHAR/SHORT/
                // LONG/DOUBLE) whose width is authoritative in field.Len — routed
                // through the width-explicit decode.
                return field.Digits > 0
                    ? Binary.Decode(Latin1StringToBytes(raw), field.Digits, field.Scale, field.Signed)
                    : Binary.Decode(Latin1StringToBytes(raw), field.Scale, field.Signed);

            case FieldType.NumericDisplay:
                return DecodeNumericDisplay(DecodeText(raw, encoding), field);

            default:
                throw new NotSupportedException($"Unknown field type: {field.Type}");
        }
    }

    private static decimal DecodeNumericDisplay(string raw, FieldSpec field)
    {
        string digitsText;
        var negative = false;

        if (field.SignSeparate)
        {
            if (field.SignLeading)
            {
                negative = raw[0] == '-';
                digitsText = raw.Substring(1);
            }
            else
            {
                negative = raw[raw.Length - 1] == '-';
                digitsText = raw.Substring(0, raw.Length - 1);
            }
        }
        else if (field.Signed)
        {
            // Overpunched sign: the sign lives in the zone nibble of one digit —
            // the trailing digit by default, the leading digit under SIGN IS
            // LEADING. Recover the plain digit and the sign, then substitute the
            // plain digit back so the magnitude parses. See Overpunch.cs.
            var signIndex = field.SignLeading ? 0 : raw.Length - 1;
            var (digit, isNegative) = Overpunch.Decode(raw[signIndex]);
            negative = isNegative;
            var chars = raw.ToCharArray();
            chars[signIndex] = digit;
            digitsText = new string(chars);
        }
        else
        {
            digitsText = raw;
        }

        var magnitude = decimal.Parse(digitsText) / Pow10(field.Scale);
        return negative ? -magnitude : magnitude;
    }

    private static string EncodeField(object value, FieldSpec field, CharacterEncoding encoding)
    {
        switch (field.Type)
        {
            case FieldType.Text:
            {
                var s = Convert.ToString(value) ?? "";
                if (s.Length > field.Len) s = s.Substring(0, field.Len);
                return EncodeText(s.PadRight(field.Len, ' '), encoding);
            }

            case FieldType.Comp3:
            {
                // Deliberately not translated — see the class remarks.
                var d = Convert.ToDecimal(value);
                var bytes = Comp3.Encode(d, field.Digits, field.Scale, field.Signed);
                return BytesToLatin1String(bytes);
            }

            case FieldType.Binary:
            {
                // Deliberately not translated — big-endian two's-complement bytes.
                // Digits > 0: digit-derived COMP width. Digits == 0: a named binary
                // usage whose width is field.Len (width-explicit encode).
                var d = Convert.ToDecimal(value);
                var bytes = field.Digits > 0
                    ? Binary.Encode(d, field.Digits, field.Scale, field.Signed)
                    : Binary.EncodeWidth(d, field.Len, field.Scale, field.Signed);
                return BytesToLatin1String(bytes);
            }

            case FieldType.NumericDisplay:
                return EncodeText(EncodeNumericDisplay(Convert.ToDecimal(value), field), encoding);

            default:
                throw new NotSupportedException($"Unknown field type: {field.Type}");
        }
    }

    private static string EncodeNumericDisplay(decimal value, FieldSpec field)
    {
        var negative = value < 0;
        if (negative && !field.Signed)
            throw new ArgumentException($"Negative value for an unsigned field '{field.Name}'.");

        var scaled = Math.Truncate(Math.Abs(value) * Pow10(field.Scale));
        var digitsText = scaled.ToString("F0");
        if (digitsText.Length > field.Digits)
            throw new OverflowException($"Value has more than {field.Digits} digits of precision for field '{field.Name}'.");
        digitsText = digitsText.PadLeft(field.Digits, '0');

        if (!field.SignSeparate)
        {
            if (!field.Signed)
                return digitsText;

            // Overpunched sign: fold the sign into the zone nibble of the trailing
            // digit (default) or leading digit (SIGN IS LEADING), adding no byte.
            // Always the PREFERRED representation (zone C positive, zone D
            // negative). A source that stored a positive value in the alternate
            // zone-F form (plain '0'-'9') decodes correctly but re-encodes to the
            // zone-C letter here — same value, different byte. See Overpunch.cs.
            var signIndex = field.SignLeading ? 0 : digitsText.Length - 1;
            var chars = digitsText.ToCharArray();
            chars[signIndex] = Overpunch.Encode(chars[signIndex], negative);
            return new string(chars);
        }

        var signChar = negative ? '-' : '+';
        return field.SignLeading ? signChar + digitsText : digitsText + signChar;
    }

    private static decimal Pow10(int exponent)
    {
        decimal result = 1m;
        for (var i = 0; i < exponent; i++) result *= 10m;
        return result;
    }

    private static string BytesToLatin1String(byte[] bytes)
    {
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
        return new string(chars);
    }

    private static byte[] Latin1StringToBytes(string s)
    {
        var bytes = new byte[s.Length];
        for (var i = 0; i < s.Length; i++) bytes[i] = (byte)s[i];
        return bytes;
    }
}
