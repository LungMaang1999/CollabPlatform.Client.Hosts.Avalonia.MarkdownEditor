using System;
using System.Collections.Generic;
using System.Linq;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Rendering.Graph;

public sealed record CompositeStageValidationResult(
    IReadOnlyList<CompositeStageDefinition> Stages,
    IReadOnlyList<GraphDiagnostic> Diagnostics);

/// <summary>
/// 复合阶段的声明集校验。保存时用返回值拒绝保存，装载时用返回的 <see cref="CompositeStageValidationResult.Stages"/>
/// 丢弃坏声明——同一套函数、两种调用策略。
/// 环检测只靠“定义 Id 集合 + 显式访问状态”，不存在深度上限或计数上限。
/// </summary>
public static class CompositeStageValidator
{
    public const string CycleCode = GraphCompositeExpander.CycleCode;
    public const string MissingReferenceCode = "graph.composite_missing_ref";
    public const string PortInvalidCode = GraphCompositeExpander.PortInvalidCode;
    public const string InvalidDeclarationCode = "graph.composite_invalid";

    private const string StageIdPrefix = "stage.";

    public static CompositeStageValidationResult Validate(
        IReadOnlyList<CompositeStageDefinition> stages,
        GraphNodeRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(stages);

        var diagnostics = new List<GraphDiagnostic>();
        var baseRegistry = registry ?? NodeGraphStageLibrary.CreateRegistry();

        // 阶段 Id 重复时先到先得，后到的整条丢弃。
        var map = new Dictionary<string, CompositeStageDefinition>(StringComparer.Ordinal);
        foreach (var stage in stages)
        {
            if (string.IsNullOrWhiteSpace(stage.Id))
            {
                diagnostics.Add(new GraphDiagnostic(
                    InvalidDeclarationCode, "复合阶段缺少 Id，已跳过该条声明。"));
                continue;
            }

            if (baseRegistry.Contains(stage.Id))
            {
                diagnostics.Add(new GraphDiagnostic(
                    InvalidDeclarationCode,
                    $"复合阶段 Id「{stage.Id}」与既有定义冲突，已跳过该条声明。",
                    stage.Id));
                continue;
            }

            if (!map.TryAdd(stage.Id, stage))
            {
                diagnostics.Add(new GraphDiagnostic(
                    InvalidDeclarationCode,
                    $"复合阶段 Id「{stage.Id}」重复，已保留第一条声明。",
                    stage.Id));
            }
        }

        DropMissingReferences(map, baseRegistry, diagnostics);
        DropCycles(map, diagnostics);

        var invalid = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stage in map.Values.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            if (!ValidatePorts(stage, diagnostics))
            {
                invalid.Add(stage.Id);
            }
        }

        foreach (var id in invalid)
        {
            map.Remove(id);
        }

