using System;
using System.Collections.Generic;
using System.Linq;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Rendering.Graph;

public sealed record GraphConnectionCheck(bool Allowed, string Code, string Message);

/// <summary>图编辑期的连线校验：方向、类型、占位、成环。</summary>
public static class NodeGraphValidator
{
    public static GraphConnectionCheck CanConnect(
        NodeGraph graph,
        string fromNodeId,
        string fromPort,
        string toNodeId,
        string toPort)
    {
        ArgumentNullException.ThrowIfNull(graph);

        if (string.Equals(fromNodeId, toNodeId, StringComparison.Ordinal))
        {
            return Reject("graph.self_edge", "节点不能连接自身。");
        }

        var fromNode = graph.FindNode(fromNodeId);
        var toNode = graph.FindNode(toNodeId);
        if (fromNode is null || toNode is null)
        {
            return Reject("graph.unknown_node", "连线端点不在图中。");
        }

        var source = fromNode.FindOutput(fromPort);
        if (source is null)
        {
            return Reject("graph.unknown_port", $"输出端口 {fromPort} 不存在。");
        }

        var target = toNode.FindInput(toPort);
        if (target is null)
        {
            return Reject("graph.unknown_port", $"输入端口 {toPort} 不存在。");
        }

        if (!GraphValueCompatibility.IsAssignable(source.Kind, target.Kind))
        {
            return Reject(
                "graph.edge_kind_mismatch",
                $"端口类型不兼容：{source.Kind} → {target.Kind}。");
        }

        if (!target.IsMulti && graph.Edges.Any(edge =>
                string.Equals(edge.ToNodeId, toNodeId, StringComparison.Ordinal) &&
                string.Equals(edge.ToPort, toPort, StringComparison.Ordinal)))
        {
            return Reject("graph.input_occupied", $"输入端口 {toPort} 已被占用；先断线再连。");
        }

        if (IsReachable(graph, toNodeId, fromNodeId))
        {
            return Reject("graph.cycle", "该连线会形成环。");
        }

        return new GraphConnectionCheck(true, "graph.ok", "可以连接。");
    }

    public static IReadOnlyList<GraphDiagnostic> Validate(NodeGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var diagnostics = new List<GraphDiagnostic>();
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in graph.Nodes)
        {
            if (!nodeIds.Add(node.Id))
            {
                diagnostics.Add(new GraphDiagnostic("graph.duplicate_node", $"节点 Id 重复：{node.Id}。", node.Id));
            }
        }

        foreach (var edge in graph.Edges)
        {
            var fromNode = graph.FindNode(edge.FromNodeId);
            var toNode = graph.FindNode(edge.ToNodeId);
            if (fromNode is null || toNode is null)
            {
                diagnostics.Add(new GraphDiagnostic(
                    "graph.unknown_node",
                    $"连线端点不存在：{edge.FromNodeId} → {edge.ToNodeId}。"));
                continue;
            }

            var source = fromNode.FindOutput(edge.FromPort);
            var target = toNode.FindInput(edge.ToPort);
            if (source is null || target is null)
            {
                diagnostics.Add(new GraphDiagnostic(
                    "graph.unknown_port",
                    $"连线端口不存在：{edge.FromNodeId}/{edge.FromPort} → {edge.ToNodeId}/{edge.ToPort}.",
                    edge.ToNodeId,
                    edge.ToPort));
                continue;
            }

            if (!GraphValueCompatibility.IsAssignable(source.Kind, target.Kind))
            {
                diagnostics.Add(new GraphDiagnostic(
                    "graph.edge_kind_mismatch",
                    $"端口类型不兼容：{source.Kind} → {target.Kind}。",
                    edge.ToNodeId,
                    edge.ToPort));
            }
        }

        foreach (var group in graph.Edges.GroupBy(edge => (ToNodeId: edge.ToNodeId, ToPort: edge.ToPort)))
        {
            var target = graph.FindNode(group.Key.ToNodeId)?.FindInput(group.Key.ToPort);
            if (target is not null && !target.IsMulti && group.Count() > 1)
            {
                diagnostics.Add(new GraphDiagnostic(
                    "graph.input_occupied",
                    $"输入端口 {group.Key.ToPort} 有多条连线。",
                    group.Key.ToNodeId,
                    group.Key.ToPort));
            }
        }

        if (HasCycle(graph, out var cycleNode))
        {
            diagnostics.Add(new GraphDiagnostic("graph.cycle", "图存在环。", cycleNode));
        }

        return diagnostics;
    }

    private static bool IsReachable(NodeGraph graph, string startNodeId, string targetNodeId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(startNodeId);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            if (string.Equals(current, targetNodeId, StringComparison.Ordinal))
            {
                return true;
            }

            foreach (var edge in graph.Edges.Where(edge =>
                         string.Equals(edge.FromNodeId, current, StringComparison.Ordinal)))
            {
                stack.Push(edge.ToNodeId);
            }
        }

        return false;
    }

    private static bool HasCycle(NodeGraph graph, out string? cycleNode)
    {
        var indegree = graph.Nodes.ToDictionary(node => node.Id, _ => 0, StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            if (indegree.ContainsKey(edge.ToNodeId))
            {
                indegree[edge.ToNodeId]++;
            }
        }

        var queue = new Queue<string>(indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var visited = 0;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            visited++;
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
                    queue.Enqueue(edge.ToNodeId);
                }
            }
        }

        cycleNode = visited == graph.Nodes.Count
            ? null
            : indegree.FirstOrDefault(pair => pair.Value > 0).Key;
        return cycleNode is not null;
    }

    private static GraphConnectionCheck Reject(string code, string message) => new(false, code, message);
}
