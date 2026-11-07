using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>'''radar / '''spider 三引号围栏块（正文为类 Markdown 表格数据）。</summary>
public sealed class RadarBlock : LeafBlock
{
    public RadarBlock(BlockParser parser) : base(parser)
    {
    }

    /// <summary>0-based source line index of the closing fence, when matched.</summary>
    public int? ClosingFenceLineIndex { get; set; }

    /// <summary>开围栏关键字 radar/spider 之后的选项原文（如 <c>title=对比 labels=show</c>），无则为 null。</summary>
    public string? Info { get; set; }
}
