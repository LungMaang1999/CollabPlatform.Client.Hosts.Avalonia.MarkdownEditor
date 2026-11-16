namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

/// <summary>
/// 单个样式属性的来源链。由 <see cref="StyleResolver.ResolveProvenance"/> 只读产出，
/// 与合并后的 <see cref="ComputedStyle"/> 一一对应：最后一条有值的层就是生效值。
/// </summary>
public sealed record StylePropertyProvenance(
    string Property,
    string Value,
    string Source,
    IReadOnlyList<StylePropertyLayer> Chain);

/// <summary>
/// 某一层对某个属性的写入记录。<see cref="OverwrittenBy"/> 是被后一层覆盖时记下的层名；
/// <see cref="Origin"/> 仅用于 <c>inherited</c> 层：它继续指向祖先真正写这个值的层，
/// 避免只说“从父节点继承”却说不清父节点是从哪来的。
/// </summary>
public sealed record StylePropertyLayer(
    string Layer,
    string Value,
    string? DefinitionId,
    string? NodeId,
    string? OverwrittenBy = null,
    string? Origin = null);
