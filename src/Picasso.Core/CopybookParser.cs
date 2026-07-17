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

    public static List<string> SplitStatements(string text)
    {
        var statements = new List<string>();
        foreach (var chunk in text.Split('.'))
        {
            var trimmed = Regex.Replace(chunk, @"\s+", " ").Trim();
            if (trimmed.Length > 0)
                statements.Add(trimmed);
        }
        return statements;
    }

    private sealed class ParsedStatement
    {
        public int LevelNumber;
        public string Name = "";
        public FieldSpec? Field;
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
                    throw new FormatException(
                        $"OCCURS is not supported: field '{name}' repeats, which a flat {{Start, Len}} " +
                        "list can't express — it needs either indexed field names or a nested result " +
                        "shape. See README's 'Not supported (v1)' section.");

                case "REDEFINES":
                    throw new FormatException(
                        $"REDEFINES is not supported: field '{name}' would redefine another field's " +
                        "bytes, which this parser does not model — every field is assumed to occupy its " +
                        "own bytes, never bytes another field also claims. See README's " +
                        "'Not supported (v1)' section.");

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

        return new ParsedStatement { LevelNumber = level, Name = name, Field = field };
    }

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
            var node = new CopybookNode(parsed.Name, parsed.LevelNumber) { Field = parsed.Field };

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

    public static int ComputeOffsets(CopybookNode node, int startOffset)
    {
        if (node.IsGroup)
        {
            var offset = startOffset;
            foreach (var child in node.Children)
                offset = ComputeOffsets(child, offset);

            node.Start = startOffset;
            node.Len = offset - startOffset;
            return offset;
        }

        node.Start = startOffset;
        node.Len = node.Field!.Len;
        node.Field.Start = startOffset;
        return startOffset + node.Field.Len;
    }

    public static void Flatten(CopybookNode node, List<FieldSpec> into)
    {
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
}
