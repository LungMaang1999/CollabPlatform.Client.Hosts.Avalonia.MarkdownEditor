using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.Services;

internal static class MarkdownSyntaxPlanner
{
    internal sealed record SyntaxPlan(
        SourceRange Range,
        string Replacement,
        int SelectionStart,
        int SelectionLength);

    public static SyntaxPlan Build(
        string source,
        int selectionStart,
        int selectionLength,
        MarkdownSyntaxType syntax)
    {
        source ??= string.Empty;

        int safeStart = Math.Clamp(selectionStart, 0, source.Length);
        int safeEnd = Math.Clamp(safeStart + Math.Max(0, selectionLength), 0, source.Length);

        if (safeStart > 0 && safeStart < source.Length && char.IsLowSurrogate(source[safeStart]))
        {
            safeStart--;
        }
        if (safeEnd > 0 && safeEnd < source.Length && char.IsHighSurrogate(source[safeEnd - 1]))
        {
            safeEnd++;
        }

        return syntax switch
        {
            MarkdownSyntaxType.Heading1 => ToggleHeading(source, safeStart, 1),
            MarkdownSyntaxType.Heading2 => ToggleHeading(source, safeStart, 2),
            MarkdownSyntaxType.Heading3 => ToggleHeading(source, safeStart, 3),
            MarkdownSyntaxType.Heading4 => ToggleHeading(source, safeStart, 4),
            MarkdownSyntaxType.Heading5 => ToggleHeading(source, safeStart, 5),
            MarkdownSyntaxType.Heading6 => ToggleHeading(source, safeStart, 6),

            MarkdownSyntaxType.Bold => InlineWrap(source, safeStart, safeEnd, "**", "**"),
            MarkdownSyntaxType.Italic => InlineWrap(source, safeStart, safeEnd, "*", "*"),
            MarkdownSyntaxType.Strikethrough => InlineWrap(source, safeStart, safeEnd, "~~", "~~"),
            MarkdownSyntaxType.InlineCode => InlineWrap(source, safeStart, safeEnd, "`", "`"),

            MarkdownSyntaxType.CodeBlock => ToggleCodeFence(source, safeStart, safeEnd),
            MarkdownSyntaxType.Quote => ToggleLinePrefix(source, safeStart, safeEnd, LinePrefixKind.Quote),
            MarkdownSyntaxType.UnorderedList => ToggleLinePrefix(source, safeStart, safeEnd, LinePrefixKind.Unordered),
            MarkdownSyntaxType.OrderedList => ToggleLinePrefix(source, safeStart, safeEnd, LinePrefixKind.Ordered),
            MarkdownSyntaxType.TaskList => ToggleLinePrefix(source, safeStart, safeEnd, LinePrefixKind.Task),

            MarkdownSyntaxType.Link => InsertLink(source, safeStart, safeEnd, image: false),
            MarkdownSyntaxType.Image => InsertLink(source, safeStart, safeEnd, image: true),

            MarkdownSyntaxType.Table => InsertTable(source, safeStart),
            MarkdownSyntaxType.HorizontalRule => InsertHorizontalRule(source, safeStart),

            _ => throw new ArgumentOutOfRangeException(nameof(syntax), syntax, "Unknown markdown syntax type.")
        };
    }

    private static (int LineStart, int LineEnd) LineBounds(string text, int offset)
    {
        int lineStart = text.LastIndexOf('\n', Math.Min(offset, text.Length - 1)) + 1;
        if (offset < lineStart)
        {
            lineStart = 0;
        }

        int nextBreak = text.IndexOf('\n', offset);
        int lineEnd = nextBreak < 0 ? text.Length : nextBreak;
        return (lineStart, lineEnd);
    }

    private static (int FirstLineStart, int LastLineEnd) BlockLineBounds(string text, int start, int end)
    {
        var (firstStart, _) = LineBounds(text, start);
        var (_, lastEnd) = LineBounds(text, Math.Max(start, end));
        return (firstStart, lastEnd);
    }

    private static SourceRange CreateRange(string text, int start, int end)
    {
        if (end < start)
        {
            end = start;
        }

        int startLine = 1;
        int startCol = 1;
        for (int i = 0; i < start; i++)
        {
            if (text[i] == '\n')
            {
                startLine++;
                startCol = 1;
            }
            else
            {
                startCol++;
            }
        }

        int endLine = startLine;
        int endCol = startCol;
        for (int i = start; i < end; i++)
        {
            if (text[i] == '\n')
            {
                endLine++;
                endCol = 1;
            }
            else
            {
                endCol++;
            }
        }

        return new SourceRange
        {
            StartOffset = start,
            Length = end - start,
            StartLine = startLine,
            StartColumn = startCol,
            EndLine = endLine,
            EndColumn = endCol
        };
    }

