using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

public sealed class PoemFenceParser : BlockParser
{
    private const char FenceChar = '\'';

    private static readonly (string Keyword, PoemType Type)[] Aliases =
    {
        ("ancient-poetry", PoemType.AncientPoem),
        ("ancient-poem", PoemType.AncientPoem),
        ("poetry", PoemType.General),
        ("poem", PoemType.General),
        ("verse", PoemType.General),
        ("shi", PoemType.Shi),
        ("ci", PoemType.Ci),
        ("qu", PoemType.Qu),
    };

    public PoemFenceParser()
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
        if (!TryMatchAlias(rest, out var alias, out var type))
        {
            return BlockState.None;
        }

        var block = new PoemBlock(this)
        {
            Column = processor.Column,
            Span = new SourceSpan(processor.Start, line.End),
            PoemType = type,
            Alias = alias
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
                ((PoemBlock)block).ClosingFenceLineIndex = processor.LineIndex;
                block.UpdateSpanEnd(line.Start);
                return BlockState.BreakDiscard;
            }
        }

        return BlockState.Continue;
    }

    private static bool TryMatchAlias(string text, out string alias, out PoemType type)
    {
        alias = string.Empty;
        type = PoemType.General;
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var (keyword, poemType) in Aliases)
        {
            if (text.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
            {
                var next = keyword.Length < text.Length ? text[keyword.Length] : '\0';
                if (next == '\0' || char.IsWhiteSpace(next))
                {
                    alias = keyword;
                    type = poemType;
                    return true;
                }
            }
        }

        return false;
    }
}
