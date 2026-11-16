using System;
using System.Collections.Generic;
using System.Linq;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Rendering.Graph;

public enum GraphEvaluationStatus
{
    Ok = 0,
    Incomplete = 1,
    Failed = 2,
}

public sealed class GraphEvaluationOptions
{
    public MarkdownNode? RootNode { get; init; }

    /// <summary>求值所属文档；样式链与类型声明从这里读，图文件不复制它们。</summary>
    public MarkdownDocument? Document { get; init; }

    /// <summary>实例级值覆盖。求值期只读，不写回任何图结构。</summary>
    public IReadOnlyDictionary<string, string?> InstanceValues { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>外部种子值，键为 "nodeId/portName"，用于入口节点。</summary>
    public IReadOnlyDictionary<string, GraphValue> Seeds { get; init; } =
        new Dictionary<string, GraphValue>(StringComparer.Ordinal);

    /// <summary>
    /// 被旁路的阶段定义 Id（如 stage.style）。求值本身不消费它，
    /// 只透传给发射阶段，让画布在旁路时进入“输出按旁路真实变化”的实验态。
    /// </summary>
    public IReadOnlySet<string> BypassedStageIds { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// 复合阶段内部求值时继承引用者的元素类型：子图声明里的 AppliesTo 只是画布归类，
    /// 真正取节点时以调用方的类型为准。
    /// </summary>
    internal NodeType? AppliesToOverride { get; init; }
}

public sealed record GraphEvaluationResult(
    GraphEvaluationStatus Status,
    IReadOnlyDictionary<string, GraphValue> Outputs,
    IReadOnlyList<string> ExecutionOrder,
    IReadOnlyList<GraphDiagnostic> Diagnostics)
{
    public static string Key(string nodeId, string portName) => nodeId + "/" + portName;

    public GraphValue GetOutput(string nodeId, string portName) =>
        Outputs.TryGetValue(Key(nodeId, portName), out var value) ? value : GraphValue.None;
}

/// <summary>
/// 拓扑序求值。缺输入降级为诊断并跳过节点；结构非法整图失败；不支持并行与环。
/// </summary>
public sealed class NodeGraphEvaluator
{
    private readonly GraphNodeRegistry _registry;

    public NodeGraphEvaluator(GraphNodeRegistry registry)
    {
        _registry = registry;
    }

    public GraphEvaluationResult Evaluate(NodeGraph graph, GraphEvaluationOptions? options = null) =>
        Evaluate(graph, options ?? new GraphEvaluationOptions(), [], requireSink: true);

    private GraphEvaluationResult Evaluate(
        NodeGraph graph,
        GraphEvaluationOptions options,
        List<string> compositeStack,
        bool requireSink)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var diagnostics = new List<GraphDiagnostic>();
        var validation = NodeGraphValidator.Validate(graph);
        diagnostics.AddRange(validation);
        if (validation.Any(item =>
                item.Code is "graph.duplicate_node" or "graph.unknown_node" or "graph.unknown_port"
                    or "graph.edge_kind_mismatch" or "graph.input_occupied" or "graph.cycle"))
        {
            return new GraphEvaluationResult(GraphEvaluationStatus.Failed, EmptyOutputs(), [], diagnostics);
        }

        var order = BuildOrder(graph, out var orderFailure);
        if (orderFailure is not null)
        {
            diagnostics.Add(orderFailure);
            return new GraphEvaluationResult(GraphEvaluationStatus.Failed, EmptyOutputs(), [], diagnostics);
        }

        var outputs = new Dictionary<string, GraphValue>(StringComparer.Ordinal);
        var executed = new List<string>();
        var incomplete = false;

        foreach (var nodeId in order)
        {
            var node = graph.FindNode(nodeId)!;
            var definition = _registry.Find(node.DefinitionId);
            if (definition is null && !node.IsBypassed)
            {
                diagnostics.Add(new GraphDiagnostic(
                    "graph.unknown_definition",
                    $"未知节点定义：{node.DefinitionId}。",
                    node.Id));
                incomplete = true;
                continue;
            }

            var inputs = new Dictionary<string, GraphValue>(StringComparer.Ordinal);
            var missing = false;
            foreach (var port in node.Inputs)
            {
                var key = GraphEvaluationResult.Key(node.Id, port.Name);
                var incoming = graph.Edges
                    .Where(edge => string.Equals(edge.ToNodeId, node.Id, StringComparison.Ordinal) &&
                                   string.Equals(edge.ToPort, port.Name, StringComparison.Ordinal))
                    .ToList();

                if (incoming.Count > 0)
                {
                    var value = CollectInput(incoming, port, outputs, diagnostics, node.Id);
                    if (value.IsNone && port.IsRequired)
                    {
                        missing = true;
                        break;
                    }

                    inputs[port.Name] = value;
                    continue;
                }

                if (options.Seeds.TryGetValue(key, out var seed))
                {
                    inputs[port.Name] = seed;
                    continue;
                }

                if (port.Constant is { } constant && constant.HasValue)
                {
                    inputs[port.Name] = constant.ToValue();
                    continue;
                }

                if (port.IsRequired)
                {
                    diagnostics.Add(new GraphDiagnostic(
                        "graph.missing_input",
                        $"节点 {node.DefinitionId} 缺少必需输入 {port.Name}。",
                        node.Id,
                        port.Name));
                    missing = true;
                    break;
                }

                inputs[port.Name] = GraphValue.None;
            }

            if (missing)
            {
                incomplete = true;

                // 发射阶段不依赖部件树（HTML 由唯一渲染器按文档产出）：
                // 上游缺值时仍执行，由执行器自己决定给出降级输出还是空输出，
                // 缺失诊断与 Incomplete 状态照常保留。
                if (definition?.RunsWhenInputsMissing != true)
                {
                    continue;
                }
            }
            else if (node.IsBypassed)
            {
                ExecuteBypass(node, inputs, outputs);
                executed.Add(node.Id);
                continue;
            }

            IReadOnlyDictionary<string, GraphValue> produced;
            try
            {
                var appliesTo = options.AppliesToOverride ?? graph.AppliesTo;
                produced = definition!.Composite is { } composite
                    ? EvaluateComposite(
                        node, composite, inputs, options, appliesTo, diagnostics, compositeStack)
                    : definition.Executor is null
                        ? new Dictionary<string, GraphValue>(StringComparer.Ordinal)
                        : definition.Executor(new GraphEvaluationContext(
                            node,
                            inputs,
                            options.InstanceValues,
                            options.RootNode,
                            appliesTo,
                            options.Document,
                            options.BypassedStageIds,
                            diagnostics.Add));
            }
            catch (Exception exception)
            {
                diagnostics.Add(new GraphDiagnostic(
                    "graph.node_failed",
                    $"节点 {node.DefinitionId} 执行失败：{exception.GetType().Name}。",
                    node.Id));
                incomplete = true;
                continue;
            }

            executed.Add(node.Id);
            foreach (var port in node.Outputs)
            {
                if (!produced.TryGetValue(port.Name, out var value))
                {
                    outputs[GraphEvaluationResult.Key(node.Id, port.Name)] = GraphValue.None;
                    continue;
                }

                if (!GraphValueCompatibility.IsAssignable(value.Kind, port.Kind))
                {
                    diagnostics.Add(new GraphDiagnostic(
                        "graph.output_kind_mismatch",
                        $"节点 {node.DefinitionId} 输出 {port.Name} 类型为 {value.Kind}，端口声明为 {port.Kind}。",
                        node.Id,
                        port.Name));
                    outputs[GraphEvaluationResult.Key(node.Id, port.Name)] = GraphValue.None;
                    incomplete = true;
                    continue;
                }

                outputs[GraphEvaluationResult.Key(node.Id, port.Name)] = value;
            }
        }

        if (requireSink)
        {
            var sink = graph.FindSink();
            var ambiguousSink = false;
            if (sink is null)
            {
                // 顶层没有 emit 时，允许复合阶段把内层 emit 的 html 暴露成汇点；
                // 多于一个候选时报错，不静默取首个。
                var resolution = GraphSinkResolver.Resolve(graph, options.Document?.GraphStages);
                if (resolution.Ambiguous)
                {
                    diagnostics.Add(new GraphDiagnostic(
                        GraphSinkResolver.AmbiguousCode,
                        GraphSinkResolver.DescribeAmbiguity(resolution),
                        resolution.Candidates[0].Id));
                    ambiguousSink = true;
                    incomplete = true;
                }
                else
                {
                    sink = resolution.Node;
                }
            }

            if (!ambiguousSink
                && (sink is null
                    || !outputs.TryGetValue(GraphEvaluationResult.Key(sink.Id, "html"), out var html)
                    || html.IsNone))
            {
                diagnostics.Add(new GraphDiagnostic(
                    "graph.no_root_output",
                    "图没有产出 html.emit 的 html 输出。",
                    sink?.Id));
                incomplete = true;
            }
        }

        return new GraphEvaluationResult(
            incomplete ? GraphEvaluationStatus.Incomplete : GraphEvaluationStatus.Ok,
            outputs,
            executed,
            diagnostics);
    }

    /// <summary>
    /// 复合阶段求值：外部输入按映射注入子图，跑同一套求值器，再按映射取回输出。
    /// 环由 compositeStack（定义 Id 显式栈）拦截，不设深度上限。
    /// </summary>
    private IReadOnlyDictionary<string, GraphValue> EvaluateComposite(
        GraphNode node,
        CompositeStageDefinition composite,
        IReadOnlyDictionary<string, GraphValue> inputs,
        GraphEvaluationOptions options,
        NodeType appliesTo,
        List<GraphDiagnostic> diagnostics,
        List<string> compositeStack)
    {
        if (compositeStack.Contains(composite.Id))
        {
            diagnostics.Add(new GraphDiagnostic(
                CompositeStageValidator.CycleCode,
                "复合阶段引用成环：" + string.Join(" → ", compositeStack.Append(composite.Id)) + "。",
                node.Id));
            return new Dictionary<string, GraphValue>(StringComparer.Ordinal);
        }

        compositeStack.Add(composite.Id);
        try
        {
            var seeds = new Dictionary<string, GraphValue>(StringComparer.Ordinal);
            foreach (var binding in composite.Inputs)
            {
                if (!inputs.TryGetValue(binding.Name, out var value) || value.IsNone)
                {
                    // 外部没有值时绝不注入 None：让内部端口的常量与缺省逻辑照常生效。
                    continue;
                }

                if (CompositeReference.TrySplit(binding.InnerTarget, out var innerNode, out var innerPort))
                {
                    seeds[GraphEvaluationResult.Key(innerNode, innerPort)] = value;
                }
            }

            var nested = Evaluate(
                composite.Graph,
                new GraphEvaluationOptions
                {
                    RootNode = options.RootNode,
                    Document = options.Document,
                    InstanceValues = options.InstanceValues,
                    Seeds = seeds,
                    BypassedStageIds = options.BypassedStageIds,
                    AppliesToOverride = appliesTo,
                },
                compositeStack,
                requireSink: false);

            foreach (var diagnostic in nested.Diagnostics)
            {
                diagnostics.Add(diagnostic with
                {
                    NodeId = diagnostic.NodeId is null ? node.Id : node.Id + "/" + diagnostic.NodeId,
                });
            }

            var produced = new Dictionary<string, GraphValue>(StringComparer.Ordinal);
            foreach (var output in composite.Outputs)
            {
                var value = GraphValue.None;
                if (CompositeReference.TrySplit(output.InnerSource, out var innerNode, out var innerPort) &&
                    nested.Outputs.TryGetValue(GraphEvaluationResult.Key(innerNode, innerPort), out var nestedValue))
                {
                    value = nestedValue;
                }

                produced[output.Name] = value;
            }

            return produced;
        }
        finally
        {
            compositeStack.RemoveAt(compositeStack.Count - 1);
        }
    }

    private static GraphValue CollectInput(
        IReadOnlyList<GraphEdge> incoming,
        GraphPort port,
        IReadOnlyDictionary<string, GraphValue> outputs,
        ICollection<GraphDiagnostic> diagnostics,
        string nodeId)
    {
        var values = new List<GraphValue>(incoming.Count);
        foreach (var edge in incoming)
        {
            var key = GraphEvaluationResult.Key(edge.FromNodeId, edge.FromPort);
            if (!outputs.TryGetValue(key, out var value) || value.IsNone)
            {
                diagnostics.Add(new GraphDiagnostic(
                    "graph.upstream_missing",
                    $"上游 {edge.FromNodeId}/{edge.FromPort} 没有产出值。",
                    nodeId,
                    port.Name));
                return GraphValue.None;
            }

            values.Add(value);
        }

        if (!port.IsMulti)
        {
            return values[0];
        }

        if (port.Kind == GraphValueKind.NodeSeq)
        {
            var nodes = new List<MarkdownNode>();
            foreach (var value in values)
            {
                if (value.Kind == GraphValueKind.NodeRef)
                {
                    nodes.Add(value.AsNode());
                }
                else if (value.Kind == GraphValueKind.NodeSeq)
                {
                    nodes.AddRange(value.AsNodes());
                }
                else if (!value.IsNone)
                {
                    diagnostics.Add(new GraphDiagnostic(
                        "graph.multi_kind_mismatch",
                        $"多输入 {port.Name} 收到 {value.Kind}，无法聚合为 NodeSeq。",
                        nodeId,
                        port.Name));
                }
            }

            return GraphValue.FromNodes(nodes);
        }

        if (IsPartKind(port.Kind))
        {
            // 分支与循环会同时往一个部件输入扇入：按出现顺序拼成一片森林，
            // 由下游部件节点决定包哪层壳。
            var parts = new List<GraphPart>();
            var styled = port.Kind == GraphValueKind.StyledPartTree;
            foreach (var value in values)
            {
                if (!IsPartKind(value.Kind))
                {
                    diagnostics.Add(new GraphDiagnostic(
                        "graph.multi_kind_mismatch",
                        $"多输入 {port.Name} 收到 {value.Kind}，无法聚合为部件树。",
                        nodeId,
                        port.Name));
                    continue;
                }

                parts.AddRange(value.AsParts());
                styled |= value.Kind == GraphValueKind.StyledPartTree;
            }

            return GraphValue.FromParts(parts, styled);
        }

        if (port.Kind == GraphValueKind.StyleMap)
        {
            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var value in values.Where(item => item.Kind == GraphValueKind.StyleMap))
            {
                foreach (var pair in value.AsStyleMap())
                {
                    merged[pair.Key] = pair.Value;
                }
            }

            return GraphValue.FromStyleMap(merged);
        }

        return values[0];
    }

