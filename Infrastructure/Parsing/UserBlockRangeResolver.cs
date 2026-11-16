using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 解析侧产物：把 <see cref="UserBlock"/> 的块区间（开围栏到闭围栏，未闭合时到 EOF）
/// 在 Markdig 解析完成后一次性算好，转换器只读 <see cref="UserBlock.Range"/>。
/// 闭围栏行必须计入，否则增量解析会把闭围栏行当成块外内容而错误复用旧块。
/// </summary>
internal static class UserBlockRangeResolver
{
    public static void Resolve(ContainerBlock root, string source)
    {
        foreach (var block in root)
        {
            if (block is UserBlock userBlock)
            {
                userBlock.Range = ExtractRange(userBlock, source);
            }

            if (block is ContainerBlock nested)
            {
                Resolve(nested, source);
            }
        }
    }

    private static SourceRange ExtractRange(UserBlock userBlock, string source)
    {
        if (source.Length == 0)
        {
            return SourceRange.Empty();
        }

        var startOffset = Math.Clamp(userBlock.Span.Start, 0, source.Length - 1);

        var endOffset = startOffset;
        if (userBlock.Closed && userBlock.ClosingFenceEnd >= startOffset)
        {
            endOffset = userBlock.ClosingFenceEnd;
        }
        else if (userBlock.Span.End >= startOffset)
        {
            endOffset = userBlock.Span.End;
        }
        endOffset = Math.Clamp(endOffset, startOffset, source.Length - 1);

        var startLine = Math.Max(1, userBlock.Line + 1);
        int endLine;
        if (userBlock.Closed && userBlock.ClosingFenceLineIndex >= 0)
        {
            endLine = userBlock.ClosingFenceLineIndex + 1;
        }
        else
        {
            endLine = startLine + CountNewlines(source.AsSpan(startOffset, endOffset - startOffset + 1));
        }

        // 末列：从 endOffset 回退到本行行首，1-based 列号。
        var lineStart = endOffset;
        while (lineStart >= startOffset && source[lineStart] != '\n')
        {
            lineStart--;
        }

        return new SourceRange
        {
            StartOffset = startOffset,
            Length = endOffset - startOffset + 1,
            StartLine = startLine,
            StartColumn = Math.Max(1, userBlock.Column + 1),
            EndLine = endLine,
            EndColumn = Math.Max(1, endOffset - lineStart)
        };
    }

    // 计数区间内换行；末尾换行视为块尾分隔符，不计入行数。
    private static int CountNewlines(ReadOnlySpan<char> span)
    {
        var count = 0;
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] == '\n' && i < span.Length - 1)
            {
                count++;
            }
        }
        return count;
    }
}
