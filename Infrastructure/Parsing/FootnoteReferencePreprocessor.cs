using System.Text;
using System.Text.RegularExpressions;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 多引用脚注的源码预处理。
/// <para>
/// Markdig 只认单个已定义标签：<c>[^1,2]</c> 会按 '^' 切成上标片段，
/// <c>[^1][^2]</c> 会被当成引用式链接只保留后一个。这里把这两种写法里的
/// 定界符替换成等长的私有区占位符（标签内容原样保留），让 Markdig 当普通文字跳过，
/// 再由转换器按原偏移还原成多个引用节点。
/// </para>
/// <para>
/// 被遮蔽的标签在 Markdig 眼里没有引用，脚注定义不会进 FootnoteGroup；
/// 因此对「被遮蔽且确有定义」的标签在文末追加一行隐藏引用（带哨兵前缀），
/// 让定义照常解析，转换后整段丢弃。
/// </para>
/// </summary>
internal static class FootnoteReferencePreprocessor
{
    internal const char OpenMarker = '\uE001';
    internal const char CaretMarker = '\uE003';
    internal const char CloseMarker = '\uE002';
    internal const char EscapedBackslashMarker = '\uE004';
    internal const char EscapedBracketMarker = '\uE005';
    internal const char HiddenMarker = '\uE00F';

    private static readonly Regex ReferencePattern = new(
        "(?<!\\\\)\\[\\^([^\\[\\]\\r\\n]+)\\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EscapedReferencePattern = new(
        "(?<!\\\\)\\\\\\[\\^",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DefinitionPattern = new(
        "^ {0,3}\\[\\^([^\\[\\]\\r\\n]+)\\]:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>
    /// 多引用与转义引用的一次性预处理：遮蔽多引用、给有定义的标签补隐藏引用、
    /// 把 <c>\[^</c> 换成等长字面量占位符。返回值与输入等长（隐藏引用仅追加在尾部）。
    /// </summary>
    public static string Prepare(string source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return source;
        }

        var masked = MaskMultiReferences(source, out var maskedLabels);
        if (maskedLabels.Count == 0)
        {
            return masked;
        }

        return AppendHiddenReferences(masked, maskedLabels, CollectDefinitionLabels(source));
    }

    /// <summary>把围栏外、行内代码外的多引用写法遮蔽成等长占位符。</summary>
    public static string MaskMultiReferences(string source, out HashSet<string> maskedLabels)
    {
        maskedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(source) || source.IndexOf("[^", StringComparison.Ordinal) < 0)
        {
            return source;
        }

        var insideFence = ComputeFenceMask(source);
        StringBuilder? builder = null;

        var lineStart = 0;
        while (lineStart <= source.Length)
        {
            var lineEnd = source.IndexOf('\n', lineStart);
            var isLastLine = lineEnd < 0;
            if (isLastLine)
            {
                lineEnd = source.Length;
            }

            if (!insideFence[lineStart])
            {
                MaskLine(source, lineStart, lineEnd, maskedLabels, ref builder);
            }

            if (isLastLine)
            {
                break;
            }

            lineStart = lineEnd + 1;
        }

        return builder?.ToString() ?? source;
    }

    /// <summary>围栏外的脚注定义标签集合。</summary>
    public static HashSet<string> CollectDefinitionLabels(string source)
    {
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(source))
        {
            return labels;
        }

        var insideFence = ComputeFenceMask(source);
        foreach (Match match in DefinitionPattern.Matches(source))
        {
            if (!insideFence[match.Index])
            {
                labels.Add(match.Groups[1].Value.Trim());
            }
        }

