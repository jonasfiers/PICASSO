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
        CharacterEncoding encoding = CharacterEncoding.Latin1)
    {
        var records = new List<Dictionary<string, object>>();
        var expectedLength = spec.Sum(f => f.Len);
        foreach (var line in SplitLines(text))
        {
            if (line.Length != expectedLength)
                throw new FormatException(
                    $"Record is {line.Length} bytes, expected {expectedLength} bytes for this layout.");

            var record = new Dictionary<string, object>();
            foreach (var field in spec)
                record[field.Name] = DecodeField(line.Substring(field.Start, field.Len), field, encoding);
            records.Add(record);
        }
        return records;
    }

    public static string Encode(
        IReadOnlyList<FieldSpec> spec,
        IEnumerable<Dictionary<string, object>> records,
        CharacterEncoding encoding = CharacterEncoding.Latin1)
    {
        var lines = new List<string>();
        foreach (var record in records)
        {
            var sb = new StringBuilder();
            foreach (var field in spec)
            {
                if (!record.TryGetValue(field.Name, out var value))
                    throw new FormatException($"Record is missing field '{field.Name}'.");
                sb.Append(EncodeField(value, field, encoding));
            }
            lines.Add(sb.ToString());
        }
        return lines.Count == 0 ? "" : string.Join("\n", lines) + "\n";
    }

    /// <summary>Text bytes -> readable text. COMP-3 never goes through here.</summary>
    private static string DecodeText(string raw, CharacterEncoding encoding) =>
        encoding == CharacterEncoding.Ebcdic037 ? Ebcdic.Decode(raw) : raw;

    /// <summary>Readable text -> text bytes. COMP-3 never goes through here.</summary>
    private static string EncodeText(string text, CharacterEncoding encoding) =>
        encoding == CharacterEncoding.Ebcdic037 ? Ebcdic.Encode(text) : text;

    private static IEnumerable<string> SplitLines(string text)
    {
        return text.Replace("\r\n", "\n").Split('\n').Where(line => line.Length > 0);
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
            return digitsText;

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
