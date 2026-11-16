using System;
using System.Collections.Generic;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Rendering.Graph;

/// <summary>可编排阶段的种类。定义只声明种类与端口，执行器随定义一起给出。</summary>
public enum ElementStageKind
{
    Syntax,
    Fields,
    Template,
    Bindings,
    Style,
    Emit,
}

/// <summary>
/// 一个阶段定义：身份、端口、控件与执行器。
/// 节点编辑器只消费这份数据，不再认识任何具体阶段的字面量。
/// </summary>
public sealed class ElementStageDefinition
{
    public required ElementStageKind Kind { get; init; }

    /// <summary>定义 Id，写进 .graph.xml 的 Definition 字段。</summary>
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>
    /// 一句话说明该阶段实际做什么（读什么、写什么、有没有副作用）。
    /// 名字必须与实现一致；解释放在这里，不靠用户从端口名反推。
    /// </summary>
    public string Description { get; init; } = string.Empty;

    public string Category { get; init; } = "阶段";

    /// <summary>图文件里持久化的节点 Id；留空时按 Kind 生成（syntax/fields/...）。</summary>
    public string NodeId { get; init; } = string.Empty;

    public IReadOnlyList<GraphPortDescriptor> Inputs { get; init; } = [];

    public IReadOnlyList<GraphPortDescriptor> Outputs { get; init; } = [];

    public IReadOnlyList<GraphControlDescriptor> Controls { get; init; } = [];

    /// <summary>
    /// 上游输入缺失时仍执行。目前只给发射阶段用：HTML 由唯一渲染器按文档产出，
    /// 不依赖部件树；上游缺值时仍能给出降级输出，避免“旁路了某个阶段就整块全白”。
    /// 缺失诊断与 Incomplete 状态照常上报。
    /// </summary>
    public bool RunsWhenInputsMissing { get; init; }

    public GraphNodeExecutor? Executor { get; init; }

    public string ResolveNodeId() =>
        string.IsNullOrEmpty(NodeId) ? Kind.ToString().ToLowerInvariant() : NodeId;
}

/// <summary>
/// 元素类型的阶段定义：有哪些阶段、按什么顺序串起来。
/// 新增类型只加一条定义，画布代码不动；定义可以从任何来源派生（声明树、继承、槽位约束）。
/// </summary>
public sealed class ElementGraphDefinition
{
    public required NodeType NodeType { get; init; }

    /// <summary>画布里的类型显示名；不再由画布按枚举写死。</summary>
    public required string DisplayName { get; init; }

    public IReadOnlyList<ElementStageDefinition> Stages { get; init; } = [];

    public ElementStageDefinition? FindStage(ElementStageKind kind)
    {
        foreach (var stage in Stages)
        {
            if (stage.Kind == kind)
            {
                return stage;
            }
        }

        return null;
    }
}

/// <summary>
/// 标准阶段目录：六段流水线的端口与执行器声明。
/// 这是定义数据的一部分，节点编辑器只按 <see cref="ElementGraphDefinition"/> 消费。
/// </summary>
public static class ElementStageCatalog
{
    public const string SyntaxStage = "stage.syntax";
    public const string FieldsStage = "stage.fields";
    public const string TemplateStage = "stage.template";
    public const string BindingsStage = "stage.bindings";
    public const string StyleStage = "stage.style";
    public const string EmitStage = "stage.emit";

