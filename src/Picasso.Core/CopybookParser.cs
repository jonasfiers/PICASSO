using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Picasso.Core.Models;

namespace Picasso.Core;

/// <summary>
/// Parses raw COBOL copybook source (DATA DIVISION entries only — no
/// PROCEDURE DIVISION verbs) into a nested <see cref="CopybookNode"/>
/// tree with computed byte offsets, plus a flattened leaf-only field
/// list. Level-number nesting is inherently stateful tree-building, so
/// this is a hand-written structural parser rather than regex-per-line.
/// </summary>
public static class CopybookParser
{
    /// <summary>
    /// Parses copybook source into a tree plus a flat field list.
    ///
    /// <paramref name="recordName"/> names the 01 record synthesized for a
    /// headless copy member — one with no 01 of its own. It's a caller choice
    /// because the copybook genuinely doesn't say: the real name lives in the
    /// program that COPY's it, which PICASSO never sees. Ignored for a copybook
    /// that has its own 01. Check <see cref="ParsedCopybook.RootIsSynthetic"/>
    /// to tell whether it was used.
    /// </summary>
    public static ParsedCopybook Parse(string source, string recordName = SyntheticRecordName)
    {
        if (string.IsNullOrWhiteSpace(recordName))
            throw new ArgumentException("Record name cannot be blank.", nameof(recordName));

        var stripped = StripComments(source);
        var statements = SplitStatements(stripped);

        var root = BuildTree(statements, recordName, out var rootIsSynthetic);
        if (root is null)
            throw new FormatException("Copybook has no data-item entries.");

        return FinalizeRecord(root, rootIsSynthetic);
    }

    /// <summary>
    /// Parses copybook source into <b>every</b> top-level record it defines, each
    /// as an independent layout with its own byte offsets starting at 0 and its
    /// own flat field list. This is the additive companion to <see cref="Parse"/>,
    /// which handles only a single record and rejects a copybook with more than
    /// one 01-level entry.
    ///
    /// <para>
    /// Multiple 01-level records in one copybook are <b>alternative</b> layouts —
    /// different record types sharing one physical file (e.g. a header record and
    /// a detail record) — not fields of one larger record. They are never
    /// concatenated: each begins at byte offset 0 and is decoded independently,
    /// the caller choosing which layout applies to a given record (typically by a
    /// record-type discriminator field). This method returns them in source order,
    /// each finalized exactly as <see cref="Parse"/> would finalize a lone record.
    /// </para>
    ///
    /// <para>Shapes:</para>
    /// <list type="bullet">
    /// <item>A single 01 record → a one-element list, byte-identical to what
    /// <see cref="Parse"/> returns.</item>
    /// <item>A headless copy member (no 01) → a one-element list wrapping the
    /// entries under the synthetic 01 named <paramref name="recordName"/>, exactly
    /// as <see cref="Parse"/> does. The name is shared across the (single) record.</item>
    /// <item>N alternative 01 records → an N-element list, each record independent
    /// and offset from 0.</item>
    /// </list>
    ///
    /// <paramref name="recordName"/> is used only for the headless case (there is
    /// no 01 to name); a copybook that supplies its own 01 names ignores it.
    /// Each record is subject to the same fail-loud empty-record guard as
    /// <see cref="Parse"/>: a record with no storage-bearing elementary field
    /// throws.
    /// </summary>
    public static IReadOnlyList<ParsedCopybook> ParseRecords(string source, string recordName = SyntheticRecordName)
    {
        if (string.IsNullOrWhiteSpace(recordName))
            throw new ArgumentException("Record name cannot be blank.", nameof(recordName));

        var stripped = StripComments(source);
        var statements = SplitStatements(stripped);
        var tops = BuildTops(statements);

        if (tops.Count == 0)
            throw new FormatException("Copybook has no data-item entries.");

        var records = new List<ParsedCopybook>(tops.Count);

        if (tops.Any(t => t.LevelNumber == 1))
        {
            // One or more 01-level records. In well-formed source every top-level
            // entry is then an 01 (a lower-level item after an 01 nests under it,
            // and only another 01 starts a new top). A non-01 top mixed in means
            // an orphan entry belonging to no record — malformed, not guessed at.
            var orphan = tops.FirstOrDefault(t => t.LevelNumber != 1);
            if (orphan != null)
                throw new FormatException(
                    $"Copybook mixes an 01-level record with a top-level entry at level {orphan.LevelNumber} " +
                    $"('{orphan.Name}') that belongs to no record. Every top-level entry must either be an " +
                    "01-level record or nest under one.");

            // Each 01 is already an independent subtree; finalize each from offset 0.
            foreach (var top in tops)
                records.Add(FinalizeRecord(top, rootIsSynthetic: false));
            return records;
        }

        // Headless: no 01 anywhere. All top entries are fields of one record and
        // must share a level number (same rule BuildTree applies); wrap them in a
        // single synthetic 01. A headless member is one record, never several.
        var level = tops[0].LevelNumber;
        var odd = tops.FirstOrDefault(t => t.LevelNumber != level);
        if (odd != null)
            throw new FormatException(
                $"Copybook has no 01-level record, and its top-level entries disagree on level number " +
                $"(level {level} '{tops[0].Name}' vs level {odd.LevelNumber} '{odd.Name}'). " +
                "Entries of one record must share a level number.");

        var synthetic = new CopybookNode(recordName, 1);
        synthetic.Children.AddRange(tops);
        records.Add(FinalizeRecord(synthetic, rootIsSynthetic: true));
        return records;
    }

    /// <summary>
    /// Turns one resolved record root into a <see cref="ParsedCopybook"/>: computes
    /// byte offsets from 0, flattens to (nested-OCCURS-expanded) leaf fields, and
    /// applies the fail-loud empty-record guard. Shared by <see cref="Parse"/> and
    /// <see cref="ParseRecords"/> so a record is finalized identically either way.
    /// </summary>
    private static ParsedCopybook FinalizeRecord(CopybookNode root, bool rootIsSynthetic)
    {
        // A group's USAGE is inherited by every subordinate elementary item that
        // states none of its own (standard COBOL). Push it down NOW — before any
        // offset is computed — so a field that inherits COMP-3/Binary is sized by
        // its packed/binary width, not the DISPLAY default it was first built with.
        // Must run before RejectSyncInOccurs (which inspects Field.Type) and
        // ComputeOffsets (which reads Field.Len).
        InheritGroupUsage(root, DeclaredUsage.None);

        // An elementary item must define storage. A leaf node (no subordinates)
        // that also carries no FieldSpec is an item with neither a PICTURE nor a
        // width-bearing USAGE and no children — e.g. "05 B." — which a real
        // compiler rejects ("PICTURE clause required"). Left unchecked the
        // flattener drops it silently, vanishing a field the author intended and
        // shifting nothing to signal it — a fail-loud hole. Reject it by name.
        // (Level-88/66 never reach the tree, so a "group" whose only listed
        // subordinates were 88s lands here too — correctly: a group with no
        // elementary item is itself invalid.)
        RejectStoragelessLeaves(root, isRoot: true);

        // Nested FIXED-count OCCURS (a table of tables) is supported: it flattens
        // recursively, each level carrying its own 1-based index. No dedicated
        // rejection pass is needed for it — the offset math (ComputeOffsets) and the
        // name/offset expansion (Flatten) both recurse. Any nesting that involves an
        // OCCURS ... DEPENDING ON table (an ODO inside an OCCURS, or any OCCURS
        // inside an ODO) stays rejected loudly, enforced by ValidateAndFindOdos below.
        RejectSyncInOccurs(root, insideOccurs: false);

        // Locate the OCCURS ... DEPENDING ON tables and validate their structural
        // preconditions before any offset is computed. An ODO nested in another
        // OCCURS (fixed or ODO), or an OCCURS (fixed or ODO) nested inside an ODO's
        // own subtree, are each rejected loudly — only all-FIXED-count nesting is
        // supported; any variable-length nesting is out of scope.
        var odoNodes = ValidateAndFindOdos(root);

        // For an ODO copybook every ODO table is expanded to its MINIMUM count
        // for the representative static layout below; the authoritative per-record
        // layout is rebuilt at decode time by BuildConcreteLayout.
        foreach (var odoNode in odoNodes)
            odoNode.OccursCount = odoNode.OdoMin;

        ComputeOffsets(root, 0);

        var flat = new List<FieldSpec>();
        Flatten(root, flat);

        // Every elementary field has Len >= 1, so an empty flat list means the
        // record has statements but no storage-bearing field (a bare 01, a
        // childless group, or a group whose only children are level-88
        // condition-names). Decoding against that would silently produce a
        // zero-byte record — fail loudly instead.
        if (flat.Count == 0)
            throw new FormatException(
                $"Record '{root.Name}' defines no elementary fields with storage — it would be zero bytes. " +
                "A layout needs at least one PIC field.");

        // One OdoInfo per table, in source order, so the codec can resolve the
        // occurrence counts left-to-right (a later table's dep field offset
        // depends on earlier tables' counts).
        IReadOnlyList<OdoInfo>? odos = null;
        if (odoNodes.Count > 0)
            odos = odoNodes.Select(n => BuildOdoInfo(n, flat)).ToList();

        return new ParsedCopybook(root, flat, rootIsSynthetic, odos);
    }

    /// <summary>
    /// Rejects a leaf node carrying neither a <see cref="FieldSpec"/> nor children —
    /// a data-name with no PICTURE, no width-bearing USAGE, and no subordinates
    /// (e.g. <c>05 B.</c>). A real COBOL compiler requires such an item to have a
    /// PICTURE; PICASSO's flattener would otherwise drop it silently and lose the
    /// storage the record's author intended, so it fails loudly here instead. The
    /// record root is exempt — a wholly empty record is reported separately (with a
    /// whole-record message) by the flat-count guard.
    /// </summary>
    private static void RejectStoragelessLeaves(CopybookNode node, bool isRoot)
    {
        if (!isRoot && node.Field is null && node.Children.Count == 0)
            throw new FormatException(
                $"Item '{node.Name}' (level {node.LevelNumber:D2}) has no PICTURE, no width-bearing USAGE, " +
                "and no subordinate items — it defines no storage. An elementary item needs a PIC clause and " +
                "a group needs subordinate items; a real compiler rejects this, and silently skipping it would " +
                "drop the field and mis-size the record. Add a PIC clause, or remove the item.");
        foreach (var child in node.Children)
            RejectStoragelessLeaves(child, isRoot: false);
    }

