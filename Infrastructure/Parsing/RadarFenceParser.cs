using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 雷达图围栏解析器：开围栏为行首 ≥3 个 ' 后跟关键字 <c>radar</c> 或 <c>spider</c>，
/// 闭围栏为 ≥3 个 ' 且行内无其他非空白字符。规则与 <see cref="TimelineFenceParser"/> 一致。
/// </summary>
public sealed class RadarFenceParser : BlockParser
{
    private const char FenceChar = '\'';

    public RadarFenceParser()
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
        if (!TryMatchKeyword(rest, out var keywordLength))
        {
            return BlockState.None;
        }

        var block = new RadarBlock(this)
        {
            Column = processor.Column,
            Span = new SourceSpan(processor.Start, line.End),
            // 开围栏 radar/spider 之后的空白与选项，供转换器读取容器属性。
            Info = rest.Length > keywordLength ? rest[keywordLength..].Trim() : null
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
                ((RadarBlock)block).ClosingFenceLineIndex = processor.LineIndex;
                block.UpdateSpanEnd(line.Start);
                return BlockState.BreakDiscard;
            }
        }

        return BlockState.Continue;
    }

    private static bool TryMatchKeyword(string text, out int keywordLength)
    {
        keywordLength = 0;

        const string radar = "radar";
        const string spider = "spider";

        if (text.StartsWith(radar, StringComparison.OrdinalIgnoreCase))
        {
            keywordLength = radar.Length;
        }
        else if (text.StartsWith(spider, StringComparison.OrdinalIgnoreCase))
        {
            keywordLength = spider.Length;
        }
        else
        {
            return false;
        }

        if (text.Length == keywordLength)
        {
            return true;
        }

        return char.IsWhiteSpace(text[keywordLength]);
    }
}
