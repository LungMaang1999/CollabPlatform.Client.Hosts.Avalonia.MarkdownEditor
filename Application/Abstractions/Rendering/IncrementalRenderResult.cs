using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;

/// <summary>
/// 增量渲染结果：首帧/epoch 失效/显式全量时为全量 HTML；
/// 稳态帧为块级补丁。两种结果互斥，由 <see cref="IsPatch"/> 区分。
/// </summary>
public sealed class IncrementalRenderResult
{
    /// <summary>true=补丁帧（Patch 有值，FullHtml 为 null）；false=全量帧（FullHtml 有值）。</summary>
    public bool IsPatch { get; }

    /// <summary>全量帧的整篇 body HTML（含 article 外壳，与 Render 输出一致）。</summary>
    public string? FullHtml { get; }

    /// <summary>补丁帧的块操作列表。</summary>
    public PreviewPatch? Patch { get; }

    public IReadOnlyList<DiagnosticMessage> Diagnostics { get; }

    private IncrementalRenderResult(
        bool isPatch,
        string? fullHtml,
        PreviewPatch? patch,
        IReadOnlyList<DiagnosticMessage> diagnostics)
    {
        IsPatch = isPatch;
        FullHtml = fullHtml;
        Patch = patch;
        Diagnostics = diagnostics;
    }

    public static IncrementalRenderResult Full(string html, IReadOnlyList<DiagnosticMessage> diagnostics) =>
        new(false, html, null, diagnostics);

    public static IncrementalRenderResult Incremental(PreviewPatch patch, IReadOnlyList<DiagnosticMessage> diagnostics) =>
        new(true, null, patch, diagnostics);
}
