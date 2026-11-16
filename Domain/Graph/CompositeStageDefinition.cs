using System;
using System.Collections.Generic;
using System.Linq;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;

/// <summary>外部输入端口到内部节点端口的映射；<see cref="InnerTarget"/> 形如 <c>nodeId/port</c>。</summary>
public sealed record CompositePortBinding(
    string Name,
    GraphValueKind Kind,
    bool IsRequired,
    bool IsMulti,
    string InnerTarget);

/// <summary>外部输出端口到内部节点端口的映射；<see cref="InnerSource"/> 形如 <c>nodeId/port</c>。</summary>
public sealed record CompositeOutputBinding(
    string Name,
    GraphValueKind Kind,
    string InnerSource);

/// <summary>
/// 复合阶段：行为由一张子图加端口映射给出，不含 C# 执行器。
/// 这是“新增阶段只加数据”的唯一承载：声明在装载期合成一条普通
/// <see cref="GraphNodeDefinition"/>，画布与求值器都按普通节点对待。
/// </summary>
public sealed class CompositeStageDefinition
{
    public required string Id { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Category { get; init; } = "复合阶段";

    /// <summary>建议挂载的元素类型；仅用于画布归类，求值时以引用图的类型为准。</summary>
    public NodeType AppliesTo { get; init; } = NodeType.Custom;

    public IReadOnlyList<CompositePortBinding> Inputs { get; init; } = [];

    public IReadOnlyList<CompositeOutputBinding> Outputs { get; init; } = [];

    /// <summary>子图。声明解析是两段式：先建空图登记端口，再回填节点与连线。</summary>
    public NodeGraph Graph { get; set; } = new();

    /// <summary>合成普通节点定义；端口由声明推导，执行路径不新增委托。</summary>
    public GraphNodeDefinition ToNodeDefinition() => new()
    {
        Id = Id,
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName,
        Description = Description,
        Category = Category,
        Inputs = Inputs
            .Select(input => new GraphPortDescriptor(input.Name, input.Kind, input.IsRequired, input.IsMulti))
            .ToList(),
        Outputs = Outputs
            .Select(output => new GraphPortDescriptor(output.Name, output.Kind))
            .ToList(),
        Composite = this,
    };
}

/// <summary>端口映射里的 <c>nodeId/port</c> 引用。</summary>
public static class CompositeReference
{
    public const char Separator = '/';

    public static bool TrySplit(string? reference, out string nodeId, out string portName)
    {
        nodeId = string.Empty;
        portName = string.Empty;

        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        // 节点 Id 允许含分隔符（例如复合展开形），因此按最后一个分隔符切分。
        var index = reference.LastIndexOf(Separator);
        if (index <= 0 || index >= reference.Length - 1)
        {
            return false;
        }

        nodeId = reference[..index];
        portName = reference[(index + 1)..];
        return true;
    }

    public static string Join(string nodeId, string portName) => nodeId + Separator + portName;
}
