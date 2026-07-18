using System.Collections.Generic;

namespace Picasso.Core.Models;

/// <summary>
/// The USAGE a data item declares in its own entry, insofar as PICASSO models
/// inheritance of it. <see cref="None"/> means the entry stated no USAGE clause
/// at all (so it inherits from the nearest ancestor group that did); the other
/// values name an explicitly-stated, inheritable usage. Only the usages that can
/// be carried down from a group to its elementary children live here —
/// <see cref="Comp3"/> (COMP-3 / PACKED-DECIMAL), <see cref="Binary"/>
/// (COMP / COMP-4 / COMP-5 / BINARY), and <see cref="Display"/> (the default, an
/// explicit no-op that still overrides an inherited COMP-3/Binary). Unsupported
/// usages (COMP-1/2 float, POINTER, …) never reach here: they fail loud at their
/// own statement in <c>CopybookParser.ParseStatement</c>, whether on a group or an
/// elementary item, so a group that declares one is rejected at its own line.
/// SIGN is deliberately NOT modelled as inheritable — group-level SIGN inheritance
/// is out of scope for this pass.
/// </summary>
public enum DeclaredUsage
{
    None,
    Display,
    Comp3,
    Binary,
}

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
    /// The inheritable USAGE this entry stated in its own clause, or
    /// <see cref="DeclaredUsage.None"/> when it stated none. Captured for BOTH
    /// group and elementary items so a group-level USAGE (e.g. <c>05 GRP COMP-3.</c>)
    /// can be pushed down to subordinate elementary items that state no USAGE of
    /// their own — standard COBOL, which PICASSO used to ignore (silently defaulting
    /// the children to DISPLAY and mis-sizing them). Applied top-down by
    /// <c>CopybookParser.InheritGroupUsage</c> before offsets are computed: nearest
    /// ancestor wins, a child's own usage overrides an inherited one, and an inner
    /// group's usage overrides an outer group's for its subtree. Set by
    /// <c>CopybookParser.ParseStatement</c>.
    /// </summary>
    public DeclaredUsage DeclaredUsage { get; set; } = DeclaredUsage.None;

    /// <summary>
    /// The parsed PICTURE of an elementary item, retained so its
    /// <see cref="FieldSpec"/> can be REBUILT with an inherited group USAGE once
    /// the ancestor groups are known (the field is first built at parse time with
    /// only its own usage). Null for a group item, or an elementary item with no
    /// PIC. Set by <c>CopybookParser.ParseStatement</c>; consumed only by
    /// <c>CopybookParser.InheritGroupUsage</c>.
    /// </summary>
    public PicSpec? DeclaredPic { get; set; }

    /// <summary>
    /// The elementary item's SIGN IS ... SEPARATE / LEADING flags, retained
    /// alongside <see cref="DeclaredPic"/> so a rebuilt (usage-inherited) field
    /// preserves them. Meaningless for a group. Set by
    /// <c>CopybookParser.ParseStatement</c>.
    /// </summary>
    public bool SignSeparate { get; set; }

    /// <summary>See <see cref="SignSeparate"/>.</summary>
    public bool SignLeading { get; set; }

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
    /// True for an OCCURS ... DEPENDING ON table in a variable-length
    /// (ODO) copybook. Set by CopybookParser.ParseStatement. Unlike a fixed
    /// OCCURS, the repeat count is NOT known at parse time — it is read per
    /// record, at decode time, out of the field named <see cref="OdoDependsOn"/>.
    /// <see cref="OccursCount"/> is therefore not the authoritative count for an
    /// ODO table; it is set transiently to a concrete count while a concrete
    /// layout is built (see CopybookParser.BuildConcreteLayout). Several nodes in
    /// a tree may carry this flag — a record may hold multiple flat ODO tables,
    /// resolved left-to-right. An ODO table nested inside (or containing) another
    /// OCCURS is still rejected loudly at parse.
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
/// The descriptor for one OCCURS ... DEPENDING ON table — the one thing a
/// static flat layout cannot express. A variable-length copybook carries one
/// of these per ODO table, in source order, on
/// <see cref="ParsedCopybook.Odos"/>.
///
/// PICASSO supports several flat ODO tables per record (each dep defined
/// before its table so its value is readable before that table's section
/// begins) and lower bounds down to 0 (OCCURS 0 TO n). Because a later table's
/// dep field sits at an offset that itself depends on earlier tables' counts,
/// the counts are resolved left-to-right per record. Everything harder — an
/// ODO nested inside an OCCURS, an OCCURS nested inside an ODO, nested OCCURS,
/// or a dep that comes after its table — is rejected loudly at parse.
///
/// The concrete byte layout is not fixed: it is built per record, once the
/// actual counts are read from the depending fields, by
/// CopybookParser.BuildConcreteLayout(counts).
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
    /// The depending field's flat spec, taken from the representative (minimum
    /// count) layout. For the FIRST ODO table its offset is fixed and
    /// authoritative — nothing variable precedes it. For a LATER table it is
    /// only representative: the field's real offset shifts with the earlier
    /// tables' counts, so the codec re-reads it by name from the per-record
    /// partial layout rather than trusting this offset. Retained for the
    /// single-table case and for callers that want the field's type/length.
    /// </summary>
    public FieldSpec DependingField { get; }
}

public sealed class ParsedCopybook
{
    public ParsedCopybook(
        CopybookNode root,
        IReadOnlyList<FieldSpec> flat,
        bool rootIsSynthetic = false,
        IReadOnlyList<OdoInfo>? odos = null)
    {
        Root = root;
        Flat = flat;
        RootIsSynthetic = rootIsSynthetic;
        Odos = odos ?? System.Array.Empty<OdoInfo>();
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
    /// The variable-length (OCCURS ... DEPENDING ON) descriptors, one per ODO
    /// table, in source (record) order. Empty for an ordinary fixed-layout
    /// copybook. The codec resolves the tables' occurrence counts left-to-right
    /// per record (a later table's dep field offset depends on earlier counts).
    /// See <see cref="OdoInfo"/>.
    /// </summary>
    public IReadOnlyList<OdoInfo> Odos { get; }

    /// <summary>
    /// The first ODO table's descriptor, or null for a fixed-layout copybook —
    /// a convenience for the common single-table case and for callers that only
    /// need to report "this copybook is variable-length". Use <see cref="Odos"/>
    /// when a record may hold more than one ODO table.
    /// </summary>
    public OdoInfo? Odo => Odos.Count > 0 ? Odos[0] : null;

    /// <summary>True when this copybook's record length is data-dependent (ODO).</summary>
    public bool IsVariableLength => Odos.Count > 0;
}
