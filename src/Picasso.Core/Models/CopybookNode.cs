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

    /// <summary>
    /// The name of the prior sibling this item REDEFINES, or null for an ordinary
    /// item. When set, this node (and its whole subtree) is laid out starting at
    /// the target sibling's byte offset rather than at the running offset — an
    /// overlay, sharing the target's bytes. Set by CopybookParser.ParseStatement;
    /// resolved and applied by ComputeOffsets, which fails loudly if the named
    /// target is not a prior same-level sibling. A REDEFINES adds no new storage:
    /// the next ordinary sibling continues from where it would have without it
    /// (unless the redefinition is longer than its target, which extends the group).
    /// </summary>
    public string? RedefinesTarget { get; set; }
    /// True for the single OCCURS ... DEPENDING ON table in a variable-length
    /// (ODO) copybook. Set by CopybookParser.ParseStatement. Unlike a fixed
    /// OCCURS, the repeat count is NOT known at parse time — it is read per
    /// record, at decode time, out of the field named <see cref="OdoDependsOn"/>.
    /// <see cref="OccursCount"/> is therefore not the authoritative count for an
    /// ODO table; it is set transiently to a concrete count while a concrete
    /// layout is built (see CopybookParser.BuildConcreteLayout). Only one node in
    /// a tree may carry this flag — more than one is rejected loudly at parse.
    /// </summary>
    public bool IsOdoTable { get; set; }

    /// <summary>Lower bound of an ODO table's occurrence count (the "m" in OCCURS m TO n).</summary>
    public int OdoMin { get; set; }

    /// <summary>Upper bound of an ODO table's occurrence count (the "n" in OCCURS m TO n).</summary>
    public int OdoMax { get; set; }

    /// <summary>
    /// The name of the field whose runtime value gives an ODO table its actual
    /// occurrence count. Null unless <see cref="IsOdoTable"/>. Must be defined
    /// earlier in the record than the table itself (validated at parse).
    /// </summary>
    public string? OdoDependsOn { get; set; }

    /// <summary>
    /// True when this item carries a SYNCHRONIZED / SYNC clause. It changes only
    /// WHERE the item sits, never how its value is read: a SYNC <b>binary</b>
    /// (COMP/COMP-4/COMP-5/BINARY) item is aligned to its natural 2/4/8-byte
    /// boundary by inserting slack (padding) bytes before it, which shifts every
    /// following field. SYNC on any non-binary item (DISPLAY, COMP-3, Text/edited,
    /// or a group) is a documented no-op in IBM COBOL — tolerated, no padding.
    /// Set by CopybookParser.ParseStatement; consumed by ComputeOffsets.
    /// </summary>
    public bool Synchronized { get; set; }

    /// <summary>
    /// The number of slack (padding) bytes ComputeOffsets inserted <b>before</b> a
    /// SYNC binary item to align it to its byte boundary — 0 when no alignment was
    /// needed or the item isn't a SYNC binary. Recomputed on every ComputeOffsets
    /// pass (an ODO record's SYNC field can shift between counts), and materialized
    /// by Flatten into a synthetic FILLER leaf so the flat layout stays contiguous.
    /// </summary>
    public int SyncSlack { get; set; }

    /// <summary>Populated by CopybookParser.ComputeOffsets.</summary>
    public int Start { get; set; }
    public int Len { get; set; }
}

/// <summary>
/// The descriptor for a variable-length (OCCURS ... DEPENDING ON) copybook —
/// the one thing a static flat layout cannot express. Present on
/// <see cref="ParsedCopybook.Odo"/> exactly when the copybook has an ODO table.
///
/// PICASSO supports the single, common case: EXACTLY ONE OCCURS m TO n
/// DEPENDING ON dep per record, dep defined before the table so its value is
/// readable before the variable section begins. Everything harder (two ODO
/// tables, nested ODO, an ODO combined with an OCCURS that can't be cleanly
/// modelled, or a dep that comes after the table) is rejected loudly at parse.
///
/// The concrete byte layout is not fixed: it is built per record, once the
/// actual count is read from <see cref="DependingField"/>, by
/// CopybookParser.BuildConcreteLayout(count).
/// </summary>
public sealed class OdoInfo
{
    public OdoInfo(string tableName, string dependsOn, int min, int max, FieldSpec dependingField)
    {
        TableName = tableName;
        DependsOn = dependsOn;
        Min = min;
        Max = max;
        DependingField = dependingField;
    }

    /// <summary>The ODO table's own name (e.g. TAB) — the prefix its expanded leaf names carry, TAB(1)…TAB(count).</summary>
    public string TableName { get; }

    /// <summary>The name of the field whose value sets the occurrence count.</summary>
    public string DependsOn { get; }

    /// <summary>Inclusive lower bound on the occurrence count.</summary>
    public int Min { get; }

    /// <summary>Inclusive upper bound on the occurrence count.</summary>
    public int Max { get; }

    /// <summary>
    /// The depending field's flat spec, at its fixed offset in the record's
    /// prefix (identical in every concrete layout, since it precedes the table).
    /// The codec reads it to learn each record's occurrence count before it can
    /// build that record's concrete layout.
    /// </summary>
    public FieldSpec DependingField { get; }
}

public sealed class ParsedCopybook
{
    public ParsedCopybook(
        CopybookNode root,
        IReadOnlyList<FieldSpec> flat,
        bool rootIsSynthetic = false,
        OdoInfo? odo = null)
    {
        Root = root;
        Flat = flat;
        RootIsSynthetic = rootIsSynthetic;
        Odo = odo;
    }

    public CopybookNode Root { get; }

    /// <summary>
    /// The flat leaf layout. For a fixed (non-ODO) copybook this is THE
    /// authoritative layout. For a variable-length (ODO) copybook it is a
    /// representative snapshot at the minimum occurrence count only — NOT
    /// authoritative for decoding, because a record's real layout depends on the
    /// per-record count. Decode/encode an ODO copybook through the
    /// <see cref="FlatFileCodec"/> overloads that take a ParsedCopybook (they
    /// build the concrete layout per record), never straight off this list. Check
    /// <see cref="IsVariableLength"/> first.
    /// </summary>
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

    /// <summary>
    /// The variable-length (OCCURS ... DEPENDING ON) descriptor, or null for an
    /// ordinary fixed-layout copybook. See <see cref="OdoInfo"/>.
    /// </summary>
    public OdoInfo? Odo { get; }

    /// <summary>True when this copybook's record length is data-dependent (ODO).</summary>
    public bool IsVariableLength => Odo != null;
}
