using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

public sealed class ImageMatrixBlock : LeafBlock, IFencedBlock
{
    public ImageMatrixBlock(BlockParser parser) : base(parser)
    {
    }

    /// <summary>预扫描计划中的围栏层级（从 1 开始）；0 表示退回旧的闭合规则。</summary>
    public int NestingDepth { get; set; }

    /// <summary>0-based source line index of the closing fence, when matched.</summary>
    public int? ClosingFenceLineIndex { get; set; }
}
