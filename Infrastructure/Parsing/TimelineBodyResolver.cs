using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>时间线条目正文的解析侧产物：正文切片原文与其 Markdig 文档。</summary>
internal sealed class TimelineBodyDocument
{
    private TimelineBodyDocument(string? text, MarkdownDocument? document, Exception? error)
    {
        Text = text;
        Document = document;
        Error = error;
    }

    public string? Text { get; }

    public MarkdownDocument? Document { get; }

    public Exception? Error { get; }

    public static TimelineBodyDocument Success(string text, MarkdownDocument document) =>
        new(text, document, null);

    public static TimelineBodyDocument Failure(Exception error) =>
        new(null, null, error);
}

/// <summary>
/// 解析侧产物：把时间线条目正文的切片与再解析从转换器搬到解析边界。
/// 正文文档与主文档走同一套解析侧标记（未闭合、用户块范围、块区间），转换器只消费文档。
/// </summary>
internal static class TimelineBodyResolver
{
    public static void Resolve(
        ContainerBlock root,
        string source,
        Markdig.MarkdownPipeline? pipeline,
        UserBlockRegistry? userBlocks)
    {
        foreach (var block in root)
        {
            if (block is TimelineBlock timeline)
            {
                ResolveTimeline(timeline, source, pipeline, userBlocks);
            }

            if (block is ContainerBlock nested)
            {
                Resolve(nested, source, pipeline, userBlocks);
            }
        }
    }

    private static void ResolveTimeline(
        TimelineBlock timeline,
        string source,
        Markdig.MarkdownPipeline? pipeline,
        UserBlockRegistry? userBlocks)
    {
        var bodies = new Dictionary<TimelineItemSlice, TimelineBodyDocument>(ReferenceEqualityComparer.Instance);

        foreach (var item in timeline.Items)
        {
            if (item.BodyLength <= 0)
            {
                continue;
            }

            try
            {
                var bodyText = source.Substring(item.BodyStart, item.BodyLength);
                var bodyDocument = ParsedDocumentPipeline.ParseEnriched(bodyText, pipeline, userBlocks);
                bodies[item] = TimelineBodyDocument.Success(bodyText, bodyDocument);
            }
            catch (Exception ex)
            {
                // 与旧转换期行为一致：正文解析失败让所属时间线块整体失败（BLOCK_CONVERSION_FAILED），
                // 因此在读取时原样抛出，而不是在此处吞掉。
                bodies[item] = TimelineBodyDocument.Failure(ex);
            }
        }

        timeline.BodyDocuments = bodies;
    }
}
