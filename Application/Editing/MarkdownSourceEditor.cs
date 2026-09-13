using System;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Editing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing;

/// <summary>
/// 高性能 Markdown 源码切片与区间编辑引擎（基于 Memory/Span 与 string.Create 零中间分配优化）。
/// </summary>
public sealed class MarkdownSourceEditor : IMarkdownSourceEditor
{
    /// <summary>
    /// 修改指定标题区间的层级（1 - 6 级）。
    /// </summary>
    public string ChangeHeadingLevel(string source, SourceRange headingRange, int newLevel)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(headingRange);
        if (newLevel is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(newLevel));

        int start = Math.Clamp(headingRange.StartOffset, 0, source.Length);
        int length = Math.Clamp(headingRange.Length, 0, source.Length - start);
        var slice = source.AsSpan(start, length);

        int trimHeader = 0;
        // 跳过现有前导 '#'
        while (trimHeader < slice.Length && slice[trimHeader] == '#') trimHeader++;
        // 跳过紧跟的空格
        while (trimHeader < slice.Length && slice[trimHeader] == ' ') trimHeader++;

        int contentOffset = start + trimHeader;
        int contentLength = length - trimHeader;

        int newTotalLength = newLevel + 1 + contentLength;
        string replacement = string.Create(newTotalLength, (source, contentOffset, contentLength, newLevel), (span, state) =>
        {
            var (src, cOff, cLen, lvl) = state;
            span[..lvl].Fill('#');
            span[lvl] = ' ';
            src.AsSpan(cOff, cLen).CopyTo(span[(lvl + 1)..]);
        });

        return ReplaceRange(source, headingRange, replacement);
    }

    /// <summary>
    /// 将指定文本块移动到目标偏移量位置。
    /// </summary>
    public string MoveBlock(string source, SourceRange range, int targetOffset)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(range);
        if (targetOffset < 0 || targetOffset > source.Length) throw new ArgumentOutOfRangeException(nameof(targetOffset));

        int blockStart = Math.Clamp(range.StartOffset, 0, source.Length);
        int blockLength = Math.Clamp(range.Length, 0, source.Length - blockStart);

        if (blockLength == 0) return source;
        // 如果目标偏移量位于块内部，无须移动，直接返回原字符串
        if (targetOffset >= blockStart && targetOffset <= blockStart + blockLength) return source;

        int finalTarget = targetOffset > blockStart ? targetOffset - blockLength : targetOffset;

        return string.Create(source.Length, (source, blockStart, blockLength, finalTarget), (span, state) =>
        {
            var (src, bStart, bLen, tOffset) = state;
            var blockSpan = src.AsSpan(bStart, bLen);

            if (tOffset <= bStart)
            {
                // 向前移动
                src.AsSpan(0, tOffset).CopyTo(span);
                blockSpan.CopyTo(span[tOffset..]);
                src.AsSpan(tOffset, bStart - tOffset).CopyTo(span[(tOffset + bLen)..]);
                src.AsSpan(bStart + bLen).CopyTo(span[(bStart + bLen)..]);
            }
            else
            {
                // 向后移动
                src.AsSpan(0, bStart).CopyTo(span);
                src.AsSpan(bStart + bLen, tOffset - bStart).CopyTo(span[bStart..]);
                blockSpan.CopyTo(span[tOffset..]);
                src.AsSpan(tOffset + bLen).CopyTo(span[(tOffset + bLen)..]);
            }
        });
    }

    /// <summary>
    /// 删除指定范围内的文本。
    /// </summary>
    public string DeleteRange(string source, SourceRange range) =>
        ReplaceRange(source, range, string.Empty);

    /// <summary>
    /// 将指定范围内的文本替换为目标字符串。
    /// </summary>
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
}