    /// <summary>
    /// 旁路透传：按同名输出→输入优先，其次按类型兼容的输入直通。
    /// 部件树与带样式部件树视为同一族（样式只是附加内联声明），
    /// 因此旁路「解样式」仍能把部件树交给下游 emit，链路保持连通。
    /// 没有任何兼容上游时产出 None，由下游按“必需输入缺失”显式报诊断，绝不伪造默认值。
    /// </summary>
    private static void ExecuteBypass(
        GraphNode node,
        IReadOnlyDictionary<string, GraphValue> inputs,
        IDictionary<string, GraphValue> outputs)
    {
        foreach (var output in node.Outputs)
        {
            var match = node.FindInput(output.Name)
                        ?? node.Inputs.FirstOrDefault(port =>
                            inputs.TryGetValue(port.Name, out var value)
                            && IsBypassTransparent(value.Kind, output.Kind));
            outputs[GraphEvaluationResult.Key(node.Id, output.Name)] =
                match is not null && inputs.TryGetValue(match.Name, out var value)
                    ? value
                    : GraphValue.None;
        }
    }

    private static bool IsBypassTransparent(GraphValueKind from, GraphValueKind to) =>
        from == to
        || (from == GraphValueKind.NodeRef && to == GraphValueKind.NodeSeq)
        || (IsPartKind(from) && IsPartKind(to));