        return new CompositeStageValidationResult(
            map.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToList(),
            diagnostics);
    }

    /// <summary>引用了不存在的阶段声明：按前缀识别，避免把未知库节点误报成缺声明。</summary>
    private static void DropMissingReferences(
        Dictionary<string, CompositeStageDefinition> map,
        GraphNodeRegistry baseRegistry,
        List<GraphDiagnostic> diagnostics)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var stage in map.Values.ToList())
            {
                var missing = stage.Graph.Nodes
                    .Select(node => node.DefinitionId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .FirstOrDefault(id =>
                        id.StartsWith(StageIdPrefix, StringComparison.Ordinal) &&
                        !baseRegistry.Contains(id) &&
                        !map.ContainsKey(id));

                if (missing is null)
                {
                    continue;
                }

                diagnostics.Add(new GraphDiagnostic(
                    MissingReferenceCode,
                    $"复合阶段「{stage.Id}」引用了未声明的阶段「{missing}」，已丢弃该条声明。",
                    stage.Id));
                map.Remove(stage.Id);
                changed = true;
                break;
            }
        }
    }

    /// <summary>跨图引用成环：与图内连线环（graph.cycle）不是同一概念，码不复用。</summary>
    private static void DropCycles(
        Dictionary<string, CompositeStageDefinition> map,
        List<GraphDiagnostic> diagnostics)
    {
        while (true)
        {
            var cycle = FindCycle(map);
            if (cycle is null)
            {
                return;
            }

            diagnostics.Add(new GraphDiagnostic(
                CycleCode,
                "复合阶段引用成环：" + string.Join(" → ", cycle) + "。",
                cycle[0]));

            // 环上所有阶段一起丢弃：只丢一个会让其余阶段在下一轮继续成环，诊断翻倍。
            foreach (var id in cycle.Take(cycle.Count - 1))
            {
                map.Remove(id);
            }
        }
    }

    /// <summary>迭代式 DFS（显式栈），每张声明只访问一次；终止由状态集保证，不设深度上限。</summary>
    private static List<string>? FindCycle(IReadOnlyDictionary<string, CompositeStageDefinition> map)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1=在栈上 2=已完成
        var path = new List<string>();
        var pathIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Stack<Frame>();

        foreach (var start in map.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            if (state.ContainsKey(start))
            {
                continue;
            }

            state[start] = 1;
            path.Add(start);
            pathIndex[start] = path.Count - 1;
            stack.Push(new Frame(start, ChildrenOf(start, map), 0));

            while (stack.Count > 0)
            {
                var frame = stack.Pop();
                if (frame.Next >= frame.Children.Count)
                {
                    state[frame.Id] = 2;
                    path.RemoveAt(path.Count - 1);
                    pathIndex.Remove(frame.Id);
                    continue;
                }

                stack.Push(frame with { Next = frame.Next + 1 });
                var child = frame.Children[frame.Next];
                if (!map.ContainsKey(child))
                {
                    continue;
                }

                if (state.TryGetValue(child, out var childState))
                {
                    if (childState != 1)
                    {
                        continue;
                    }

                    var cycle = path.Skip(pathIndex[child]).ToList();
                    cycle.Add(child);
                    return cycle;
                }

                state[child] = 1;
                path.Add(child);
                pathIndex[child] = path.Count - 1;
                stack.Push(new Frame(child, ChildrenOf(child, map), 0));
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ChildrenOf(
        string stageId,
        IReadOnlyDictionary<string, CompositeStageDefinition> map) =>
        map.TryGetValue(stageId, out var stage)
            ? stage.Graph.Nodes
                .Select(node => node.DefinitionId)
                .Where(id => !string.IsNullOrWhiteSpace(id) && map.ContainsKey(id))
                .Distinct(StringComparer.Ordinal)
                .ToList()
            : [];

    private static bool ValidatePorts(
        CompositeStageDefinition stage,
        List<GraphDiagnostic> diagnostics)
    {
        var valid = true;

        if (stage.Outputs.Count == 0)
        {
            diagnostics.Add(new GraphDiagnostic(
                InvalidDeclarationCode,
                $"复合阶段「{stage.Id}」没有输出端口，已丢弃该条声明。",
                stage.Id));
            return false;
        }

        var inputNames = new HashSet<string>(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in stage.Inputs)
        {
            if (string.IsNullOrWhiteSpace(input.Name) || !inputNames.Add(input.Name))
            {
                diagnostics.Add(new GraphDiagnostic(
                    PortInvalidCode,
                    $"复合阶段「{stage.Id}」的输入端口名缺失或重复：「{input.Name}」。",
                    stage.Id,
                    input.Name));
                valid = false;
                continue;
            }

            if (!CompositeReference.TrySplit(input.InnerTarget, out var nodeId, out var portName))
            {
                diagnostics.Add(new GraphDiagnostic(
                    PortInvalidCode,
                    $"复合阶段「{stage.Id}」的输入「{input.Name}」映射「{input.InnerTarget}」不是 nodeId/port 形式。",
                    stage.Id,
                    input.Name));
                valid = false;
                continue;
            }

            var target = stage.Graph.Nodes.FirstOrDefault(node =>
                string.Equals(node.Id, nodeId, StringComparison.Ordinal));
            var targetPort = target?.FindInput(portName);
            if (target is null || targetPort is null)
            {
                diagnostics.Add(new GraphDiagnostic(
                    PortInvalidCode,
                    $"复合阶段「{stage.Id}」的输入「{input.Name}」指向不存在的内部端口「{input.InnerTarget}」。",
                    stage.Id,
                    input.Name));
                valid = false;
                continue;
            }

            if (!GraphValueCompatibility.IsAssignable(input.Kind, targetPort.Kind))
            {
                diagnostics.Add(new GraphDiagnostic(
                    PortInvalidCode,
                    $"复合阶段「{stage.Id}」的输入「{input.Name}」类型 {input.Kind} 不能接到内部端口 {targetPort.Kind}。",
                    stage.Id,
                    input.Name));
                valid = false;
                continue;
            }

            if (!targetPort.IsMulti && !targets.Add(input.InnerTarget))
            {
                diagnostics.Add(new GraphDiagnostic(
                    PortInvalidCode,
                    $"复合阶段「{stage.Id}」有两个输入映射到同一内部端口「{input.InnerTarget}」。",
                    stage.Id,
                    input.Name));
                valid = false;
            }
        }

        var outputNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var output in stage.Outputs)
        {
            if (string.IsNullOrWhiteSpace(output.Name) || !outputNames.Add(output.Name))
            {
                diagnostics.Add(new GraphDiagnostic(
                    PortInvalidCode,
                    $"复合阶段「{stage.Id}」的输出端口名缺失或重复：「{output.Name}」。",
                    stage.Id,
                    output.Name));
                valid = false;
                continue;
            }

            if (!CompositeReference.TrySplit(output.InnerSource, out var nodeId, out var portName))
            {
                diagnostics.Add(new GraphDiagnostic(
                    PortInvalidCode,
                    $"复合阶段「{stage.Id}」的输出「{output.Name}」映射「{output.InnerSource}」不是 nodeId/port 形式。",
                    stage.Id,
                    output.Name));
                valid = false;
                continue;
            }

            var source = stage.Graph.Nodes.FirstOrDefault(node =>
                string.Equals(node.Id, nodeId, StringComparison.Ordinal));
            var sourcePort = source?.FindOutput(portName);
            if (source is null || sourcePort is null)
            {
                diagnostics.Add(new GraphDiagnostic(
                    PortInvalidCode,
                    $"复合阶段「{stage.Id}」的输出「{output.Name}」指向不存在的内部端口「{output.InnerSource}」。",
                    stage.Id,
                    output.Name));
                valid = false;
                continue;
            }

            if (!GraphValueCompatibility.IsAssignable(sourcePort.Kind, output.Kind))
            {
                diagnostics.Add(new GraphDiagnostic(
                    PortInvalidCode,
                    $"复合阶段「{stage.Id}」的输出「{output.Name}」类型 {sourcePort.Kind} 不能赋给声明端口 {output.Kind}。",
                    stage.Id,
                    output.Name));
                valid = false;
            }
        }

        return valid;
    }

    private readonly record struct Frame(string Id, IReadOnlyList<string> Children, int Next);
}
