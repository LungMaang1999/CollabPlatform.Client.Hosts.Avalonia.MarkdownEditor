using System;
using System.Collections.Generic;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Rendering.Graph;

/// <summary>
/// 阶段来源。画布只依赖这个接口：换一个实现就换了一套定义体系
/// （声明树、继承覆写、槽位约束、多套变体），画布代码不需要改。
/// </summary>
public interface IGraphStageProvider
{
    /// <summary>可编排的元素类型，按展示顺序排列。</summary>
    IReadOnlyList<ElementGraphDefinition> Definitions { get; }

    ElementGraphDefinition? Find(NodeType nodeType);

    ElementStageDefinition? FindStage(NodeType nodeType, ElementStageKind kind);

    /// <summary>所有定义里出现过的阶段，按定义 Id 去重后注册。</summary>
    GraphNodeRegistry CreateRegistry();

    /// <summary>按定义生成默认投影：节点与连线都来自定义，不写死阶段 Id。</summary>
    NodeGraph CreateDefaultProjection(NodeType nodeType);
}

/// <summary>
/// 声明式提供者：定义以 <see cref="ElementGraphDefinition"/> 列表给出。
/// 图定义与节点注册表都从这份列表派生，不在画布或求值代码里重复列举阶段。
/// </summary>
public sealed class DeclaredNodeGraphProvider : IGraphStageProvider
{
    public static DeclaredNodeGraphProvider Default { get; } =
        new(ElementGraphDefinitionCatalog.Default);

    private readonly IReadOnlyList<ElementGraphDefinition> _definitions;
    private readonly IReadOnlyList<CompositeStageDefinition> _compositeStages;
    private readonly GraphNodeRegistry _registry;

    public DeclaredNodeGraphProvider(
        IReadOnlyList<ElementGraphDefinition> definitions,
        IReadOnlyList<CompositeStageDefinition>? compositeStages = null)
    {
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _compositeStages = compositeStages ?? [];
        _registry = BuildRegistry(definitions, _compositeStages);
    }

    public IReadOnlyList<ElementGraphDefinition> Definitions => _definitions;

    /// <summary>文件声明的复合阶段；只进注册表，不进默认投影，默认态字节不变。</summary>
    public IReadOnlyList<CompositeStageDefinition> CompositeStages => _compositeStages;

    public ElementGraphDefinition? Find(NodeType nodeType)
    {
        foreach (var definition in _definitions)
        {
            if (definition.NodeType == nodeType)
            {
                return definition;
            }
        }

        return null;
    }

    public ElementStageDefinition? FindStage(NodeType nodeType, ElementStageKind kind) =>
        Find(nodeType)?.FindStage(kind);

    public GraphNodeRegistry CreateRegistry() => _registry;

    public NodeGraph CreateDefaultProjection(NodeType nodeType) =>
        ElementGraphProjectionBuilder.Build(
            Find(nodeType) ?? ElementGraphDefinitionCatalog.CreateFallback(nodeType),
            _registry);

    private static GraphNodeRegistry BuildRegistry(
        IReadOnlyList<ElementGraphDefinition> definitions,
        IReadOnlyList<CompositeStageDefinition> compositeStages)
    {
        var registry = new GraphNodeRegistry();
        foreach (var definition in definitions)
        {
            foreach (var stage in definition.Stages)
            {
                if (registry.Contains(stage.Id))
                {
                    continue;
                }

                registry.Register(new GraphNodeDefinition
                {
                    Id = stage.Id,
                    DisplayName = stage.DisplayName,
                    Description = stage.Description,
                    Category = stage.Category,
                    Inputs = stage.Inputs,
                    Outputs = stage.Outputs,
                    Controls = stage.Controls,
                    Executor = stage.Executor,
                    RunsWhenInputsMissing = stage.RunsWhenInputsMissing,
                });
            }
        }

        GraphNodeLibrary.RegisterInto(registry);

        foreach (var stage in compositeStages)
        {
            if (!registry.Contains(stage.Id))
            {
                registry.Register(stage.ToNodeDefinition());
            }
        }

        return registry;
    }
}

/// <summary>
/// 投影构建器：把一条元素定义铺成单链图。
/// 连线按端口名与值类型自动匹配，不认识任何具体阶段，因此定义换形状时这里不用改。
/// </summary>
public static class ElementGraphProjectionBuilder
{
    private const int StartX = 48;
    private const int ColumnStep = 288;
    private const int StartY = 96;

    public static NodeGraph Build(ElementGraphDefinition definition, GraphNodeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(registry);

        var graph = new NodeGraph
        {
            Id = "type:" + definition.NodeType,
            AppliesTo = definition.NodeType,
        };

        var created = new List<CreatedStage>(definition.Stages.Count);
        foreach (var stage in definition.Stages)
        {
            var node = registry.CreateNode(stage.Id, stage.ResolveNodeId());
            if (node is null)
            {
                continue;
            }

            node.X = StartX + (created.Count * ColumnStep);
            node.Y = StartY;
            graph.Nodes.Add(node);
            created.Add(new CreatedStage(stage, node));
        }

        for (var index = 0; index < created.Count; index++)
        {
            var target = created[index];
            foreach (var input in target.Stage.Inputs)
            {
                var source = FindSource(created, index, input);
                if (source is null)
                {
                    continue;
                }

                graph.Edges.Add(new GraphEdge(
                    source.Value.Node.Id,
                    source.Value.Port,
                    target.Node.Id,
                    input.Name));
            }
        }

        return graph;
    }

    /// <summary>
    /// 先找同名同类型的端口（nodes→nodes、parts→parts），再退化为最近一个类型匹配的端口。
    /// 两种规则都只依赖定义里的端口声明。
    /// </summary>
    private static CreatedPort? FindSource(
        IReadOnlyList<CreatedStage> created,
        int targetIndex,
        GraphPortDescriptor input)
    {
        for (var index = targetIndex - 1; index >= 0; index--)
        {
            foreach (var output in created[index].Stage.Outputs)
            {
                if (output.Kind == input.Kind
                    && string.Equals(output.Name, input.Name, StringComparison.Ordinal))
                {
                    return new CreatedPort(created[index].Node, output.Name);
                }
            }
        }

        for (var index = targetIndex - 1; index >= 0; index--)
        {
            foreach (var output in created[index].Stage.Outputs)
            {
                if (output.Kind == input.Kind)
                {
                    return new CreatedPort(created[index].Node, output.Name);
                }
            }
        }

        return null;
    }

    private sealed record CreatedStage(ElementStageDefinition Stage, GraphNode Node);

    private readonly record struct CreatedPort(GraphNode Node, string Port);
}
