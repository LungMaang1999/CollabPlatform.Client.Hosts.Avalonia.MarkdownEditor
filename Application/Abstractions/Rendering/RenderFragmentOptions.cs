namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;

/// <summary>
/// 单块渲染的降级开关：只供画布检查器在“某阶段被旁路”时进入实验态使用。
/// 全部为 false 时输出与整篇渲染逐字节一致；开关只减少渲染器本来会做的加工，
/// 不会引入第二套模板或第二个发射器。
/// <para>
/// <see cref="SuppressTemplate"/>：只跳过“定义/图提供的结构”（自定义元素的定义结构），
/// 内置描述模板是唯一发射器（直出分支已删除），不再被该开关跳过；
/// 否则诗体、时间线这类“结构即内容”的类型会静默变成空串。
/// </para>
/// <para>
/// <see cref="Structure"/> 非空时用图产出的部件树替换模板根部件，
/// 槽位填充、节点身份与内联样式仍走同一个渲染器。
/// </para>
/// </summary>
public sealed record RenderFragmentOptions(
    bool SuppressTemplate = false,
    bool SuppressBindings = false,
    bool SuppressInlineStyle = false)
{
    public static RenderFragmentOptions None { get; } = new();

    public ElementStructureNode? Structure { get; init; }
}
