using System;
using System.Collections.Generic;
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
    public static ParsedCopybook Parse(string source)
    {
        var stripped = StripComments(source);
        var statements = SplitStatements(stripped);

        var root = BuildTree(statements);
        if (root is null)
            throw new FormatException("Copybook has no data-item entries.");

        ComputeOffsets(root, 0);

        var flat = new List<FieldSpec>();
        Flatten(root, flat);

        return new ParsedCopybook(root, flat);
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

    public static CopybookNode? BuildTree(IReadOnlyList<string> statements)
    {
        CopybookNode? root = null;
        var stack = new List<CopybookNode>();

        foreach (var statementText in statements)
        {
            var parsed = ParseStatement(statementText);
            var node = new CopybookNode(parsed.Name, parsed.LevelNumber) { Field = parsed.Field };

            while (stack.Count > 0 && stack[stack.Count - 1].LevelNumber >= node.LevelNumber)
                stack.RemoveAt(stack.Count - 1);

            if (stack.Count == 0)
            {
                if (root != null)
                    throw new FormatException(
                        $"Copybook has more than one top-level entry ('{root.Name}' and '{node.Name}') — " +
                        "multiple 01-level records per copybook are not supported.");
                root = node;
            }
            else
            {
                stack[stack.Count - 1].Children.Add(node);
            }

            stack.Add(node);
        }

        return root;
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
