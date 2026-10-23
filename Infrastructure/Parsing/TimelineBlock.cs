using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>'''timeline 三引号围栏块（正文为 @@ 节点序列，LeafBlock 由转换器手工展开）。</summary>
public sealed class TimelineBlock : LeafBlock
{
    public TimelineBlock(BlockParser parser) : base(parser)
    {
    }

    /// <summary>0-based source line index of the closing fence, when matched.</summary>
    public int? ClosingFenceLineIndex { get; set; }

    /// <summary>开围栏关键字 timeline 之后的选项原文（如 <c>line=dashed gap=6</c>），无则为 null。</summary>
    public string? Info { get; set; }
}
