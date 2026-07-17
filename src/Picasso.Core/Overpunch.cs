using System;

namespace Picasso.Core;

/// <summary>
/// IBM zoned-decimal (overpunch) signed DISPLAY encoding, expressed at the
/// character level. A <c>PIC S9(n)</c> field WITHOUT <c>SIGN IS ... SEPARATE</c>
/// carries its sign in the zone nibble of one digit — the trailing digit by
/// default, the leading digit under <c>SIGN IS LEADING</c> — and occupies no
/// extra byte. This is the standard IBM overpunch table used by GnuCOBOL,
/// JRecord, and Micro Focus.
///
/// Working at the character level (not the byte level) is what lets a single
/// implementation serve both encodings: the codec translates bytes to chars
/// (per-field <see cref="Models.CharacterEncoding"/>) before numeric decode
/// and chars to bytes after numeric encode, and cp037 maps the relevant bytes
/// to exactly these characters — 0xC1 -> 'A', 0xD1 -> 'J', 0xF1 -> '1', and so
/// on — while Latin-1 stores the letters directly. So the overpunch table is
/// identical in both, and Comp3 (its own sign nibble) is never involved here.
///
/// Three input forms are accepted on decode:
///   positive preferred (zone C): '{' = 0, 'A'..'I' = 1..9
///   negative           (zone D): '}' = 0, 'J'..'R' = 1..9
///   positive alternate (zone F): the plain digits '0'..'9'
/// Encode always emits the PREFERRED (zone C / zone D) form — see the codec's
/// round-trip note about the zone-F alternate.
/// </summary>
internal static class Overpunch
{
    // Indexed by digit value 0..9.
    private const string PositivePreferred = "{ABCDEFGHI";   // zone C
    private const string Negative = "}JKLMNOPQR";            // zone D

    /// <summary>
    /// Decodes a sign-carrying character into its plain digit ('0'..'9') and the
    /// field's sign. Accepts positive-preferred, negative, and positive-alternate
    /// (plain digit) forms; anything else is rejected loudly rather than guessed.
    /// </summary>
    public static (char Digit, bool Negative) Decode(char c)
    {
        var i = PositivePreferred.IndexOf(c);
        if (i >= 0) return ((char)('0' + i), false);

        i = Negative.IndexOf(c);
        if (i >= 0) return ((char)('0' + i), true);

        if (c >= '0' && c <= '9') return (c, false);   // zone-F alternate positive

        throw new FormatException(
            $"Character '{c}' (U+{(int)c:X4}) is not a valid overpunched sign digit " +
            "({A-I for positive, }J-R for negative, or 0-9 for alternate positive).");
    }

    /// <summary>
    /// Encodes a plain digit ('0'..'9') and sign into the PREFERRED overpunched
    /// character: zone C ('{','A'..'I') when positive, zone D ('}','J'..'R') when
    /// negative.
    /// </summary>
    public static char Encode(char digit, bool negative)
    {
        if (digit < '0' || digit > '9')
            throw new FormatException($"'{digit}' (U+{(int)digit:X4}) is not a digit that can be overpunched.");

        var d = digit - '0';
        return negative ? Negative[d] : PositivePreferred[d];
    }
}
