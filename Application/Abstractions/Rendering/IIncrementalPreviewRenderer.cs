using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;

/// <summary>
/// 支持块级增量补丁输出的预览渲染器。渲染器实例跨帧持有上一帧的
/// 有序块状态；全量渲染（Render）会重置增量基线。
/// </summary>
public interface IIncrementalPreviewRenderer
{
    /// <summary>
    /// 增量渲染：基线未建立或必须全量时返回全量结果；否则返回只含变化块的补丁。
    /// </summary>
    IncrementalRenderResult RenderIncremental(MarkdownDocument document);

    /// <summary>丢弃增量基线，下一次 RenderIncremental 必返回全量。</summary>
    void ResetIncrementalBaseline();
}
