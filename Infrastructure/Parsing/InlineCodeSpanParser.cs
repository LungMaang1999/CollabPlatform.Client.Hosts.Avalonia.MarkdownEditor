using System.Text;
using Markdig.Extensions.Tables;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 行内接管的第一片叶子：代码段（CommonMark §6.3）。
/// <para>
/// 代码段是“叶子”而不是开/闭配对构造：它不向 Markdig 的 <c>DelimiterStack</c> 压任何定界符，
/// 只在一段连续文本内向前找“等长反引号串”并一次吞掉整段。因此接管它不需要复制分隔符栈，
/// 与“同一触发符不许半接管”也不冲突：<c>`</c> 这一族由本解析器独占。
/// </para>
/// <para>
/// 产出的仍是 Markdig 原生 <see cref="CodeInline"/>，字段（<c>Content</c> / <c>Span</c> /
/// <c>Line</c> / <c>Column</c> / <c>Delimiter</c> / <c>DelimiterCount</c>）与
/// Markdig 1.5.0 <c>CodeInlineParser</c> 逐项对齐，转换器与渲染器无需任何改动。
/// </para>
/// </summary>
public sealed class InlineCodeSpanParser : InlineParser
{
    public InlineCodeSpanParser()
    {
        OpeningCharacters = ['`'];
    }

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        var match = slice.CurrentChar;

        // 反引号串只有第一个反引号参与匹配：整串未闭合时，串内后续反引号不能再重新尝试，
        // 否则会把“3 个开、2 个闭”这类不等长输入误判成代码段。
        if (slice.PeekCharExtra(-1) == match)
        {
            return false;
        }

        var openingStart = slice.Start;
        var openSticks = 0;
        while (slice.CurrentChar == match)
        {
            openSticks++;
            slice.NextChar();
        }

        // 从开串之后开始向前找闭合串；跨行是允许的，换行在取内容时归一。
        var span = RemainingSpan(slice);
        var containsNewLines = false;
        while (true)
        {
            var i = span.IndexOfAny('\r', '\n', match);
            if ((uint)i >= (uint)span.Length)
            {
                // 到叶块末尾都没有等长闭合串：本位置不是代码段，交回后续 parser。
                return false;
            }

            var closeSticks = 0;
            while ((uint)i < (uint)span.Length && span[i] == match)
            {
                closeSticks++;
                i++;
            }

            span = span.Slice(i);
            if (openSticks == closeSticks)
            {
                break;
            }

            if (closeSticks == 0)
            {
                // 管道表内的未闭合代码段会把后续行吞进同一格，破坏表格形状；
                // 命中“下一行以 | 开头 + 当前在管道表内”时放弃匹配，按原文保留。
                var nextLine = span.TrimStart(['\r', '\n']);
                if (nextLine.Length > 0
                    && nextLine[0] == '|'
                    && processor.Inline is not null
                    && processor.Inline.ContainsParentOrSiblingOfType<PipeTableDelimiterInline>())
                {
                    slice.Start = openingStart;
                    return false;
                }

                containsNewLines = true;
                span = span.Slice(1);
            }
        }

        var rawContent = RemainingSpan(slice).Slice(0, slice.Length - span.Length - openSticks);
        var content = containsNewLines ? ReplaceNewLines(rawContent) : rawContent.ToString();

        // 首尾各去一个空格：条件与 Markdig 一致——原串长度大于 2、首尾都是空格或换行、
        // 且不全由空白构成。判断用 rawContent，裁剪作用在归一后的 content 上。
        if (rawContent.Length > 2
            && rawContent[0] is ' ' or '\n'
            && rawContent[^1] is ' ' or '\n'
            && rawContent.ContainsAnyExcept(' ', '\r', '\n'))
        {
            content = content.Substring(1, content.Length - 2);
        }

        var startPosition = slice.Start;
        slice.Start = startPosition + rawContent.Length + openSticks;
        startPosition -= openSticks;

        var codeInline = new CodeInline(content)
        {
            Delimiter = slice.Text[startPosition],
            Span = new SourceSpan(
                processor.GetSourcePosition(startPosition, out var line, out var column),
                processor.GetSourcePosition(slice.Start - 1)),
            Line = line,
            Column = column,
            DelimiterCount = openSticks,
        };

        if (processor.TrackTrivia)
        {
            codeInline.ContentWithTrivia = new StringSlice(
                slice.Text,
                startPosition + openSticks,
                slice.Start - openSticks - 1);
        }

        processor.Inline = codeInline;
        return true;
    }

    // slice 当前区间（开串之后到叶块末尾）。与 Markdig 的 slice.AsSpan() 同义，
    // 但显式按 Start/Length 取，Start 越过 End 时得到空区间而不是抛异常。
    private static ReadOnlySpan<char> RemainingSpan(in StringSlice slice) =>
        slice.Text.AsSpan(slice.Start, Math.Max(0, slice.Length));

    // 行尾归一：'\n' 变一个空格，'\r' 直接丢弃（CRLF 因此只留一个空格）。
    private static string ReplaceNewLines(ReadOnlySpan<char> content)
    {
        var builder = new StringBuilder(content.Length + 8);
        while (true)
        {
            var i = content.IndexOfAny('\r', '\n');
            if ((uint)i >= (uint)content.Length)
            {
                builder.Append(content);
                break;
            }

            builder.Append(content[..i]);
            if (content[i] == '\n')
            {
                builder.Append(' ');
            }

            content = content[(i + 1)..];
        }

        return builder.ToString();
    }
}
