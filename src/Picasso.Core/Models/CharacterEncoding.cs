namespace Picasso.Core.Models;

/// <summary>
/// How a flat file encodes the bytes of its *text* fields — PIC X and the
/// digits of DISPLAY numerics. COMP-3 fields are unaffected by this and are
/// never translated: packed decimal nibbles are not a character encoding, and
/// running them through a code page table is precisely what corrupts them.
/// </summary>
public enum CharacterEncoding
{
    /// <summary>
    /// One char per raw byte, no translation — ASCII and its ISO-8859-1
    /// superset. The default, and what every non-mainframe extract here uses.
    /// </summary>
    Latin1 = 0,

    /// <summary>
    /// EBCDIC code page 037 (US/Canada). See <see cref="Picasso.Core.Ebcdic"/>
    /// for why the code page is named explicitly rather than just "EBCDIC".
    /// </summary>
    Ebcdic037 = 1,
}
