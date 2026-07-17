namespace Picasso.Core.Models;

/// <summary>
/// How records are separated within a flat file — which is a different
/// question from how wide they are. The width always comes from the layout;
/// this says whether anything sits between one record and the next.
/// </summary>
public enum RecordFormat
{
    /// <summary>
    /// Records separated by "\n" or "\r\n". The default, and what every
    /// Unix-produced extract here uses (including GnuCOBOL's line-sequential
    /// output).
    /// </summary>
    NewlineDelimited = 0,

    /// <summary>
    /// No delimiters at all: the file is a bare concatenation of fixed-length
    /// records, sliced by the layout's own record length. This is the
    /// mainframe's normal shape (RECFM=F/FB) — newlines between records are a
    /// Unix convention, not a universal one.
    ///
    /// It is never auto-detected. A delimited file's length can divide evenly
    /// by the record length too (27 delimited 27-byte records are 756 bytes,
    /// and 756 / 27 = 28), so guessing would silently produce a plausible
    /// number of misaligned records.
    /// </summary>
    FixedLength = 1,
}
