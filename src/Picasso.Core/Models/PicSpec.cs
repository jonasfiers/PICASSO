namespace Picasso.Core.Models;

public enum PicCategory
{
    Numeric,
    Alphanumeric,

    /// <summary>
    /// An edited picture (numeric-edited or alphanumeric-edited): one carrying
    /// any insertion/replacement/floating edit symbol beyond the bare
    /// <c>9 X A V</c> set — <c>Z * . , / B 0 + - $</c> or the <c>CR</c>/<c>DB</c>
    /// tokens (or the <c>P</c> scaling position). PICASSO computes only its
    /// display WIDTH (character positions = bytes) and surfaces it as
    /// <see cref="FieldType.Text"/> byte passthrough — the numeric value is
    /// deliberately not re-interpreted. <see cref="PicSpec.Length"/> holds the
    /// computed width; <see cref="PicSpec.Digits"/>/<see cref="PicSpec.Scale"/>
    /// are not meaningful for this category.
    /// </summary>
    Edited,
}

/// <summary>
/// Result of parsing just the PIC/PICTURE picture-string itself (e.g.
/// "9(7)V99", "X(30)"), before any SIGN/COMP-3 clauses that live outside
/// the picture string are folded in by <see cref="CopybookParser"/>.
/// </summary>
public sealed class PicSpec
{
    public PicCategory Category { get; set; }
    public int Digits { get; set; }
    public int Scale { get; set; }
    public bool Signed { get; set; }
    public int Length { get; set; }
}
