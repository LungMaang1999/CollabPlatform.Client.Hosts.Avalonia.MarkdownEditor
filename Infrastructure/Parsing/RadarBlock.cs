using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>'''radar / '''spider 三引号围栏块（正文为类 Markdown 表格数据）。</summary>
public sealed class RadarBlock : LeafBlock, IFencedBlock
{
    private RadarFenceInfo? _options;

    public RadarBlock(BlockParser parser) : base(parser)
    {
    }

    /// <summary>0-based source line index of the closing fence, when matched.</summary>
    public int? ClosingFenceLineIndex { get; set; }

    /// <summary>开围栏关键字 radar/spider 之后的选项原文（如 <c>title=对比 labels=show</c>），无则为 null。</summary>
    public string? Info { get; set; }

    /// <summary>
    /// 开围栏选项的解析产物。解析归块自己负责，首次读取时计算一次；
    /// 转换器只按结果落属性，不再调用选项解析。
    /// </summary>
    public RadarFenceInfo Options => _options ??= RadarMarker.ParseFenceInfo(Info);
}

