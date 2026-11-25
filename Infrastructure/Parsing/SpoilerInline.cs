using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Parsers.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 剧透行内节点：<c>||文字||</c> 成对后由 <see cref="SpoilerInlineParser"/> 建出。
/// 子节点仍是普通行内语法（强调、代码、链接等），渲染侧只负责遮罩与展开。
/// </summary>
public sealed class SpoilerInline : ContainerInline
{
}

/// <summary>
/// 剧透定界符。是否成对只能等这一层行内解析结束才知道，因此解析阶段只落一个
/// 定界符节点；收尾阶段在 <see cref="SpoilerInlineParser.PostProcess"/> 里配对，
/// 落单或跨行的一律降级为字面量 <c>||</c>，不吞正文。
/// </summary>
internal sealed class SpoilerDelimiterInline : DelimiterInline
{
    public SpoilerDelimiterInline(InlineParser parser)
        : base(parser)
    {
    }

    public override string ToLiteral() => SpoilerInlineParser.DelimiterLiteral;
}

/// <summary>
/// 剧透解析器。
/// <para>
/// 触发符同时收裸 <c>||</c> 与转义形态 <c>\|\|</c>：后者用于表格行内，
/// 避免表格列分隔先吃掉竖线；GFM 表格在行内解析前已去转义，届时裸形态同样命中。
/// </para>
/// <para>
/// 必须排在表格行内解析器之前：表格解析器会无条件认领每个 <c>|</c>，
/// 排在它后面时成对竖线先被拆成两个列分隔，剧透永远看不到。
/// 同时必须早于转义解析器，否则 <c>\|\|</c> 先被转义吃掉。
/// </para>
/// </summary>
public sealed class SpoilerInlineParser : InlineParser, IPostInlineProcessor
{
    public const string DelimiterLiteral = "||";

    public SpoilerInlineParser()
    {
        OpeningCharacters = ['|', '\\'];
    }

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        if (!TryMeasure(ref slice, out var length))
        {
            return false;
        }

        var start = slice.Start;
        var position = processor.GetSourcePosition(start, out var line, out var column);
        var end = processor.GetSourcePosition(start + length - 1, out _, out _);

        processor.Inline = new SpoilerDelimiterInline(this)
        {
            Span = new SourceSpan(position, end),
            Line = line,
            Column = column,
        };

        for (var i = 0; i < length; i++)
        {
            slice.SkipChar();
        }

        return true;
    }

    /// <summary>
    /// 只认两种定界符：裸 <c>||</c> 与转义 <c>\|\|</c>。
    /// 被转义的反斜杠（<c>\\|\|</c>）不再当定界符，交回转义解析器。
    /// </summary>
    private static bool TryMeasure(ref StringSlice slice, out int length)
    {
        length = 0;
        if (slice.CurrentChar == '|')
        {
            if (slice.PeekChar(1) != '|')
            {
                return false;
            }

            length = 2;
            return true;
        }

        if (slice.CurrentChar == '\\'
            && slice.PeekChar(1) == '|'
            && slice.PeekChar(2) == '\\'
            && slice.PeekChar(3) == '|'
            && slice.PeekCharExtra(-1) != '\\')
        {
            length = 4;
            return true;
        }

        return false;
    }

    public bool PostProcess(
        InlineProcessor state,
        Inline? root,
        Inline? lastChild,
        int postInlineProcessorIndex,
        bool isFinalProcessing)
    {
        // 只在整段行内解析收尾时动手：中途调用会看到半截树，且链接标签等子树还会被搬运。
        if (!isFinalProcessing || root is not ContainerInline container)
        {
            return true;
        }

        var delimiters = new List<SpoilerDelimiterInline>();
        Collect(container, delimiters);

        SpoilerDelimiterInline? pending = null;
        foreach (var delimiter in delimiters)
        {
            if (pending is null)
            {
                pending = delimiter;
                continue;
            }

            if (pending.Line != delimiter.Line)
            {
                // 剧透不跨行：上一行落单的定界符退回字面量，当前这个重新起头。
                pending.ReplaceByLiteral();
                pending = delimiter;
                continue;
            }

            Pair(state, pending, delimiter);
            pending = null;
        }

        pending?.ReplaceByLiteral();
        return true;
    }

    /// <summary>
    /// 按文档顺序收集剧透定界符。必须递归：定界符本身是容器，
    /// 后出现的定界符会挂在先前定界符内部。
    /// </summary>
    private static void Collect(ContainerInline container, List<SpoilerDelimiterInline> delimiters)
    {
        foreach (var child in container)
        {
            if (child is SpoilerDelimiterInline spoilerDelimiter)
            {
                delimiters.Add(spoilerDelimiter);
            }

            // 定界符本身也是容器：后出现的定界符挂在先前定界符内部，必须继续往下收。
            if (child is ContainerInline childContainer)
            {
                Collect(childContainer, delimiters);
            }
        }
    }

    /// <summary>
    /// 成对收尾，沿用 Markdig 强调解析的搬运顺序：先把开启定界符的子节点移入剧透容器，
    /// 再把闭合定界符之后解析的内容挪回原位，最后用剧透容器顶替开启定界符。
    /// </summary>
    private static void Pair(
        InlineProcessor state,
        SpoilerDelimiterInline open,
        SpoilerDelimiterInline close)
    {
        var spoiler = new SpoilerInline
        {
            Span = new SourceSpan(open.Span.Start, close.Span.End),
            Line = open.Line,
            Column = open.Column,
            IsClosed = true,
        };

        open.EmbraceChildrenBy(spoiler);
        close.MoveChildrenAfter(open);
        close.Remove();
        open.MoveChildrenAfter(open);
        open.Remove();

        // 强调的收尾处理器晚于本解析器注册，若不在这里就地对剧透内容跑一遍，
        // 内容里的强调定界符会留在树里（转换器不认识，星号会丢）。
        var emphasis = state.Parsers.PostInlineProcessors
            .OfType<EmphasisInlineParser>()
            .FirstOrDefault();
        emphasis?.PostProcess(state, spoiler, null, 0, false);
    }
}
