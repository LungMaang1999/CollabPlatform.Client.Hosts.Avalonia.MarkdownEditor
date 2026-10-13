using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

public sealed class ImageMatrixBlock : LeafBlock
{
    public ImageMatrixBlock(BlockParser parser) : base(parser)
    {
    }

    /// <summary>0-based source line index of the closing fence, when matched.</summary>
    public int? ClosingFenceLineIndex { get; set; }
}
