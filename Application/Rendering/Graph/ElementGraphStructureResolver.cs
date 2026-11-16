using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Rendering.Graph;

/// <summary>
/// 渲染期结构解析：自定义元素挂了图时，取「取结构」的实际产出当结构声明。
/// 只求值到取结构为止——发射阶段会回头渲染同一节点，整图求值会无限递归。
/// </summary>
internal static class ElementGraphStructureResolver
{
    // 求值链路里会新建渲染器（快照回落/画布发射），实例级开关挡不住递归，只能在调用栈上按节点去重。
    [ThreadStatic]
    private static HashSet<MarkdownNode>? _resolving;

    // 注册表按文档重建：复合阶段声明随 .md.graph.xml 走，绝不写进全局单例。
    private static readonly ConditionalWeakTable<MarkdownDocument, RegistryEntry> Registries = new();

    /// <summary>进入某节点的图求值；已在求值中返回 false，调用方应直接走结构声明/内置模板。</summary>
    public static bool TryEnter(MarkdownNode node)
    {
        _resolving ??= new HashSet<MarkdownNode>(ReferenceEqualityComparer.Instance);
        return _resolving.Add(node);
    }

    public static void Exit(MarkdownNode node) => _resolving?.Remove(node);

    public static ElementStructureNode? TryResolve(MarkdownDocument document, MarkdownNode node, NodeGraph graph)
    {
        // 复合节点先展开成普通节点（Id 为 外层/内层），再交给既有求值器；
        // 这样取结构阶段即便被包在复合子图里也能被找到，且不新增渲染路径。
        var expanded = GraphCompositeExpander.Expand(graph, document.GraphStages);
        var effectiveGraph = expanded.Expanded ? expanded.Graph : graph;

        var templateNode = effectiveGraph.Nodes.FirstOrDefault(item =>
            string.Equals(item.DefinitionId, ElementStageCatalog.TemplateStage, StringComparison.Ordinal));
        if (templateNode is null)
        {
            return null;
        }

        var reachable = CollectUpstream(effectiveGraph, templateNode.Id);
        var truncated = new NodeGraph
        {
            Id = effectiveGraph.Id,
            SchemaVersion = effectiveGraph.SchemaVersion,
            AppliesTo = effectiveGraph.AppliesTo,
        };
        truncated.Nodes.AddRange(effectiveGraph.Nodes.Where(item => reachable.Contains(item.Id)).Select(item => item.Clone()));
        truncated.Edges.AddRange(effectiveGraph.Edges.Where(edge =>
            reachable.Contains(edge.FromNodeId) && reachable.Contains(edge.ToNodeId)));

        var instanceValues = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in node.Attributes)
        {
            instanceValues[pair.Key] = pair.Value;
        }

        var result = new NodeGraphEvaluator(ResolveRegistry(document)).Evaluate(truncated, new GraphEvaluationOptions
        {
            // 画布与整图渲染都按选中实例求值，这里保持同一约定。
            RootNode = node,
            Document = document,
            InstanceValues = instanceValues,
        });

        var parts = result.GetOutput(templateNode.Id, "parts");
        if (parts.Kind != GraphValueKind.PartTree)
        {
            return null;
        }

        var produced = parts.AsParts();
        return GraphPartStructure.HasCustomParts(produced) ? GraphPartStructure.ToStructureRoot(produced) : null;
    }

    /// <summary>沿连线反向收集取结构的上游闭包，把发射/绑定/样式阶段排除在求值之外。</summary>
    private static HashSet<string> CollectUpstream(NodeGraph graph, string startId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { startId };
        var pending = new Stack<string>();
        pending.Push(startId);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var edge in graph.Edges)
            {
                if (!string.Equals(edge.ToNodeId, current, StringComparison.Ordinal))
                {
                    continue;
                }

                if (visited.Add(edge.FromNodeId))
                {
                    pending.Push(edge.FromNodeId);
                }
            }
        }

        return visited;
    }

    private static GraphNodeRegistry ResolveRegistry(MarkdownDocument document)
    {
        var entry = Registries.GetOrCreateValue(document);
        if (!ReferenceEquals(entry.Stages, document.GraphStages))
        {
            entry.Stages = document.GraphStages;
            entry.Registry = NodeGraphStageLibrary.CreateRegistry(document.GraphStages);
        }

        return entry.Registry!;
    }

    private sealed class RegistryEntry
    {
        public IReadOnlyList<CompositeStageDefinition>? Stages { get; set; }

        public GraphNodeRegistry? Registry { get; set; }
    }
}
