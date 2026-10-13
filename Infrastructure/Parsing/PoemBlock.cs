using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

public enum PoemType
{
    General,
    AncientPoem,
    Shi,
    Ci,
    Qu
}

public sealed class PoemBlock : LeafBlock
{
    public PoemBlock(BlockParser parser) : base(parser)
    {
    }

    public PoemType PoemType { get; set; } = PoemType.General;

    public string Alias { get; set; } = "poem";

    /// <summary>0-based source line index of the closing fence, when matched.</summary>
    public int? ClosingFenceLineIndex { get; set; }
}
