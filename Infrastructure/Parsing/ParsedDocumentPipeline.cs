using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

internal enum ParsedDocumentStage
{
    Parse,
    Convert
}

/// <summary>解析/转换阶段异常，供调用方保留既有诊断码分流。</summary>
internal sealed class ParsedDocumentException : Exception
{
    public ParsedDocumentException(ParsedDocumentStage stage, Exception inner)
        : base(inner.Message, inner)
    {
        Stage = stage;
    }

    public ParsedDocumentStage Stage { get; }
}

/// <summary>
/// 唯一的“Markdig 解析 → 解析侧产物 → 转换”入口。
/// 未闭合标记、用户块范围、块区间投影等解析侧产物都在这里写入，
/// 转换器只读产物；<see cref="MarkdigToNodeConverter.ConvertBlocks"/> 会对未盖章文档直接抛错。
/// </summary>
internal static class ParsedDocumentPipeline
{
    // 文档级盖章：Enrich 完成后写入，转换入口据此拒绝未经解析侧标记的文档。
    private static readonly object EnrichedKey = new();

    public static List<MarkdownNode> ParseAndConvert(
        string source,
        Markdig.MarkdownPipeline? pipeline,
        MarkdigToNodeConverter converter,
        UserBlockRegistry? userBlocks,
        ICollection<DiagnosticMessage> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(converter);
        ArgumentNullException.ThrowIfNull(diagnostics);

        MarkdownDocument document;
        try
        {
            // 多引用/转义脚的写法 Markdig 原生解析会切碎或漏字，先按等长占位符遮蔽再解析；
            // 转换仍用原始源码取 RawMarkdown，保证增量拼接与源码回读拿到的是用户原文。
            var parseSource = FootnoteReferencePreprocessor.Prepare(source);
            document = ParseEnriched(parseSource, pipeline, userBlocks);
            return converter.ConvertBlocks(document, diagnostics, source);
        }
        catch (ParsedDocumentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ParsedDocumentException(ParsedDocumentStage.Convert, ex);
        }
    }

    /// <summary>
    /// 解析并写入解析侧产物，但不转换。仅用于需要在转换期按块拼接的嵌套文档（时间线正文）；
    /// 调用方拿到的文档已盖章，可直接交给转换器。
    /// </summary>
    internal static MarkdownDocument ParseEnriched(
        string source,
        Markdig.MarkdownPipeline? pipeline,
        UserBlockRegistry? userBlocks)
    {
        MarkdownDocument document;
        try
        {
            document = pipeline is null
                ? Markdig.Markdown.Parse(source)
                : Markdig.Markdown.Parse(source, pipeline);
        }
        catch (Exception ex)
        {
            throw new ParsedDocumentException(ParsedDocumentStage.Parse, ex);
        }

        EnrichDocument(document, source, pipeline, userBlocks);
        return document;
    }

    public static bool IsEnriched(ContainerBlock rootContainer) => rootContainer.ContainsData(EnrichedKey);

    /// <summary>对一份刚解析出的文档写入全部解析侧产物并盖章；正文子树递归走同一入口。</summary>
    internal static void EnrichDocument(
        MarkdownDocument document,
        string source,
        Markdig.MarkdownPipeline? pipeline,
        UserBlockRegistry? userBlocks)
    {
        UnclosedBlockMarker.Mark(document, source, userBlocks);
        UserBlockRangeResolver.Resolve(document, source);
        BlockRangeProjector.Resolve(document);
        TimelineBodyResolver.Resolve(document, source, pipeline, userBlocks);
        document.SetData(EnrichedKey, true);
    }
}
