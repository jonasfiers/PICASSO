using System;
using System.Text.RegularExpressions;
using Picasso.Core.Models;

namespace Picasso.Core;

/// <summary>
/// Parses a PIC/PICTURE picture-string in isolation (e.g. "9(7)V99",
/// "S9(9)V99", "X(30)"). SIGN/COMP-3 clauses live outside the picture
/// string in COBOL and are folded in separately by CopybookParser.
/// </summary>
public static class Pic
{
    private static readonly Regex SymbolRun = new Regex(@"([9XAV])(?:\((\d+)\))?", RegexOptions.Compiled);

    public static PicSpec ParsePicClause(string picture)
    {
        if (string.IsNullOrWhiteSpace(picture))
            throw new FormatException("PIC clause is empty.");

        var text = picture.Trim().ToUpperInvariant();

        var signed = false;
        if (text.StartsWith("S", StringComparison.Ordinal))
        {
            signed = true;
            text = text.Substring(1);
        }

        var matches = SymbolRun.Matches(text);
        if (matches.Count == 0)
            throw new FormatException($"Unrecognized PIC clause: \"{picture}\".");

        var consumed = 0;
        var isAlphanumeric = false;
        var alphaLength = 0;
        var digitsBeforeV = 0;
        var digitsAfterV = 0;
        var seenV = false;

        foreach (Match m in matches)
        {
            if (m.Index != consumed)
                throw new FormatException($"Unrecognized character in PIC clause: \"{picture}\".");
            consumed = m.Index + m.Length;

            var symbol = m.Groups[1].Value[0];
            var count = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;

            switch (symbol)
            {
                case '9':
                    if (seenV) digitsAfterV += count; else digitsBeforeV += count;
                    break;
                case 'V':
                    if (seenV)
                        throw new FormatException($"PIC clause has more than one V: \"{picture}\".");
                    seenV = true;
                    break;
                case 'X':
                case 'A':
                    isAlphanumeric = true;
                    alphaLength += count;
                    break;
            }
        }

        if (consumed != text.Length)
            throw new FormatException($"Unrecognized trailing characters in PIC clause: \"{picture}\".");

        if (isAlphanumeric)
        {
            if (digitsBeforeV > 0 || digitsAfterV > 0 || seenV || signed)
                throw new FormatException($"PIC clause mixes alphanumeric and numeric symbols: \"{picture}\".");

            return new PicSpec
            {
                Category = PicCategory.Alphanumeric,
                Length = alphaLength,
            };
        }

        return new PicSpec
        {
            Category = PicCategory.Numeric,
            Digits = digitsBeforeV + digitsAfterV,
            Scale = digitsAfterV,
            Signed = signed,
        };
    }
}
