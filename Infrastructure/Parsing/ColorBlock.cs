using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 颜色集中声明围栏（<c>'''color</c> / <c>```color</c>）：一行一个色值，空行分行，
/// 行的其余部分可带名称。正文是纯文本，不走 Markdown 解析，避免色值与列表/强调语法互相干扰。
/// </summary>
public sealed class ColorBlock : LeafBlock, IFencedBlock
{
    public ColorBlock(BlockParser parser)
        : base(parser)
    {
    }

    /// <summary>开围栏字符：单引号或反引号。</summary>
    public char FenceChar { get; set; }

    /// <summary>开围栏的字符数量（≥3），闭合行必须同字符且不少于该长度。</summary>
    public int FenceCount { get; set; }

    /// <summary>预扫描计划中的围栏层级（从 1 开始）；0 表示退回旧的闭合规则。</summary>
    public int NestingDepth { get; set; }

    /// <summary>0-based source line index of the closing fence, when matched.</summary>
    public int? ClosingFenceLineIndex { get; set; }
}

/// <summary>
/// 颜色围栏解析器：只认 <c>color</c> 关键字，其余围栏交回原有解析器。
/// 不登记为关键字表里的可定义目标（颜色是纯渲染语法），但 color 会占用保留关键字，
/// 防止定义层把它顶掉，也让围栏状态扫描能定位到它的起始行。
/// </summary>
public sealed class ColorFenceParser : BlockParser
{
    private const string Keyword = "color";

    public ColorFenceParser()
    {
        OpeningCharacters = ['\'', '`'];
    }

    public override bool CanInterrupt(BlockProcessor processor, Block block) => true;

    public override BlockState TryOpen(BlockProcessor processor)
    {
        if (processor.IsCodeIndent)
        {
            return BlockState.None;
        }

        var line = processor.Line;
        var fenceChar = line.CurrentChar;
        if (fenceChar is not ('\'' or '`'))
        {
            return BlockState.None;
        }

        var count = 0;
        while (line.CurrentChar == fenceChar)
        {
            count++;
            line.NextChar();
        }

        if (count < 3 || !MatchesKeyword(line.ToString().TrimStart()))
        {
            return BlockState.None;
        }

        var block = new ColorBlock(this)
        {
            FenceChar = fenceChar,
            FenceCount = count,
            NestingDepth = FenceNestingPlan.FromContext(processor.Context)?.GetOpenDepth(processor.LineIndex) ?? 0,
        };
        block.Column = processor.Column;
        block.Span = new SourceSpan(processor.Start, line.End);
        block.Lines = new StringLineGroup(4);

        processor.NewBlocks.Push(block);
        return BlockState.ContinueDiscard;
    }

    public override BlockState TryContinue(BlockProcessor processor, Block block)
    {
        var colorBlock = (ColorBlock)block;
        var line = processor.Line;

        var closeCount = 0;
        while (line.CurrentChar == colorBlock.FenceChar)
        {
            closeCount++;
            line.NextChar();
        }

        var plan = FenceNestingPlan.FromContext(processor.Context);
        if (colorBlock.NestingDepth > 0 && plan is not null)
        {
            if (plan.GetCloseDepth(processor.LineIndex) == colorBlock.NestingDepth)
            {
                colorBlock.ClosingFenceLineIndex = processor.LineIndex;
                block.UpdateSpanEnd(line.Start);
                return BlockState.BreakDiscard;
            }

            return BlockState.Continue;
        }

        if (closeCount >= colorBlock.FenceCount
            && !processor.IsCodeIndent
            && line.ToString().Trim().Length == 0)
        {
            colorBlock.ClosingFenceLineIndex = processor.LineIndex;
            block.UpdateSpanEnd(line.Start);
            return BlockState.BreakDiscard;
        }

        return BlockState.Continue;
    }

    private static bool MatchesKeyword(string rest) =>
        rest.StartsWith(Keyword, StringComparison.OrdinalIgnoreCase)
        && (rest.Length == Keyword.Length || char.IsWhiteSpace(rest[Keyword.Length]));
}
