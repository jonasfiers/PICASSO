using System;
using System.Linq;
using Picasso.Core;
using Xunit;

namespace Picasso.Core.Tests;

public class Latin1Tests
{
    [Fact]
    public void RoundTripsEveryPossibleByteValue()
    {
        var allBytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var text = Latin1.ToText(allBytes);
        Assert.Equal(256, text.Length);
        Assert.Equal(allBytes, Latin1.ToBytes(text));
    }

    [Fact]
    public void RoundTripsRealComp3Bytes()
    {
        // 0x5C, for instance, is neither a valid standalone UTF-8 byte nor
        // printable ASCII -- exactly the kind of byte a packed-decimal field
        // produces routinely.
        var comp3Bytes = new byte[] { 0x00, 0x12, 0x34, 0x5C, 0xFF, 0x80 };
        Assert.Equal(comp3Bytes, Latin1.ToBytes(Latin1.ToText(comp3Bytes)));
    }

    [Fact]
    public void ToBytesRejectsCharactersOutsideLatin1Range()
    {
        var ex = Assert.Throws<ArgumentException>(() => Latin1.ToBytes("café €")); // € is U+20AC
        Assert.Contains("U+20AC", ex.Message);
    }
}
