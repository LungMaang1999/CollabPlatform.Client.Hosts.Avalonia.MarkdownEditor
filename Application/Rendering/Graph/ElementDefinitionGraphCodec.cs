using System;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Rendering.Graph;

/// <summary>
/// 定义段与结构图之间的编解码。语法、属性声明、内容模型仍只存在于
/// <c>.md.elements.xml</c>；图只承载结构声明这一份可渲染语义，
/// 因此往返只覆盖结构，其余字段原样保留。
/// </summary>
public static class ElementDefinitionGraphCodec
{
    /// <summary>「取结构」阶段接收结构声明的端口名。</summary>
    public const string StructurePort = "structure";

    /// <summary>元素图的 Id 前缀，与 <c>.md.graph.xml</c> 的既有约定一致。</summary>
    public const string ElementIdPrefix = "element:";

    /// <summary>把结构声明编译成图；空结构不写常量，保持“未声明 → 内置模板”的回退。</summary>
    public static NodeGraph Compile(UserBlockDefinition definition, GraphNodeRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        registry ??= NodeGraphStageLibrary.CreateRegistry();

        var stageDefinition = new ElementGraphDefinition
        {
            NodeType = NodeType.Custom,
            DisplayName = definition.Name,
            Stages = ElementStageCatalog.StandardPipeline,
        };

        var graph = ElementGraphProjectionBuilder.Build(stageDefinition, registry);
        graph.Id = ElementIdPrefix + definition.Id;

        var template = graph.FindNode(NodeGraphMigration.TemplateNodeId);
        var port = template?.FindInput(StructurePort);
        var text = WriteStructure(definition.Structure);
        if (port is not null && text.Length > 0)
        {
            port.Constant = new GraphConstant
            {
                Kind = GraphValueKind.PartTree,
                Raw = text,
            };
        }

        return graph;
    }

    /// <summary>从图还原结构声明；没有结构常量时返回空结构（即回退内置模板）。</summary>
    public static UserBlockStructure Decompile(NodeGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var template = graph.FindNode(NodeGraphMigration.TemplateNodeId);
        var constant = template?.FindInput(StructurePort)?.Constant;
        if (constant is null
            || constant.Kind != GraphValueKind.PartTree
            || string.IsNullOrEmpty(constant.Raw))
        {
            return UserBlockStructure.Empty;
        }

        return UserBlockStructureText.Parse(constant.Raw);
    }

    /// <summary>结构声明的规范文本；先净化，保证编译结果只含合法部件。</summary>
    public static string WriteStructure(UserBlockStructure? structure) =>
        UserBlockStructureText.Write(UserBlockStructureRules.Sanitize(structure).Structure);

    /// <summary>结构指纹，与渲染缓存使用的规范文本保持一致。</summary>
    public static string ComputeStructureFingerprint(UserBlockStructure? structure) =>
        UserBlockStructureRules.Describe(UserBlockStructureRules.Sanitize(structure).Structure);
}
