using System.Text.RegularExpressions;
using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 锚点矩阵：一行 <c>[ANCHORMATRIX]</c> / <c>[ANCHORMATRIX depth=2 cols=3]</c>，
/// 渲染时自动收集文档标题生成卡片网格，不手写目录条目。
/// 与 <c>[TOC]</c> 同源复用标题 slug 与同名标题规则，但输出形态是响应式卡片。
/// </summary>
public sealed class AnchorMatrixBlock : LeafBlock
{
    public AnchorMatrixBlock(BlockParser parser)
        : base(parser)
    {
    }

    /// <summary>参与矩阵的标题最大层级（1–6）。</summary>
    public int Depth { get; set; } = AnchorMatrixSyntax.DefaultDepth;

    /// <summary>宽屏下的列数（1–6），窄屏由样式自动减列。</summary>
    public int Cols { get; set; } = AnchorMatrixSyntax.DefaultCols;

    /// <summary>原始标记行，转换节点时作为文本保留。</summary>
    public string RawText { get; set; } = string.Empty;
}

/// <summary>
/// 锚点矩阵语法的唯一入口：解析器、属性面板改写与渲染共用同一份规则。
/// 参数写在方括号内，支持 <c>depth=2</c> / <c>depth:2</c>、<c>cols=3</c> / <c>cols:3</c>。
/// </summary>
public static partial class AnchorMatrixSyntax
{
    /// <summary>解析器写到节点上的层级派生属性（只读）。</summary>
    public const string DepthAttribute = "anchor-matrix-depth";

    /// <summary>解析器写到节点上的列数派生属性（只读）。</summary>
    public const string ColsAttribute = "anchor-matrix-cols";

    public const int DefaultDepth = 6;
    public const int DefaultCols = 3;
    public const int MinDepth = 1;
    public const int MaxDepth = 6;
    public const int MinCols = 1;
    public const int MaxCols = 6;

    /// <summary>一行锚点矩阵标记的解析结果；下标均相对于传入的整行。</summary>
    public readonly record struct LineInfo(
        int Depth,
        int Cols,
        int DepthTokenStart,
        int DepthTokenLength,
        int ColsTokenStart,
        int ColsTokenLength,
        int CloseIndex);

    [GeneratedRegex(
        @"^(?<lead>[ \t]{0,3})\[(?:ANCHORMATRIX|ANCHOR-MATRIX|锚点矩阵)(?<attrs>[^\[\]\r\n]*)\]$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex LinePattern();

    [GeneratedRegex(
        @"(?<key>depth|cols)\s*[=:]\s*(?<value>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex AttributePattern();

    public static bool TryParse(string? line, out LineInfo info)
    {
        info = default;
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        // 调用方可能带入 Windows 换行里的 \r。
        line = line.TrimEnd('\r');

        var match = LinePattern().Match(line);
        if (!match.Success)
        {
            return false;
        }

        var attrs = match.Groups["attrs"];
        var depth = DefaultDepth;
        var cols = DefaultCols;
        var depthStart = -1;
        var depthLength = 0;
        var colsStart = -1;
        var colsLength = 0;

        foreach (Match token in AttributePattern().Matches(attrs.Value))
        {
            if (!int.TryParse(token.Groups["value"].Value, out var value))
            {
                continue;
            }

            if (token.Groups["key"].Value.Equals("depth", StringComparison.OrdinalIgnoreCase))
            {
                depth = Math.Clamp(value, MinDepth, MaxDepth);
                depthStart = attrs.Index + token.Index;
                depthLength = token.Length;
            }
            else
            {
                cols = Math.Clamp(value, MinCols, MaxCols);
                colsStart = attrs.Index + token.Index;
                colsLength = token.Length;
            }
        }

        info = new LineInfo(
            depth,
            cols,
            depthStart,
            depthLength,
            colsStart,
            colsLength,
            match.Groups["attrs"].Index + attrs.Length);
        return true;
    }
}

/// <summary>只认独占一行的 <c>[ANCHORMATRIX ...]</c>；正文里的普通方括号链接不受影响。</summary>
public sealed class AnchorMatrixParser : BlockParser
{
    public AnchorMatrixParser()
    {
        OpeningCharacters = ['['];
    }

    public override bool CanInterrupt(BlockProcessor processor, Block block) => true;

    public override BlockState TryOpen(BlockProcessor processor)
    {
        if (processor.IsCodeIndent)
        {
            return BlockState.None;
        }

        if (!AnchorMatrixSyntax.TryParse(processor.Line.ToString(), out var info))
        {
            return BlockState.None;
        }

        var block = new AnchorMatrixBlock(this)
        {
            Depth = info.Depth,
            Cols = info.Cols,
            RawText = processor.Line.ToString(),
            Column = processor.Column,
            Span = new SourceSpan(processor.Start, processor.Line.End),
        };

        processor.NewBlocks.Push(block);
        return BlockState.BreakDiscard;
    }
}
