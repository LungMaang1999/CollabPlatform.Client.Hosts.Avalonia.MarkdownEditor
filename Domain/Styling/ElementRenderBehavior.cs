namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

/// <summary>预览帧策略：决定该类型的变更是否能走块级补丁帧。</summary>
public enum ElementFrameStrategy
{
    /// <summary>沿用渲染管线默认判断（补丁可用则补丁）。</summary>
    Auto,

    /// <summary>本帧变更块含该类型时强制全量帧，放弃补丁。</summary>
    ForceFull
}

/// <summary>类型级渲染行为。只登记渲染管线里真实存在消费者的开关。</summary>
public sealed class ElementRenderBehavior
{
    public ElementFrameStrategy FrameStrategy { get; set; } = ElementFrameStrategy.Auto;

    /// <summary>模板变体；未选择时走 <see cref="ElementTemplateVariant.Default"/>。</summary>
    public ElementTemplateVariant TemplateVariant { get; set; } = ElementTemplateVariant.Default;

    public ElementRenderBehavior Clone() =>
        new()
        {
            FrameStrategy = FrameStrategy,
            TemplateVariant = TemplateVariant,
        };
}
