using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

/// <summary>
/// 时间线 <c>@@</c> 条目在全文源码中的切片（开围栏解析产物，供转换器展开节点）。
/// </summary>
/// <param name="Info">标记行的解析结果（时间/标签/侧别）。</param>
/// <param name="Range">条目在全文中的源码区间（标记行行首到条目末尾）。</param>
/// <param name="RawMarkdown">条目原文（不含尾部换行）。</param>
/// <param name="BodyStart">正文在全文中的起始偏移（无正文为 0）。</param>
/// <param name="BodyLength">正文长度（已去尾部换行，无正文为 0）。</param>
/// <param name="BodyStartLine">正文首行的 1-based 行号。</param>
public sealed record TimelineItemSlice(
    TimelineMarkerInfo Info,
    SourceRange Range,
    string RawMarkdown,
    int BodyStart,
    int BodyLength,
    int BodyStartLine);
