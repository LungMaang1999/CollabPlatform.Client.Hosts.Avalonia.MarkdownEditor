using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.SyntaxTakeover;
using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>'''timeline 三引号围栏块（正文为 @@ 节点序列，LeafBlock 由转换器手工展开）。</summary>
public sealed class TimelineBlock : LeafBlock, IFencedBlock
{
    private IReadOnlyList<TimelineItemSlice>? _items;

    public TimelineBlock(BlockParser parser) : base(parser)
    {
    }

    /// <summary>预扫描计划中的围栏层级（从 1 开始）；0 表示退回旧的闭合规则。</summary>
    public int NestingDepth { get; set; }

    /// <summary>0-based source line index of the closing fence, when matched.</summary>
    public int? ClosingFenceLineIndex { get; set; }

    /// <summary>开围栏关键字 timeline 之后的选项原文（如 <c>line=dashed gap=6</c>），无则为 null。</summary>
    public string? Info { get; set; }

    /// <summary>解析器在开围栏时捕获的全文源码，供条目切分使用。</summary>
    public string? SourceText { get; set; }

    /// <summary>条目行起始标记；由定义（<c>item-marker</c>）声明，缺省 <c>@@</c>。</summary>
    public string? ItemMarker { get; set; }

    /// <summary>
    /// <c>@@</c> 条目切片。切分由 parser 侧完成（<see cref="TimelineItemScanner"/>），
    /// 首次读取时计算一次；转换器只消费结果，不再自行扫描行。
    /// </summary>
    public IReadOnlyList<TimelineItemSlice> Items =>
        _items ??= SourceText is null
            ? []
            : TimelineItemScanner.Scan(SourceText, Span.Start, Line, ClosingFenceLineIndex, ItemMarker);

    /// <summary>
    /// 条目正文切片及其解析结果。由 parser 侧（<see cref="TimelineBodyResolver"/>）在文档解析完成后写入，
    /// 转换器按条目切片读取，不再自行切割源码或再解析。
    /// </summary>
    internal Dictionary<TimelineItemSlice, TimelineBodyDocument>? BodyDocuments { get; set; }

    internal TimelineBodyDocument? GetBody(TimelineItemSlice item) =>
        BodyDocuments is not null && BodyDocuments.TryGetValue(item, out var body) ? body : null;
}
