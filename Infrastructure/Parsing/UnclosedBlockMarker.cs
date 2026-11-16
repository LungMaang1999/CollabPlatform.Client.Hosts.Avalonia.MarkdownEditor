using Markdig.Extensions.Mathematics;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 解析侧产物：在 Markdig AST 上标记文档末尾未闭合围栏对应的块。
/// 判定发生在解析管线内（<see cref="MarkdownParser"/>、影子链与正文切片解析），
/// 转换器只读 <see cref="IsUnclosed"/>，不再自行扫描源码，也不依赖 RawMarkdown 的填充顺序。
/// </summary>
internal static class UnclosedBlockMarker
{
    // MarkdownObject 的公开数据袋：以对象实例作键，避免与外部同名数据冲突。
    private static readonly object UnclosedKey = new();

    /// <summary>扫描源码并在根容器上标记；必须在 <c>Markdig.Markdown.Parse</c> 之后、转换之前调用。</summary>
    public static void Mark(ContainerBlock root, string source, UserBlockRegistry? userBlocks)
    {
        var unclosedFenceStartLine = FenceStateScanner.FindTrailingUnclosedFenceStartLine(source, userBlocks);
        MarkBlocks(root, source, unclosedFenceStartLine);
    }

    /// <summary>转换器唯一读取入口：该块是否已被解析侧标记为未闭合。</summary>
    public static bool IsUnclosed(Block block) => block.ContainsData(UnclosedKey);

    private static void MarkBlocks(ContainerBlock container, string source, int? unclosedFenceStartLine)
    {
        foreach (var block in container)
        {
            if (unclosedFenceStartLine.HasValue
                && block.Line + 1 == unclosedFenceStartLine.Value
                && block is CodeBlock or MathBlock or ImageMatrixBlock or PoemBlock or TimelineBlock or RadarBlock)
            {
                // 文档末尾未闭合围栏对应的代码块、公式块、流程图块等。
                block.SetData(UnclosedKey, true);
            }
            else if (block is MathBlock && IsUnclosedLatex(block, source))
            {
                // \[ 块公式未在源码中出现 \] 闭合（块解析会吞到 EOF）。
                block.SetData(UnclosedKey, true);
            }

            if (block is ContainerBlock nested)
            {
                MarkBlocks(nested, source, unclosedFenceStartLine);
            }
        }
    }

    private static bool IsUnclosedLatex(Block block, string source)
    {
        var rawMarkdown = ExtractRaw(block, source);
        var trimmed = rawMarkdown.AsSpan().TrimStart();
        if (trimmed.Length < 2 || trimmed[0] != '\\' || trimmed[1] != '[')
        {
            return false;
        }

        return rawMarkdown.IndexOf("\\]", StringComparison.Ordinal) < 0;
    }

    // 与转换器 ExtractRaw 同口径：按块 Span 取原文，去掉尾部换行。
    private static string ExtractRaw(Block block, string source)
    {
        var start = Math.Max(0, block.Span.Start);
        var length = Math.Max(0, block.Span.Length);
        if (length <= 0 || start >= source.Length)
        {
            return string.Empty;
        }

        var take = Math.Min(length, source.Length - start);
        return source.AsSpan(start, take).TrimEnd('\n').TrimEnd('\r').ToString();
    }
}
