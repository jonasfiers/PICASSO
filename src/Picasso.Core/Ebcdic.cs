using System;
using System.Collections.Generic;

namespace Picasso.Core;

/// <summary>
/// EBCDIC code page 037 (US/Canada) &lt;-&gt; Unicode, for the text bytes of a
/// mainframe extract. Real mainframe files are routinely EBCDIC: a file
/// carrying COMP-3 has to be transferred in binary mode, because an
/// ASCII-mode transfer would run the packed-decimal bytes through a code
/// page table and destroy them — so it arrives with its text still EBCDIC.
///
/// Only cp037 is supported, and it's named cp037 rather than "EBCDIC"
/// deliberately: EBCDIC is a family (cp273 German, cp500 International,
/// cp1047 Open Systems, and more) that disagrees on punctuation placement.
/// Guessing which one a file uses would be the silent-wrong-answer failure
/// this project exists to avoid. cp037 is the variant DTAR020 is confirmed
/// to be, and the only one with a real file here to verify against.
///
/// The table is not hand-transcribed from a spec — it was generated from
/// .NET's own Encoding.GetEncoding(37) and is pinned by tests that check it
/// against the real DTAR020.bin plus the invariants below. It's inlined
/// rather than obtained via Encoding.GetEncoding at runtime because that
/// needs the System.Text.Encoding.CodePages package on .NET Core while
/// working natively on .NET Framework — this ships identically on both.
/// </summary>
public static class Ebcdic
{
    /// <summary>
    /// cp037 byte -> Unicode char, indexed by byte value. A bijection over all
    /// 256 bytes (asserted by test), so <see cref="Encode"/> is lossless and
    /// every byte round-trips.
    /// </summary>
    private const string Cp037ToUnicode =
        "\u0000\u0001\u0002\u0003\u009C\u0009\u0086\u007F\u0097\u008D\u008E\u000B\u000C\u000D\u000E\u000F" +   // 0x00-0x0F
        "\u0010\u0011\u0012\u0013\u009D\u0085\u0008\u0087\u0018\u0019\u0092\u008F\u001C\u001D\u001E\u001F" +   // 0x10-0x1F
        "\u0080\u0081\u0082\u0083\u0084\u000A\u0017\u001B\u0088\u0089\u008A\u008B\u008C\u0005\u0006\u0007" +   // 0x20-0x2F
        "\u0090\u0091\u0016\u0093\u0094\u0095\u0096\u0004\u0098\u0099\u009A\u009B\u0014\u0015\u009E\u001A" +   // 0x30-0x3F
        " \u00A0\u00E2\u00E4\u00E0\u00E1\u00E3\u00E5\u00E7\u00F1\u00A2.<(+|" +   // 0x40-0x4F
        "&\u00E9\u00EA\u00EB\u00E8\u00ED\u00EE\u00EF\u00EC\u00DF!$*);\u00AC" +   // 0x50-0x5F
        "-/\u00C2\u00C4\u00C0\u00C1\u00C3\u00C5\u00C7\u00D1\u00A6,%_>?" +   // 0x60-0x6F
        "\u00F8\u00C9\u00CA\u00CB\u00C8\u00CD\u00CE\u00CF\u00CC`:#@'=\"" +   // 0x70-0x7F
        "\u00D8abcdefghi\u00AB\u00BB\u00F0\u00FD\u00FE\u00B1" +   // 0x80-0x8F
        "\u00B0jklmnopqr\u00AA\u00BA\u00E6\u00B8\u00C6\u00A4" +   // 0x90-0x9F
        "\u00B5~stuvwxyz\u00A1\u00BF\u00D0\u00DD\u00DE\u00AE" +   // 0xA0-0xAF
        "^\u00A3\u00A5\u00B7\u00A9\u00A7\u00B6\u00BC\u00BD\u00BE[]\u00AF\u00A8\u00B4\u00D7" +   // 0xB0-0xBF
        "{ABCDEFGHI\u00AD\u00F4\u00F6\u00F2\u00F3\u00F5" +   // 0xC0-0xCF
        "}JKLMNOPQR\u00B9\u00FB\u00FC\u00F9\u00FA\u00FF" +   // 0xD0-0xDF
        "\\\u00F7STUVWXYZ\u00B2\u00D4\u00D6\u00D2\u00D3\u00D5" +   // 0xE0-0xEF
        "0123456789\u00B3\u00DB\u00DC\u00D9\u00DA\u009F";  // 0xF0-0xFF
    private static readonly Dictionary<char, byte> UnicodeToCp037 = BuildReverse();

    private static Dictionary<char, byte> BuildReverse()
    {
        var map = new Dictionary<char, byte>(256);
        for (var b = 0; b < 256; b++)
            map[Cp037ToUnicode[b]] = (byte)b;
        return map;
    }

    /// <summary>cp037 byte -> Unicode char.</summary>
    public static char ToChar(byte value) => Cp037ToUnicode[value];

    /// <summary>
    /// Unicode char -> cp037 byte. Throws rather than substituting '?' for a
    /// character cp037 can't represent: silently writing the wrong byte into a
    /// fixed-width record is worse than refusing.
    /// </summary>
    public static byte ToByte(char value)
    {
        if (!UnicodeToCp037.TryGetValue(value, out var b))
            throw new ArgumentException(
                $"Character '{value}' (U+{(int)value:X4}) has no EBCDIC cp037 representation.");
        return b;
    }

    /// <summary>
    /// Translates cp037 bytes to text. Input is one char per raw byte
    /// (Latin-1 semantics), matching the byte contract in FlatFileCodec.
    /// </summary>
    public static string Decode(string ebcdicBytes)
    {
        var chars = new char[ebcdicBytes.Length];
        for (var i = 0; i < ebcdicBytes.Length; i++)
            chars[i] = ToChar((byte)ebcdicBytes[i]);
        return new string(chars);
    }

    /// <summary>
    /// Translates text to cp037 bytes, returned as one char per raw byte.
    /// </summary>
    public static string Encode(string text)
    {
        var chars = new char[text.Length];
        for (var i = 0; i < text.Length; i++)
            chars[i] = (char)ToByte(text[i]);
        return new string(chars);
    }
}
