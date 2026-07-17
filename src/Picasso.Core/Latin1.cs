using System;

namespace Picasso.Core;

/// <summary>
/// One byte per char, and reversibly so: Unicode's first 256 codepoints were
/// defined to mirror ISO-8859-1 byte values exactly, so every one of the 256
/// possible byte values round-trips through this without loss. That's not
/// true of UTF-8 — a COMP-3 or EBCDIC byte is effectively a random value from
/// this codec's perspective, and plenty of those aren't valid on their own in
/// UTF-8, so converting through it doesn't error so much as quietly corrupt
/// the bytes. This is a lossless carrier, not a way to read text.
///
/// Spelled out by hand because Encoding.Latin1 doesn't exist on
/// netstandard2.0.
/// </summary>
public static class Latin1
{
    public static string ToText(byte[] bytes)
    {
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
            chars[i] = (char)bytes[i];
        return new string(chars);
    }

    /// <summary>
    /// The inverse. Throws for a character above U+00FF rather than
    /// truncating it to a byte: that can only happen if something upstream
    /// handed this real Unicode text instead of the byte-exact string this
    /// codec expects, and silently mangling it is the wrong failure mode.
    /// </summary>
    public static byte[] ToBytes(string text)
    {
        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c > 0xFF)
                throw new ArgumentException(
                    $"Character U+{(int)c:X4} at position {i} is outside Latin-1's range (0-255) " +
                    "and cannot be converted back to a single byte.");
            bytes[i] = (byte)c;
        }
        return bytes;
    }
}
