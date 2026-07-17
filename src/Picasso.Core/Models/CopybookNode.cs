using System.Collections.Generic;

namespace Picasso.Core.Models;

/// <summary>
/// One DATA DIVISION entry (group or elementary item) in the parsed
/// copybook tree, after level-number nesting has been resolved but
/// before byte offsets are computed.
/// </summary>
public sealed class CopybookNode
{
    public CopybookNode(string name, int levelNumber)
    {
        Name = name;
        LevelNumber = levelNumber;
    }

    public string Name { get; }
    public int LevelNumber { get; }
    public List<CopybookNode> Children { get; } = new List<CopybookNode>();

    /// <summary>Null for group items; set for elementary (leaf) items.</summary>
    public FieldSpec? Field { get; set; }

    public bool IsGroup => Field is null;

    /// <summary>Populated by CopybookParser.ComputeOffsets.</summary>
    public int Start { get; set; }
    public int Len { get; set; }
}

public sealed class ParsedCopybook
{
    public ParsedCopybook(CopybookNode root, IReadOnlyList<FieldSpec> flat)
    {
        Root = root;
        Flat = flat;
    }

    public CopybookNode Root { get; }
    public IReadOnlyList<FieldSpec> Flat { get; }
}
