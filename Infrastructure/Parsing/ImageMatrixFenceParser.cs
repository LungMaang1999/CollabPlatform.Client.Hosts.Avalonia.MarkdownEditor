using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

public sealed class ImageMatrixFenceParser : BlockParser
{
    private const char FenceChar = '\'';

    private static readonly string[] Keywords = { "image", "img" };

    public ImageMatrixFenceParser()
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
        if (!TryMatchKeyword(rest, out _))
        {
            return BlockState.None;
        }

        var block = new ImageMatrixBlock(this)
        {
            Column = processor.Column,
            Span = new SourceSpan(processor.Start, line.End)
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
                ((ImageMatrixBlock)block).ClosingFenceLineIndex = processor.LineIndex;
                block.UpdateSpanEnd(line.Start);
                return BlockState.BreakDiscard;
            }
        }

        return BlockState.Continue;
    }

    private static bool TryMatchKeyword(string text, out string keyword)
    {
        keyword = string.Empty;
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var kw in Keywords)
        {
            if (text.StartsWith(kw, StringComparison.OrdinalIgnoreCase))
            {
                var next = kw.Length < text.Length ? text[kw.Length] : '\0';
                if (next == '\0' || char.IsWhiteSpace(next))
                {
                    keyword = kw;
                    return true;
                }
            }
        }

        return false;
    }
}