    private static bool IsPartKind(GraphValueKind kind) =>
        kind is GraphValueKind.PartTree or GraphValueKind.StyledPartTree;

    private static List<string> BuildOrder(NodeGraph graph, out GraphDiagnostic? failure)
    {
        var indegree = graph.Nodes.ToDictionary(node => node.Id, _ => 0, StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            if (indegree.ContainsKey(edge.ToNodeId))
            {
                indegree[edge.ToNodeId]++;
            }
        }

        var ready = new SortedSet<string>(
            indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key),
            StringComparer.Ordinal);
        var order = new List<string>(graph.Nodes.Count);

        while (ready.Count > 0)
        {
            var current = ready.Min!;
            ready.Remove(current);
            order.Add(current);

            foreach (var edge in graph.Edges.Where(edge =>
                         string.Equals(edge.FromNodeId, current, StringComparison.Ordinal)))
            {
                if (!indegree.TryGetValue(edge.ToNodeId, out var value))
                {
                    continue;
                }

                indegree[edge.ToNodeId] = value - 1;
                if (value - 1 == 0)
                {
                    ready.Add(edge.ToNodeId);
                }
            }
        }

        failure = order.Count == graph.Nodes.Count
            ? null
            : new GraphDiagnostic("graph.cycle", "图存在环，无法拓扑求值。");
        return order;
    }

    private static IReadOnlyDictionary<string, GraphValue> EmptyOutputs() =>
        new Dictionary<string, GraphValue>(StringComparer.Ordinal);
}
