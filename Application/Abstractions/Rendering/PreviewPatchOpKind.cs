namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;

/// <summary>
/// 预览增量补丁的单块操作类型。
/// </summary>
public enum PreviewPatchOpKind
{
    /// <summary>新增或内容变化：携带整块 HTML，按 AfterId 定位。</summary>
    Upsert,

    /// <summary>块存在但相对位置变化：按 AfterId 移动现有 DOM 元素。</summary>
    Move,

    /// <summary>上一帧存在、本帧消失：按 Id 删除。</summary>
    Remove
}
