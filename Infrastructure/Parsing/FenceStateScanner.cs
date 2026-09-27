namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 扫描 Markdown 源码的围栏状态（``` 与 ~~~），定位文档末尾仍未闭合的
/// 那个围栏块。流式输出时半截围栏会被 Markdig 当成到 EOF 为止的代码块，
/// 前端却不应反复高亮/尝试渲染它，需要服务端给出未闭合标记。
/// </summary>
internal static class FenceStateScanner
{
    /// <summary>
    /// 返回未闭合围栏的起始行号（1-based，与 SourceRange.StartLine 对齐）；
    /// 全部围栏正常闭合时返回 null。
    /// </summary>
    public static int? FindTrailingUnclosedFenceStartLine(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var open = false;
        var fenceChar = '\0';
        var fenceLength = 0;
        var openStartLine = 0;

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

            var contentEnd = lineEnd;
            if (contentEnd > pos && source[contentEnd - 1] == '\r')
            {
                contentEnd--;
            }

            if (open)
            {
                if (IsClosingFence(source, pos, contentEnd, fenceChar, fenceLength))
                {
                    open = false;
                }
            }
            else if (TryOpenFence(source, pos, contentEnd, out var openedChar, out var openedLength))
            {
                open = true;
                fenceChar = openedChar;
                fenceLength = openedLength;
                openStartLine = lineIndex + 1;
            }

            if (isLastLine)
            {
                break;
            }

            pos = lineEnd + 1;
            lineIndex++;
        }

        return open ? openStartLine : null;
    }

    private static bool TryOpenFence(
        string line,
        int start,
        int end,
        out char fenceChar,
        out int fenceLength)
    {
        fenceChar = '\0';
        fenceLength = 0;

        var i = start;
        var spaces = 0;
        while (i < end && line[i] == ' ' && spaces < 3)
        {
            i++;
            spaces++;
        }

        if (i >= end || (line[i] != '`' && line[i] != '~'))
        {
            return false;
        }

        fenceChar = line[i];
        var fenceStart = i;
        while (i < end && line[i] == fenceChar)
        {
            i++;
        }

        fenceLength = i - fenceStart;
        if (fenceLength < 3)
        {
            return false;
        }

        // 反引号围栏的 info string 不允许再含反引号（Markdig 规则），
        // 否则该行不构成围栏（波浪线无此限制）。
        if (fenceChar == '`')
        {
            for (var j = i; j < end; j++)
            {
                if (line[j] == '`')
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsClosingFence(
        string line,
        int start,
        int end,
        char expectedChar,
        int expectedLength)
    {
        var i = start;
        var spaces = 0;
        while (i < end && line[i] == ' ' && spaces < 3)
        {
            i++;
            spaces++;
        }

        if (i >= end || line[i] != expectedChar)
        {
            return false;
        }

        var closeLength = 0;
        while (i < end && line[i] == expectedChar)
        {
            closeLength++;
            i++;
        }

        if (closeLength < expectedLength)
        {
            return false;
        }

        // 闭合围栏之后只允许空白。
        for (var j = i; j < end; j++)
        {
            if (line[j] != ' ' && line[j] != '\t')
            {
                return false;
            }
        }

        return true;
    }
}
