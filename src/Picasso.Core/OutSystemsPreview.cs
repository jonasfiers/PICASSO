using System.Collections.Generic;
using Picasso.Core.Models;

namespace Picasso.Core;

public sealed class OutSystemsAttributePreview
{
    public string AttributeName { get; set; } = "";
    public string DataType { get; set; } = "";
    public int Length { get; set; }
    public int Decimals { get; set; }
}

/// <summary>
/// Maps a flattened copybook field list onto the shape of an OutSystems
/// Entity/Structure definition — the framing hook that ties the parser
/// back to "this is what a real OutSystems connector would hand you."
/// Sign-separate fields never get a row of their own: SIGN IS ...
/// SEPARATE inflates the byte length of the field it belongs to (see
/// CopybookParser), it never becomes a distinct FieldSpec.
/// </summary>
public static class OutSystemsPreview
{
    public static List<OutSystemsAttributePreview> Build(IReadOnlyList<FieldSpec> flat)
    {
        var rows = new List<OutSystemsAttributePreview>(flat.Count);
        foreach (var field in flat)
        {
            if (field.Type == FieldType.Text)
            {
                rows.Add(new OutSystemsAttributePreview
                {
                    AttributeName = field.Name,
                    DataType = "Text",
                    Length = field.Len,
                    Decimals = 0,
                });
                continue;
            }

            rows.Add(new OutSystemsAttributePreview
            {
                AttributeName = field.Name,
                DataType = field.Scale == 0 ? "Integer" : "Decimal",
                Length = field.Digits,
                Decimals = field.Scale,
            });
        }
        return rows;
    }
}
