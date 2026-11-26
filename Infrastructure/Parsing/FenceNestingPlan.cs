using Markdig;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>预扫描为每一行定下的角色。</summary>
internal enum FenceLineAction
{
    /// <summary>普通正文（含代码围栏内部、缩进字面量、未知关键字）。</summary>
    None = 0,

    /// <summary>开一个容器围栏（折叠/目录/用户块），正文继续按 Markdown 解析。</summary>
    OpenContainer = 1,

    /// <summary>开一个叶围栏（颜色/古诗/时间线/雷达/图组），正文按原始行读。</summary>
    OpenLeaf = 2,

    /// <summary>闭合当前最内层的围栏。</summary>
    Close = 3,
}

internal readonly record struct FenceLineDirective(FenceLineAction Action, int Depth, int QuoteCount);

/// <summary>闭合引号数少于栈顶开围栏数时的错配记录（行号 0-based，偏移与列用于诊断区间）。</summary>
internal readonly record struct FenceNestingIssue(
    int StartOffset,
    int StartColumn,
    int LineIndex,
    int QuoteCount,
    int OpenQuoteCount);

/// <summary>
/// 三撇围栏的逐行开/关计划：在 Markdig 之前先用栈扫一遍，把「这一行开哪个围栏 / 关哪一层」
/// 先定下来，再交给块解析器执行。闭合语义是「关最内层」，因此外层与内层可以同用 3 个引号；
/// 旧写法（外层 4、内层 3 或 5/4/3）仍然成立，因为每层的围栏行数只作下界判断。
/// 代码围栏内部、缩进字面量与栈空时的裸引号行都不会被当成闭合。
/// </summary>
internal sealed class FenceNestingPlan
{
    /// <summary>解析上下文键：解析器通过 <see cref="MarkdownParserContext.Properties"/> 取本次计划。</summary>
    internal static readonly object ContextKey = new();

    private readonly FenceLineDirective[] _lines;

    public FenceNestingPlan(FenceLineDirective[] lines, IReadOnlyList<FenceNestingIssue> issues)
    {
        _lines = lines;
        Issues = issues;
    }

    public IReadOnlyList<FenceNestingIssue> Issues { get; }

    public static FenceNestingPlan? FromContext(MarkdownParserContext? context)
    {
        if (context is null)
        {
            return null;
        }

        return context.Properties.TryGetValue(ContextKey, out var value)
            ? value as FenceNestingPlan
            : null;
    }

    /// <summary>该行若是开围栏，返回它所在层级（从 1 开始）；否则返回 0。</summary>
    public int GetOpenDepth(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= _lines.Length)
        {
            return 0;
        }

        var line = _lines[lineIndex];
        return line.Action is FenceLineAction.OpenContainer or FenceLineAction.OpenLeaf
            ? line.Depth
            : 0;
    }

    /// <summary>该行若是闭合行，返回被闭合的层级；否则返回 0。</summary>
    public int GetCloseDepth(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= _lines.Length)
        {
            return 0;
        }

        var line = _lines[lineIndex];
        return line.Action == FenceLineAction.Close ? line.Depth : 0;
    }
}

/// <summary>
/// 围栏预扫描器：逐行维护一个开围栏栈。容器围栏（折叠/目录/用户块）体内允许继续开围栏，
/// 叶围栏体内一律视为内容；代码围栏（``` / ~~~）内部整体跳过，三撇开围栏与关闭行都不参与判定。
/// </summary>
internal static class FenceNestingScanner
{
    private readonly record struct OpenFence(
        int QuoteCount,
        int CloseQuoteCount,
        bool AllowsNesting,
        string? Keyword,
        bool IsUserBlock);

