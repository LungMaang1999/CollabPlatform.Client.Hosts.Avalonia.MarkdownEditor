using Markdig.Extensions.Mathematics;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

public sealed class LatexBlockMathParser : BlockParser
{
    public LatexBlockMathParser()
    {
        OpeningCharacters = ['\\'];
    }

    public override bool CanInterrupt(BlockProcessor processor, Block block) => true;

    public override BlockState TryOpen(BlockProcessor processor)
    {
        if (processor.IsCodeIndent)
        {
            return BlockState.None;
        }

        var line = processor.Line;
        if (line.CurrentChar != '\\' || line.PeekChar(1) != '[')
        {
            return BlockState.None;
        }

        var block = new MathBlock(this)
        {
            Column = processor.Column,
            Span = new SourceSpan(processor.Start, line.End)
        };

        var c = line.NextChar();
        c = line.NextChar();
        int contentStart = line.Start;

        char previousChar = '[';
        while (c != '\0')
        {
            if (previousChar == '\\' && c == ']')
            {
                int backslashPosition = line.Start - 1;

                block.Lines = new StringLineGroup(1);
                if (contentStart <= backslashPosition - 1)
                {
                    block.Lines.Add(new StringSlice(line.Text, contentStart, backslashPosition - 1));
                }
                block.Span.End = line.Start;

                processor.NewBlocks.Push(block);
                return BlockState.BreakDiscard;
            }

            previousChar = c;
            c = line.NextChar();
        }

        if (contentStart <= line.End)
        {
            block.Lines = new StringLineGroup(4);
            block.Lines.Add(new StringSlice(line.Text, contentStart, line.End, line.NewLine));
        }
        else
        {
            block.Lines = new StringLineGroup(4);
        }

        processor.NewBlocks.Push(block);
        return BlockState.ContinueDiscard;
    }

    public override BlockState TryContinue(BlockProcessor processor, Block block)
    {
        var mathBlock = (MathBlock)block;
        var line = processor.Line;

        var scan = line;
        char c = scan.CurrentChar;
        char previousChar = '\0';

        while (c != '\0')
        {
            if (previousChar == '\\' && c == ']')
            {
                int backslashPosition = scan.Start - 1;

                if (backslashPosition >= line.Start)
                {
                    mathBlock.Lines.Add(new StringSlice(line.Text, line.Start, backslashPosition - 1));
                }

                block.UpdateSpanEnd(scan.Start);
                return BlockState.BreakDiscard;
            }

            previousChar = c;
            c = scan.NextChar();
        }

        processor.GoToColumn(processor.ColumnBeforeIndent);
        return BlockState.Continue;
    }
}
