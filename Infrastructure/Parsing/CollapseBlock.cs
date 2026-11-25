using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 三单引号折叠块：<c>'''collapse 标题</c> 开，纯引号行（长度不小于开围栏）闭。
/// 正文走普通 Markdown 解析，是真实 ContainerBlock，子块照常建树；
/// 嵌套折叠必须逐层减少引号数（外层四引号、内层三引号），同级或更多引号按正文保留。
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

        // 嵌套必须严格减少引号数：外层四引号、内层三引号；同级或更多按普通正文保留。
        var enclosing = FindEnclosingCollapse(processor);
        if (enclosing is not null && count >= enclosing.FenceCount)
        {
            return BlockState.None;
        }

        var block = new CollapseBlock(this)
        {
            FenceCount = count,
            Title = rest.Length > Keyword.Length ? rest[Keyword.Length..].Trim() : string.Empty
        };
        block.Column = processor.Column;
        block.Span = new SourceSpan(processor.Start, line.End);

        processor.NewBlocks.Push(block);
        return BlockState.ContinueDiscard;
    }

    public override BlockState TryContinue(BlockProcessor processor, Block block)
    {
        var line = processor.Line;

        var closeCount = 0;
        while (line.CurrentChar == FenceChar)
        {
            closeCount++;
            line.NextChar();
        }

        if (closeCount >= ((CollapseBlock)block).FenceCount
            && !processor.IsCodeIndent
            && line.ToString().Trim().Length == 0)
        {
            ((CollapseBlock)block).ClosingFenceLineIndex = processor.LineIndex;
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
