using System.Text;
using System.Text.RegularExpressions;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 把「引用式注释」整行遮蔽成空白行，再交给 Markdig。
/// <para>
/// CommonMark 规定链接引用定义不能打断段落，因此紧贴正文行书写的
/// <c>[//]: # "注释"</c> / <c>[comment]: # (注释)</c> 会被当成普通段落文字渲染出来。
/// 这里在解析入口按行判断：整行只有注释语法时，把行内容替换为等长空格，
/// 既让 Markdig 视作空行吞掉，又不改变偏移与行数，公开的源码文本不受影响。
/// </para>
/// <para>
/// 围栏（```、~~~、''' 关键字围栏）内部一律跳过，代码里的同样写法原样保留。
/// </para>
/// </summary>
internal static class ReferenceCommentMasker
{
    // 行首最多 3 个空格 + [//] 或 [comment] + 冒号 + # 目标 + 可选引号/括号标题，整行仅此。
    private static readonly Regex CommentLinePattern = new(
        "^(?<indent> {0,3})\\[(?:/{2}|comment)\\][ \\t]*:[ \\t]*#[ \\t]*(?:\"[^\"\\r\\n]*\"|\\([^()\\r\\n]*\\))?[ \\t]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Mask(string source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return source;
        }

        StringBuilder? builder = null;

        var inFence = false;
        var fenceChar = '\0';
        var fenceLength = 0;

        var lineStart = 0;
        while (lineStart <= source.Length)
        {
            var lineEnd = source.IndexOf('\n', lineStart);
            var isLastLine = lineEnd < 0;
            if (isLastLine)
            {
                lineEnd = source.Length;
            }

            var contentEnd = lineEnd;
            if (contentEnd > lineStart && source[contentEnd - 1] == '\r')
            {
                contentEnd--;
            }

            if (inFence)
            {
                if (IsClosingFence(source, lineStart, contentEnd, fenceChar, fenceLength))
                {
                    inFence = false;
                }
            }
            else if (TryOpenFence(source, lineStart, contentEnd, out var openedChar, out var openedLength))
            {
                inFence = true;
                fenceChar = openedChar;
                fenceLength = openedLength;
            }
            else if (CommentLinePattern.IsMatch(source.Substring(lineStart, contentEnd - lineStart)))
            {
                builder ??= new StringBuilder(source);
                for (var i = lineStart; i < contentEnd; i++)
                {
                    builder[i] = ' ';
                }
            }

            if (isLastLine)
            {
                break;
            }

            lineStart = lineEnd + 1;
        }

        return builder?.ToString() ?? source;
    }

    private static bool TryOpenFence(string line, int start, int end, out char fenceChar, out int fenceLength)
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

        // 单引号围栏必须带关键字（如 '''collapse），单独的 ''' 是闭合行或普通文本。
        if (fenceChar == '\'' && i >= end)
        {
            return false;
        }

        // 反引号围栏的 info string 不允许再含反引号（Markdown 规则）。
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

    private static bool IsClosingFence(string line, int start, int end, char expectedChar, int expectedLength)
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