    public static IReadOnlyList<ElementStageDefinition> StandardPipeline { get; } =
    [
        new()
        {
            Kind = ElementStageKind.Syntax,
            Id = SyntaxStage,
            DisplayName = "取节点",
            Description = "按类型遍历文档 AST，收集该类型的全部节点并输出 nodes；不创建实例、不改正文。",
            Category = "输入",
            Outputs = [new GraphPortDescriptor("nodes", GraphValueKind.NodeSeq)],
            Executor = NodeGraphStageLibrary.CollectInstances,
        },
        new()
        {
            Kind = ElementStageKind.Fields,
            Id = FieldsStage,
            DisplayName = "抽节点值",
            Description = "逐节点取 text / raw / level 与 attr:* 扁平值并输出 fields；不做类型定义的属性映射。",
            Category = "解析",
            Inputs = [new GraphPortDescriptor("nodes", GraphValueKind.NodeSeq)],
            Outputs = [new GraphPortDescriptor("fields", GraphValueKind.Fields)],
            Executor = NodeGraphStageLibrary.ExtractFields,
        },
        new()
        {
            Kind = ElementStageKind.Template,
            Id = TemplateStage,
            DisplayName = "取结构",
            Description = "上游接 structure 时原样转发；未接时从渲染器取该节点的模板部件树快照，不组装新结构。",
            Category = "结构",
            Inputs =
            [
                new GraphPortDescriptor("nodes", GraphValueKind.NodeSeq),
                new GraphPortDescriptor("fields", GraphValueKind.Fields),
                new GraphPortDescriptor("content", GraphValueKind.Html, IsRequired: false),
                new GraphPortDescriptor("structure", GraphValueKind.PartTree, IsRequired: false),
            ],
            Outputs = [new GraphPortDescriptor("parts", GraphValueKind.PartTree)],
            Executor = NodeGraphStageLibrary.BuildParts,
        },
        new()
        {
            Kind = ElementStageKind.Bindings,
            Id = BindingsStage,
            DisplayName = "绑属性",
            Description = "按类型定义的属性绑定，把实例值写进已有部件的类名、属性或 CSS 变量；不增删部件。",
            Category = "呈现",
            Inputs =
            [
                new GraphPortDescriptor("parts", GraphValueKind.PartTree),
                new GraphPortDescriptor("values", GraphValueKind.StyleMap, IsRequired: false, IsMulti: true),
            ],
            Outputs = [new GraphPortDescriptor("parts", GraphValueKind.PartTree)],
            Executor = NodeGraphStageLibrary.ApplyBindings,
        },
        new()
        {
            Kind = ElementStageKind.Style,
            Id = StyleStage,
            DisplayName = "解样式",
            Description = "合并渲染器已算好的内联样式与绑定阶段产生的变量，输出 styled；不重新解析样式链。",
            Category = "呈现",
            Inputs =
            [
                new GraphPortDescriptor("parts", GraphValueKind.PartTree),
                new GraphPortDescriptor("nodes", GraphValueKind.NodeSeq),
                new GraphPortDescriptor("style", GraphValueKind.StyleMap, IsRequired: false),
            ],
            Outputs = [new GraphPortDescriptor("styled", GraphValueKind.StyledPartTree)],
            Executor = NodeGraphStageLibrary.ResolveStyle,
        },
        new()
        {
            Kind = ElementStageKind.Emit,
            Id = EmitStage,
            DisplayName = "出 HTML",
            Description = "调用唯一渲染器按文档导出该块 HTML；有渲染类阶段被旁路时按旁路降级，不伪造内容。",
            Category = "输出",
            Inputs = [new GraphPortDescriptor("structure", GraphValueKind.StyledPartTree)],
            Outputs = [new GraphPortDescriptor("html", GraphValueKind.Html)],
            Executor = NodeGraphStageLibrary.EmitHtml,
            RunsWhenInputsMissing = true,
        },
    ];
}

/// <summary>
/// 默认元素定义目录：当前可编排的类型各自一条阶段定义。
/// 新增类型时只改这里（或换一个提供者），画布与求值代码都不需要改。
/// </summary>
public static class ElementGraphDefinitionCatalog
{
    public static IReadOnlyList<ElementGraphDefinition> Default { get; } =
    [
        new()
        {
            NodeType = NodeType.Quote,
            DisplayName = "提示块",
            Stages = ElementStageCatalog.StandardPipeline,
        },
        new()
        {
            NodeType = NodeType.Custom,
            DisplayName = "自定义块",
            Stages = ElementStageCatalog.StandardPipeline,
        },
        new()
        {
            NodeType = NodeType.Table,
            DisplayName = "表格",
            Stages = ElementStageCatalog.StandardPipeline,
        },
        new()
        {
            NodeType = NodeType.CodeBlock,
            DisplayName = "代码块",
            Stages = ElementStageCatalog.StandardPipeline,
        },
        new()
        {
            NodeType = NodeType.Timeline,
            DisplayName = "时间线",
            Stages = ElementStageCatalog.StandardPipeline,
        },
    ];

    public static ElementGraphDefinition? Find(NodeType nodeType)
    {
        foreach (var definition in Default)
        {
            if (definition.NodeType == nodeType)
            {
                return definition;
            }
        }

        return null;
    }

    /// <summary>
    /// 没有专门定义的类型：沿用标准流水线，保证旧图文件与临时类型仍可求值。
    /// 这是兜底而不是画布知识——画布看到的仍然是“定义”。
    /// </summary>
    public static ElementGraphDefinition CreateFallback(NodeType nodeType) =>
        new()
        {
            NodeType = nodeType,
            DisplayName = nodeType.ToString(),
            Stages = ElementStageCatalog.StandardPipeline,
        };
}
