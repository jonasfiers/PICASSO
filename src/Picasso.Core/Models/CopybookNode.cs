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

    /// <summary>
    /// Fixed OCCURS repeat count: how many times this item (elementary field or
    /// whole group) repeats. 1 means no OCCURS clause. Set by
    /// CopybookParser.ParseStatement; consumed by ComputeOffsets (which multiplies
    /// the item's byte span) and Flatten (which expands it into that many indexed
    /// leaf fields). Variable OCCURS (DEPENDING ON) is rejected at parse time and
    /// never reaches this, so the count is always a concrete integer.
    /// </summary>
    public int OccursCount { get; set; } = 1;

    /// <summary>Populated by CopybookParser.ComputeOffsets.</summary>
    public int Start { get; set; }
    public int Len { get; set; }
}

public sealed class ParsedCopybook
{
    public ParsedCopybook(CopybookNode root, IReadOnlyList<FieldSpec> flat, bool rootIsSynthetic = false)
    {
        Root = root;
        Flat = flat;
        RootIsSynthetic = rootIsSynthetic;
    }

    public CopybookNode Root { get; }
    public IReadOnlyList<FieldSpec> Flat { get; }

    /// <summary>
    /// True when the copybook had no 01 of its own and the parser supplied one
    /// — a headless copy member, meant to be COPY'd into a record the including
    /// program names. Exposed rather than left implicit: <see cref="Root"/> then
    /// carries a name PICASSO invented, not one the copybook ever stated, and a
    /// caller showing the tree should be able to tell the difference. No field's
    /// offset or length is affected either way.
    /// </summary>
    public bool RootIsSynthetic { get; }
}
