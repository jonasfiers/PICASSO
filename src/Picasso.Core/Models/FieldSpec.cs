namespace Picasso.Core.Models;

public enum FieldType
{
    NumericDisplay,
    Comp3,
    Text,
}

/// <summary>
/// A single leaf (elementary) field in a flattened copybook layout — the
/// unit both <see cref="FlatFileCodec"/> and <see cref="OutSystemsPreview"/>
/// operate on. Shaped to mirror CATALOG-74's hand-written specs.js arrays,
/// but derived instead of transcribed.
/// </summary>
public sealed class FieldSpec
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
