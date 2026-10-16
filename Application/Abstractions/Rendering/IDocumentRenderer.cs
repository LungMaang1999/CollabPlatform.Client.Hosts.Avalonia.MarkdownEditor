using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;

public interface IDocumentRenderer
{
    RenderResult Render(MarkdownDocument document);

    /// <summary>
    /// 生成指定节点当前样式对应的完整内联 CSS，与全量渲染写到该节点元素
    /// style 属性上的声明（含按块类型追加的变量、替换型对齐补充）逐字一致。
    /// 供属性区连续调整时的局部 CSS 直写通道在防抖全量帧落地前即时预览；
    /// 节点无任何内联声明时返回 null。
    /// </summary>
    string? BuildNodeInlineCss(MarkdownNode node);
}
