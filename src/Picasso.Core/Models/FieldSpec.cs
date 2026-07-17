namespace Picasso.Core.Models;

public enum FieldType
{
    NumericDisplay,
    Comp3,
    Binary,
    Text,
}

/// <summary>
/// A single leaf (elementary) field in a flattened copybook layout — the
/// unit both <see cref="FlatFileCodec"/> and <see cref="OutSystemsPreview"/>
/// operate on. Shaped to mirror CATALOG-74's hand-written specs.js arrays,
/// but derived instead of transcribed.
///
/// A record rather than a plain class specifically so a caller reshaping one
/// property (see CopybookParser.ShiftField, which changes only Name/Start
/// for an OCCURS-expanded copy) can use a `with` expression instead of
/// listing every property by hand — a hand-written copy silently stops
/// copying a property the day this record gains one it doesn't know about;
/// `with` cannot.
/// </summary>
public sealed record FieldSpec
{
    public string Name { get; set; } = "";
    public int Start { get; set; }
    public int Len { get; set; }
    public FieldType Type { get; set; }
    public int Digits { get; set; }
    public int Scale { get; set; }
    public bool Signed { get; set; }
    public bool SignSeparate { get; set; }
    public bool SignLeading { get; set; }
}
