using Picasso.Core;
using Picasso.Core.Models;
using Xunit;

namespace Picasso.Core.Tests;

public class PicTests
{
    [Fact]
    public void ParsesSimpleUnsignedInteger()
    {
        var spec = Pic.ParsePicClause("9(10)");
        Assert.Equal(PicCategory.Numeric, spec.Category);
        Assert.Equal(10, spec.Digits);
        Assert.Equal(0, spec.Scale);
        Assert.False(spec.Signed);
    }

    [Fact]
    public void ParsesRepeatedDigitsWithoutParentheses()
    {
        var spec = Pic.ParsePicClause("999999");
        Assert.Equal(PicCategory.Numeric, spec.Category);
        Assert.Equal(6, spec.Digits);
        Assert.Equal(0, spec.Scale);
    }

    [Fact]
    public void ParsesAlphanumeric()
    {
        var spec = Pic.ParsePicClause("X(30)");
        Assert.Equal(PicCategory.Alphanumeric, spec.Category);
        Assert.Equal(30, spec.Length);
    }

    [Fact]
    public void ParsesImpliedDecimal()
    {
        var spec = Pic.ParsePicClause("9(7)V99");
        Assert.Equal(PicCategory.Numeric, spec.Category);
        Assert.Equal(9, spec.Digits);
        Assert.Equal(2, spec.Scale);
        Assert.False(spec.Signed);
    }

    [Fact]
    public void ParsesSignedImpliedDecimal()
    {
        var spec = Pic.ParsePicClause("S9(7)V99");
        Assert.Equal(9, spec.Digits);
        Assert.Equal(2, spec.Scale);
        Assert.True(spec.Signed);
    }

    [Fact]
    public void ParsesSignedLargeDecimal()
    {
        var spec = Pic.ParsePicClause("S9(9)V99");
        Assert.Equal(11, spec.Digits);
        Assert.Equal(2, spec.Scale);
        Assert.True(spec.Signed);
    }

    [Fact]
    public void RejectsEmptyPicture()
    {
        Assert.Throws<System.FormatException>(() => Pic.ParsePicClause(""));
    }

    [Fact]
    public void RejectsMixedAlphanumericAndNumericSymbols()
    {
        Assert.Throws<System.FormatException>(() => Pic.ParsePicClause("9(3)X(2)"));
    }

    [Fact]
    public void RejectsUnrecognizedCharacters()
    {
        // '@' is not a picture symbol at all — still rejected loudly, even now
        // that the edit symbols (Z $ , . - + …) are recognized.
        Assert.Throws<System.FormatException>(() => Pic.ParsePicClause("9@9"));
    }
}
