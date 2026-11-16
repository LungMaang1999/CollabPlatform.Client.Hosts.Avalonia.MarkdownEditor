using System;
using System.Collections.Generic;
using System.Linq;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Rendering.Graph;

/// <summary>
/// 汇点解析：只服务“谁是 <c>stage.emit</c>”这一处判定与出 HTML 取值，不参与求值、不进签名。
/// <para>
/// 规则（按优先级）：
/// 1. 顶层存在 <c>stage.emit</c> 时优先，行为与既有 <see cref="NodeGraph.FindSink"/> 完全一致；
/// 2. 否则找“把内层 emit 的 <c>html</c> 暴露成同名输出端口”的复合包装节点，恰好一个才用；
/// 3. 多于一个不静默取首个，调用方报 <see cref="AmbiguousCode"/> 并把锚点落在包装节点 Id 上。
/// </para>
/// </summary>
public static class GraphSinkResolver
{
    /// <summary>多个复合节点都能产出 html 时的固定诊断码；锚点是画布上真实存在的包装节点 Id。</summary>
    public const string AmbiguousCode = "graph.composite_sink_ambiguous";

    private const string SinkPort = "html";

    public static GraphSinkResolution Resolve(
        NodeGraph graph,
        IReadOnlyList<CompositeStageDefinition>? stages)
    {
        ArgumentNullException.ThrowIfNull(graph);

        // 规则 1：顶层 emit 优先，沿用既有实现，未包裹的图行为一条不变。
        var topLevel = graph.FindSink();
        if (topLevel is not null)
        {
            return new GraphSinkResolution(topLevel, IsTopLevel: true, Ambiguous: false, Candidates: [topLevel]);
        }

        if (stages is not { Count: > 0 })
        {
            return GraphSinkResolution.None;
        }

        var byId = new Dictionary<string, CompositeStageDefinition>(StringComparer.Ordinal);
        foreach (var stage in stages)
        {
            // 声明顺序优先；重复 Id 已被装载期拒绝，这里只防御。
            byId.TryAdd(stage.Id, stage);
        }

        var candidates = new List<GraphNode>();
        foreach (var node in graph.Nodes)
        {
            if (ProducesHtml(node.DefinitionId, byId, new HashSet<string>(StringComparer.Ordinal)))
            {
                candidates.Add(node);
            }
        }

        return candidates.Count switch
        {
            0 => GraphSinkResolution.None,
            1 => new GraphSinkResolution(candidates[0], IsTopLevel: false, Ambiguous: false, candidates),
            // 规则 3：歧义时不替调用方挑一个——选错的产物看起来仍然“正常”，比报错更危险。
            _ => new GraphSinkResolution(null, IsTopLevel: false, Ambiguous: true, candidates),
        };
    }

    public static string DescribeAmbiguity(GraphSinkResolution resolution) =>
        "多个复合节点都产出 html："
        + string.Join("、", resolution.Candidates.Select(node => node.Id))
        + "。汇点只能有一个；请保留一个，或恢复顶层 "
        + ElementStageCatalog.EmitStage
        + "。";

    /// <summary>
    /// 复合阶段是否把内层 emit 的 html 暴露成同名输出端口；嵌套复合按输出映射逐层下探。
    /// 用访问集合防声明环（装载期已拒绝，这里纯防御），不设深度上限。
    /// </summary>
    private static bool ProducesHtml(
        string definitionId,
        IReadOnlyDictionary<string, CompositeStageDefinition> stages,
        HashSet<string> visiting)
    {
        if (!stages.TryGetValue(definitionId, out var composite) || !visiting.Add(definitionId))
        {
            return false;
        }

        try
        {
            foreach (var output in composite.Outputs)
            {
                if (!string.Equals(output.Name, SinkPort, StringComparison.Ordinal)
                    || !CompositeReference.TrySplit(output.InnerSource, out var innerNodeId, out var innerPort)
                    || !string.Equals(innerPort, SinkPort, StringComparison.Ordinal))
                {
                    continue;
                }

                var inner = composite.Graph.FindNode(innerNodeId);
                if (inner is null)
                {
                    continue;
                }

                if (string.Equals(inner.DefinitionId, ElementStageCatalog.EmitStage, StringComparison.Ordinal)
                    || ProducesHtml(inner.DefinitionId, stages, visiting))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            visiting.Remove(definitionId);
        }
    }
}

/// <summary>
/// 汇点解析结果。<see cref="Node"/> 为 null 且 <see cref="Ambiguous"/> 为 true 表示多个候选未选取，
/// 调用方应报 <see cref="GraphSinkResolver.AmbiguousCode"/>，不要退回“取首个”。
/// </summary>
public sealed record GraphSinkResolution(
    GraphNode? Node,
    bool IsTopLevel,
    bool Ambiguous,
    IReadOnlyList<GraphNode> Candidates)
{
    public static GraphSinkResolution None { get; } = new(null, IsTopLevel: false, Ambiguous: false, []);
}
