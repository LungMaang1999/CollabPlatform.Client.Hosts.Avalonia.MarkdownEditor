using System;
using System.Collections.Generic;
using System.Linq;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;

/// <summary>
/// 复合阶段展开：把“引用子图”的节点替换为带 <c>外层/内层</c> 前缀的内层节点，
/// 并把外部入边 / 出边按端口映射重定向。
/// <para>
/// 展开是纯图变换，产物仍是普通 <see cref="NodeGraph"/>：求值器与渲染哈希都按同一份
/// 图结构消费，不新增渲染路径。没有复合节点时原样返回，保证既有哈希逐字节不变。
/// </para>
/// </summary>
public static class GraphCompositeExpander
{
    /// <summary>跨图复合引用成环。与装载期校验共用同一个码，不并入 L4 的单图环码。</summary>
    public const string CycleCode = "graph.composite_cycle";

    /// <summary>端口映射指向不存在的内层节点 / 端口。</summary>
    public const string PortInvalidCode = "graph.composite_port_invalid";

    /// <summary>
    /// 展开图中的复合节点。<paramref name="stages"/> 为空或图中没有复合节点时，
    /// 返回原图且 <see cref="GraphExpansionResult.Expanded"/> 为 false。
    /// </summary>
    public static GraphExpansionResult Expand(
        NodeGraph graph,
        IReadOnlyList<CompositeStageDefinition>? stages)
    {
        ArgumentNullException.ThrowIfNull(graph);

        if (stages is null || stages.Count == 0)
        {
            return new GraphExpansionResult(graph, false, []);
        }

        var byId = stages
            .Where(stage => !string.IsNullOrWhiteSpace(stage.Id))
            .GroupBy(stage => stage.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        if (!graph.Nodes.Any(node => !node.IsBypassed && byId.ContainsKey(node.DefinitionId)))
        {
            return new GraphExpansionResult(graph, false, []);
        }

        var expanded = new NodeGraph
        {
            Id = graph.Id,
            SchemaVersion = graph.SchemaVersion,
            AppliesTo = graph.AppliesTo,
        };
        var diagnostics = new List<GraphDiagnostic>();
        var stack = new List<string>();

        if (!ExpandLevel(graph, prefix: string.Empty, byId, stack, expanded, diagnostics, topLevel: true))
        {
            return new GraphExpansionResult(graph, false, diagnostics);
        }

        return new GraphExpansionResult(expanded, true, diagnostics);
    }

    private static bool ExpandLevel(
        NodeGraph source,
        string prefix,
        IReadOnlyDictionary<string, CompositeStageDefinition> stages,
        List<string> stack,
        NodeGraph target,
        List<GraphDiagnostic> diagnostics,
        bool topLevel)
    {
        var infos = new Dictionary<string, NodeLevelInfo>(StringComparer.Ordinal);

        foreach (var node in source.Nodes)
        {
            var flatId = topLevel ? node.Id : prefix + node.Id;
            if (!node.IsBypassed && stages.TryGetValue(node.DefinitionId, out var stage))
            {
                infos[node.Id] = new NodeLevelInfo(flatId, node, stage);
                continue;
            }

            var clone = node.Clone();
            clone.Id = flatId;
            target.Nodes.Add(clone);
            infos[node.Id] = new NodeLevelInfo(flatId, node, null);
        }

        foreach (var edge in source.Edges)
        {
            if (!infos.TryGetValue(edge.FromNodeId, out var from) || !infos.TryGetValue(edge.ToNodeId, out var to))
            {
                // 悬空连线由装载期校验负责；展开不重复报，保持原图语义。
                continue;
            }

            var fromNodeId = from.FlatId;
            var fromPort = edge.FromPort;
            if (from.Stage is { } fromStage)
            {
                var output = fromStage.Outputs.FirstOrDefault(item =>
                    string.Equals(item.Name, edge.FromPort, StringComparison.Ordinal));
                if (output is null
                    || !CompositeReference.TrySplit(output.InnerSource, out var innerNode, out var innerPort))
                {
                    diagnostics.Add(new GraphDiagnostic(
                        PortInvalidCode,
                        $"复合阶段 {fromStage.Id} 的输出端口 {edge.FromPort} 没有合法的内层映射。",
                        from.FlatId,
                        edge.FromPort));
                    return false;
                }

                fromNodeId = from.FlatId + CompositeReference.Separator + innerNode;
                fromPort = innerPort;
            }

            var toNodeId = to.FlatId;
            var toPort = edge.ToPort;
            if (to.Stage is { } toStage)
            {
                var input = toStage.Inputs.FirstOrDefault(item =>
                    string.Equals(item.Name, edge.ToPort, StringComparison.Ordinal));
                if (input is null
                    || !CompositeReference.TrySplit(input.InnerTarget, out var innerNode, out var innerPort))
                {
                    diagnostics.Add(new GraphDiagnostic(
                        PortInvalidCode,
                        $"复合阶段 {toStage.Id} 的输入端口 {edge.ToPort} 没有合法的内层映射。",
                        to.FlatId,
                        edge.ToPort));
                    return false;
                }

                toNodeId = to.FlatId + CompositeReference.Separator + innerNode;
                toPort = innerPort;
            }

            target.Edges.Add(new GraphEdge(fromNodeId, fromPort, toNodeId, toPort));
        }

        foreach (var info in infos.Values.Where(item => item.Stage is not null))
        {
            var stage = info.Stage!;
            if (stack.Contains(stage.Id, StringComparer.Ordinal))
            {
                diagnostics.Add(new GraphDiagnostic(
                    CycleCode,
                    $"复合阶段引用形成环：{string.Join(" → ", stack)} → {stage.Id}。",
                    info.FlatId));
                return false;
            }

            stack.Add(stage.Id);
            var inner = stage.Graph.Clone();
            ApplyOuterConstants(info, stage, inner);

            if (!ExpandLevel(
                    inner,
                    info.FlatId + CompositeReference.Separator,
                    stages,
                    stack,
                    target,
                    diagnostics,
                    topLevel: false))
            {
                return false;
            }

            stack.RemoveAt(stack.Count - 1);
        }

        return true;
    }

    /// <summary>
    /// 外层端口常量在内层没有入边时直接落到映射目标端口，等价于求值器把常量当种子注入。
    /// </summary>
    private static void ApplyOuterConstants(
        NodeLevelInfo info,
        CompositeStageDefinition stage,
        NodeGraph inner)
    {
        foreach (var input in stage.Inputs)
        {
            var outerPort = info.Node.FindInput(input.Name);
            if (outerPort?.Constant is not { } constant
                || !CompositeReference.TrySplit(input.InnerTarget, out var innerNodeId, out var innerPortName))
            {
                continue;
            }

            var innerPort = inner.FindNode(innerNodeId)?.FindInput(innerPortName);
            if (innerPort is not null)
            {
                innerPort.Constant = constant.Clone();
            }
        }
    }

    private sealed record NodeLevelInfo(string FlatId, GraphNode Node, CompositeStageDefinition? Stage);
}

/// <summary>展开结果。<see cref="Graph"/> 在未展开时就是传入的原图实例。</summary>
public sealed record GraphExpansionResult(
    NodeGraph Graph,
    bool Expanded,
    IReadOnlyList<GraphDiagnostic> Diagnostics);
