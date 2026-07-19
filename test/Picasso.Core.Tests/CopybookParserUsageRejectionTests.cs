using System;
using Picasso.Core;
using Picasso.Core.Models;
using Xunit;

namespace Picasso.Core.Tests;

/// <summary>
/// The float/Micro-Focus-binary/aligned/pointer/DBCS USAGE clauses that remain
/// unsupported must be rejected with a named FormatException rather than silently
/// mis-sized. Before this behaviour, the clause loop's default case skipped these
/// one token at a time, producing a wrong-but-plausible DISPLAY-sized layout, and
/// COMP-1/COMP-2 (which carry no PIC) made the whole field vanish and shifted every
/// following offset. Binary COMP/COMP-4/COMP-5/BINARY and PACKED-DECIMAL are now
/// SUPPORTED and no longer appear here — see <see cref="BinaryTests"/>; what stays
/// rejected is float (COMP-1/COMP-2), the Micro Focus COMP-6/COMP-X, and the
/// pointer/index/DBCS family. (SYNC/SYNCHRONIZED alignment is now supported —
/// it aligns rather than rejects — so it is no longer in this list.)
/// </summary>
public class CopybookParserUsageRejectionTests
{
    // Each entry: the exact USAGE token as written in the copybook, and the
    // usage label that must appear in the rejection message so a caller can tell
    // which usage tripped it. Both spellings (COMP-n and COMPUTATIONAL-n) are
    // covered where COBOL allows them.
    [Theory]
    [InlineData("COMP-1", "COMP-1")]
    [InlineData("COMPUTATIONAL-1", "COMP-1")]
    [InlineData("COMP-2", "COMP-2")]
    [InlineData("COMPUTATIONAL-2", "COMP-2")]
    [InlineData("COMP-6", "COMP-6")]
    [InlineData("COMPUTATIONAL-6", "COMP-6")]
    [InlineData("COMP-X", "COMP-X")]
    [InlineData("COMPUTATIONAL-X", "COMP-X")]
    [InlineData("INDEX", "INDEX")]
    [InlineData("POINTER", "POINTER")]
    [InlineData("POINTER-32", "POINTER-32")]
    [InlineData("POINTER-64", "POINTER-64")]
    [InlineData("PROCEDURE-POINTER", "PROCEDURE-POINTER")]
    [InlineData("FUNCTION-POINTER", "FUNCTION-POINTER")]
    [InlineData("NATIONAL", "NATIONAL")]
    [InlineData("DISPLAY-1", "DISPLAY-1")]
    [InlineData("UTF-8", "UTF-8")]
    public void RejectsUnsupportedUsageClause(string usageToken, string expectedInMessage)
    {
        // A single field carrying the usage. INDEX/POINTER/COMP-1/COMP-2 legitimately
        // carry no PIC in real COBOL; a PIC here is harmless and keeps one shape for
        // the whole theory — the usage token is rejected before sizing either way.
        var source = $"01  R.\n    05  A PIC 9(4) {usageToken}.\n";
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(source));
        Assert.Contains(expectedInMessage, ex.Message);
        Assert.Contains("'A'", ex.Message);
        Assert.Contains("not supported", ex.Message);
    }

    [Fact]
    public void UsageIsComputationalFormParsesAsBinary()
    {
        // USAGE and IS fall through the default skip, so "USAGE IS COMPUTATIONAL",
        // "USAGE COMP" and bare "COMP" all reach the COMP token identically. COMP is
        // now supported binary, so the fullest spelling must parse to a 2-byte binary
        // halfword (PIC 9(4) -> 2 bytes), not be rejected.
        var parsed = CopybookParser.Parse("01  R.\n    05  A PIC 9(4) USAGE IS COMPUTATIONAL.\n");
        Assert.Single(parsed.Flat);
        Assert.Equal(FieldType.Binary, parsed.Flat[0].Type);
        Assert.Equal(2, parsed.Flat[0].Len);
    }

    [Fact]
    public void RejectsComp1FieldWithNoPictureInsteadOfDroppingIt()
    {
        // COMP-1 carries no PIC, so the pre-fix parser built no FieldSpec, treated
        // the item as a childless group and dropped it entirely — the following
        // field B silently slid to offset 0. Must reject, not vanish.
        var ex = Assert.Throws<FormatException>(
            () => CopybookParser.Parse("01  R.\n    05  A COMP-1.\n    05  B PIC X(3).\n"));
        Assert.Contains("COMP-1", ex.Message);
        Assert.Contains("'A'", ex.Message);
    }

    [Fact]
    public void RejectsComp2FieldWithNoPictureInsteadOfDroppingIt()
    {
        var ex = Assert.Throws<FormatException>(
            () => CopybookParser.Parse("01  R.\n    05  A COMP-2.\n    05  B PIC X(3).\n"));
        Assert.Contains("COMP-2", ex.Message);
        Assert.Contains("'A'", ex.Message);
    }

    [Fact]
    public void PackedDecimalIsAliasedToComp3()
    {
        // PACKED-DECIMAL is byte-identical to COMP-3, so it now aliases to the same
        // packed-decimal path: PIC 9(5) -> a 3-byte Comp3 field, not a rejection.
        var parsed = CopybookParser.Parse("01  R.\n    05  A PIC 9(5) PACKED-DECIMAL.\n");
        Assert.Single(parsed.Flat);
        Assert.Equal(FieldType.Comp3, parsed.Flat[0].Type);
        Assert.Equal(3, parsed.Flat[0].Len);
    }

    [Fact]
    public void RejectsObjectReferenceInsteadOfDroppingIt()
    {
        // OBJECT REFERENCE is two tokens and carries no PIC. Rejecting on the
        // OBJECT token means REFERENCE never matters; without it the field would
        // vanish (no FieldSpec) and slide B to offset 0.
        var ex = Assert.Throws<FormatException>(
            () => CopybookParser.Parse("01  R.\n    05  A OBJECT REFERENCE.\n    05  B PIC X(3).\n"));
        Assert.Contains("OBJECT REFERENCE", ex.Message);
        Assert.Contains("'A'", ex.Message);
    }

    [Fact]
    public void RejectsDisplay1DbcsUsage()
    {
        // DBCS: a DISPLAY-1 char is 2 bytes, so PIC X(4) DISPLAY-1 is 8 bytes, not
        // the 4 that plain DISPLAY sizing would give.
        var ex = Assert.Throws<FormatException>(
            () => CopybookParser.Parse("01  R.\n    05  A PIC X(4) DISPLAY-1.\n"));
        Assert.Contains("DISPLAY-1", ex.Message);
        Assert.Contains("'A'", ex.Message);
    }

    [Theory]
    [InlineData("05  A PIC X(4) DISPLAY.\n")]
    [InlineData("05  A PIC X(4) USAGE DISPLAY.\n")]
    [InlineData("05  A PIC X(4) USAGE IS DISPLAY.\n")]
    public void PlainDisplayUsageStaysSupported(string entry)
    {
        // Guard the DISPLAY-1 / DISPLAY boundary: plain DISPLAY is not a rejected
        // token — it falls through the default skip like VALUE/JUSTIFIED — so a
        // field explicitly declared USAGE DISPLAY must still parse to 4 bytes.
        var parsed = CopybookParser.Parse("01  R.\n    " + entry);
        Assert.Single(parsed.Flat);
        Assert.Equal(4, parsed.Flat[0].Len);
    }

    [Fact]
    public void Comp3StaysSupported()
    {
        // Guard the boundary: the sibling of every rejected usage must still parse.
        var parsed = CopybookParser.Parse("01  R.\n    05  A PIC 9(5) COMP-3.\n");
        Assert.Single(parsed.Flat);
        Assert.Equal(3, parsed.Flat[0].Len); // 5 digits -> 3 packed bytes
    }

    [Fact]
    public void FieldNamedLikeAUsageTokenIsUnaffected()
    {
        // The rejection matches a usage in clause position, not a field name. A
        // field named COMP-RATE is a single different token and must parse.
        var parsed = CopybookParser.Parse("01  R.\n    05  COMP-RATE PIC 9(4).\n");
        Assert.Single(parsed.Flat);
        Assert.Equal("COMP-RATE", parsed.Flat[0].Name);
    }
}
