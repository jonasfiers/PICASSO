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

    // ---- Edited pictures: exact display width, surfaced as Edited category ----

    /// <summary>
    /// Real pictures pulled from the copybook corpus (edited numerics are the
    /// #1 rejection there) plus per-symbol unit cases, each asserting the exact
    /// display WIDTH. A wrong width silently shifts every following field's
    /// offset — the precise silent-miscompute PICASSO exists to prevent — so
    /// these widths are hand-computed and pinned.
    /// </summary>
    [Theory]
    // Corpus pictures.
    [InlineData("ZZ9", 3)]
    [InlineData("-9(4)", 5)]
    [InlineData("+9(10)", 11)]
    [InlineData("----9", 5)]
    [InlineData("-(4)9", 5)]
    [InlineData("-(5)9", 6)]
    [InlineData("-(7)9", 8)]
    [InlineData("-(12)9", 13)]
    [InlineData("-ZZZ,ZZZ,ZZZ", 12)]
    [InlineData("--,---,---,---,---,--9", 22)]
    // Per-symbol: insertion characters each count 1.
    [InlineData("9.9", 3)]
    [InlineData("9,9", 3)]
    [InlineData("9/9", 3)]
    [InlineData("9B9", 3)]
    [InlineData("90", 2)]
    // CR / DB count 2; a lone B counts 1.
    [InlineData("9(5)CR", 7)]
    [InlineData("9(5)DB", 7)]
    // V (and P) count 0.
    [InlineData("ZZV99", 4)]
    [InlineData("ZZP99", 4)]
    // Floating strings and grouped counts.
    [InlineData("$$$,$$9", 7)]
    [InlineData("$$$,$$9.99", 10)]
    [InlineData("Z(6)", 6)]
    public void ComputesEditedDisplayWidth(string picture, int expectedWidth)
    {
        var spec = Pic.ParsePicClause(picture);
        Assert.Equal(PicCategory.Edited, spec.Category);
        Assert.Equal(expectedWidth, spec.Length);
    }

    [Fact]
    public void EditedPictureWithLeadingSignStillEdited()
    {
        // A leading S is stripped before edit-symbol detection; the picture is
        // still edited on the strength of its Z/. symbols, and S adds no width.
        var spec = Pic.ParsePicClause("SZZ9.99");
        Assert.Equal(PicCategory.Edited, spec.Category);
        Assert.Equal(6, spec.Length); // Z Z 9 . 9 9
    }

    [Fact]
    public void PlainDigitsAndTextStayNonEdited()
    {
        // Guard the boundary: 9/X/A/V-only pictures must NOT be reclassified as
        // edited. A repeat count like 9(40) carries digits inside its parens,
        // which must not be mistaken for edit symbols.
        Assert.Equal(PicCategory.Numeric, Pic.ParsePicClause("9(40)").Category);
        Assert.Equal(PicCategory.Numeric, Pic.ParsePicClause("9(7)V99").Category);
        Assert.Equal(PicCategory.Alphanumeric, Pic.ParsePicClause("X(40)").Category);
    }

    [Fact]
    public void DbTokenIsDebitNotDPlusB()
    {
        // DB must read as the 2-wide debit token, not D + B. 'D' is not a
        // standalone symbol, so a mis-tokenization would instead reject.
        Assert.Equal(6, Pic.ParsePicClause("9(4)DB").Length); // 4 + 2
        // A lone trailing B is the 1-wide blank-insertion symbol.
        Assert.Equal(5, Pic.ParsePicClause("9(4)B").Length);  // 4 + 1
    }

    [Fact]
    public void RejectsUnknownCharacterInsideEditedPicture()
    {
        // A truly-unknown char alongside edit symbols still rejects loudly — the
        // fail-loud guarantee holds; only the known edit symbols are supported.
        Assert.Throws<System.FormatException>(() => Pic.ParsePicClause("ZZ9@"));
        // 'D' with no following 'B' is not a standalone symbol.
        Assert.Throws<System.FormatException>(() => Pic.ParsePicClause("9(4)D"));
    }
}
