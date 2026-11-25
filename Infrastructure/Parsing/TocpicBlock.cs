using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 目录块（Tocpic）：<c>'''tocpic</c> 开，纯引号行（长度不小于开围栏）闭。
/// 正文走普通 Markdown 解析，由嵌套列表表达层级，列表项里的文章链接就是目录条目。
/// 目录只引用文章、不复制正文，文章更新后条目跟随更新。
/// </summary>
public sealed class TocpicBlock : ContainerBlock, IFencedBlock
{
    public TocpicBlock(BlockParser parser)
        : base(parser)
    {
    }

    /// <summary>开围栏的引号数量（≥3），闭合行必须不少于该长度。</summary>
    public int FenceCount { get; set; }

    public int? ClosingFenceLineIndex { get; set; }
}

/// <summary>
/// 目录块解析器：只认 <c>'''tocpic</c> 前缀，其余三引号开围栏交回
/// <see cref="FenceDispatchParser"/>。目录不允许嵌套目录，内层按普通正文保留。
/// </summary>
public sealed class TocpicParser : BlockParser
{
    private const char FenceChar = '\'';
    private const string Keyword = "tocpic";

    public TocpicParser()
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

        // 目录里再开目录没有语义，也会让折叠层级失控：内层按普通正文保留。
        if (FindEnclosingTocpic(processor) is not null)
        {
            return BlockState.None;
        }

        var block = new TocpicBlock(this)
        {
            FenceCount = count,
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

        if (closeCount >= ((TocpicBlock)block).FenceCount
            && !processor.IsCodeIndent
            && line.ToString().Trim().Length == 0)
        {
            ((TocpicBlock)block).ClosingFenceLineIndex = processor.LineIndex;
            block.UpdateSpanEnd(line.Start);
            return BlockState.BreakDiscard;
        }

        // 容器块不消费缩进：把行位置退回缩进之前，嵌套列表/引用才能看到原始缩进。
        // 少了这一步，带缩进的嵌套列表会被当成同级的列表项。
        processor.GoToColumn(processor.ColumnBeforeIndent);
        return BlockState.Continue;
    }

    private static bool MatchesKeyword(string rest) =>
        rest.StartsWith(Keyword, StringComparison.OrdinalIgnoreCase)
        && (rest.Length == Keyword.Length || char.IsWhiteSpace(rest[Keyword.Length]));

    private static TocpicBlock? FindEnclosingTocpic(BlockProcessor processor)
    {
        for (var current = processor.CurrentContainer; current is not null; current = current.Parent as ContainerBlock)
        {
            if (current is TocpicBlock tocpic)
            {
                return tocpic;
            }
        }

        return null;
    }
}
