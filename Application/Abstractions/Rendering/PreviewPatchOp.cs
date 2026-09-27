namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;

/// <summary>
/// 针对一个顶层块（data-node-id）的增量操作。
/// </summary>
/// <param name="Id">块的稳定节点 Id（缓存 key / DOM data-node-id）。</param>
/// <param name="Kind">操作类型。</param>
/// <param name="AfterId">
/// 应用后该块在文档顺序中的前一个“可见块”Id；
/// null 表示位于 .markdown-document 首位。Remove 不使用。
/// </param>
/// <param name="Html">Upsert 时携带的整块 HTML 片段；Move/Remove 为 null。</param>
public sealed record PreviewPatchOp(
    string Id,
    PreviewPatchOpKind Kind,
    string? AfterId = null,
    string? Html = null);
