using Markdig.Extensions.Mathematics;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

public sealed class LatexInlineMathParser : InlineParser
{
    public LatexInlineMathParser()
    {
        OpeningCharacters = ['\\'];
    }

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        if (slice.CurrentChar != '\\' || slice.PeekChar(1) != '(')
        {
            return false;
        }

        int openPosition = slice.Start;

        var c = slice.NextChar();
        c = slice.NextChar();

        int contentStart = slice.Start;
        char previousChar = '(';

        while (c != '\0')
        {
            if (c == '\r' || c == '\n')
            {
                return false;
            }

            if (previousChar == '\\' && c == ')')
            {
                int backslashPosition = slice.Start - 1;
                int closePosition = slice.Start;
                int contentEnd = backslashPosition - 1;

                var inline = new MathInline
                {
                    Span = new SourceSpan(
                        processor.GetSourcePosition(openPosition, out int line, out int column),
                        processor.GetSourcePosition(closePosition)),
                    Line = line,
                    Column = column,
                    Delimiter = '\\',
                    DelimiterCount = 1,
                    Content = slice
                };
                inline.Content.Start = contentStart;
                inline.Content.End = contentEnd;

                processor.Inline = inline;
                slice.NextChar();
                return true;
            }

            previousChar = c;
            c = slice.NextChar();
        }

        return false;
    }
}
