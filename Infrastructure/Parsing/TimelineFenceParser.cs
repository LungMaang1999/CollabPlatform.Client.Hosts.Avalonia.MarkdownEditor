using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 时间线围栏解析器：开围栏为行首 ≥3 个 ' 后跟关键字 <c>timeline</c>，
/// 闭围栏为 ≥3 个 ' 且行内无其他非空白字符。规则与 <see cref="PoemFenceParser"/> 一致。
/// </summary>
public sealed class TimelineFenceParser : BlockParser
{
    private const char FenceChar = '\'';
    private const string Keyword = "timeline";

    public TimelineFenceParser()
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
        if (!TryMatchKeyword(rest))
        {
            return BlockState.None;
        }

        var block = new TimelineBlock(this)
        {
            Column = processor.Column,
            Span = new SourceSpan(processor.Start, line.End),
            // 开围栏 timeline 之后的空白与选项（line=/gap= 等），供转换器读取容器属性。
            Info = rest.Length > Keyword.Length ? rest[Keyword.Length..].Trim() : null
        };

        block.Lines = new StringLineGroup(4);

        processor.NewBlocks.Push(block);
        return BlockState.ContinueDiscard;
    }

    public override BlockState TryContinue(BlockProcessor processor, Block block)
    {
        var line = processor.Line;

        var indent = 0;
        while (indent < 3 && line.CurrentChar == ' ')
        {
            indent++;
            line.NextChar();
        }

        var closeCount = 0;
        while (line.CurrentChar == FenceChar)
        {
            closeCount++;
            line.NextChar();
        }

        if (closeCount >= 3)
        {
            var tail = line.ToString().Trim();
            if (tail.Length == 0)
            {
                ((TimelineBlock)block).ClosingFenceLineIndex = processor.LineIndex;
                block.UpdateSpanEnd(line.Start);
                return BlockState.BreakDiscard;
            }
        }

        return BlockState.Continue;
    }

    private static bool TryMatchKeyword(string text)
    {
        if (text.Length == 0 || !text.StartsWith(Keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (text.Length == Keyword.Length)
        {
            return true;
        }

        return char.IsWhiteSpace(text[Keyword.Length]);
    }
}
