using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Picasso.Core;
using Picasso.Core.Models;
using Xunit;

namespace Picasso.Core.Tests;

/// <summary>
/// Edited PIC pictures (Z $ , . - + CR DB …) parsed for their exact display
/// width and surfaced as Text byte-passthrough. These tests pin the two things
/// that matter: the width is right IN SITU (a wrong width silently shifts every
/// following field's offset), and the passthrough round-trips byte-for-byte in
/// both Latin-1 and EBCDIC — including zero-suppressed (leading-space) and
/// all-blank values, where the codec's TrimEnd/PadRight could in principle bite.
/// </summary>
public class EditedPicTests
{
    // ---- BuildFieldSpec: an edited picture becomes a Text field of its width ----

    [Fact]
    public void EditedPictureBecomesTextFieldOfComputedWidth()
    {
        var parsed = CopybookParser.Parse(
            "01 REC.\n" +
            "   05 AMT PIC --,---,---,---,---,--9.\n");

        var field = parsed.Flat.Single();
        Assert.Equal("AMT", field.Name);
        Assert.Equal(FieldType.Text, field.Type);
        Assert.Equal(22, field.Len);
    }

    // ---- Decimal-point edited pictures parse THROUGH the full copybook parse ----
    // These go via CopybookParser.Parse (hence SplitStatements), the path the
    // isolated Pic.ParsePicClause tests never exercised — where a '.' in the PIC
    // was wrongly treated as the statement terminator.

    [Theory]
    [InlineData("ZZ,ZZ9.99", 9)]
    [InlineData("$$$,$$9.99", 10)]   // the README's advertised example
    [InlineData("ZZ,ZZ9.99CR", 11)]
    public void DecimalPointEditedPictureParsesThroughFullCopybookParse(string picture, int expectedWidth)
    {
        var parsed = CopybookParser.Parse(
            "01 REC.\n" +
            $"   05 AMT PIC {picture}.\n");

        var field = parsed.Flat.Single();
        Assert.Equal(FieldType.Text, field.Type);
        Assert.Equal(expectedWidth, field.Len);
    }

    [Fact]
    public void DecimalPointEditedFieldBetweenTwoFieldsKeepsTrailingOffsetCorrect()
    {
        // The bug's real cost was a split statement, but the offset-shift guard
        // still matters: a decimal-edited field between two fields must leave the
        // trailing field at edited.Start + width.
        var parsed = CopybookParser.Parse(
            "01 REC.\n" +
            "   05 LEAD PIC X(4).\n" +
            "   05 AMT  PIC ZZ,ZZ9.99.\n" +   // width 9
            "   05 C    PIC X(4).\n");

        var lead = parsed.Flat[0];
        var amt = parsed.Flat[1];
        var c = parsed.Flat[2];

        Assert.Equal(0, lead.Start);
        Assert.Equal(4, amt.Start);
        Assert.Equal(9, amt.Len);
        Assert.Equal(amt.Start + amt.Len, c.Start);
        Assert.Equal(13, c.Start);
        Assert.Equal(17, parsed.Root.Len);
    }

    [Fact]
    public void TwoDecimalEditedFieldsOnConsecutiveLinesBothParse()
    {
        // Guards that a '.'-terminated decimal-edited statement doesn't bleed into
        // the next line: two adjacent decimal-edited fields must stay two fields.
        var parsed = CopybookParser.Parse(
            "01 REC.\n" +
            "   05 A PIC $$$,$$9.99.\n" +
            "   05 B PIC ZZ,ZZ9.99.\n");

        Assert.Equal(new[] { "A", "B" }, parsed.Flat.Select(f => f.Name));
        Assert.Equal(10, parsed.Flat[0].Len);
        Assert.Equal(9, parsed.Flat[1].Len);
    }

