using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 解析侧产物：为 Markdig 块与行内节点预计算 <see cref="SourceRange"/>。
/// 区间只依赖解析器写入的 Span/Line/Column/Lines 与各围栏块的闭围栏行号，
/// 因此在解析完成后一次完成；转换器只读，不再自行投影。
/// </summary>
internal static class BlockRangeProjector
{
    private static readonly object RangeKey = new();

    public static void Resolve(ContainerBlock root)
    {
        foreach (var block in root)
        {
            block.SetData(RangeKey, Project(block));

            if (block is LeafBlock { Inline: not null } leafBlock)
            {
                ResolveInlines(leafBlock.Inline);
            }

            if (block is ContainerBlock nested)
            {
                Resolve(nested);
            }
        }
    }

    public static SourceRange Get(MarkdownObject? obj)
    {
        if (obj is null)
        {
            return SourceRange.Empty();
        }

        if (obj.GetData(RangeKey) is SourceRange range)
        {
            return range;
        }

        throw new InvalidOperationException(
            $"{obj.GetType().Name} 缺少解析侧区间投影；文档必须先经 ParsedDocumentPipeline 解析侧标记。");
    }

    private static void ResolveInlines(ContainerInline container)
    {
        foreach (var inline in container)
        {
            inline.SetData(RangeKey, Project(inline));

            if (inline is ContainerInline nested)
            {
                ResolveInlines(nested);
            }
        }
    }

    private static SourceRange Project(MarkdownObject? obj)
    {
        if (obj == null || obj.Span.Start < 0)
        {
            return SourceRange.Empty();
        }

        var startOffset = Math.Max(0, obj.Span.Start);
        var length = Math.Max(0, obj.Span.Length);
        var startLine = Math.Max(1, obj.Line + 1);
        var startColumn = Math.Max(1, obj.Column + 1);

        int endLine = startLine;
        int endColumn = startColumn + length;

        if (obj is ImageMatrixBlock { ClosingFenceLineIndex: >= 0 } matrixFence)
        {
            endLine = matrixFence.ClosingFenceLineIndex.Value + 1;
        }
        else if (obj is PoemBlock { ClosingFenceLineIndex: >= 0 } poemFence)
        {
            endLine = poemFence.ClosingFenceLineIndex.Value + 1;
        }
        else if (obj is TimelineBlock { ClosingFenceLineIndex: >= 0 } timelineFence)
        {
            endLine = timelineFence.ClosingFenceLineIndex.Value + 1;
        }
        else if (obj is RadarBlock { ClosingFenceLineIndex: >= 0 } radarFence)
        {
            endLine = radarFence.ClosingFenceLineIndex.Value + 1;
        }
        else if (obj is CollapseBlock { ClosingFenceLineIndex: >= 0 } collapseFence)
        {
            endLine = collapseFence.ClosingFenceLineIndex.Value + 1;
        }
        else if (obj is LeafBlock leafBlock && leafBlock.Lines.Lines != null && leafBlock.Lines.Count > 0)
        {
            endLine = startLine + leafBlock.Lines.Count - 1;
            var lastSlice = leafBlock.Lines.Lines[leafBlock.Lines.Count - 1];
            endColumn = lastSlice.Slice.Length + 1;
        }

        return new SourceRange
        {
            StartOffset = startOffset,
            Length = length,
            StartLine = startLine,
            StartColumn = startColumn,
            EndLine = endLine,
            EndColumn = endColumn
        };
    }
}