        return labels;
    }

    /// <summary>为被遮蔽且有定义的标签补一行隐藏引用，保证定义进入 FootnoteGroup。</summary>
    public static string AppendHiddenReferences(
        string maskedSource,
        ISet<string> maskedLabels,
        ISet<string> definitionLabels)
    {
        var pending = maskedLabels.Where(definitionLabels.Contains).ToList();
        if (pending.Count == 0)
        {
            return maskedSource;
        }

        var builder = new StringBuilder(maskedSource);
        if (builder.Length > 0 && builder[^1] != '\n')
        {
            builder.Append('\n');
        }

        builder.Append('\n');
        foreach (var label in pending)
        {
            builder.Append(HiddenMarker).Append("[^").Append(label).Append("]\n\n");
        }

        return builder.ToString();
    }

    private static void MaskLine(
        string source,
        int lineStart,
        int lineEnd,
        HashSet<string> maskedLabels,
        ref StringBuilder? builder)
    {
        var line = source.AsSpan(lineStart, lineEnd - lineStart).ToString();

        // 行内代码跨度内的写法不动。
        var codeSpans = ComputeInlineCodeSpans(line);

        // 反斜杠转义的 \[^ 不是引用：把 \[ 换成等长占位符，转换器还原成字面量。
        foreach (Match escaped in EscapedReferencePattern.Matches(line))
        {
            if (InAnySpan(codeSpans, escaped.Index))
            {
                continue;
            }

            builder ??= new StringBuilder(source);
            var offset = lineStart + escaped.Index;
            builder[offset] = EscapedBackslashMarker;
            builder[offset + 1] = EscapedBracketMarker;
        }

        var matches = ReferencePattern.Matches(line);
        if (matches.Count == 0)
        {
            return;
        }

        var index = 0;
        while (index < matches.Count)
        {
            var first = matches[index];
            var last = first;
            var runEnd = index + 1;
            while (runEnd < matches.Count
                && matches[runEnd].Index == last.Index + last.Length)
            {
                last = matches[runEnd];
                runEnd++;
            }

            var isRun = runEnd - index > 1;
            var hasComma = first.Groups[1].Value.Contains(',', StringComparison.Ordinal);

            if ((isRun || hasComma) && !InAnySpan(codeSpans, first.Index))
            {
                for (var k = index; k < runEnd; k++)
                {
                    var match = matches[k];
                    var label = match.Groups[1].Value.Trim();
                    if (label.Length > 0)
                    {
                        foreach (var part in label.Split(','))
                        {
                            var token = part.Trim();
                            if (token.Length > 0)
                            {
                                maskedLabels.Add(token);
                            }
                        }
                    }

                    builder ??= new StringBuilder(source);
                    var start = lineStart + match.Index;
                    builder[start] = OpenMarker;
                    builder[start + 1] = CaretMarker;
                    builder[start + match.Length - 1] = CloseMarker;
                }
            }

            index = runEnd;
        }
    }

    private static bool InAnySpan(List<(int Start, int End)> spans, int index)
    {
        foreach (var (start, end) in spans)
        {
            if (index >= start && index < end)
            {
                return true;
            }
        }

        return false;
    }

    private static List<(int Start, int End)> ComputeInlineCodeSpans(string line)
    {
        var spans = new List<(int Start, int End)>();
        var i = 0;
        while (i < line.Length)
        {
            if (line[i] != '`')
            {
                i++;
                continue;
            }

            var fenceStart = i;
            while (i < line.Length && line[i] == '`')
            {
                i++;
            }

            var fenceLength = i - fenceStart;
            var close = line.IndexOf(new string('`', fenceLength), i, StringComparison.Ordinal);
            if (close < 0)
            {
                break;
            }

            spans.Add((fenceStart, close + fenceLength));
            i = close + fenceLength;
        }

        return spans;
    }

    // 逐行围栏状态：```/~~~ 与 ''' 关键字围栏内部一律跳过。
    private static bool[] ComputeFenceMask(string source)
    {
        var mask = new bool[source.Length + 1];
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
                mask[lineStart] = true;
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

            if (isLastLine)
            {
                break;
            }

            lineStart = lineEnd + 1;
        }

        return mask;
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

        if (fenceChar == '\'' && i >= end)
        {
            return false;
        }

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
