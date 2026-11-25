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

    /// <summary>0-based source line index of the closing fence, when matched.</summary>
    public int? ClosingFenceLineIndex { get; set; }
}

/// <summary>
/// 颜色围栏解析器：只认 <c>color</c> 关键字，其余围栏交回原有解析器。
/// 不复用围栏关键字注册表，是因为那张表是与服务端共享的保留字契约，
/// 颜色属于纯渲染语法，不应改变共享契约。
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
