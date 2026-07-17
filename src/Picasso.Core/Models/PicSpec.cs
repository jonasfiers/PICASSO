namespace Picasso.Core.Models;

public enum PicCategory
{
    Numeric,
    Alphanumeric,
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
