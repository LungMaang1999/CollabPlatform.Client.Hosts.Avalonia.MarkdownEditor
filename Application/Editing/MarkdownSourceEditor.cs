using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Editing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing;

/// <summary>
/// 高性能 Markdown 源码切片与区间编辑引擎（基于 Span 与 string.Create 零中间分配优化）
/// </summary>
public sealed class MarkdownSourceEditor : IMarkdownSourceEditor
{
    public string ChangeHeadingLevel(string source, SourceRange headingRange, int newLevel)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(headingRange);
        if (newLevel is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(newLevel));

        int start = Math.Clamp(headingRange.StartOffset, 0, source.Length);
        int length = Math.Clamp(headingRange.Length, 0, source.Length - start);
        var slice = source.AsSpan(start, length);

        int trimHeader = 0;
        while (trimHeader < slice.Length && slice[trimHeader] == '#') trimHeader++;
        while (trimHeader < slice.Length && slice[trimHeader] == ' ') trimHeader++;

        var content = slice[trimHeader..];

        int newTotalLength = newLevel + 1 + content.Length;
        string replacement = string.Create(newTotalLength, (newLevel, content.ToString()), (span, state) =>
        {
            span[..state.newLevel].Fill('#');
            span[state.newLevel] = ' ';
            state.Item2.AsSpan().CopyTo(span[(state.newLevel + 1)..]);
        });

        return ReplaceRange(source, headingRange, replacement);
    }

    public string DeleteRange(string source, SourceRange range) =>
        ReplaceRange(source, range, string.Empty);

    public string ReplaceRange(string source, SourceRange range, string replacement)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(range);
        replacement ??= string.Empty;

        int start = Math.Clamp(range.StartOffset, 0, source.Length);
        int length = Math.Clamp(range.Length, 0, source.Length - start);

        int newCapacity = source.Length - length + replacement.Length;
        return string.Create(newCapacity, (source, start, length, replacement), (span, state) =>
        {
            var (src, s, len, rep) = state;
            src.AsSpan(0, s).CopyTo(span);
            rep.AsSpan().CopyTo(span[s..]);
            src.AsSpan(s + len).CopyTo(span[(s + rep.Length)..]);
        });
    }

    public string MoveBlock(string source, SourceRange range, int targetOffset)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(range);
        if (targetOffset < 0 || targetOffset > source.Length) throw new ArgumentOutOfRangeException(nameof(targetOffset));

        int blockStart = Math.Clamp(range.StartOffset, 0, source.Length);
        int blockLength = Math.Clamp(range.Length, 0, source.Length - blockStart);

        if (blockLength == 0) return source;

        int finalTarget = targetOffset > blockStart ? targetOffset - blockLength : targetOffset;

        return string.Create(source.Length, (source, blockStart, blockLength, finalTarget), (span, state) =>
        {
            var (src, bStart, bLen, tOffset) = state;
            var blockSpan = src.AsSpan(bStart, bLen);

            if (tOffset <= bStart)
            {
                // 向前移
                src.AsSpan(0, tOffset).CopyTo(span);
                blockSpan.CopyTo(span[tOffset..]);
                src.AsSpan(tOffset, bStart - tOffset).CopyTo(span[(tOffset + bLen)..]);
                src.AsSpan(bStart + bLen).CopyTo(span[(bStart + bLen)..]);
            }
            else
            {
                // 向后移
                src.AsSpan(0, bStart).CopyTo(span);
                src.AsSpan(bStart + bLen, tOffset - bStart).CopyTo(span[bStart..]);
                blockSpan.CopyTo(span[tOffset..]);
                src.AsSpan(tOffset + bLen).CopyTo(span[(tOffset + bLen)..]);
            }
        });
    }
}