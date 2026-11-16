using System;
using System.Collections.Generic;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Rendering.Graph;

/// <summary>
/// 默认图投影：把元素定义投影成一条阶段链。
/// 阶段与连线都来自 <see cref="ElementGraphDefinition"/>，这里不再写死六个节点。
/// 旧文件没有图时即时生成，不写盘；首次编辑结构才持久化。
/// </summary>
public static class NodeGraphMigration
{
    public const string SyntaxNodeId = "syntax";
    public const string FieldsNodeId = "fields";
    public const string TemplateNodeId = "template";
    public const string BindingsNodeId = "bindings";
    public const string StyleNodeId = "style";
    public const string EmitNodeId = "emit";

    /// <summary>旧图文件里 template.variant 端口的名字；变体现在只由类型行为决定。</summary>
    public const string RetiredTemplateVariantInput = "variant";

    /// <summary>
    /// 旧图文件里的 variant 常量已废弃：读取时静默丢弃，不迁移、不覆盖类型行为。
    /// 这样旧文件不会继续盖住 .md.styles.xml 里的当前变体。
    /// </summary>
    public static bool IsRetiredInput(string definitionId, string inputName) =>
        string.Equals(definitionId, NodeGraphStageLibrary.TemplateStage, StringComparison.Ordinal)
        && string.Equals(inputName, RetiredTemplateVariantInput, StringComparison.Ordinal);

    public static NodeGraph CreateDefaultProjection(
        NodeType appliesTo,
        GraphNodeRegistry? registry = null)
    {
        registry ??= NodeGraphStageLibrary.CreateRegistry();

        // 有专门定义就按定义生成；没有的类型沿用标准流水线兜底。
        // 画布与求值都走同一条路径，新增类型不需要改这里。
        var definition = ElementGraphDefinitionCatalog.Find(appliesTo)
                         ?? ElementGraphDefinitionCatalog.CreateFallback(appliesTo);
        return ElementGraphProjectionBuilder.Build(definition, registry);
    }

    /// <summary>
    /// 补齐定义新增的端口：旧图文件按当时的端口落盘，不看这一步就永远接不上新阶段输入。
    /// 只加不改，已有端口的常量与连线保持原样；返回是否有改动。
    /// </summary>
    public static bool SyncDefinitionPorts(NodeGraph graph, GraphNodeRegistry? registry = null)
    {
        registry ??= NodeGraphStageLibrary.CreateRegistry();
        var changed = false;
        foreach (var node in graph.Nodes)
        {
            var definition = registry.Find(node.DefinitionId);
            if (definition is null)
            {
                continue;
            }

            foreach (var descriptor in definition.Inputs)
            {
                if (node.Inputs.Exists(port => string.Equals(port.Name, descriptor.Name, StringComparison.Ordinal)))
                {
                    continue;
                }

                node.Inputs.Add(new GraphPort(descriptor.Name, descriptor.Kind, descriptor.IsRequired, descriptor.IsMulti));
                changed = true;
            }

            foreach (var descriptor in definition.Outputs)
            {
                if (node.Outputs.Exists(port => string.Equals(port.Name, descriptor.Name, StringComparison.Ordinal)))
                {
                    continue;
                }

                node.Outputs.Add(new GraphPort(descriptor.Name, descriptor.Kind, descriptor.IsRequired, descriptor.IsMulti));
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>旧文件迁移入口：没有图文件时按类型批量生成默认投影，不落盘。</summary>
    public static IReadOnlyList<NodeGraph> CreateDefaultProjections(
        IEnumerable<NodeType> types,
        GraphNodeRegistry? registry = null)
    {
        registry ??= NodeGraphStageLibrary.CreateRegistry();
        var graphs = new List<NodeGraph>();
        foreach (var type in types)
        {
            graphs.Add(CreateDefaultProjection(type, registry));
        }

        return graphs;
    }
}