    /// <summary>
    /// The offset-shift guard: an edited field sitting BETWEEN two ordinary
    /// fields. The trailing field's Start must equal the edited field's Start
    /// plus the computed width — i.e. the width is correct where it counts.
    /// </summary>
    [Fact]
    public void EditedFieldBetweenTwoFieldsKeepsTrailingOffsetCorrect()
    {
        var parsed = CopybookParser.Parse(
            "01 REC.\n" +
            "   05 LEAD  PIC X(4).\n" +
            "   05 EDITED PIC -ZZZ,ZZZ,ZZZ.\n" +   // width 12
            "   05 TAIL  PIC 9(3).\n");

        var lead = parsed.Flat[0];
        var edited = parsed.Flat[1];
        var tail = parsed.Flat[2];

        Assert.Equal(0, lead.Start);
        Assert.Equal(4, edited.Start);
        Assert.Equal(12, edited.Len);
        // The load-bearing assertion: trailing Start == edited Start + width.
        Assert.Equal(edited.Start + edited.Len, tail.Start);
        Assert.Equal(16, tail.Start);
        Assert.Equal(19, parsed.Root.Len);
    }

    // ---- Nonsensical combinations rejected loudly ----

    [Fact]
    public void Comp3OnEditedPictureRejectsLoudly()
    {
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(
            "01 REC.\n" +
            "   05 BAD PIC ZZ9 COMP-3.\n"));
        Assert.Contains("COMP-3", ex.Message);
    }

    [Fact]
    public void SignSeparateOnEditedPictureRejectsLoudly()
    {
        var ex = Assert.Throws<FormatException>(() => CopybookParser.Parse(
            "01 REC.\n" +
            "   05 BAD PIC ZZ9 SIGN IS LEADING SEPARATE.\n"));
        Assert.Contains("SEPARATE", ex.Message);
    }

    // ---- Round-trip: Text passthrough is byte-identical in both encodings ----

    // Field is 12 wide (-ZZZ,ZZZ,ZZZ). Cover a zero-suppressed value carrying
    // leading spaces, an all-blank value, and an ordinary formatted value.
    public static TheoryData<string> EditedFieldRawValues => new()
    {
        "     123,456",   // zero-suppressed: leading spaces, digits/commas trailing
        "            ",   // all-blank (12 spaces)
        "-999,999,999",   // fully populated with a leading floating sign
        "          -7",   // heavily suppressed, trailing sign-less digit
    };

    [Theory]
    [MemberData(nameof(EditedFieldRawValues))]
    public void EditedFieldRoundTripsInLatin1(string rawValue)
    {
        AssertEditedRoundTrip(rawValue, CharacterEncoding.Latin1);
    }

    [Theory]
    [MemberData(nameof(EditedFieldRawValues))]
    public void EditedFieldRoundTripsInEbcdic(string rawValue)
    {
        AssertEditedRoundTrip(rawValue, CharacterEncoding.Ebcdic037);
    }

    /// <summary>
    /// Builds a one-field, 12-byte edited layout, lays <paramref name="rawValue"/>
    /// into a record encoded in <paramref name="encoding"/>, decodes it, re-encodes
    /// it, and asserts the bytes are identical. Comparison is on bytes, not the
    /// decoded string, so an encoding fault or a mis-trimmed pad can't hide.
    /// </summary>
    private static void AssertEditedRoundTrip(string rawValue, CharacterEncoding encoding)
    {
        Assert.Equal(12, rawValue.Length); // guard the fixture itself

        var spec = CopybookParser.Parse(
            "01 REC.\n" +
            "   05 EDITED PIC -ZZZ,ZZZ,ZZZ.\n").Flat;

        // The on-disk record: the 12 display characters, encoded for the target
        // code page. For EBCDIC each char maps to its cp037 byte; the codec must
        // reverse exactly that.
        var record = encoding == CharacterEncoding.Ebcdic037 ? Ebcdic.Encode(rawValue) : rawValue;

        var decoded = FlatFileCodec.Decode(spec, record, encoding, RecordFormat.FixedLength);
        var reencoded = FlatFileCodec.Encode(spec, decoded, encoding, RecordFormat.FixedLength);

        Assert.Equal(
            Encoding.Latin1.GetBytes(record),
            Encoding.Latin1.GetBytes(reencoded));
    }
}
