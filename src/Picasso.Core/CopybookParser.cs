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

        RejectNestedOccurs(root, insideOccurs: false);

        ComputeOffsets(root, 0);

        var flat = new List<FieldSpec>();
        Flatten(root, flat);

        return new ParsedCopybook(root, flat, rootIsSynthetic);
    }

    /// <summary>
    /// Columns 1-6 of a traditional fixed-format line: a six-digit line-sequence
    /// number. Free-format COBOL never opens a line with six digits — real lines
    /// start with a level number ("01", "05") or a keyword — so a match here
    /// identifies the line as fixed-format on its own, with no mode flag needed.
    /// Detection is per line, so a file may mix both forms.
    /// </summary>
    private static readonly Regex FixedFormatSequenceNumber =
        new Regex(@"^\d{6}", RegexOptions.Compiled);

    /// <summary>Columns 8-72 of a fixed-format line, once columns 1-6 are gone.</summary>
    private const int FixedFormatCodeLength = 66;

    /// <summary>
    /// Strips free-format "*&gt;" comments (to end of line, anywhere on the
    /// line) and handles traditional fixed-format lines: the columns-1-6
    /// sequence number is dropped, a '*' in column 7 marks the whole line as a
    /// comment, and anything at or past column 73 (the identification area, which
    /// the compiler ignores) is truncated so it can't reach the tokenizer.
    ///
    /// A statement wrapping across several fixed-format lines needs no special
    /// handling: SplitStatements splits on '.' across the whole document and
    /// collapses embedded newlines. The column-7 '-' continuation indicator
    /// (splitting a text literal with no intervening space) is an intentional
    /// non-goal — PICASSO never parses literal content.
    /// </summary>
    public static string StripComments(string source)
    {
        var sb = new StringBuilder();
        foreach (var rawLine in source.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine;

            if (FixedFormatSequenceNumber.IsMatch(line))
            {
                line = line.Substring(6);
                if (line.Length > FixedFormatCodeLength)
                    line = line.Substring(0, FixedFormatCodeLength);

                // What was column 7 is now the first character.
                if (line.Length > 0 && line[0] == '*')
                {
                    sb.Append('\n');
                    continue;
                }
            }
            // Fixed-format comment line whose sequence number is blank rather
            // than numbered: column 7 (0-indexed: 6) is '*'.
            else if (line.Length > 6 && line[6] == '*' && (line.Length == 7 || line[7] != '>'))
            {
                sb.Append('\n');
                continue;
            }

            var commentStart = line.IndexOf("*>", StringComparison.Ordinal);
            if (commentStart >= 0)
                line = line.Substring(0, commentStart);

            sb.Append(line).Append('\n');
        }
        return sb.ToString();
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
                Flush();
                continue;
            }

            current.Append(c);
        }

        Flush();
        return statements;
    }

    private sealed class ParsedStatement
    {
        public int LevelNumber;
        public string Name = "";
        public FieldSpec? Field;
        public int OccursCount = 1;
    }

    private static ParsedStatement ParseStatement(string statement)
    {
        var tokens = statement.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
            throw new FormatException($"Malformed copybook entry: \"{statement}\".");

        if (!int.TryParse(tokens[0], out var level))
            throw new FormatException($"Expected a level number, got \"{tokens[0]}\" in \"{statement}\".");

        if (level == 66 || level == 88)
            throw new FormatException(
                $"Level {level} ({(level == 66 ? "RENAMES" : "condition-name")}) is not supported: \"{statement}\".");

        var name = tokens[1];

        string? pictureText = null;
        var comp3 = false;
        var signSeparate = false;
        var signLeading = false;
        var occursCount = 1;

        var i = 2;
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
                    // Variable-length OCCURS (ODO): "OCCURS m TO n TIMES DEPENDING
                    // ON f". Rejected up front, distinctly, and never treated as a
                    // fixed count — there is no single fixed count in that clause
                    // (m/n are bounds), and its record length becomes data-dependent
                    // at decode time, which PICASSO's compute-once offset model
                    // can't express. A DEPENDING or a TO anywhere in the OCCURS
                    // clause marks the variable form; both keywords occur only here.
                    for (var j = i + 1; j < tokens.Length; j++)
                    {
                        var t = tokens[j].ToUpperInvariant();
                        if (t == "DEPENDING" || t == "TO")
                            throw new FormatException(
                                $"OCCURS ... DEPENDING ON (variable-length / ODO) is not supported: field '{name}' " +
                                $"in \"{statement}\". Its length is data-dependent at decode time, which this " +
                                "parser's fixed, compute-once offsets cannot model. Only fixed-count " +
                                "'OCCURS n [TIMES]' is supported.");
                    }

                    i += 1; // consume OCCURS
                    if (i >= tokens.Length || !int.TryParse(tokens[i], out occursCount))
                        throw new FormatException(
                            $"OCCURS must be followed by an integer repeat count: field '{name}' in \"{statement}\".");
                    if (occursCount < 1)
                        throw new FormatException(
                            $"OCCURS count must be at least 1: field '{name}' in \"{statement}\".");
                    i += 1;

                    // TIMES is optional (some dialects write "OCCURS 5", others
                    // "OCCURS 5 TIMES"); both are accepted. Any INDEXED BY / KEY IS
                    // sub-clauses that follow fall through to the default skip below,
                    // one token at a time, exactly like VALUE/JUSTIFIED — a real
                    // clause after them (PIC, COMP-3) is still matched by its own
                    // keyword, so nothing load-bearing is swallowed.
                    if (i < tokens.Length && tokens[i].Equals("TIMES", StringComparison.OrdinalIgnoreCase))
                        i += 1;
                    break;
                }

                case "REDEFINES":
                    throw new FormatException(
                        $"REDEFINES is not supported: field '{name}' would redefine another field's " +
                        "bytes, which this parser does not model — every field is assumed to occupy its " +
                        "own bytes, never bytes another field also claims. See README's " +
                        "'Not supported (v1)' section.");

                // Unsupported USAGE clauses. Each changes a field's physical byte
                // width away from the DISPLAY (one byte per digit/char) sizing this
                // parser computes — a binary halfword/fullword, a 4- or 8-byte
                // float, packed decimal, an aligned/synchronized item, a national
                // (double-byte) field, or an index/pointer with an implementation-
                // defined size. Skipping the clause (the pre-fix behaviour) left the
                // field mis-sized with no error. COMP-1/COMP-2 carry no PIC at all,
                // so the item would have vanished entirely, shifting every following
                // offset. Rejected loudly here, one named error per usage, exactly
                // like REDEFINES — never silently miscomputed. COMP-3
                // (COMPUTATIONAL-3) packed decimal stays supported, matched above.
                case "COMP":
                case "COMPUTATIONAL":
                    throw UnsupportedUsage("COMP", "binary", name, statement);
                case "COMP-1":
                case "COMPUTATIONAL-1":
                    throw UnsupportedUsage("COMP-1", "single-precision float", name, statement);
                case "COMP-2":
                case "COMPUTATIONAL-2":
                    throw UnsupportedUsage("COMP-2", "double-precision float", name, statement);
                case "COMP-4":
                case "COMPUTATIONAL-4":
                    throw UnsupportedUsage("COMP-4", "binary", name, statement);
                case "COMP-5":
                case "COMPUTATIONAL-5":
                    throw UnsupportedUsage("COMP-5", "native binary", name, statement);
                case "COMP-6":
                case "COMPUTATIONAL-6":
                    throw UnsupportedUsage("COMP-6", "unsigned packed decimal", name, statement);
                case "COMP-X":
                case "COMPUTATIONAL-X":
                    throw UnsupportedUsage("COMP-X", "binary", name, statement);
                case "BINARY":
                    throw UnsupportedUsage("BINARY", "binary", name, statement);
                case "PACKED-DECIMAL":
                    throw UnsupportedUsage("PACKED-DECIMAL", "packed decimal", name, statement);
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
                // OBJECT in clause position is the OBJECT REFERENCE usage (two
                // tokens; REFERENCE falls through the default skip and never
                // matters). Like COMP-1/COMP-2 it carries no PIC, so skipping it
                // used to drop the field entirely and shift every following offset.
                case "OBJECT":
                    throw UnsupportedUsage("OBJECT REFERENCE", "object reference", name, statement);
                case "SYNC":
                case "SYNCHRONIZED":
                    throw UnsupportedUsage("SYNC", "synchronized/aligned", name, statement);
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
                    // Unrecognized clause (e.g. VALUE, JUSTIFIED) — skip
                    // one token at a time; out of scope for v1.
                    i += 1;
                    break;
            }
        }

        FieldSpec? field = null;
        if (pictureText != null)
            field = BuildFieldSpec(name, Pic.ParsePicClause(pictureText), comp3, signSeparate, signLeading);

        return new ParsedStatement { LevelNumber = level, Name = name, Field = field, OccursCount = occursCount };
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
            $"{usage} ({kind}) usage is not supported: field '{name}' in \"{statement}\". PICASSO models " +
            "DISPLAY and COMP-3 only; a field with this usage has a different physical width this parser does " +
            "not compute, and silently skipping the clause would mis-size it. See README's " +
            "'Not supported (v1)' section.");

    private static FieldSpec BuildFieldSpec(string name, PicSpec pic, bool comp3, bool signSeparate, bool signLeading)
    {
        if (pic.Category == PicCategory.Alphanumeric)
        {
            if (comp3)
                throw new FormatException($"COMP-3 is only valid for numeric PIC clauses: field '{name}'.");
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

        if (pic.Signed && !signSeparate)
            throw new FormatException(
                $"Signed DISPLAY numeric field '{name}' needs an explicit SIGN IS LEADING/TRAILING SEPARATE clause " +
                "(overpunched sign encoding is not supported).");

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
    public static CopybookNode? BuildTree(
        IReadOnlyList<string> statements,
        string recordName,
        out bool rootIsSynthetic)
    {
        rootIsSynthetic = false;

        var tops = new List<CopybookNode>();
        var stack = new List<CopybookNode>();

        foreach (var statementText in statements)
        {
            var parsed = ParseStatement(statementText);
            var node = new CopybookNode(parsed.Name, parsed.LevelNumber)
            {
                Field = parsed.Field,
                OccursCount = parsed.OccursCount,
            };

            while (stack.Count > 0 && stack[stack.Count - 1].LevelNumber >= node.LevelNumber)
                stack.RemoveAt(stack.Count - 1);

            if (stack.Count == 0)
                tops.Add(node);
            else
                stack[stack.Count - 1].Children.Add(node);

            stack.Add(node);
        }

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
    /// Rejects a table of tables — an OCCURS item anywhere beneath another OCCURS
    /// item. A deliberate, named non-goal for this pass, distinct from OCCURS ...
    /// DEPENDING ON: the offset math generalizes cleanly, but the flattened-name
    /// convention (which index binds to which level) and its test surface are a
    /// separate design decision left for future work. Rejected loudly rather than
    /// mis-expanded, matching how this parser treats every other unmodeled shape.
    /// </summary>
    private static void RejectNestedOccurs(CopybookNode node, bool insideOccurs)
    {
        if (node.OccursCount > 1 && insideOccurs)
            throw new FormatException(
                $"Nested OCCURS is not supported: field '{node.Name}' has an OCCURS clause inside another " +
                "OCCURS item (a table of tables). Only a single level of OCCURS is supported. This is a " +
                "distinct non-goal from OCCURS ... DEPENDING ON — both are unsupported, for different reasons.");

        var nowInside = insideOccurs || node.OccursCount > 1;
        foreach (var child in node.Children)
            RejectNestedOccurs(child, nowInside);
    }

    /// <summary>
    /// Depth-first running byte counter. An OCCURS item's span is its single
    /// iteration's length times its repeat count: the children (or the elementary
    /// field) are laid out once, then the whole item is multiplied so that every
    /// field after it starts past all the copies. The per-copy field offsets
    /// themselves are materialized later, in <see cref="Flatten"/>.
    /// </summary>
    public static int ComputeOffsets(CopybookNode node, int startOffset)
    {
        int oneIterationLen;
        if (node.IsGroup)
        {
            var offset = startOffset;
            foreach (var child in node.Children)
                offset = ComputeOffsets(child, offset);
            oneIterationLen = offset - startOffset;
        }
        else
        {
            node.Field!.Start = startOffset;
            oneIterationLen = node.Field.Len;
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
    {
        if (node.OccursCount > 1)
        {
            // One OCCURS item -> OccursCount indexed copies. COBOL tables are
            // 1-indexed, so copies run (1)..(n). Nested OCCURS is rejected before
            // we get here, so an OCCURS subtree contains no further OCCURS and one
            // iteration's byte length is simply node.Len / OccursCount.
            var oneIterationLen = node.Len / node.OccursCount;
            for (var index = 1; index <= node.OccursCount; index++)
            {
                var shift = (index - 1) * oneIterationLen;
                var indexedName = $"{node.Name}({index})";

                if (node.IsGroup)
                {
                    // Group OCCURS: the parenthesized index rides on the group name
                    // and prefixes every descendant leaf, e.g. LINE-ITEM(2)-ITEM-QTY.
                    foreach (var child in node.Children)
                        EmitIndexedLeaves(child, into, shift, indexedName);
                }
                else
                {
                    // Elementary OCCURS: the leaf itself becomes NAME(index).
                    into.Add(ShiftField(node.Field!, shift, indexedName));
                }
            }
            return;
        }

        if (node.IsGroup)
        {
            foreach (var child in node.Children)
                Flatten(child, into);
        }
        else
        {
            into.Add(node.Field!);
        }
    }

    /// <summary>
    /// Emits the leaves of one iteration of an OCCURS group, offset by
    /// <paramref name="shift"/> bytes and named <paramref name="prefix"/> + '-' +
    /// leaf name. The '-' separator is unambiguous because the prefix always
    /// carries a parenthesized index (NAME(k)-...), and '(' / ')' are not legal in
    /// a COBOL identifier — so an expanded name can never collide with a real one.
    /// Only OccursCount == 1 subtrees reach here (nested OCCURS is rejected).
    /// </summary>
    private static void EmitIndexedLeaves(CopybookNode node, List<FieldSpec> into, int shift, string prefix)
    {
        if (node.IsGroup)
        {
            foreach (var child in node.Children)
                EmitIndexedLeaves(child, into, shift, prefix);
        }
        else
        {
            into.Add(ShiftField(node.Field!, shift, $"{prefix}-{node.Field!.Name}"));
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
