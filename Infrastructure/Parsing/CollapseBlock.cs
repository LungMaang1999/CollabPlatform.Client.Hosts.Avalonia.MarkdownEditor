using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 三单引号折叠块：<c>'''collapse open 标题</c> 开，纯引号行（长度不小于开围栏）闭。
/// 标题前可选的 <c>open</c> 独立词表示默认展开；<c>open</c> 只认首词，
/// 因此标题正文里的 open 不会误判（需要以 open 开头的标题时改用行内强调或用词避开）。
/// 正文走普通 Markdown 解析，是真实 ContainerBlock，子块照常建树。
/// 嵌套闭合按最内层优先（见 <see cref="FenceNestingPlan"/>），因此外层与内层可以同用 3 个引号；
/// 外层多引号、内层少引号的旧写法同样成立。
/// </summary>
public sealed class CollapseBlock : ContainerBlock, IFencedBlock
{
    public CollapseBlock(BlockParser parser)
        : base(parser)
    {
    }

    /// <summary>开围栏的引号数量（≥3），闭合行必须不少于该长度。</summary>
    public int FenceCount { get; set; }

    /// <summary>折叠标题；为空时由转换器给默认标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>是否默认展开（开围栏关键字后紧跟独立 <c>open</c> 词）。</summary>
    public bool Open { get; set; }

    /// <summary>预扫描计划中的围栏层级（从 1 开始）；0 表示退回旧的闭合规则。</summary>
    public int NestingDepth { get; set; }

    public int? ClosingFenceLineIndex { get; set; }
}

/// <summary>
/// 折叠块解析器：只认 <c>'''collapse</c> 前缀，其余三引号开围栏交回
/// <see cref="FenceDispatchParser"/>；闭合判定只看纯引号行。
/// </summary>
public sealed class CollapseParser : BlockParser
{
    private const char FenceChar = '\'';
    private const string Keyword = "collapse";
    private const string OpenToken = "open";

    public CollapseParser()
    {
        OpeningCharacters = [FenceChar];
    }

    public override bool CanInterrupt(BlockProcessor processor, Block block) => true;

    public override BlockState TryOpen(BlockProcessor processor)
    {
        if (processor.IsCodeIndent)
        {
            return BlockState.None;
        }

        var line = processor.Line;
        var count = 0;
        while (line.CurrentChar == FenceChar)
        {
            count++;
            line.NextChar();
        }

        if (count < 3)
        {
            return BlockState.None;
        }

        var rest = line.ToString().TrimStart();
        if (!MatchesKeyword(rest))
        {
            return BlockState.None;
        }

        // 预扫描计划覆盖不到的行（缩进列表、引用等）保持旧规则：嵌套必须减少引号数，
        // 同级或更多按普通正文保留，避免计划与解析器对层级判断分叉。
        var plan = FenceNestingPlan.FromContext(processor.Context);
        var depth = plan?.GetOpenDepth(processor.LineIndex) ?? 0;
        if (depth == 0)
        {
            var enclosing = FindEnclosingCollapse(processor);
            if (enclosing is not null && count >= enclosing.FenceCount)
            {
                return BlockState.None;
            }
        }

        var block = new CollapseBlock(this)
        {
            FenceCount = count,
            NestingDepth = depth,
        };

        // 关键字之后的第一个独立词若是 open，则它就是默认展开开关，其余部分才是标题。
        // 只认首词，保证标题里出现的 open 不会被误当成开关。
        var afterKeyword = rest.Length > Keyword.Length
            ? rest[Keyword.Length..].TrimStart()
            : string.Empty;
        if (StartsWithOpenFlag(afterKeyword))
        {
            block.Open = true;
            afterKeyword = afterKeyword[OpenToken.Length..].TrimStart();
        }

        block.Title = afterKeyword.Trim();
        block.Column = processor.Column;
        block.Span = new SourceSpan(processor.Start, line.End);

        processor.NewBlocks.Push(block);
        return BlockState.ContinueDiscard;
    }

    public override BlockState TryContinue(BlockProcessor processor, Block block)
    {
        var collapse = (CollapseBlock)block;
        var line = processor.Line;

        var closeCount = 0;
        while (line.CurrentChar == FenceChar)
        {
            closeCount++;
            line.NextChar();
        }

        var plan = FenceNestingPlan.FromContext(processor.Context);
        if (collapse.NestingDepth > 0 && plan is not null)
        {
            // 计划内：只有「关这一层」的行才闭合，内层的闭合行留给内层。
            if (plan.GetCloseDepth(processor.LineIndex) == collapse.NestingDepth)
            {
                collapse.ClosingFenceLineIndex = processor.LineIndex;
                block.UpdateSpanEnd(line.Start);
                return BlockState.BreakDiscard;
            }

            processor.GoToColumn(processor.ColumnBeforeIndent);
            return BlockState.Continue;
        }

        if (closeCount >= collapse.FenceCount
            && !processor.IsCodeIndent
            && line.ToString().Trim().Length == 0)
        {
            collapse.ClosingFenceLineIndex = processor.LineIndex;
            block.UpdateSpanEnd(line.Start);
            return BlockState.BreakDiscard;
        }

        // 容器块不消费缩进：把行位置退回缩进之前，嵌套列表/引用才能看到原始缩进。
        processor.GoToColumn(processor.ColumnBeforeIndent);
        return BlockState.Continue;
    }

    private static bool MatchesKeyword(string rest) =>
        rest.StartsWith(Keyword, StringComparison.OrdinalIgnoreCase)
        && (rest.Length == Keyword.Length || char.IsWhiteSpace(rest[Keyword.Length]));

    /// <summary>首词是否为独立 open（后接空白或行尾）。</summary>
    private static bool StartsWithOpenFlag(string text) =>
        text.StartsWith(OpenToken, StringComparison.OrdinalIgnoreCase)
        && (text.Length == OpenToken.Length || char.IsWhiteSpace(text[OpenToken.Length]));

    private static CollapseBlock? FindEnclosingCollapse(BlockProcessor processor)
    {
        for (var current = processor.CurrentContainer; current is not null; current = current.Parent as ContainerBlock)
        {
            if (current is CollapseBlock collapse)
            {
                return collapse;
            }
        }

        return null;
    }
}