    public static FenceNestingPlan Scan(
        string source,
        FenceKeywordRegistry? keywords = null,
        UserBlockRegistry? userBlocks = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        keywords ??= FenceKeywordRegistry.BuiltIn;

        var lines = new FenceLineDirective[CountLines(source)];
        var issues = new List<FenceNestingIssue>();
        var stack = new List<OpenFence>();
        var userBlockDepth = 0;

        var inCodeFence = false;
        var codeFenceChar = '\0';
        var codeFenceLength = 0;

        var lineIndex = 0;
        var pos = 0;

        while (pos <= source.Length)
        {
            var lineEnd = source.IndexOf('\n', pos);
            var isLastLine = lineEnd < 0;
            if (isLastLine)
            {
                lineEnd = source.Length;
            }

            var contentEnd = lineEnd > pos && source[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
            var start = SkipIndent(source, pos, contentEnd, out var isCandidate);

            if (inCodeFence)
            {
                if (isCandidate && IsCodeFenceClose(source, start, contentEnd, codeFenceChar, codeFenceLength))
                {
                    inCodeFence = false;
                }
            }
            else if (stack.Count > 0 && !stack[^1].AllowsNesting)
            {
                // 叶围栏体内只有它自己的闭合行有意义，其余一律原样保留。
                var leaf = stack[^1];
                if (isCandidate && TryReadPureQuoteLine(source, start, contentEnd, out var quoteCount))
                {
                    if (quoteCount >= leaf.CloseQuoteCount)
                    {
                        lines[lineIndex] = new FenceLineDirective(FenceLineAction.Close, stack.Count, quoteCount);
                        stack.RemoveAt(stack.Count - 1);
                        if (leaf.IsUserBlock)
                        {
                            userBlockDepth--;
                        }
                    }
                    else if (quoteCount >= 3)
                    {
                        issues.Add(new FenceNestingIssue(
                            start, start - pos + 1, lineIndex, quoteCount, leaf.CloseQuoteCount));
                    }
                }
            }
            else if (isCandidate)
            {
                if (TryReadPureQuoteLine(source, start, contentEnd, out var quoteCount))
                {
                    if (stack.Count > 0)
                    {
                        var top = stack[^1];
                        if (quoteCount >= top.CloseQuoteCount)
                        {
                            // 关最内层：同长度也只会关掉栈顶，外层继续。
                            lines[lineIndex] = new FenceLineDirective(FenceLineAction.Close, stack.Count, quoteCount);
                            stack.RemoveAt(stack.Count - 1);
                            if (top.IsUserBlock)
                            {
                                userBlockDepth--;
                            }
                        }
                        else if (quoteCount >= 3)
                        {
                            issues.Add(new FenceNestingIssue(
                                start, start - pos + 1, lineIndex, quoteCount, top.CloseQuoteCount));
                        }
                    }
                }
                else
                {
                    var quotes = CountQuoteRun(source, start, contentEnd);
                    if (quotes >= 3)
                    {
                        var rest = source.Substring(start + quotes, contentEnd - start - quotes).TrimStart();
                        if (TryClassifyOpen(
                                rest, quotes, keywords, userBlocks, stack, userBlockDepth,
                                out var action, out var keyword, out var isUserBlock))
                        {
                            // 闭合门槛按各类解析器原来的规则：折叠/目录/用户块/颜色要求
                            // 不低于开围栏；古诗/时间线/雷达/图组这类分派叶块历史上是「3 个即可」。
                            var closeQuoteCount = action == FenceLineAction.OpenLeaf
                                && !string.Equals(keyword, "color", StringComparison.OrdinalIgnoreCase)
                                    ? 3
                                    : quotes;

                            lines[lineIndex] = new FenceLineDirective(action, stack.Count + 1, quotes);
                            stack.Add(new OpenFence(
                                quotes,
                                closeQuoteCount,
                                action == FenceLineAction.OpenContainer,
                                keyword,
                                isUserBlock));
                            if (isUserBlock)
                            {
                                userBlockDepth++;
                            }
                        }
                    }
                    else if (TryOpenCodeFence(source, start, contentEnd, out var fenceChar, out var fenceLength))
                    {
                        inCodeFence = true;
                        codeFenceChar = fenceChar;
                        codeFenceLength = fenceLength;
                    }
                }
            }

            if (isLastLine)
            {
                break;
            }

            pos = lineEnd + 1;
            lineIndex++;
        }

        return new FenceNestingPlan(lines, issues);
    }

    /// <summary>
    /// 关键字到围栏种类：折叠/目录/用户块是容器，其余三撇关键字是叶块。
    /// 与解析器注册次序和各自的拒绝规则保持一致，避免计划里多开出一个并不存在的围栏。
    /// </summary>
    private static bool TryClassifyOpen(
        string rest,
        int quoteCount,
        FenceKeywordRegistry keywords,
        UserBlockRegistry? userBlocks,
        IReadOnlyList<OpenFence> stack,
        int userBlockDepth,
        out FenceLineAction action,
        out string? keyword,
        out bool isUserBlock)
    {
        action = FenceLineAction.None;
        keyword = null;
        isUserBlock = false;

        if (MatchesWord(rest, "collapse"))
        {
            action = FenceLineAction.OpenContainer;
            keyword = "collapse";
            return true;
        }

        if (MatchesWord(rest, "tocpic"))
        {
            // 与 TocpicParser 一致：目录不许套目录，内层开围栏按正文保留。
            foreach (var open in stack)
            {
                if (string.Equals(open.Keyword, "tocpic", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            action = FenceLineAction.OpenContainer;
            keyword = "tocpic";
            return true;
        }

        if (MatchesWord(rest, "color"))
        {
            action = FenceLineAction.OpenLeaf;
            keyword = "color";
            return true;
        }

        if (keywords.TryMatchPrefix(FenceFamily.PrimeFence, rest, out var entry, out _)
            && entry.Target is FenceTarget.Poem or FenceTarget.Timeline or FenceTarget.Radar or FenceTarget.ImageMatrix)
        {
            action = FenceLineAction.OpenLeaf;
            keyword = entry.Keyword;
            return true;
        }

        if (userBlocks is not null
            && !userBlocks.IsEmpty
            && userBlockDepth < UserBlockRegistry.MaxNestingDepth
            && userBlocks.TryResolve(rest.Trim(), quoteCount, UserBlockRegistry.PrimeFenceChar, out _, out _))
        {
            action = FenceLineAction.OpenContainer;
            isUserBlock = true;
            return true;
        }

        return false;
    }

    private static int SkipIndent(string source, int start, int end, out bool isCandidate)
    {
        var i = start;
        var spaces = 0;
        while (i < end && source[i] == ' ')
        {
            spaces++;
            if (spaces > 3)
            {
                isCandidate = false;
                return i;
            }

            i++;
        }

        // 制表符在 Markdig 里按 4 列展开，行首出现即视为缩进代码。
        isCandidate = i < end && source[i] != '\t';
        return i;
    }

    private static bool TryReadPureQuoteLine(string source, int start, int end, out int quoteCount)
    {
        quoteCount = CountQuoteRun(source, start, end);
        if (quoteCount == 0)
        {
            return false;
        }

        for (var i = start + quoteCount; i < end; i++)
        {
            if (source[i] != ' ' && source[i] != '\t')
            {
                return false;
            }
        }

        return true;
    }

    private static int CountQuoteRun(string source, int start, int end)
    {
        var count = 0;
        while (start + count < end && source[start + count] == '\'')
        {
            count++;
        }

        return count;
    }

    private static bool TryOpenCodeFence(
        string source,
        int start,
        int end,
        out char fenceChar,
        out int fenceLength)
    {
        fenceChar = '\0';
        fenceLength = 0;

        if (start >= end || (source[start] != '`' && source[start] != '~'))
        {
            return false;
        }

        fenceChar = source[start];
        var i = start;
        while (i < end && source[i] == fenceChar)
        {
            i++;
        }

        fenceLength = i - start;
        if (fenceLength < 3)
        {
            return false;
        }

        // 反引号围栏的 info string 不允许再含反引号（Markdig 规则）。
        if (fenceChar == '`')
        {
            for (var j = i; j < end; j++)
            {
                if (source[j] == '`')
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsCodeFenceClose(
        string source,
        int start,
        int end,
        char fenceChar,
        int fenceLength)
    {
        var i = start;
        var count = 0;
        while (i < end && source[i] == fenceChar)
        {
            count++;
            i++;
        }

        if (count < fenceLength)
        {
            return false;
        }

        for (var j = i; j < end; j++)
        {
            if (source[j] != ' ' && source[j] != '\t')
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesWord(string text, string word) =>
        text.StartsWith(word, StringComparison.OrdinalIgnoreCase)
        && (text.Length == word.Length || char.IsWhiteSpace(text[word.Length]));

    private static int CountLines(string source)
    {
        var count = 1;
        foreach (var ch in source)
        {
            if (ch == '\n')
            {
                count++;
            }
        }

        return count;
    }
}
