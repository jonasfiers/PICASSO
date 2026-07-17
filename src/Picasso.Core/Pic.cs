using System;
using System.Collections.Generic;
using Picasso.Core.Models;

namespace Picasso.Core;

/// <summary>
/// Parses a PIC/PICTURE picture-string in isolation (e.g. "9(7)V99",
/// "S9(9)V99", "X(30)", "--,---,--9.99"). SIGN/COMP-3 clauses live outside
/// the picture string in COBOL and are folded in separately by CopybookParser.
/// </summary>
public static class Pic
{
    /// <summary>
    /// Single-character picture symbols this parser recognizes. Anything
    /// outside this set (and the two-character CR/DB tokens handled separately)
    /// is a genuinely-unknown character and is rejected loudly — never guessed
    /// at a width. <c>S</c> is deliberately NOT here: it is legal only as the
    /// leftmost picture character, so a leading S is stripped before tokenizing
    /// and any other S is a genuinely-invalid character that must reject.
    /// </summary>
    private const string KnownSymbols = "9XAVZ*.,/B0+-$P";

    /// <summary>
    /// Edit symbols — the presence of any one flips a picture to the Edited
    /// category. Everything here contributes to display width; <c>P</c> is a
    /// scaling position (width 0) but still marks the picture as edited, so it
    /// takes the width-passthrough path rather than the value-decoding path.
    /// The two-char CR/DB tokens are edit symbols too, handled separately.
    /// </summary>
    private const string EditSymbols = "Z*.,/B0+-$P";

    /// <summary>
    /// One tokenized picture element: a symbol (a single char like "9"/"Z"/"$",
    /// or a two-char "CR"/"DB") and its repeat count. A parenthesized
    /// <c>(n)</c> applies to the immediately-preceding symbol; a bare symbol
    /// has count 1. Floating strings (e.g. <c>----9</c>, <c>$$$,$$9</c>) are
    /// just repeated single-count symbols.
    /// </summary>
    private readonly struct PicToken
    {
        public PicToken(string symbol, int count)
        {
            Symbol = symbol;
            Count = count;
        }

        public string Symbol { get; }
        public int Count { get; }
    }

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

        var tokens = Tokenize(text, picture);
        if (tokens.Count == 0)
            throw new FormatException($"Unrecognized PIC clause: \"{picture}\".");

        // A picture is "edited" if, after stripping a leading S, it carries any
        // symbol beyond the bare 9 X A V set (Z * . , / B 0 + - $, CR/DB, or the
        // P scaling position). Such a picture is not value-interpreted: PICASSO
        // computes only its display width and surfaces it as Text passthrough.
        var edited = false;
        foreach (var t in tokens)
        {
            if (IsEditSymbol(t.Symbol)) { edited = true; break; }
        }

        if (edited)
        {
            var width = 0;
            foreach (var t in tokens)
                width += WidthOf(t.Symbol) * t.Count;

            return new PicSpec
            {
                Category = PicCategory.Edited,
                Length = width,
                // Recorded for completeness (a leading S may precede an edited
                // picture); the value is never decoded, so it is otherwise unused.
                Signed = signed,
            };
        }

        // Non-edited: only 9 X A V (plus the already-stripped leading S). The
        // original numeric / alphanumeric interpretation, unchanged in behaviour.
        var isAlphanumeric = false;
        var alphaLength = 0;
        var digitsBeforeV = 0;
        var digitsAfterV = 0;
        var seenV = false;

        foreach (var t in tokens)
        {
            switch (t.Symbol)
            {
                case "9":
                    if (seenV) digitsAfterV += t.Count; else digitsBeforeV += t.Count;
                    break;
                case "V":
                    if (seenV)
                        throw new FormatException($"PIC clause has more than one V: \"{picture}\".");
                    seenV = true;
                    break;
                case "X":
                case "A":
                    isAlphanumeric = true;
                    alphaLength += t.Count;
                    break;
                // No other symbol reaches here: an edited picture returned above,
                // and a non-leading S is rejected as unrecognized during tokenizing,
                // so only 9 / X / A / V remain.
            }
        }

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

    /// <summary>
    /// Splits a picture body into symbol+count tokens. Two-character CR/DB
    /// tokens are matched as a unit before single characters, so a trailing
    /// <c>B</c> is never mistaken for the blank-insertion symbol and <c>C/D/R</c>
    /// are never treated as standalone symbols. A genuinely-unknown character
    /// throws the same "Unrecognized character in PIC clause" error as before —
    /// the fail-loud guarantee holds; only the known edit symbols are added.
    /// </summary>
    private static List<PicToken> Tokenize(string text, string original)
    {
        var tokens = new List<PicToken>();
        var i = 0;

        while (i < text.Length)
        {
            string symbol;

            // CR (credit) and DB (debit): two-char tokens, valid only at the end
            // of a real picture, but for width purposes just counted as 2 wide.
            // Matched before the single-char branch so DB is read as the debit
            // token, not D + B (a lone B is the blank-insertion symbol).
            if (i + 1 < text.Length &&
                ((text[i] == 'C' && text[i + 1] == 'R') || (text[i] == 'D' && text[i + 1] == 'B')))
            {
                symbol = text.Substring(i, 2);
                i += 2;
            }
            else if (KnownSymbols.IndexOf(text[i]) >= 0)
            {
                symbol = text[i].ToString();
                i += 1;
            }
            else
            {
                throw new FormatException($"Unrecognized character in PIC clause: \"{original}\".");
            }

            var count = 1;
            if (i < text.Length && text[i] == '(')
            {
                var close = text.IndexOf(')', i);
                if (close < 0)
                    throw new FormatException($"Unclosed '(' in PIC clause: \"{original}\".");

                var inner = text.Substring(i + 1, close - i - 1);
                if (!int.TryParse(inner, out count) || count < 1)
                    throw new FormatException(
                        $"Invalid repeat count \"({inner})\" in PIC clause: \"{original}\".");

                i = close + 1;
            }

            tokens.Add(new PicToken(symbol, count));
        }

        return tokens;
    }

    /// <summary>
    /// True for any symbol that makes a picture "edited" — i.e. anything beyond
    /// the bare numeric/alphanumeric set 9 X A V (S being the sign indicator,
    /// not an edit symbol). Presence of even one flips the whole picture to the
    /// Edited category.
    /// </summary>
    private static bool IsEditSymbol(string symbol)
    {
        if (symbol == "CR" || symbol == "DB")
            return true;
        return symbol.Length == 1 && EditSymbols.IndexOf(symbol[0]) >= 0;
    }

    /// <summary>
    /// Display width (character positions = bytes) contributed by ONE occurrence
    /// of a symbol. V (implied decimal point) and P (decimal scaling position)
    /// occupy no character position; CR/DB occupy two; every other symbol occupies
    /// one. (A leading S is stripped before tokenizing and never reaches here.)
    /// Multiplied by the token's repeat count by the caller.
    /// </summary>
    private static int WidthOf(string symbol)
    {
        if (symbol == "CR" || symbol == "DB")
            return 2;

        switch (symbol[0])
        {
            case 'V':
            case 'P':
                return 0;
            default:
                // 9 X A Z * . , / B 0 + - $
                return 1;
        }
    }
}
