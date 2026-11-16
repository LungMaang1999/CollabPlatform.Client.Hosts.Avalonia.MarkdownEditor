using System;
using System.Collections.Generic;
using System.Linq;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;

public enum GraphControlKind
{
    Text = 0,
    Number = 1,
    Toggle = 2,
    Select = 3,
    Color = 4,
    Slider = 5,
}

/// <summary>定义侧的端口声明；节点实例化时复制成 <see cref="GraphPort"/>。</summary>
public sealed record GraphPortDescriptor(
    string Name,
    GraphValueKind Kind,
    bool IsRequired = true,
    bool IsMulti = false);

/// <summary>控件面板声明：控件只绑定到输入端口，不能直接改部件树。</summary>
public sealed record GraphControlDescriptor(
    string PortName,
    string Label,
    GraphControlKind Kind,
    IReadOnlyList<string>? Options = null);

/// <summary>一条图诊断。缺输入与连线失败都走这里，不抛异常。</summary>
public sealed record GraphDiagnostic(
    string Code,
    string Message,
    string? NodeId = null,
    string? PortName = null);

/// <summary>求值时传给节点的只读上下文。实例值从这里进，不写回图。</summary>
public sealed class GraphEvaluationContext
{
    private readonly Action<GraphDiagnostic> _report;

    internal GraphEvaluationContext(
        GraphNode node,
        IReadOnlyDictionary<string, GraphValue> inputs,
        IReadOnlyDictionary<string, string?> instanceValues,
        MarkdownNode? rootNode,
        NodeType appliesTo,
        MarkdownDocument? document,
        IReadOnlySet<string> bypassedStageIds,
        Action<GraphDiagnostic> report)
    {
        Node = node;
        Inputs = inputs;
        InstanceValues = instanceValues;
        RootNode = rootNode;
        AppliesTo = appliesTo;
        Document = document;
        BypassedStageIds = bypassedStageIds;
        _report = report;
    }

    public GraphNode Node { get; }

    public IReadOnlyDictionary<string, GraphValue> Inputs { get; }

    public IReadOnlyDictionary<string, string?> InstanceValues { get; }

    public MarkdownNode? RootNode { get; }

    public NodeType AppliesTo { get; }

    /// <summary>求值所属文档：样式链与类型声明的唯一来源，只读。</summary>
    public MarkdownDocument? Document { get; }

    /// <summary>被旁路的阶段定义 Id，只读；发射阶段据此进入实验态。</summary>
    public IReadOnlySet<string> BypassedStageIds { get; }

    public void Report(string code, string message, string? portName = null) =>
        _report(new GraphDiagnostic(code, message, Node.Id, portName));

    public string InputText(string portName, string fallback = "") =>
        Inputs.TryGetValue(portName, out var value) && !value.IsNone ? value.AsText() : fallback;

    public GraphValue InputValue(string portName) =>
        Inputs.TryGetValue(portName, out var value) ? value : GraphValue.None;

    /// <summary>实例值只读查询；键不存在时返回 fallback，不写回任何结构。</summary>
    public string? InstanceText(string key, string? fallback = null) =>
        InstanceValues.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
}

public delegate IReadOnlyDictionary<string, GraphValue> GraphNodeExecutor(GraphEvaluationContext context);

/// <summary>
/// 节点定义：端口、控件面板、执行委托。
/// 原语定义（阶段与库节点）是代码常量；复合阶段由 <c>.md.graph.xml</c> 声明，
/// 装载期合成同类型的定义，行为由 <see cref="Composite"/> 的子图提供。
/// </summary>
public sealed class GraphNodeDefinition
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>
    /// 一句话说明该节点实际做什么（读什么、写什么、有没有副作用）。
    /// 画布直接展示这句话，避免让用户按名字猜语义。
    /// </summary>
    public string Description { get; init; } = string.Empty;

    public string Category { get; init; } = "stage";

    public IReadOnlyList<GraphPortDescriptor> Inputs { get; init; } = [];

    public IReadOnlyList<GraphPortDescriptor> Outputs { get; init; } = [];

    public IReadOnlyList<GraphControlDescriptor> Controls { get; init; } = [];

    public GraphNodeExecutor? Executor { get; init; }

    /// <summary>
    /// 复合阶段声明：子图 + 端口映射。与 <see cref="Executor"/> 互斥，
    /// 二者皆空表示该定义没有实现。这里不新增委托，求值器按数据分派。
    /// </summary>
    public CompositeStageDefinition? Composite { get; init; }

    /// <summary>
    /// 上游输入缺失时仍执行该节点。目前只给发射阶段用：
    /// HTML 由唯一渲染器按文档产出，不依赖部件树；上游缺值时仍能给出降级输出，
    /// 避免“旁路了某个阶段就整块全白”。缺失诊断与 Incomplete 状态照常上报。
    /// </summary>
    public bool RunsWhenInputsMissing { get; init; }

    public GraphNode CreateNode(string? nodeId = null)
    {
        var node = new GraphNode
        {
            Id = nodeId ?? Guid.NewGuid().ToString("N"),
            DefinitionId = Id,
        };

        foreach (var descriptor in Inputs)
        {
            node.Inputs.Add(new GraphPort(descriptor.Name, descriptor.Kind, descriptor.IsRequired, descriptor.IsMulti));
        }

        foreach (var descriptor in Outputs)
        {
            node.Outputs.Add(new GraphPort(descriptor.Name, descriptor.Kind));
        }

        return node;
    }
}

/// <summary>定义注册表。图文件只存定义 Id，读取时按注册表重建端口。</summary>
public sealed class GraphNodeRegistry
{
    private readonly Dictionary<string, GraphNodeDefinition> _definitions = new(StringComparer.Ordinal);

    public IReadOnlyCollection<GraphNodeDefinition> Definitions => _definitions.Values;

    public void Register(GraphNodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _definitions[definition.Id] = definition;
    }

    public GraphNodeDefinition? Find(string id) =>
        _definitions.TryGetValue(id, out var definition) ? definition : null;

    public GraphNode? CreateNode(string definitionId, string? nodeId = null) =>
        Find(definitionId)?.CreateNode(nodeId);

    public bool Contains(string id) => _definitions.ContainsKey(id);
}