    /// <summary>
    /// Finds every OCCURS ... DEPENDING ON table in the tree, in source order,
    /// and enforces the supported scope loudly. Returns an empty list for an
    /// ordinary fixed copybook. Several flat, top-level ODO tables per record ARE
    /// supported: the codec reads each table's depending-field count and resolves
    /// them left-to-right (a later table's dep field sits at an offset that depends
    /// on the earlier tables' counts), then lays the record out against the fully
    /// resolved layout (see <see cref="FlatFileCodec"/> and
    /// <see cref="BuildConcreteLayout(ParsedCopybook, IReadOnlyList{int})"/>).
    /// Rejected, per table: an ODO table nested inside another OCCURS (fixed or
    /// ODO), and any OCCURS (fixed or ODO) nested inside an ODO table's own
    /// subtree — each a shape PICASSO does not model.
    /// </summary>
    private static List<CopybookNode> ValidateAndFindOdos(CopybookNode root)
    {
        var odoNodes = new List<CopybookNode>();
        CollectOdo(root, odoNodes);

        foreach (var odo in odoNodes)
        {
            // The ODO table must not sit inside any other OCCURS (fixed or ODO):
            // its offset would itself be index-dependent, which the read-once
            // per-table count model cannot express.
            if (IsNestedUnderOccurs(root, odo, insideOccurs: false))
                throw new FormatException(
                    $"OCCURS ... DEPENDING ON table '{odo.Name}' is nested inside another OCCURS. A variable-length " +
                    "table nested in a repeating group is not supported; only flat, top-level ODO tables are.");

            // No OCCURS of any kind may appear inside the ODO table's subtree — a
            // table-of-tables where the outer table is variable is out of scope
            // and rejected rather than half-modelled.
            foreach (var child in odo.Children)
                RejectAnyOccursInSubtree(child, odo.Name);
        }

        return odoNodes;
    }

    private static void CollectOdo(CopybookNode node, List<CopybookNode> into)
    {
        if (node.IsOdoTable)
            into.Add(node);
        foreach (var child in node.Children)
            CollectOdo(child, into);
    }

    /// <summary>True when <paramref name="target"/> has an OCCURS/ODO ancestor.</summary>
    private static bool IsNestedUnderOccurs(CopybookNode node, CopybookNode target, bool insideOccurs)
    {
        if (ReferenceEquals(node, target))
            return insideOccurs;

        var nowInside = insideOccurs || node.OccursCount > 1 || node.IsOdoTable;
        foreach (var child in node.Children)
            if (IsNestedUnderOccurs(child, target, nowInside))
                return true;
        return false;
    }

    private static void RejectAnyOccursInSubtree(CopybookNode node, string odoName)
    {
        if (node.OccursCount > 1 || node.IsOdoTable)
            throw new FormatException(
                $"OCCURS on field '{node.Name}' sits inside the variable-length table '{odoName}'. An OCCURS " +
                "nested within an OCCURS ... DEPENDING ON table is not supported — only a flat repeating item " +
                "(elementary, or a group of non-repeating fields) may depend on a count.");
        foreach (var child in node.Children)
            RejectAnyOccursInSubtree(child, odoName);
    }

    /// <summary>
    /// Builds the <see cref="OdoInfo"/> descriptor. The depending field must be
    /// defined earlier in the record than the table (so its value is readable
    /// before the variable section begins) — validated here against the
    /// representative layout, since the table's start offset is fixed regardless
    /// of the eventual count.
    /// </summary>
    private static OdoInfo BuildOdoInfo(CopybookNode odoNode, List<FieldSpec> flat)
    {
        var dep = odoNode.OdoDependsOn!;
        var tableStart = odoNode.Start;

        var prefixMatch = flat.FirstOrDefault(f => f.Name == dep && f.Start < tableStart);
        if (prefixMatch is null)
        {
            var existsAnywhere = flat.Any(f => f.Name == dep);
            if (existsAnywhere)
                throw new FormatException(
                    $"OCCURS ... DEPENDING ON '{dep}' (table '{odoNode.Name}') names a field that is not " +
                    "defined before the table. The depending field's value must be readable before the " +
                    "variable-length section begins, so it has to precede the table in the record.");

            throw new FormatException(
                $"OCCURS ... DEPENDING ON '{dep}' (table '{odoNode.Name}') does not name a plain elementary " +
                "field defined before the table. The depending field must be a simple field — not a group, and " +
                "not itself inside a table — that precedes the variable-length section, so its count is read first.");
        }

        return new OdoInfo(odoNode.Name, dep, odoNode.OdoMin, odoNode.OdoMax, prefixMatch);
    }

    /// <summary>
    /// Builds the concrete flat layout for a variable-length copybook at a given
    /// occurrence count: the ODO table expanded to <paramref name="count"/> copies
    /// (1-indexed, exactly like fixed OCCURS — TAB(1)…TAB(count)), with every
    /// trailing field's offset shifted past all the copies. The single source of
    /// truth for an ODO record's byte layout at decode/encode time.
    ///
    /// Reuses the same offset and flatten passes the fixed path uses, by setting
    /// the ODO node's transient count. Not re-entrant across threads (it mutates
    /// the shared tree's transient offsets), but every returned FieldSpec is an
    /// independent copy, so previously-returned layouts are never disturbed.
    /// </summary>
    public static List<FieldSpec> BuildConcreteLayout(ParsedCopybook parsed, int count)
        => BuildConcreteLayout(parsed, new[] { count });

    /// <summary>
    /// Builds the concrete flat layout for a variable-length copybook at given
    /// occurrence counts — one per ODO table, in source order. Each named table
    /// is expanded to its count of 1-indexed copies (TAB(1)…TAB(count)) and
    /// every trailing field's offset cascades past all the copies.
    ///
    /// <paramref name="counts"/> may be SHORTER than the number of tables: the
    /// tables it does not cover are laid out at their minimum count. That is what
    /// makes left-to-right resolution possible — build a partial layout with the
    /// tables resolved so far, read the next table's depending field at its now-
    /// known offset, then extend the list. A count of 0 (OCCURS 0 TO n) makes a
    /// table contribute no fields, with everything after it shifting to its start.
    ///
    /// Reuses the same offset and flatten passes the fixed path uses, by setting
    /// each ODO node's transient count. Not re-entrant across threads (it mutates
    /// the shared tree's transient offsets), but every returned FieldSpec is an
    /// independent copy, so previously-returned layouts are never disturbed.
    /// </summary>
    public static List<FieldSpec> BuildConcreteLayout(ParsedCopybook parsed, IReadOnlyList<int> counts)
    {
        if (!parsed.IsVariableLength)
            throw new InvalidOperationException(
                "BuildConcreteLayout is only meaningful for a variable-length (ODO) copybook.");

        var odoNodes = new List<CopybookNode>();
        CollectOdo(parsed.Root, odoNodes);
        if (odoNodes.Count == 0)
            throw new InvalidOperationException("Variable-length copybook has no ODO node in its tree.");
        if (counts.Count > odoNodes.Count)
            throw new ArgumentException(
                $"{counts.Count} counts supplied for {odoNodes.Count} ODO table(s).", nameof(counts));

        for (var j = 0; j < odoNodes.Count; j++)
        {
            // Tables not covered by the (possibly partial) counts list default to
            // their minimum — they sit after the table currently being resolved,
            // so their transient count cannot affect an earlier dep field offset.
            var c = j < counts.Count ? counts[j] : odoNodes[j].OdoMin;
            if (c < 0)
                throw new ArgumentOutOfRangeException(nameof(counts), c,
                    "Occurrence count must be zero or greater.");
            odoNodes[j].OccursCount = c;
        }

        ComputeOffsets(parsed.Root, 0);

        var flat = new List<FieldSpec>();
        Flatten(parsed.Root, flat);
        return flat;
    }

    /// <summary>
    /// Columns 1-6 of a traditional fixed-format line: a six-digit line-sequence
    /// number. Free-format COBOL never opens a line with six digits — real lines
    /// start with a level number ("01", "05") or a keyword — so a match here
    /// identifies the line as fixed-format on its own, with no mode flag needed.
    /// Detection is per line, so a file may mix both forms.
    /// </summary>
    /// <summary>COBOL source-listing control directives — no storage, formatting only.</summary>
    private static readonly HashSet<string> ListingDirectives =
        new(StringComparer.Ordinal) { "EJECT", "SKIP1", "SKIP2", "SKIP3", "TITLE" };