    private static readonly Regex HeadingMarkerRegex = new(@"^(#{1,6})[ \t]+(.*)$", RegexOptions.Compiled);

    private static SyntaxPlan ToggleHeading(string source, int offset, int level)
    {
        var (lineStart, lineEnd) = LineBounds(source, offset);
        string line = source[lineStart..lineEnd];
        string marker = new string('#', level) + " ";

        string newLine;
        var match = HeadingMarkerRegex.Match(line);
        if (match.Success && match.Groups[1].Length == level)
        {
            newLine = match.Groups[2].Value;
        }
        else if (match.Success)
        {
            newLine = marker + match.Groups[2].Value;
        }
        else
        {
            newLine = marker + line;
        }

        return new SyntaxPlan(
            CreateRange(source, lineStart, lineEnd),
            newLine,
            lineStart + newLine.Length,
            0);
    }

    private static SyntaxPlan InlineWrap(
        string source,
        int start,
        int end,
        string prefix,
        string suffix)
    {
        int length = end - start;
        string selected = source.Substring(start, length);
        string replacement;
        int localSelectionStart;
        int localSelectionLength;

        if (length == 0)
        {
            replacement = prefix + suffix;
            localSelectionStart = prefix.Length;
            localSelectionLength = 0;
        }
        else if (selected.StartsWith(prefix, StringComparison.Ordinal) &&
                 selected.EndsWith(suffix, StringComparison.Ordinal) &&
                 selected.Length >= prefix.Length + suffix.Length)
        {
            replacement = selected.Substring(
                prefix.Length,
                selected.Length - prefix.Length - suffix.Length);
            localSelectionStart = 0;
            localSelectionLength = replacement.Length;
        }
        else
        {
            replacement = prefix + selected + suffix;
            localSelectionStart = 0;
            localSelectionLength = replacement.Length;
        }

        return new SyntaxPlan(
            CreateRange(source, start, end),
            replacement,
            start + localSelectionStart,
            localSelectionLength);
    }

    private enum LinePrefixKind
    {
        Quote,
        Unordered,
        Ordered,
        Task
    }

    private static readonly Regex QuoteRegex = new(@"^(?<level>>+)[ \t]?(?<rest>.*)$", RegexOptions.Compiled);
    private static readonly Regex UnorderedRegex = new(@"^[-*+][ \t]+(?<rest>.*)$", RegexOptions.Compiled);
    private static readonly Regex OrderedRegex = new(@"^\d+\.[ \t]+(?<rest>.*)$", RegexOptions.Compiled);
    private static readonly Regex TaskRegex = new(@"^- \[[ xX]\][ \t]+(?<rest>.*)$", RegexOptions.Compiled);
    private static readonly Regex PlainDashRegex = new(@"^-[ \t]+(?<rest>.*)$", RegexOptions.Compiled);

    private static SyntaxPlan ToggleLinePrefix(
        string source,
        int start,
        int end,
        LinePrefixKind kind)
    {
        var (blockStart, blockEnd) = BlockLineBounds(source, start, end);
        string block = source[blockStart..blockEnd];
        string[] lines = block.Split('\n');

        bool allMarked = true;
        bool hasContent = false;
        foreach (string line in lines)
        {
            if (line.Length == 0)
            {
                continue;
            }

            hasContent = true;
            if (!IsMarked(line, kind))
            {
                allMarked = false;
                break;
            }
        }

        if (!hasContent)
        {
            allMarked = false;
        }

        var newLines = new List<string>(lines.Length);
        foreach (string line in lines)
        {
            newLines.Add(allMarked ? RemovePrefix(line, kind) : AddPrefix(line, kind, newLines.Count));
        }

        string newBlock = string.Join('\n', newLines);

        return new SyntaxPlan(
            CreateRange(source, blockStart, blockEnd),
            newBlock,
            blockStart,
            newBlock.Length);
    }

    private static bool IsMarked(string line, LinePrefixKind kind) =>
        kind switch
        {
            LinePrefixKind.Quote => QuoteRegex.IsMatch(line),
            LinePrefixKind.Unordered => UnorderedRegex.IsMatch(line),
            LinePrefixKind.Ordered => OrderedRegex.IsMatch(line),
            LinePrefixKind.Task => TaskRegex.IsMatch(line),
            _ => false
        };

    private static string RemovePrefix(string line, LinePrefixKind kind)
    {
        if (line.Length == 0)
        {
            return line;
        }

        switch (kind)
        {
            case LinePrefixKind.Quote:
            {
                var m = QuoteRegex.Match(line);
                return m.Success ? m.Groups["rest"].Value : line;
            }
            case LinePrefixKind.Unordered:
            {
                var m = UnorderedRegex.Match(line);
                return m.Success ? m.Groups["rest"].Value : line;
            }
            case LinePrefixKind.Ordered:
            {
                var m = OrderedRegex.Match(line);
                return m.Success ? m.Groups["rest"].Value : line;
            }
            case LinePrefixKind.Task:
            {
                var m = TaskRegex.Match(line);
                return m.Success ? m.Groups["rest"].Value : line;
            }
            default:
                return line;
        }
    }

