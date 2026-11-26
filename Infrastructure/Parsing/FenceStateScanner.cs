namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 扫描 Markdown 源码的围栏状态（```、~~~、''' 关键字围栏，以及注册过的 ':' 用户块围栏），
/// 定位文档末尾仍未闭合的那个围栏块。流式输出时半截围栏会被 Markdig 当成到 EOF
/// 为止的内容，前端却不应反复高亮/尝试渲染它，需要服务端给出未闭合标记。
/// </summary>
internal static class FenceStateScanner
{
    private readonly record struct OpenFence(
        char FenceChar,
        int FenceLength,
        int StartLine,
        bool AllowsNesting);

    /// <summary>
    /// 返回未闭合围栏的起始行号（1-based，与 SourceRange.StartLine 对齐）；
    /// 全部围栏正常闭合时返回 null。嵌套时返回最内层未闭合围栏，
    /// 外层用户块由 <see cref="UserBlock.Closed"/> 自行标记。
    /// </summary>
    public static int? FindTrailingUnclosedFenceStartLine(
        string source,
        UserBlockRegistry? userBlocks = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        // 栈式扫描：传统围栏内部一律视为内容（与 Markdig 代码围栏一致），
        // 用户块内部才允许继续嵌套传统围栏或其他用户块。
        var stack = new List<OpenFence>();

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

            var allowOpen = true;
            if (stack.Count > 0)
            {
                var top = stack[^1];
                if (IsClosingFence(source, pos, contentEnd, top.FenceChar, top.FenceLength))
                {
                    stack.RemoveAt(stack.Count - 1);
                    allowOpen = false;
                }
                else if (!top.AllowsNesting)
                {
                    allowOpen = false;
                }
            }

            if (allowOpen)
            {
                // 定义声明优先：代码围栏族命中定义时先按用户块处理，与生产管线一致；
                // 未命中定义才落到内置围栏（含 math/mermaid/image/qr 与单引号族）。
                if (TryOpenUserFence(
                        source, pos, contentEnd, userBlocks,
                        out var userFenceChar, out var userFenceLength))
                {
                    // 用户块内部允许继续嵌套传统围栏或其他用户块。
                    stack.Add(new OpenFence(userFenceChar, userFenceLength, lineIndex + 1, AllowsNesting: true));
                }
                else if (TryOpenFence(
                        source, pos, contentEnd,
                        out var openedChar, out var openedLength, out var allowsNesting))
                {
                    // 折叠/目录是容器围栏，体内可以继续嵌套（同长度也成立）；叶围栏与代码围栏内部仍是纯内容。
                    stack.Add(new OpenFence(openedChar, openedLength, lineIndex + 1, allowsNesting));
                }
            }

            if (isLastLine)
            {
                break;
            }

            pos = lineEnd + 1;
            lineIndex++;
        }

        return stack.Count > 0 ? stack[^1].StartLine : null;
    }

    private static bool TryOpenFence(
        string line,
        int start,
        int end,
        out char fenceChar,
        out int fenceLength,
        out bool allowsNesting)
    {
        fenceChar = '\0';
        fenceLength = 0;
        allowsNesting = false;

        var i = start;
        var spaces = 0;
        while (i < end && line[i] == ' ' && spaces < 3)
        {
            i++;
            spaces++;
        }

        if (i >= end || (line[i] != '`' && line[i] != '~' && line[i] != '\''))
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

        if (fenceChar == '\'')
        {
            var rest = line.Substring(i, end - i).TrimStart();
            if (!IsCustomFenceKeyword(rest))
            {
                return false;
            }

            // 折叠/目录是容器围栏：体内允许继续嵌套，未闭合时要能定位到最内层。
            allowsNesting = IsContainerKeyword(rest);
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

    /// <summary>
    /// 注册表驱动的用户块开围栏识别：围栏长度达到定义下限且名称整词命中。
    /// 冒号族与单引号族都由注册表判定，无定义时永不识别，保持旧行为。
    /// </summary>
    private static bool TryOpenUserFence(
        string line,
        int start,
        int end,
        UserBlockRegistry? userBlocks,
        out char fenceChar,
        out int fenceLength)
    {
        fenceChar = '\0';
        fenceLength = 0;
        if (userBlocks is null || userBlocks.IsEmpty)
        {
            return false;
        }

        var i = start;
        var spaces = 0;
        while (i < end && line[i] == ' ' && spaces < 3)
        {
            i++;
            spaces++;
        }

        if (i >= end || !UserBlockRegistry.IsSupportedFenceChar(line[i]))
        {
            return false;
        }

        var candidateChar = line[i];
        var fenceStart = i;
        while (i < end && line[i] == candidateChar)
        {
            i++;
        }

        var length = i - fenceStart;
        if (length < UserBlockRegistry.MinFenceLength)
        {
            return false;
        }

        var header = line.Substring(i, end - i).Trim();
        if (header.Length == 0)
        {
            return false;
        }

        if (!userBlocks.TryResolve(header, length, candidateChar, out _, out _))
        {
            return false;
        }

        fenceChar = candidateChar;
        fenceLength = length;
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

    /// <summary>
    /// 内置单引号围栏关键字的识别直接查注册表，不再维护第二份字面量清单
    /// （两份清单曾经缺 radar/spider，迟早对不上）；由专用块解析器实现、
    /// 未登记到关键字表的 collapse/tocpic/color 同样占用关键字，必须一起识别，
    /// 否则它们未闭合时定位不到起始行，pending 标记会漏。
    /// </summary>
    private static bool IsCustomFenceKeyword(string text) =>
        FenceKeywordRegistry.BuiltIn.TryMatchPrefix(FenceFamily.PrimeFence, text, out _, out _)
        || FenceKeywordRegistry.IsReservedPrimeKeyword(text);

    /// <summary>关键字是否为可嵌套的容器围栏（折叠、目录）。</summary>
    private static bool IsContainerKeyword(string text) =>
        StartsWithWord(text, "collapse") || StartsWithWord(text, "tocpic");

    private static bool StartsWithWord(string text, string word) =>
        text.StartsWith(word, StringComparison.OrdinalIgnoreCase)
        && (text.Length == word.Length || char.IsWhiteSpace(text[word.Length]));
}