    /// <summary>An EXEC SQL ... END-EXEC precompiler block (DB2 DCLGEN). No storage.</summary>
    private static readonly Regex ExecSqlBlock =
        new Regex(@"EXEC\s+SQL\b.*?\bEND-EXEC", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex FixedFormatSequenceNumber =
        new Regex(@"^\d{6}", RegexOptions.Compiled);

    /// <summary>
    /// A computational-shaped usage token that reached the clause-parser's default
    /// case, i.e. was NOT one of the recognized COMP forms — COMPUTATIONAL3 (dropped
    /// hyphen), COMP-0, COMP-7, etc. Matched against the already-upper-cased token.
    /// The recognized/rejected forms (COMP, COMP-3/4/5, COMP-1/2/6/X, COMPUTATIONAL-n)
    /// all hit explicit switch cases and never reach the default, so any COMP-N here
    /// is an unrecognized usage that would change the field width if honored.
    /// </summary>
    private static readonly Regex UnknownComputationalUsage =
        new Regex(@"^(COMP|COMPUTATIONAL)-?[0-9]+$", RegexOptions.Compiled);

    /// <summary>Columns 8-72 of a fixed-format line, once columns 1-6 are gone.</summary>
    private const int FixedFormatCodeLength = 66;

    /// <summary>
    /// The code area ends at column 72; columns 73-80 are the identification area.
    /// A line longer than this carries content past the code area.
    /// </summary>
    private const int CodeAreaEndColumn = 72;

    /// <summary>
    /// Strips free-format "*&gt;" comments (to end of line, anywhere on the
    /// line) and handles traditional fixed-format lines: the columns-1-6
    /// sequence number is dropped, a '*' in column 7 marks the whole line as a
    /// comment, and anything at or past column 73 (the identification area, which
    /// the compiler ignores) is truncated so it can't reach the tokenizer.
    ///
    /// A statement wrapping across several fixed-format lines needs no special
    /// handling for the ordinary case: SplitStatements splits on '.' across the
    /// whole document and collapses embedded newlines.
    ///
    /// The column-7 '-' continuation indicator IS handled here, before the text
    /// reaches SplitStatements. A '-' in column 7 (0-indexed 6) marks the line as
    /// a continuation of the preceding non-blank code line and is joined to it:
    /// when the preceding line left an alphanumeric literal OPEN (its closing
    /// delimiter absent), the continuation line's area B reopens the literal with a
    /// matching delimiter and the characters after that delimiter continue the
    /// literal — so <c>VALUE "A</c> + <c>-    "BC"</c> reconstructs the single,
    /// properly-closed literal <c>VALUE "ABC"</c> on one logical line. When the
    /// preceding line had no open literal, the continuation is a split word and is
    /// concatenated directly. This is only meaningful in fixed-format (free-format
    /// has no column-7 indicator). A continuation that cannot be reconstructed
    /// (open literal, but area B does not reopen it with a matching delimiter) fails
    /// loud rather than silently mis-joining. A genuinely unterminated literal with
    /// no continuation line is untouched here and still fails loud in
    /// SplitStatements.
    /// </summary>
    public static string StripComments(string source)
    {
        // Drop any 0x1A (DOS/CP-M ^Z "SUB" end-of-file marker). A copybook
        // transferred off a mainframe or through a DOS toolchain frequently carries
        // a trailing 0x1A; it is not COBOL source, but left in place it becomes a
        // stray token and falsely rejects an otherwise valid layout. It never
        // appears in real COBOL source, so removing every occurrence is safe.
        source = source.Replace("\u001A", "");

        // Remove EXEC SQL ... END-EXEC precompiler blocks. A DB2 DCLGEN copybook
        // carries one (an SQL DECLARE ... TABLE) directly above its 01 host-variable
        // structure; it is a precompiler directive with NO COBOL storage, but left in
        // place its SQL tokens ("EXEC", "(", type names) falsely reject the copybook.
        // The 01 structure below is a normal record layout. Span-wise, case-insensitive,
        // across lines (a DCLGEN block spans many lines).
        source = ExecSqlBlock.Replace(source, " ");

        // Processed code content, one entry per physical source line (comment and
        // blank lines become ""). A column-7 continuation line adds an empty entry
        // of its own — its content is folded into the last real code line instead —
        // so the 1:1 physical-line mapping is preserved.
        var outputLines = new List<string>();
        // Index into outputLines of the last line that carried real code; the target
        // a continuation line is joined onto. -1 until the first code line appears.
        var lastCodeIndex = -1;

        foreach (var rawLine in source.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine;
            var continuation = false;

            if (FixedFormatSequenceNumber.IsMatch(line) || HasAlphanumericSequenceArea(line))
            {
                line = line.Substring(6);
                if (line.Length > FixedFormatCodeLength)
                    line = line.Substring(0, FixedFormatCodeLength);

                // What was column 7 is now the first character.
                if (line.Length > 0 && line[0] == '*')
                {
                    outputLines.Add(string.Empty);
                    continue;
                }
                // Column-7 '-' continuation indicator: drop it; cols 8-72 remain.
                if (line.Length > 0 && line[0] == '-')
                {
                    continuation = true;
                    line = line.Substring(1);
                }
            }
            // Fixed-format comment line whose sequence number is blank rather
            // than numbered: column 7 (0-indexed: 6) is '*'.
            else if (line.Length > 6 && line[6] == '*' && (line.Length == 7 || line[7] != '>'))
            {
                outputLines.Add(string.Empty);
                continue;
            }
            // Fixed-format continuation line whose sequence number is blank: column 7
            // (0-indexed 6) is '-', with columns 1-6 blank. Keep cols 8-72 as content.
            else if (line.Length > 6 && line[6] == '-' && IsBlankSequenceArea(line))
            {
                continuation = true;
                var end = Math.Min(line.Length, CodeAreaEndColumn);
                line = end > 7 ? line.Substring(7, end - 7) : string.Empty;
            }

            // Off-column full-line comment. Fixed-format puts the '*' comment
            // indicator in column 7, but real exported copybooks (e.g. JRecord's
            // "cobol" sample style) routinely leave column 7 blank and start a '*'
            // banner in Area A (column 8+). After the sequence-area handling above,
            // check the first non-blank character: no COBOL data item can begin with
            // '*', so a leading '*' unambiguously marks a comment line. Excludes the
            // inline "*>" marker (handled below) and continuation lines (whose Area-B
            // content may legitimately begin with '*', e.g. a continued edited PIC).
            if (!continuation)
            {
                var t = line.TrimStart();
                if (t.Length > 0 && t[0] == '*' && !t.StartsWith("*>", StringComparison.Ordinal))
                {
                    outputLines.Add(string.Empty);
                    continue;
                }
            }

            // Separated columns-73-80 identification area on a line whose
            // columns-1-6 sequence area is BLANK (so the numeric-cols-1-6 path
            // above never fired and never truncated it). Many real fixed-format
            // copybooks leave cols 1-6 blank yet still carry an 8-digit sequence
            // number in cols 73-80; left in place it leaks into the tokenizer and
            // falsely rejects a valid layout. Strip it CONSERVATIVELY — only when
            // everything past column 72 is digits/spaces AND column 72 itself is a
            // space, i.e. a blank gap separates the code from the trailing number.
            // That space gap is what distinguishes a genuine identification field
            // from free-format content (e.g. a numeric VALUE literal running past
            // column 72 has no space immediately before its digits), so genuine
            // free-format lines are never truncated.
            if (line.Length > CodeAreaEndColumn
                && line[CodeAreaEndColumn - 1] == ' '
                && IsSeparatedIdentificationArea(line))
            {
                line = line.Substring(0, CodeAreaEndColumn);
            }

            var commentStart = line.IndexOf("*>", StringComparison.Ordinal);
            if (commentStart >= 0)
                line = line.Substring(0, commentStart);

            if (continuation)
            {
                if (lastCodeIndex < 0)
                    throw new FormatException(
                        "Fixed-format continuation line (column-7 '-') has no preceding code line to " +
                        "continue. Check the copybook's column-7 indicator area.");

                outputLines[lastCodeIndex] = JoinContinuation(outputLines[lastCodeIndex], line);
                // The continuation's content now lives on lastCodeIndex; this
                // physical line contributes nothing further.
                outputLines.Add(string.Empty);
                continue;
            }

            // Source-listing control directives (EJECT, SKIP1/2/3, TITLE) format the
            // compiler's printed listing and carry no storage. They sit on their own
            // line with no period terminator, so they must be dropped HERE, like a
            // comment — reaching SplitStatements, a period-less directive would glue
            // onto the following field's statement and swallow it.
            var trimmedStart = line.TrimStart();
            if (trimmedStart.Length > 0)
            {
                var space = trimmedStart.IndexOf(' ');
                var firstWord = (space < 0 ? trimmedStart : trimmedStart.Substring(0, space)).ToUpperInvariant();
                if (ListingDirectives.Contains(firstWord))
                {
                    outputLines.Add(string.Empty);
                    continue;
                }
            }

            outputLines.Add(line);
            if (line.Trim().Length > 0)
                lastCodeIndex = outputLines.Count - 1;
        }

        return string.Join("\n", outputLines) + "\n";
    }

    /// <summary>
    /// Joins a fixed-format column-7 continuation line's content onto the code
    /// line it continues. If <paramref name="previous"/> ends with an OPEN
    /// alphanumeric literal, <paramref name="continuationContent"/> must reopen it
    /// with a matching delimiter in area B; the reopening delimiter is dropped and
    /// the remaining characters continue the still-open literal (COBOL literal
    /// continuation). Otherwise the continuation is a split word and is
    /// concatenated directly with no separating space. An open literal that the
    /// continuation does not reopen with a matching delimiter cannot be
    /// reconstructed and fails loud rather than mis-joining.
    /// </summary>
    private static string JoinContinuation(string previous, string continuationContent)
    {
        var cont = continuationContent.TrimStart();

        if (HasOpenLiteral(previous, out var quote))
        {
            if (cont.Length == 0 || cont[0] != quote)
                throw new FormatException(
                    $"Fixed-format literal continuation (column-7 '-') does not reopen the {quote} literal " +
                    "with a matching delimiter in area B, so the continued literal cannot be reconstructed. " +
                    "Check the copybook's continuation line.");

            // Drop the reopening delimiter; the rest continues the open literal.
            return previous + cont.Substring(1);
        }

        // Non-literal continuation: the split word resumes with no separating space.
        return previous.TrimEnd() + cont;
    }

    /// <summary>
    /// Scans <paramref name="text"/> with the same quote-awareness as
    /// SplitStatements (both delimiters, doubled-delimiter escape) and reports
    /// whether it ends INSIDE an unterminated alphanumeric literal, returning the
    /// active delimiter in <paramref name="quote"/> (else '\0').
    /// </summary>
    private static bool HasOpenLiteral(string text, out char quote)
    {
        var active = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (active != '\0')
            {
                if (c == active)
                {
                    if (i + 1 < text.Length && text[i + 1] == active)
                    {
                        i++;
                        continue;
                    }
                    active = '\0';
                }
                continue;
            }
            if (c == '\'' || c == '"')
                active = c;
        }
        quote = active;
        return active != '\0';
    }

    /// <summary>
    /// True when columns 1-6 are a fixed-format sequence area that is NOT purely
    /// numeric — six non-space characters (a programmer change-tag like "JL0001"),
    /// with either a comment/continuation indicator in column 7, or a level number
    /// beginning the code area. That extra requirement is what keeps a free-format
    /// line from being mistaken for a sequenced one: a free-format data entry opens
    /// with a level number (a space by column 3), so it never presents six unbroken
    /// characters before column 7. The purely-numeric case is handled separately by
    /// <see cref="FixedFormatSequenceNumber"/>.
    /// </summary>
    private static bool HasAlphanumericSequenceArea(string line)
    {
        if (line.Length < 8) return false;
        for (var i = 0; i < 6; i++)
            if (!char.IsLetterOrDigit(line[i])) return false;

        var indicator = line[6];
        // A comment ('*'), continuation ('-') or form-feed ('/') indicator after a
        // six-char sequence area is itself the fixed-format signature.
        if (indicator == '*' || indicator == '-' || indicator == '/') return true;
        // Otherwise the indicator must be blank (or a debug 'D'), and the code area
        // must open with a level number.
        if (indicator != ' ' && indicator != 'D' && indicator != 'd') return false;

        var code = line.Substring(7).TrimStart();
        var digits = 0;
        while (digits < code.Length && char.IsDigit(code[digits])) digits++;
        return digits >= 1 && digits <= 2 && (digits == code.Length || code[digits] == ' ');
    }

    /// <summary>True when columns 1-6 (0-indexed 0-5) of a line are all blank.</summary>
    private static bool IsBlankSequenceArea(string line)
    {
        for (var i = 0; i < 6 && i < line.Length; i++)
            if (line[i] != ' ')
                return false;
        return true;
    }