    private static string AddPrefix(string line, LinePrefixKind kind, int lineIndex)
    {
        if (line.Length == 0)
        {
            return line;
        }

        switch (kind)
        {
            case LinePrefixKind.Quote:
                var qm = QuoteRegex.Match(line);
                return qm.Success ? ">" + line : "> " + line;

            case LinePrefixKind.Unordered:
                return "- " + line;

            case LinePrefixKind.Ordered:
                return $"{lineIndex + 1}. " + line;

            case LinePrefixKind.Task:
                var dm = PlainDashRegex.Match(line);
                return dm.Success ? "- [ ] " + dm.Groups["rest"].Value : "- [ ] " + line;

            default:
                return line;
        }
    }

    private static SyntaxPlan ToggleCodeFence(string source, int start, int end)
    {
        var (blockStart, blockEnd) = BlockLineBounds(source, start, end);

        int prevLineStart = 0;
        int prevLineEnd = -1;
        if (blockStart > 0)
        {
            prevLineEnd = blockStart - 1;
            prevLineStart = source.LastIndexOf('\n', prevLineEnd - 1) + 1;
        }

        int nextLineStart = -1;
        int nextLineEnd = -1;
        if (blockEnd < source.Length)
        {
            nextLineStart = blockEnd + 1;
            nextLineEnd = source.IndexOf('\n', nextLineStart);
            if (nextLineEnd < 0)
            {
                nextLineEnd = source.Length;
            }
        }

        if (prevLineEnd >= 0 && nextLineStart >= 0)
        {
            string prevLine = source[prevLineStart..prevLineEnd];
            string nextLine = source[nextLineStart..nextLineEnd];

            if (prevLine.StartsWith("```", StringComparison.Ordinal) &&
                nextLine.StartsWith("```", StringComparison.Ordinal))
            {
                int rangeStart = prevLineStart;
                int rangeEnd = nextLineEnd;
                string code = source[blockStart..blockEnd];

                return new SyntaxPlan(
                    CreateRange(source, rangeStart, rangeEnd),
                    code,
                    blockStart,
                    code.Length);
            }
        }

        string block = source[blockStart..blockEnd];
        string replacement = "```\n" + block + "\n```";

        return new SyntaxPlan(
            CreateRange(source, blockStart, blockEnd),
            replacement,
            blockStart + 4,
            block.Length);
    }

    private static SyntaxPlan InsertLink(string source, int start, int end, bool image)
    {
        int length = end - start;
        string selected = source.Substring(start, length);

        const string urlPlaceholder = "https://";
        string bang = image ? "!" : string.Empty;
        string textPlaceholder = image ? "图片描述" : "链接文字";

        string replacement;
        int localSelectionStart;
        int localSelectionLength;

        if (length == 0)
        {
            replacement = $"{bang}[{textPlaceholder}]({urlPlaceholder})";
            localSelectionStart = bang.Length + 1;
            localSelectionLength = textPlaceholder.Length;
        }
        else
        {
            replacement = $"{bang}[{selected}]({urlPlaceholder})";
            localSelectionStart = bang.Length + 1 + selected.Length + 2;
            localSelectionLength = urlPlaceholder.Length;
        }

        return new SyntaxPlan(
            CreateRange(source, start, end),
            replacement,
            start + localSelectionStart,
            localSelectionLength);
    }

    private const string TableTemplate =
        "| 列 1 | 列 2 | 列 3 |\n" +
        "| --- | --- | --- |\n" +
        "| 内容 | 内容 | 内容 |";

    private static SyntaxPlan InsertTable(string source, int offset)
    {
        var (lineStart, _) = LineBounds(source, offset);

        bool needLead = lineStart > 0;
        string lead = needLead ? "\n\n" : string.Empty;
        string replacement = lead + TableTemplate;

        int localSelectionStart = lead.Length + 2;
        int localSelectionLength = 3;

        return new SyntaxPlan(
            CreateRange(source, lineStart, lineStart),
            replacement,
            lineStart + localSelectionStart,
            localSelectionLength);
    }

    private static SyntaxPlan InsertHorizontalRule(string source, int offset)
    {
        var (lineStart, _) = LineBounds(source, offset);

        bool needLead = lineStart > 0;
        string lead = needLead ? "\n\n" : string.Empty;
        string replacement = lead + "---";

        return new SyntaxPlan(
            CreateRange(source, lineStart, lineStart),
            replacement,
            lineStart + replacement.Length,
            0);
    }

}