    /// <summary>
    /// True when every character from column 73 (0-indexed 72) to end-of-line is
    /// a digit or a space — the shape of a classic all-numeric identification /
    /// sequence-number area. Any other character (a letter, punctuation, a PIC
    /// symbol) means the tail is real code, not an identification field.
    /// </summary>
    private static bool IsSeparatedIdentificationArea(string line)
    {
        for (var i = CodeAreaEndColumn; i < line.Length; i++)
        {
            var c = line[i];
            if (c != ' ' && (c < '0' || c > '9'))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Splits source into statements on the COBOL '.' terminator, but only when
    /// the period is NOT inside a quoted string literal. A VALUE clause may hold
    /// a literal like <c>'Thank you... '</c> whose embedded periods are data, not
    /// terminators — a naive <c>Split('.')</c> would shatter such a literal and
    /// falsely reject a valid copybook. Both <c>'</c> and <c>"</c> delimiters are
    /// recognised, and the COBOL doubled-delimiter escape (<c>''</c> inside a
    /// '-literal, <c>""</c> inside a "-literal) is treated as a literal quote, not
    /// a close. Each emitted chunk is whitespace-collapsed exactly as before.
    ///
    /// Line-continuation of a literal across two source lines (the column-7 '-'
    /// indicator) is a deliberate non-goal: real copybooks put a VALUE literal on
    /// one line, and PICASSO never interprets literal content.
    /// </summary>
    public static List<string> SplitStatements(string text)
    {
        var statements = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';       // active literal delimiter, or '\0' when outside one

        void Flush()
        {
            var trimmed = Regex.Replace(current.ToString(), @"\s+", " ").Trim();
            if (trimmed.Length > 0)
                statements.Add(trimmed);
            current.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (quote != '\0')
            {
                // Inside a literal. A doubled delimiter is an escaped quote that
                // stays in the literal; a lone delimiter closes it.
                if (c == quote)
                {
                    if (i + 1 < text.Length && text[i + 1] == quote)
                    {
                        current.Append(c).Append(c);
                        i++;
                        continue;
                    }
                    quote = '\0';
                }
                current.Append(c);
                continue;
            }

            if (c == '\'' || c == '"')
            {
                quote = c;
                current.Append(c);
                continue;
            }

            if (c == '.')
            {
                // A COBOL statement-terminating period is always followed by
                // whitespace, a newline, or end of input. A period embedded in a
                // PIC (the decimal point of an edited picture, e.g. ZZ,ZZ9.99) or
                // inside a numeric literal (VALUE 1.5) is followed by a non-space
                // and is data, not a terminator — splitting on it would shatter the
                // statement (ZZ,ZZ9.99 -> "ZZ,ZZ9" + "99"). Only a period at a
                // token boundary terminates.
                var next = i + 1 < text.Length ? text[i + 1] : '\0';
                if (next == '\0' || char.IsWhiteSpace(next))
                {
                    Flush();
                    continue;
                }
                // Embedded period: keep it in the current statement (falls through
                // to the append below).
            }

            current.Append(c);
        }

        // A literal opened but never closed swallowed the rest of the copybook
        // into one statement. Historically the malformed statement was caught
        // downstream (a leaked terminator period landed in a PIC token, which the
        // strict picture parser rejected); now that the picture parser recognizes
        // '.' as an edit symbol, that incidental backstop is gone, so the
        // unterminated literal is rejected here at its source — loudly, never a
        // silent single-field miscompute. No well-formed copybook trips this.
        if (quote != '\0')
            throw new FormatException(
                $"Unterminated string literal (opening {quote}) in copybook source: a VALUE literal was " +
                "opened but never closed, which would swallow the rest of the copybook into a single " +
                "statement. Check the copybook's quoting.");

        Flush();
        return statements;
    }

    private sealed class ParsedStatement
    {
        public int LevelNumber;
        public string Name = "";
        public FieldSpec? Field;
        public int OccursCount = 1;
        public string? RedefinesTarget;
        public bool Synchronized;

        // The inheritable USAGE this entry stated in its own clause (None if it
        // stated none), plus the ingredients needed to REBUILD an elementary
        // FieldSpec if it turns out to inherit a group's USAGE. Captured for both
        // group and elementary items — a group carries USAGE but no PIC, and its
        // usage must reach subordinate elementary items that state none.
        public DeclaredUsage DeclaredUsage = DeclaredUsage.None;
        public PicSpec? Pic;
        public bool SignSeparate;
        public bool SignLeading;

        // Variable-length OCCURS (ODO). IsOdo marks this statement as the table;
        // the bounds and the depending-field name are carried onto the node.
        public bool IsOdo;
        public int OdoMin;
        public int OdoMax;
        public string? OdoDependsOn;
    }

    private static ParsedStatement? ParseStatement(string statement)
    {
        var tokens = statement.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
            throw new FormatException($"Malformed copybook entry: \"{statement}\".");

        if (!int.TryParse(tokens[0], out var level))
            throw new FormatException($"Expected a level number, got \"{tokens[0]}\" in \"{statement}\".");

        // Level 88 is a condition-name: metadata on the immediately-preceding
        // data item (its VALUE clause names states, e.g. 88 STATUS-OK VALUE 'A')
        // that occupies zero storage. It's not a field, adds no bytes, and must
        // not touch the level stack or any offset — so it's tolerated and
        // silently dropped here (null == "skip this statement"), exactly as the
        // parser already ignores INDEXED BY / VALUE / JUSTIFIED. Its body
        // (VALUE/VALUES ARE/THRU literals) is never interpreted.
        // Level 66 (RENAMES) — a NEW-NAME RENAMES ITEM-1 [THRU ITEM-2] alias that
        // re-groups already-defined items under another name. Like level 88 it
        // occupies ZERO new storage (it renames existing bytes), so it is tolerated
        // and dropped: the alias itself isn't surfaced, but the byte layout is
        // unaffected. Its body (the RENAMES/THRU operands) is never interpreted.
        // Level 78 (named constant, e.g. 78 MAX-ROWS VALUE 100) — a compile-time
        // constant that occupies NO storage, so it is tolerated and dropped the same
        // way. (If such a constant is used as an OCCURS/PIC count, that reference is
        // separately unresolvable and rejected there — dropping the definition never
        // silently mis-sizes anything.) Without this, a 78 reached the tree as a
        // storageless leaf and would trip the storageless-leaf guard, wrongly
        // rejecting a valid copybook that mixes 78 constants with data fields.
        if (level == 88 || level == 66 || level == 78)
            return null;

        string? pictureText = null;
        var comp3 = false;
        var binary = false;
        var sawDisplay = false;
        var signSeparate = false;
        var signLeading = false;
        var synchronized = false;
        // A named binary usage (BINARY-CHAR/SHORT/LONG/DOUBLE) carries its byte
        // width in the usage name, not a PIC digit count. Null until one is seen.
        int? namedBinaryWidth = null;
        var namedBinarySigned = true; // SIGNED is the default; UNSIGNED flips it.
        var occursCount = 1;
        string? redefinesTarget = null;
        var isOdo = false;
        var odoMin = 0;
        var odoMax = 0;
        string? odoDependsOn = null;

        string name;
        int i;

        // Nameless REDEFINES: "<level> REDEFINES <target> [clauses...]" — the token
        // right after the level number is REDEFINES, with no data-name in between.
        // COBOL (and GnuCOBOL) treat the omitted name as FILLER: this is a FILLER
        // item that overlays <target>. Recognize it BEFORE tokens[1] is taken as a
        // name, otherwise "REDEFINES" itself would become the field name and the
        // subordinate fields would be laid out as fresh storage after the target
        // instead of overlaying it — a silent miscompute. Capturing the target here
        // routes it through the SAME REDEFINES overlay logic as the named form (see
        // ComputeOffsets); nothing downstream needs to know the name was elided.
        if (tokens[1].Equals("REDEFINES", StringComparison.OrdinalIgnoreCase))
        {
            if (tokens.Length < 3)
                throw new FormatException(
                    $"REDEFINES must be followed by the name of the item it redefines in \"{statement}\".");
            name = "FILLER";
            redefinesTarget = tokens[2];
            i = 3; // resume clause scanning after "REDEFINES <target>"
        }
        else
        {
            name = tokens[1];
            i = 2;
        }

        while (i < tokens.Length)
        {
            var token = tokens[i].ToUpperInvariant();
            switch (token)
            {
                case "PIC":
                case "PICTURE":
                    if (i + 1 >= tokens.Length)
                        throw new FormatException($"PIC clause with no picture string: \"{statement}\".");
                    pictureText = tokens[i + 1];
                    i += 2;
                    break;

                case "COMP-3":
                case "COMPUTATIONAL-3":
                    comp3 = true;
                    i += 1;
                    break;

                case "SIGN":
                    i += 1;
                    if (i < tokens.Length && tokens[i].Equals("IS", StringComparison.OrdinalIgnoreCase))
                        i += 1;
                    if (i < tokens.Length && tokens[i].Equals("LEADING", StringComparison.OrdinalIgnoreCase))
                    {
                        signLeading = true;
                        i += 1;
                    }
                    else if (i < tokens.Length && tokens[i].Equals("TRAILING", StringComparison.OrdinalIgnoreCase))
                    {
                        signLeading = false;
                        i += 1;
                    }
                    if (i < tokens.Length && tokens[i].Equals("SEPARATE", StringComparison.OrdinalIgnoreCase))
                    {
                        signSeparate = true;
                        i += 1;
                        if (i < tokens.Length && tokens[i].Equals("CHARACTER", StringComparison.OrdinalIgnoreCase))
                            i += 1;
                    }
                    break;

                case "OCCURS":
                {
                    // Two OCCURS shapes reach here:
                    //   fixed:    OCCURS n [TIMES]
                    //   variable: OCCURS [m TO] n [TIMES] DEPENDING ON dep   (ODO)
                    // The DEPENDING keyword is the discriminator. It (and any TO)
                    // only ever appears in the variable form, so scanning the
                    // OCCURS clause for it decides the shape before a count is taken
                    // — a fixed count is never mistaken for a bound and vice versa.
                    i += 1; // consume OCCURS

                    if (i >= tokens.Length || !int.TryParse(tokens[i], out var firstCount))
                        throw new FormatException(
                            $"OCCURS must be followed by an integer count: field '{name}' in \"{statement}\".");
                    i += 1;

                    // Optional "TO upper": present only in the variable form.
                    var hasTo = i < tokens.Length && tokens[i].Equals("TO", StringComparison.OrdinalIgnoreCase);
                    var upperCount = firstCount;
                    if (hasTo)
                    {
                        i += 1;
                        if (i >= tokens.Length || !int.TryParse(tokens[i], out upperCount))
                            throw new FormatException(
                                $"OCCURS ... TO must be followed by an integer upper bound: field '{name}' " +
                                $"in \"{statement}\".");
                        i += 1;
                    }

                    // TIMES is optional in both shapes.
                    if (i < tokens.Length && tokens[i].Equals("TIMES", StringComparison.OrdinalIgnoreCase))
                        i += 1;

                    var hasDepending = i < tokens.Length
                        && tokens[i].Equals("DEPENDING", StringComparison.OrdinalIgnoreCase);

                    if (hasDepending)
                    {
                        // Variable-length OCCURS (ODO). Bounds: "m TO n" gives
                        // min=m, max=n; a bare "n DEPENDING ON dep" (no TO) gives
                        // max=n with an implied min of 1 (COBOL leaves the minimum
                        // unstated in that form; 1 is the safe, conventional floor).
                        odoMin = hasTo ? firstCount : 1;
                        odoMax = upperCount;

                        i += 1; // consume DEPENDING
                        if (i < tokens.Length && tokens[i].Equals("ON", StringComparison.OrdinalIgnoreCase))
                            i += 1; // ON is syntactically optional in some dialects
                        if (i >= tokens.Length)
                            throw new FormatException(
                                $"OCCURS ... DEPENDING ON must name a field: field '{name}' in \"{statement}\".");
                        odoDependsOn = tokens[i];
                        i += 1;

                        if (odoMin < 0)
                            throw new FormatException(
                                $"OCCURS ... DEPENDING ON lower bound cannot be negative: field '{name}' " +
                                $"in \"{statement}\". (0 is allowed — OCCURS 0 TO n means the table may be empty.)");
                        if (odoMax < odoMin)
                            throw new FormatException(
                                $"OCCURS ... DEPENDING ON upper bound ({odoMax}) is below the lower bound " +
                                $"({odoMin}): field '{name}' in \"{statement}\".");

                        isOdo = true;
                        // Leave any trailing INDEXED BY / KEY IS to the default skip,
                        // one token at a time — a following PIC is still matched by
                        // its own keyword, exactly as in the fixed-count path.
                        break;
                    }

                    // No DEPENDING: this is the fixed-count form. "TO" without
                    // DEPENDING is not valid COBOL — reject it loudly rather than
                    // silently taking one of the bounds as a fixed count.
                    if (hasTo)
                        throw new FormatException(
                            $"OCCURS ... TO ... without DEPENDING ON is not a valid OCCURS clause: field " +
                            $"'{name}' in \"{statement}\". A range bound only has meaning with DEPENDING ON.");

                    occursCount = firstCount;
                    if (occursCount < 1)
                        throw new FormatException(
                            $"OCCURS count must be at least 1: field '{name}' in \"{statement}\".");
                    break;
                }

                case "REDEFINES":
                    // "REDEFINES target-name": this item overlays a prior sibling's
                    // bytes rather than starting at the running offset. Capture the
                    // target name here; the overlay (and target resolution / fail-loud
                    // if it's not a valid prior sibling) happens in ComputeOffsets,
                    // which is where sibling offsets are known.
                    if (i + 1 >= tokens.Length)
                        throw new FormatException(
                            $"REDEFINES must be followed by the name of the item it redefines: field '{name}' " +
                            $"in \"{statement}\".");
                    redefinesTarget = tokens[i + 1];
                    i += 2;
                    break;

                // Binary integer USAGE — COMP / COMPUTATIONAL / COMP-4 / COMP-5 /
                // BINARY. Big-endian two's-complement (signed) or magnitude
                // (unsigned), width fixed by the PIC digit count (see Binary.cs).
                // COMP-5 is "native binary" (host byte order on the source machine);
                // on the mainframe extracts PICASSO decodes that is big-endian too,
                // so it routes through the same path — consistent with the
                // big-endian-only assumption EBCDIC and COMP-3 already make.
                //
                // COMP-5 SIZING is deliberately the IBM Enterprise COBOL rule —
                // 2/4/8 bytes by digit count, identical to COMP-4 storage, widening
                // only the value range, not the byte width. That diverges from
                // Micro Focus / GnuCOBOL "native" COMP-5, which is byte-granular
                // (a 1-2 digit COMP-5 is 1 byte there). A differential run against
                // GnuCOBOL (2026-07-17) surfaced exactly this 1-byte gap on a handful
                // of COMP-5 test copybooks; PICASSO keeps the IBM sizing because its
                // whole target is IBM-mainframe data (EBCDIC cp037, COMP-3, big-endian).
                // A non-mainframe (MF/native) source with byte-granular COMP-5 would
                // be mis-sized — a documented dialect assumption, not a bug.
                case "COMP":
                case "COMPUTATIONAL":
                case "COMP-4":
                case "COMPUTATIONAL-4":
                case "COMP-5":
                case "COMPUTATIONAL-5":
                case "BINARY":
                    binary = true;
                    i += 1;
                    break;

                // Named binary usages — BINARY-CHAR / BINARY-SHORT / BINARY-LONG /
                // BINARY-DOUBLE. Unlike COMP/BINARY (whose width comes from the PIC
                // digit count), these carry a FIXED width in the usage name and no
                // PIC at all: 1 / 2 / 4 / 8 bytes respectively, big-endian, stored
                // two's-complement (SIGNED, the default) or plain magnitude
                // (UNSIGNED). SIGNED/UNSIGNED is a separate following token, consumed
                // here. A field with one of these used to VANISH (no PIC → no
                // FieldSpec → dropped as a childless group, shifting every following
                // offset) — a silent miscompute; now it is sized from the usage.
                case "BINARY-CHAR":
                case "BINARY-SHORT":
                case "BINARY-LONG":
                case "BINARY-DOUBLE":
                    namedBinaryWidth = token switch
                    {
                        "BINARY-CHAR" => 1,
                        "BINARY-SHORT" => 2,
                        "BINARY-LONG" => 4,
                        _ => 8, // BINARY-DOUBLE
                    };
                    i += 1;
                    if (i < tokens.Length && tokens[i].Equals("SIGNED", StringComparison.OrdinalIgnoreCase))
                    {
                        namedBinarySigned = true;
                        i += 1;
                    }
                    else if (i < tokens.Length && tokens[i].Equals("UNSIGNED", StringComparison.OrdinalIgnoreCase))
                    {
                        namedBinarySigned = false;
                        i += 1;
                    }
                    break;

                // BINARY-C-LONG is GnuCOBOL's platform-dependent C `long` (4 bytes on
                // ILP32/LLP64, 8 on LP64). Its width is not fixed by the standard, so
                // guessing one risks a silent miscompute — rejected loudly instead.
                case "BINARY-C-LONG":
                    throw UnsupportedUsage("BINARY-C-LONG", "platform-dependent C long", name, statement);

                // PACKED-DECIMAL is byte-identical to COMP-3; alias it to the same
                // packed-decimal path rather than giving it a parallel implementation.
                case "PACKED-DECIMAL":
                    comp3 = true;
                    i += 1;
                    break;

                // Explicit USAGE DISPLAY. DISPLAY is the default sizing (one byte
                // per digit/char), so on its own it is a no-op that already fell
                // through the default skip. It is captured now for ONE reason: a
                // group's USAGE is inherited by subordinate elementary items, and an
                // explicit DISPLAY on a child (or an inner group) must OVERRIDE an
                // inherited COMP-3/Binary from an outer group — "child's own usage
                // wins". Without capturing it, the inheritance pass could not tell an
                // explicit DISPLAY (override) from no usage clause at all (inherit).
                // DISPLAY-1 (DBCS) and UTF-8 are distinct tokens handled above and
                // never match here.
                case "DISPLAY":
                    sawDisplay = true;
                    i += 1;
                    break;

                // Unsupported USAGE clauses. Each changes a field's physical byte
                // width away from the DISPLAY (one byte per digit/char) sizing this
                // parser computes — a 4- or 8-byte float, a Micro Focus dialect
                // binary, an aligned/synchronized item, a national (double-byte)
                // field, or an index/pointer with an implementation-defined size.
                // Skipping the clause (the pre-fix behaviour) left the field
                // mis-sized with no error. COMP-1/COMP-2 carry no PIC at all, so the
                // item would have vanished entirely, shifting every following offset.
                // Rejected loudly here, one named error per usage, exactly like
                // REDEFINES — never silently miscomputed. COMP-3 (COMPUTATIONAL-3)
                // packed decimal and the binary COMP forms above stay supported.
                case "COMP-1":
                case "COMPUTATIONAL-1":
                    throw UnsupportedUsage("COMP-1", "single-precision float", name, statement);
                case "COMP-2":
                case "COMPUTATIONAL-2":
                    throw UnsupportedUsage("COMP-2", "double-precision float", name, statement);
                case "COMP-6":
                case "COMPUTATIONAL-6":
                    throw UnsupportedUsage("COMP-6", "unsigned packed decimal", name, statement);
                case "COMP-X":
                case "COMPUTATIONAL-X":
                    throw UnsupportedUsage("COMP-X", "binary", name, statement);
                case "INDEX":
                    throw UnsupportedUsage("INDEX", "index", name, statement);
                case "POINTER":
                    throw UnsupportedUsage("POINTER", "pointer", name, statement);
                case "POINTER-32":
                    throw UnsupportedUsage("POINTER-32", "32-bit pointer", name, statement);
                case "POINTER-64":
                    throw UnsupportedUsage("POINTER-64", "64-bit pointer", name, statement);
                case "PROCEDURE-POINTER":
                    throw UnsupportedUsage("PROCEDURE-POINTER", "procedure pointer", name, statement);
                case "FUNCTION-POINTER":
                    throw UnsupportedUsage("FUNCTION-POINTER", "function pointer", name, statement);
                case "PROGRAM-POINTER":
                    throw UnsupportedUsage("PROGRAM-POINTER", "program pointer", name, statement);
                // OBJECT in clause position is the OBJECT REFERENCE usage (two
                // tokens; REFERENCE falls through the default skip and never
                // matters). Like COMP-1/COMP-2 it carries no PIC, so skipping it
                // used to drop the field entirely and shift every following offset.
                case "OBJECT":
                    throw UnsupportedUsage("OBJECT REFERENCE", "object reference", name, statement);

                // SYNCHRONIZED / SYNC aligns a BINARY item to its natural byte
                // boundary by inserting slack bytes before it (handled in
                // ComputeOffsets, which knows the running offset). It changes only
                // WHERE a field sits, never HOW its value is read, so it is a flag,
                // not a value transform. On a non-binary item (DISPLAY, COMP-3,
                // Text/edited, a group) it is a no-op in IBM COBOL — carried anyway
                // and simply produces no padding. Any trailing LEFT/RIGHT qualifier
                // falls through the default one-token skip below.
                case "SYNC":
                case "SYNCHRONIZED":
                    synchronized = true;
                    i += 1;
                    break;
                case "NATIONAL":
                    throw UnsupportedUsage("NATIONAL", "national (double-byte)", name, statement);
                // DBCS (DISPLAY-1) and UTF-8 give a character a physical width other
                // than one byte (2 bytes DBCS; 1-4 bytes UTF-8) — DISPLAY sizing
                // would undercount. Note plain DISPLAY stays supported: it is not a
                // token here, it falls through the default skip like VALUE/JUSTIFIED.
                case "DISPLAY-1":
                    throw UnsupportedUsage("DISPLAY-1", "DBCS (double-byte)", name, statement);
                case "UTF-8":
                    throw UnsupportedUsage("UTF-8", "UTF-8", name, statement);

                default:
                    // A token we don't recognize. Most are harmless no-storage clause
                    // words — VALUE and its literal, JUSTIFIED, BLANK WHEN ZERO,
                    // INDEXED BY … — that do NOT change the byte layout, so they are
                    // still skipped one token at a time (rejecting them would break
                    // valid copybooks, and a multi-word VALUE string literal splits
                    // into arbitrary word tokens here).
                    //
                    // The exception is a computational-shaped usage we didn't match
                    // above — COMPUTATIONAL3 (a missing hyphen), COMP-0, COMP-7. Those
                    // WOULD change a field's physical width, and silently skipping one
                    // leaves the field mis-sized as DISPLAY with no error — a silent
                    // miscompute, the exact failure mode this parser exists to prevent.
                    // (The supported/rejected COMP forms — COMP-3/4/5, COMP-1/2/6/X,
                    // BINARY, PACKED-DECIMAL — all match explicit cases above and never
                    // reach here, so any COMP-N arriving in the default is unrecognized.)
                    if (UnknownComputationalUsage.IsMatch(token))
                        throw new FormatException(
                            $"Unrecognized COMPUTATIONAL usage '{tokens[i]}' on field '{name}' in " +
                            $"\"{statement}\". Supported binary usages are COMP / COMP-4 / COMP-5 / BINARY " +
                            $"and COMP-3 / PACKED-DECIMAL (COMP-1/COMP-2 float are rejected explicitly). A " +
                            $"mistyped usage (e.g. a dropped hyphen) must not be silently ignored — the " +
                            $"field would be mis-sized.");
                    i += 1;
                    break;
            }
        }

        // The inheritable USAGE this entry states in its own clause. COMP-3 and the
        // binary forms take precedence over an explicit DISPLAY (a nonsensical
        // combination would be malformed source anyway); None means the entry
        // stated no usage and will inherit any ancestor group's. This is captured
        // for a group item too (which carries no PIC), so its usage can flow down.
        // A named binary usage counts as a declared (non-None) usage of its own so
        // the group-USAGE inheritance pass treats it as already-typed and never
        // tries to rebuild its width-explicit FieldSpec from a (nonexistent) PIC.
        var declaredUsage =
            comp3 ? DeclaredUsage.Comp3 :
            (binary || namedBinaryWidth != null) ? DeclaredUsage.Binary :
            sawDisplay ? DeclaredUsage.Display :
            DeclaredUsage.None;

        PicSpec? pic = pictureText != null ? Pic.ParsePicClause(pictureText) : null;

        // A named binary usage fixes the field's width by itself and is normally
        // pic-less. Combining it with a PIC (nonstandard) would pit the usage's
        // fixed width against the digit count — reject rather than guess which wins.
        // The same intrinsic-sign reasoning as COMP applies: SIGN IS ... SEPARATE is
        // contradictory on a binary field and is rejected too.
        if (namedBinaryWidth != null)
        {
            if (pictureText != null)
                throw new FormatException(
                    $"A named binary usage (BINARY-CHAR/SHORT/LONG/DOUBLE) fixes its own byte width and cannot " +
                    $"be combined with a PIC clause: field '{name}' in \"{statement}\".");
            if (comp3 || binary)
                throw new FormatException(
                    $"A named binary usage cannot be combined with another USAGE clause: field '{name}' in \"{statement}\".");
            if (signSeparate)
                throw new FormatException(
                    $"SIGN IS ... SEPARATE cannot be combined with a named binary usage: its sign is intrinsic " +
                    $"to the two's-complement representation, not a separate byte: field '{name}' in \"{statement}\".");
        }

        FieldSpec? field = null;
        if (pic != null)
            field = BuildFieldSpec(name, pic, comp3, binary, signSeparate, signLeading);
        else if (namedBinaryWidth != null)
            field = new FieldSpec
            {
                Name = name,
                Type = FieldType.Binary,
                // Digits == 0 marks this as a width-explicit (named) binary so the
                // codec sizes it from Len, not a digit count. Scale is 0: these
                // usages take no PICTURE, so no implied decimal.
                Digits = 0,
                Scale = 0,
                Signed = namedBinarySigned,
                Len = namedBinaryWidth.Value,
            };

        return new ParsedStatement
        {
            LevelNumber = level,
            Name = name,
            Field = field,
            OccursCount = occursCount,
            RedefinesTarget = redefinesTarget,
            Synchronized = synchronized,
            DeclaredUsage = declaredUsage,
            Pic = pic,
            SignSeparate = signSeparate,
            SignLeading = signLeading,
            IsOdo = isOdo,
            OdoMin = odoMin,
            OdoMax = odoMax,
            OdoDependsOn = odoDependsOn,
        };
    }

    /// <summary>
    /// Builds the named FormatException for an unsupported USAGE clause. The
    /// message identifies which usage was seen (COMP vs COMP-1 vs BINARY, …) so a
    /// caller can tell exactly what tripped it, names the field, and points at the
    /// README — matching the REDEFINES / ODO rejection style. Every one of these
    /// gives a field a physical width other than DISPLAY's one-byte-per-digit/char,
    /// which this parser does not compute; rejecting beats silently mis-sizing.
    /// </summary>
    private static FormatException UnsupportedUsage(string usage, string kind, string name, string statement) =>
        new FormatException(
            $"{usage} ({kind}) usage is not supported: field '{name}' in \"{statement}\". A field with this " +
            "usage has a physical width this parser does not compute, and silently skipping the clause would " +
            "mis-size it. See the supported USAGE forms and the 'Not supported (v1)' section in the README.");

    /// <summary>
    /// Pushes a group's USAGE down onto the subordinate elementary items that
    /// state none of their own — standard COBOL, which the flat parse pass cannot
    /// do because it builds each elementary <see cref="FieldSpec"/> in isolation,
    /// before its ancestor groups are known. Runs top-down once the tree is
    /// assembled:
    ///
    /// <list type="bullet">
    /// <item><paramref name="inherited"/> is the effective USAGE flowing in from
    /// the nearest enclosing group that specified one (<see cref="DeclaredUsage.None"/>
    /// at the record root).</item>
    /// <item>The usage this node contributes to its subtree is its OWN declared
    /// usage if it stated one, else the inherited usage — so a child's own usage
    /// overrides an inherited one, and an inner group's usage overrides an outer
    /// group's for its subtree (nearest ancestor wins).</item>
    /// <item>An elementary item that stated NO usage and inherits COMP-3 or Binary
    /// gets its <see cref="FieldSpec"/> rebuilt from its retained PICTURE with that
    /// usage — the only case whose first-built field (a DISPLAY default) is wrong.
    /// An inherited DISPLAY/None is a no-op (DISPLAY is already the default), and an
    /// elementary item with its OWN usage keeps the field it was built with.</item>
    /// </list>
    ///
    /// If an inherited usage is illegal for the child's picture (e.g. group COMP-3
    /// over a PIC X child — a contradiction COBOL itself rejects), the rebuild goes
    /// through the same <see cref="BuildFieldSpec"/> guards and fails loud, never a
    /// silent mis-size. A group carrying an UNSUPPORTED usage (COMP-1, POINTER, …)
    /// never reaches here: <see cref="ParseStatement"/> already rejected it at the
    /// group's own statement.
    /// </summary>
    private static void InheritGroupUsage(CopybookNode node, DeclaredUsage inherited)
    {
        // The usage this node passes on: its own if declared, else what it inherited.
        var effective = node.DeclaredUsage != DeclaredUsage.None ? node.DeclaredUsage : inherited;

        if (node.IsGroup)
        {
            foreach (var child in node.Children)
                InheritGroupUsage(child, effective);
            return;
        }

        // Elementary item. Only when it declared NO usage of its own AND inherits a
        // non-DISPLAY usage is its first-built (DISPLAY-sized) FieldSpec wrong and in
        // need of rebuilding. Its own usage, or an inherited DISPLAY/None, leaves the
        // already-correct field untouched — keeping every non-group-USAGE layout
        // byte-identical to before this feature.
        if (node.DeclaredUsage == DeclaredUsage.None
            && (inherited == DeclaredUsage.Comp3 || inherited == DeclaredUsage.Binary))
        {
            // An elementary node always has a PIC here: a PIC-less elementary would
            // be a group. Rebuild from the retained picture with the inherited usage,
            // preserving the item's own SIGN flags.
            node.Field = BuildFieldSpec(
                node.Name,
                node.DeclaredPic!,
                comp3: inherited == DeclaredUsage.Comp3,
                binary: inherited == DeclaredUsage.Binary,
                node.SignSeparate,
                node.SignLeading);
        }
    }

    private static FieldSpec BuildFieldSpec(string name, PicSpec pic, bool comp3, bool binary, bool signSeparate, bool signLeading)
    {
        // An edited picture (Z $ , . - + CR DB …) is not value-interpreted: Pic
        // computed its exact display width, and it is surfaced as a Text field of
        // that width — byte passthrough that round-trips unchanged. COMP-3 and
        // SIGN IS ... SEPARATE are nonsensical on an edited (character-formatted)
        // picture, so both combinations are rejected loudly, exactly as they are
        // for a plain alphanumeric field below.
        if (pic.Category == PicCategory.Edited)
        {
            if (comp3)
                throw new FormatException($"COMP-3 cannot be combined with an edited PIC clause: field '{name}'.");
            if (binary)
                throw new FormatException($"Binary (COMP) cannot be combined with an edited PIC clause: field '{name}'.");
            if (signSeparate)
                throw new FormatException($"SIGN IS ... SEPARATE cannot be combined with an edited PIC clause: field '{name}'.");

            return new FieldSpec
            {
                Name = name,
                Type = FieldType.Text,
                Len = pic.Length,
            };
        }

        if (pic.Category == PicCategory.Alphanumeric)
        {
            if (comp3)
                throw new FormatException($"COMP-3 is only valid for numeric PIC clauses: field '{name}'.");
            if (binary)
                throw new FormatException($"Binary (COMP) is only valid for numeric PIC clauses: field '{name}'.");
            if (signSeparate)
                throw new FormatException($"SIGN clause is only valid for numeric PIC clauses: field '{name}'.");

            return new FieldSpec
            {
                Name = name,
                Type = FieldType.Text,
                Len = pic.Length,
            };
        }

        if (comp3)
        {
            if (signSeparate)
                throw new FormatException($"SIGN IS ... SEPARATE cannot be combined with COMP-3: field '{name}'.");

            return new FieldSpec
            {
                Name = name,
                Type = FieldType.Comp3,
                Digits = pic.Digits,
                Scale = pic.Scale,
                Signed = pic.Signed,
                Len = Comp3.ByteLength(pic.Digits),
            };
        }

        if (binary)
        {
            // The sign of a binary integer is intrinsic to its two's-complement
            // representation (the PIC's S), never a separate byte — so SIGN IS ...
            // SEPARATE on a COMP field is contradictory and rejected loudly, the
            // same fail-loud stance COMP-3 takes.
            if (signSeparate)
                throw new FormatException(
                    $"SIGN IS ... SEPARATE cannot be combined with binary (COMP): its sign is intrinsic " +
                    $"to the two's-complement representation, not a separate byte: field '{name}'.");

            // A COBOL numeric item holds at most 18 digits, and the binary width
            // table (2/4/8 bytes) stops there. Reject an over-long picture here with
            // a named FormatException that identifies the field — rather than letting
            // Binary.ByteLength surface a raw ArgumentOutOfRangeException, which would
            // leak an internal parameter name and break the fail-loud message style.
            if (pic.Digits > 18)
                throw new FormatException(
                    $"Binary (COMP) fields hold at most 18 digits; field '{name}' declares {pic.Digits}.");

            return new FieldSpec
            {
                Name = name,
                Type = FieldType.Binary,
                Digits = pic.Digits,
                Scale = pic.Scale,
                Signed = pic.Signed,
                Len = Binary.ByteLength(pic.Digits),
            };
        }

        // A signed DISPLAY numeric without SIGN IS ... SEPARATE carries its sign
        // as an OVERPUNCH in the zone nibble of the trailing digit (default) or
        // leading digit (SIGN IS LEADING) — no extra byte. See Overpunch.cs and
        // FlatFileCodec's numeric decode/encode. Both the separate and overpunch
        // forms land here; only the separate form adds a sign byte to the width.
        return new FieldSpec
        {
            Name = name,
            Type = FieldType.NumericDisplay,
            Digits = pic.Digits,
            Scale = pic.Scale,
            Signed = pic.Signed,
            SignSeparate = signSeparate,
            SignLeading = signLeading,
            Len = pic.Digits + (signSeparate ? 1 : 0),
        };
    }

    /// <summary>
    /// The name given to the 01 record synthesized for a headless copy member.
    /// Deliberately not a plausible COBOL name: it names something the copybook
    /// itself never said, so it should not read as though it did.
    /// </summary>
    public const string SyntheticRecordName = "SYNTHETIC-RECORD";

    public static CopybookNode? BuildTree(IReadOnlyList<string> statements) =>
        BuildTree(statements, SyntheticRecordName, out _);

    /// <summary>
    /// Resolves level-number nesting into a tree.
    ///
    /// A copy member with no 01 of its own — entries starting at 03 or 05,
    /// meant to be COPY'd into a record the including program names — gets one
    /// synthesized, named <paramref name="recordName"/>, and
    /// <paramref name="rootIsSynthetic"/> comes back true. That shape is
    /// ordinary in real copybooks (an FD record layout is usually written this
    /// way), and the wrapper changes no field's offset or length: it only gives
    /// the tree the root that COBOL would have gotten from the program.
    ///
    /// Two 01-level records in one copybook is a different thing entirely and
    /// still an error. Those are alternative record layouts, not siblings, and
    /// wrapping them would silently concatenate them into one wrong record.
    /// </summary>
    /// <summary>
    /// Resolves level-number nesting into a forest of top-level entries, without
    /// deciding what they mean (one 01 record, several alternative 01 records, or
    /// a headless copy member). Level-88 condition-names parse to null and are
    /// skipped without touching the level stack. Shared by <see cref="BuildTree"/>
    /// (which collapses the forest to a single record or rejects a multi-01) and
    /// <see cref="ParseRecords"/> (which keeps each 01 as an independent record).
    /// </summary>
    private static List<CopybookNode> BuildTops(IReadOnlyList<string> statements)
    {
        var tops = new List<CopybookNode>();
        var stack = new List<CopybookNode>();

        foreach (var statementText in statements)
        {
            var parsed = ParseStatement(statementText);

            // A null parse is a tolerated-and-ignored statement (a level-88
            // condition-name): no node, and — crucially — it never touches the
            // level stack, so it can't shift an offset or reparent a sibling.
            if (parsed is null)
                continue;

            var node = new CopybookNode(parsed.Name, parsed.LevelNumber)
            {
                Field = parsed.Field,
                OccursCount = parsed.OccursCount,
                RedefinesTarget = parsed.RedefinesTarget,
                Synchronized = parsed.Synchronized,
                DeclaredUsage = parsed.DeclaredUsage,
                DeclaredPic = parsed.Pic,
                SignSeparate = parsed.SignSeparate,
                SignLeading = parsed.SignLeading,
                IsOdoTable = parsed.IsOdo,
                OdoMin = parsed.OdoMin,
                OdoMax = parsed.OdoMax,
                OdoDependsOn = parsed.OdoDependsOn,
            };

            while (stack.Count > 0 && stack[stack.Count - 1].LevelNumber >= node.LevelNumber)
                stack.RemoveAt(stack.Count - 1);

            if (stack.Count == 0)
                tops.Add(node);
            else
            {
                // The item this one nests under. If that parent already carries a
                // PICTURE it is an ELEMENTARY item, and an elementary item cannot
                // contain sub-items — a compiler rejects "05 A PIC X. / 10 B PIC X."
                // Left unchecked, the subordinate was silently attached and then
                // dropped by the flattener (it emits either a group's children or an
                // elementary's own bytes, never both), so field B vanished and every
                // following offset shifted — a silent miscompute. Fail loudly instead.
                var parent = stack[stack.Count - 1];
                if (parent.Field is not null)
                {
                    // A named binary USAGE (BINARY-CHAR/SHORT/LONG/DOUBLE) on an item
                    // that turns out to have subordinates is a group stating a usage
                    // it means to pass DOWN. This parser does not inherit a named
                    // binary width to children (unlike COMP-3/COMP group usage), so
                    // say so specifically rather than calling the group "elementary".
                    if (parent.Field.Type == FieldType.Binary && parent.Field.Digits == 0)
                        throw new FormatException(
                            $"Item '{parent.Name}' (level {parent.LevelNumber:D2}) states a named binary USAGE " +
                            $"(BINARY-CHAR/SHORT/LONG/DOUBLE) but also has a subordinate item '{node.Name}' " +
                            $"(level {node.LevelNumber:D2}). A named binary USAGE is not inherited to subordinate " +
                            $"items here — state the USAGE on each elementary child directly (e.g. " +
                            $"'{node.LevelNumber:D2} {node.Name} BINARY-LONG.'), not on the group.");

                    throw new FormatException(
                        $"Item '{parent.Name}' (level {parent.LevelNumber:D2}) is an elementary item (it " +
                        $"defines its own value — a PICTURE or a fixed-width binary USAGE) but also has a " +
                        $"subordinate item '{node.Name}' (level {node.LevelNumber:D2}). An elementary item " +
                        $"cannot contain sub-items — fix the level numbers, or remove the PIC/USAGE so it is " +
                        $"a group. (A group item defines no value of its own; only its elementary children do.)");
                }
                parent.Children.Add(node);
            }

            stack.Add(node);
        }

        return tops;
    }

    public static CopybookNode? BuildTree(
        IReadOnlyList<string> statements,
        string recordName,
        out bool rootIsSynthetic)
    {
        rootIsSynthetic = false;

        var tops = BuildTops(statements);

        if (tops.Count == 0)
            return null;

        // A single 01 record: the ordinary case, nothing to do.
        if (tops.Count == 1 && tops[0].LevelNumber == 1)
            return tops[0];

        if (tops.Any(t => t.LevelNumber == 1))
        {
            if (tops.Count == 1)
                return tops[0];

            throw new FormatException(
                $"Copybook has more than one top-level record ('{tops[0].Name}' and '{tops[1].Name}') — " +
                "multiple records per copybook are not supported. These are alternative layouts, not " +
                "fields of one record, so they cannot be merged into a single structure.");
        }

        // Headless: no 01 anywhere. Siblings of one record must share a level
        // number; anything else is malformed and is not guessed at.
        var level = tops[0].LevelNumber;
        var odd = tops.FirstOrDefault(t => t.LevelNumber != level);
        if (odd != null)
            throw new FormatException(
                $"Copybook has no 01-level record, and its top-level entries disagree on level number " +
                $"(level {level} '{tops[0].Name}' vs level {odd.LevelNumber} '{odd.Name}'). " +
                "Entries of one record must share a level number.");

        var synthetic = new CopybookNode(recordName, 1);
        synthetic.Children.AddRange(tops);
        rootIsSynthetic = true;
        return synthetic;
    }

    /// <summary>
    /// Rejects a SYNCHRONIZED binary item that occurs, or sits inside an OCCURS
    /// table (fixed or ODO). Per-occurrence alignment adds trailing-element slack
    /// rules — a table's element stride must stay a multiple of the SYNC item's
    /// boundary across every occurrence — that this codec deliberately does not
    /// model. The flat corpus SYNC files never hit this; a table with a SYNC
    /// binary inside fails loud rather than being silently mis-aligned. SYNC on a
    /// non-binary item is a no-op and is never rejected, inside a table or not.
    /// </summary>
    private static void RejectSyncInOccurs(CopybookNode node, bool insideOccurs)
    {
        var itemRepeats = node.OccursCount > 1 || node.IsOdoTable;
        if (node.Synchronized && node.Field?.Type == FieldType.Binary && (insideOccurs || itemRepeats))
            throw new FormatException(
                $"SYNCHRONIZED (SYNC) alignment on binary field '{node.Name}' inside an OCCURS table is not " +
                "supported: per-occurrence alignment adds trailing-element slack rules this codec does not " +
                "model. A SYNC binary item must sit outside any OCCURS. (SYNC on a flat, non-repeating binary " +
                "item is supported.)");

        var nowInside = insideOccurs || itemRepeats;
        foreach (var child in node.Children)
            RejectSyncInOccurs(child, nowInside);
    }

    /// <summary>
    /// Depth-first running byte counter. An OCCURS item's span is its single
    /// iteration's length times its repeat count: the children (or the elementary
    /// field) are laid out once, then the whole item is multiplied so that every
    /// field after it starts past all the copies. The per-copy field offsets
    /// themselves are materialized later, in <see cref="Flatten"/>.
    ///
    /// A child carrying a REDEFINES target does NOT start at the running offset:
    /// it overlays the named prior sibling, starting at that sibling's byte offset
    /// and sharing its bytes. It adds no storage — the next ordinary sibling
    /// continues from where it would have without the redefinition — unless the
    /// redefinition is longer than its target, in which case the group extends to
    /// cover it. A group's length is therefore the MAX end-offset of its children,
    /// not the running cumulative sum (the two coincide when nothing overlaps).
    /// </summary>
    public static int ComputeOffsets(CopybookNode node, int startOffset)
    {
        int oneIterationLen;
        if (node.IsGroup)
        {
            var running = startOffset;   // where the next ORDINARY sibling begins
            var maxEnd = startOffset;    // farthest byte any child reaches
            // Prior siblings, by name, to resolve a REDEFINES target's offset.
            // Latest definition of a name wins (COBOL names are normally unique
            // among siblings; a later duplicate would shadow an earlier one, which
            // is the sensible reading for "the prior sibling of this name").
            var priorSiblingStart = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var child in node.Children)
            {
                int childStart;
                if (child.RedefinesTarget != null)
                {
                    if (!priorSiblingStart.TryGetValue(child.RedefinesTarget, out childStart))
                        throw new FormatException(
                            $"REDEFINES target '{child.RedefinesTarget}' not found for field '{child.Name}': " +
                            "a REDEFINES must name a prior item at the same level in the same group. No such " +
                            $"prior sibling exists in group '{node.Name}'. (A forward reference, a mis-typed " +
                            "name, or a target at a different level all trip this — the overlay offset would " +
                            "otherwise be a silent guess.)");

                    ComputeOffsets(child, childStart);
                    // No new storage: the running offset only advances if this
                    // redefinition reaches past where the next sibling already sits.
                    running = Math.Max(running, childStart + child.Len);
                }
                else
                {
                    childStart = running;
                    ComputeOffsets(child, childStart);
                    running = childStart + child.Len;
                }

                maxEnd = Math.Max(maxEnd, childStart + child.Len);
                priorSiblingStart[child.Name] = childStart;
            }

            oneIterationLen = maxEnd - startOffset;
        }
        else
        {
            // A SYNCHRONIZED binary item is aligned to its natural byte boundary
            // (its width: 2/4/8) by padding the running offset up to the next
            // multiple of that width. The slack sits BEFORE the item and inside
            // this node's span, so the parent's running offset advances past both
            // the padding and the item. Recomputed here on every pass (an ODO
            // record's SYNC field can start at a different offset per count), and
            // materialized into a synthetic FILLER leaf by Flatten. Non-binary
            // SYNC, and any non-SYNC item, get zero slack — a true no-op.
            var slack = 0;
            if (node.Synchronized && node.Field!.Type == FieldType.Binary)
            {
                var width = node.Field.Len; // 2, 4, or 8
                var remainder = startOffset % width;
                if (remainder != 0)
                    slack = width - remainder;
            }
            node.SyncSlack = slack;
            node.Field!.Start = startOffset + slack;
            oneIterationLen = slack + node.Field.Len;
        }

        node.Start = startOffset;
        try
        {
            node.Len = checked(oneIterationLen * node.OccursCount);
        }
        catch (OverflowException)
        {
            throw new OverflowException(
                $"OCCURS item '{node.Name}' has {node.OccursCount} copies of a {oneIterationLen}-byte " +
                $"iteration, which overflows a 32-bit byte length ({(long)oneIterationLen * node.OccursCount} " +
                "bytes total). No real copybook needs a repeat count this large — failing loudly here beats " +
                "silently wrapping to a negative or wrong length.");
        }
        return startOffset + node.Len;
    }

    public static void Flatten(CopybookNode node, List<FieldSpec> into)
        => EmitSubtree(node, into, shift: 0, prefix: null);

    /// <summary>
    /// Flattens a subtree into leaf <see cref="FieldSpec"/>s, expanding every OCCURS
    /// level it meets — at any depth. <paramref name="shift"/> is the byte offset to
    /// add to every leaf's <see cref="FieldSpec.Start"/> (the accumulated stride of
    /// the enclosing OCCURS iterations); <paramref name="prefix"/> is the accumulated
    /// name prefix (null at the record root, else a chain of NAME(index) segments).
    ///
    /// <para>
    /// The OCCURS expansion is <b>compositional</b>: an OCCURS node produces
    /// <c>OccursCount</c> 1-indexed copies, and for EACH copy it recurses into its
    /// own children/field with the copy's index appended to the prefix and the copy's
    /// stride added to the shift. When a copy's subtree contains a further OCCURS,
    /// that inner OCCURS expands in turn — so a table of tables yields names carrying
    /// every level's index (<c>LINE(1)-ITEM(1)-CODE</c>, <c>LINE(2)-ITEM(4)-CODE</c>,
    /// …) with sequential offsets. One OCCURS iteration's byte length is
    /// <c>node.Len / OccursCount</c>; because <see cref="ComputeOffsets"/> already
    /// rolled any inner OCCURS multiplication into that Len, the stride is the full
    /// size of one fully-expanded inner element, exactly as required.
    /// </para>
    ///
    /// <para>
    /// A non-OCCURS group contributes no name segment of its own (matching the
    /// single-level convention <c>LINE-ITEM(2)-ITEM-QTY</c>, where only the
    /// OCCURS-bearing group and the leaf name appear); it just passes prefix and
    /// shift to its children. A leaf's name is the prefix + '-' + its own name, or
    /// just its own name at the record root. The '-' separator can never collide
    /// with a real field name because every prefix segment carries a
    /// parenthesized index and '(' / ')' are not legal in a COBOL identifier.
    /// </para>
    /// </summary>
    private static void EmitSubtree(CopybookNode node, List<FieldSpec> into, int shift, string? prefix)
    {
        // An OCCURS level (fixed) or an ODO table (always expanded with 1-indexed
        // names even at count 1, so decode and encode agree on field keys).
        if (node.OccursCount > 1 || node.IsOdoTable)
        {
            // An ODO table resolved to 0 occurrences (OCCURS 0 TO n, count 0)
            // contributes no fields at all; everything after it has already been
            // laid out starting at this table's offset. Return before the division
            // below (node.Len is 0 here, so oneIterationLen would divide by zero).
            if (node.OccursCount == 0)
                return;

            // One iteration's byte length. For a nested table this already includes
            // any inner OCCURS multiplication (ComputeOffsets rolled it into Len), so
            // the stride is the full size of one fully-expanded inner element.
            var oneIterationLen = node.Len / node.OccursCount;
            for (var index = 1; index <= node.OccursCount; index++)
            {
                var childShift = shift + (index - 1) * oneIterationLen;
                var indexedName = prefix is null
                    ? $"{node.Name}({index})"
                    : $"{prefix}-{node.Name}({index})";

                if (node.IsGroup)
                {
                    // Group OCCURS: the parenthesized index rides on the group name
                    // and prefixes every descendant, recursing so an inner OCCURS
                    // expands too (e.g. LINE(2)-ITEM(1)-CODE for a table of tables).
                    foreach (var child in node.Children)
                        EmitSubtree(child, into, childShift, indexedName);
                }
                else
                {
                    // Elementary OCCURS: the leaf itself becomes prefix-NAME(index)
                    // (or NAME(index) at the root).
                    into.Add(ShiftField(node.Field!, childShift, indexedName));
                }
            }
            return;
        }

        if (node.IsGroup)
        {
            // A non-OCCURS group contributes no name segment: pass prefix/shift down.
            foreach (var child in node.Children)
                EmitSubtree(child, into, shift, prefix);
        }
        else
        {
            // A SYNC binary item that needed alignment carries slack bytes before
            // it. Emit them as a synthetic FILLER leaf so the flat layout stays
            // contiguous (no gaps) — decode captures the padding and encode emits
            // it, so a SYNC record round-trips byte-for-byte with no codec change.
            // A SYNC binary never occurs (rejected upstream), so a slack-bearing leaf
            // is never inside an OCCURS and its shift is 0; it is added anyway so the
            // filler's Start stays correct regardless. The name embeds the slack's
            // start offset, and '-' is legal in a COBOL identifier but a real field
            // would never be named this — a collision is not a correctness risk
            // anyway, since fillers carry passthrough bytes, not decoded values.
            if (node.SyncSlack > 0)
            {
                var slackStart = node.Field!.Start - node.SyncSlack + shift;
                into.Add(new FieldSpec
                {
                    Name = $"FILLER-SYNC-{slackStart}",
                    Start = slackStart,
                    Len = node.SyncSlack,
                    Type = FieldType.Text,
                });
            }

            // A copy, not the tree's own FieldSpec: BuildConcreteLayout recomputes
            // offsets on the shared tree at different counts, and a returned layout
            // must never be mutated retroactively by a later call. At the record
            // root (prefix null, shift 0) this reproduces the field's own name/offset.
            var name = prefix is null ? node.Field!.Name : $"{prefix}-{node.Field!.Name}";
            into.Add(ShiftField(node.Field!, shift, name));
        }
    }

    /// <summary>
    /// A copy of a leaf field for one OCCURS iteration: same type and sizing, its
    /// absolute start advanced by <paramref name="shift"/> and renamed. The source
    /// field's own Start is iteration 1's absolute offset, so shift == 0 reproduces
    /// it exactly. A `with` expression, not a hand-listed copy: every other
    /// property (including any FieldSpec gains later) carries over automatically.
    /// </summary>
    private static FieldSpec ShiftField(FieldSpec source, int shift, string name) => source with
    {
        Name = name,
        Start = source.Start + shift,
    };
}
