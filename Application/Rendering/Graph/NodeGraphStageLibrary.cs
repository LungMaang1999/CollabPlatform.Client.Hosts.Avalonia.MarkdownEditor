using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Html;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Rendering.Graph;

/// <summary>
/// 阶段执行器库。阶段有哪些、端口怎么连由 <see cref="ElementGraphDefinition"/> 声明；
/// 这里只提供六段各自的计算实现。
/// </summary>
public static class NodeGraphStageLibrary
{
    public const string SyntaxStage = ElementStageCatalog.SyntaxStage;
    public const string FieldsStage = ElementStageCatalog.FieldsStage;
    public const string TemplateStage = ElementStageCatalog.TemplateStage;
    public const string BindingsStage = ElementStageCatalog.BindingsStage;
    public const string StyleStage = ElementStageCatalog.StyleStage;
    public const string EmitStage = ElementStageCatalog.EmitStage;

    /// <summary>兼容入口：注册表从默认元素定义派生，不再在这里逐条写死阶段。</summary>
    public static GraphNodeRegistry CreateRegistry() =>
        DeclaredNodeGraphProvider.Default.CreateRegistry();

    /// <summary>
    /// 文档作用域注册表：在原语与库节点之上追加文件声明的复合阶段。
    /// 不写入共享单例，避免声明跨文档泄漏；无声明时直接复用默认注册表。
    /// </summary>
    public static GraphNodeRegistry CreateRegistry(IReadOnlyList<CompositeStageDefinition>? stages)
    {
        if (stages is null || stages.Count == 0)
        {
            return CreateRegistry();
        }

        var registry = new GraphNodeRegistry();
        foreach (var definition in CreateRegistry().Definitions)
        {
            registry.Register(definition);
        }

        foreach (var stage in stages)
        {
            // 冲突声明已在装载期被丢弃；这里再挡一次，绝不覆盖原语。
            if (!registry.Contains(stage.Id))
            {
                registry.Register(stage.ToNodeDefinition());
            }
        }

        return registry;
    }

    internal static IReadOnlyDictionary<string, GraphValue> CollectInstances(GraphEvaluationContext context)
    {
        if (context.RootNode is null)
        {
            context.Report("graph.missing_root", "求值缺少文档根节点。");
            return new Dictionary<string, GraphValue>(StringComparer.Ordinal)
            {
                ["nodes"] = GraphValue.FromNodes([]),
            };
        }

        var nodes = new List<MarkdownNode>();
        Walk(context.RootNode, context.AppliesTo, nodes);
        return new Dictionary<string, GraphValue>(StringComparer.Ordinal)
        {
            ["nodes"] = GraphValue.FromNodes(nodes),
        };
    }

    internal static IReadOnlyDictionary<string, GraphValue> ExtractFields(GraphEvaluationContext context)
    {
        var nodes = context.InputValue("nodes");
        var sets = new List<IReadOnlyDictionary<string, string>>();
        if (!nodes.IsNone)
        {
            foreach (var node in nodes.AsNodes())
            {
                var fields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["text"] = node.Text ?? string.Empty,
                    ["raw"] = node.RawMarkdown ?? string.Empty,
                    ["level"] = node.Level?.ToString() ?? string.Empty,
                };

                foreach (var pair in node.Attributes)
                {
                    fields["attr:" + pair.Key] = pair.Value ?? string.Empty;
                }

                sets.Add(fields);
            }
        }

        return new Dictionary<string, GraphValue>(StringComparer.Ordinal)
        {
            ["fields"] = GraphValue.FromFields(sets),
        };
    }

    internal static IReadOnlyDictionary<string, GraphValue> BuildParts(GraphEvaluationContext context)
    {
        // 上游给了结构节点产出的部件树时以图为准；没接才取渲染器快照。
        var custom = context.InputValue("structure");
        if (!custom.IsNone && custom.AsParts() is { Count: > 0 } graphParts)
        {
            return new Dictionary<string, GraphValue>(StringComparer.Ordinal)
            {
                ["parts"] = GraphValue.FromParts(graphParts),
            };
        }

        // 部件树直接取自渲染器：同一份模板、同一套变体/kind 解析与图标快照。
        // 检查器不再维护第二份模板表，48 种内置类型也不会退化成“通用骨架”。
        var node = ResolveRenderNode(context);
        var parts = context.Document is null || node is null
            ? []
            : BuildRendererParts(context.Document, node);

        return new Dictionary<string, GraphValue>(StringComparer.Ordinal)
        {
            ["parts"] = GraphValue.FromParts(parts),
        };
    }

    internal static IReadOnlyList<GraphPart> BuildRendererParts(MarkdownDocument document, MarkdownNode node)
    {
        var renderer = (HtmlRenderer)new HtmlRendererFactory().CreatePreview(document);
        var snapshot = renderer.DescribeTemplate(document, node);
        return snapshot is null ? [] : [ToGraphPart(snapshot.Root)];
    }

    /// <summary>
    /// 求值根不一定是被渲染的实例：整图求值传文档根，画布求值传选中的实例。
    /// 这里用同一规则选出真正要渲染的节点，保证部件、绑定、发射三段永远看同一个节点；
    /// 文档里没有该类型实例时返回 null，绝不拿文档根冒充实例。
    /// </summary>
    private static MarkdownNode? ResolveRenderNode(GraphEvaluationContext context)
    {
        var root = context.RootNode;
        if (root is null)
        {
            return null;
        }

        if (root.Type == context.AppliesTo)
        {
            return root;
        }

        var nodes = new List<MarkdownNode>();
        Walk(root, context.AppliesTo, nodes);
        return nodes.Count > 0 ? nodes[0] : null;
    }

    internal static GraphPart ToGraphPart(ElementPartSnapshot part)
    {
        var slot = part.Name switch
        {
            PartTokens.Icon => GraphPartSlot.Icon,
            PartTokens.TitleText => GraphPartSlot.TitleText,
            PartTokens.Body or PartTokens.Content => GraphPartSlot.Children,
            _ => GraphPartSlot.None,
        };

        return new GraphPart(
            part.Name,
            part.Tag,
            part.CssClass ?? string.Empty,
            part.Children.Select(ToGraphPart).ToList(),
            Role: part.Role,
            Slot: slot,
            SlotHtml: part.SlotHtml,
            StyleCss: part.InlineCss);
    }

    /// <summary>
    /// 绑定阶段：只把“实例值”写到已有部件的类名/属性/内联变量上，绝不增删部件。
    /// 声明与校验规则完全复用现有类型级定义。
    /// </summary>
    internal static IReadOnlyDictionary<string, GraphValue> ApplyBindings(GraphEvaluationContext context)
    {
        var partsValue = context.InputValue("parts");
        if (partsValue.IsNone)
        {
            return new Dictionary<string, GraphValue>(StringComparer.Ordinal)
            {
                ["parts"] = GraphValue.FromParts([]),
            };
        }

        var parts = partsValue.AsParts();
        var node = ResolveRenderNode(context);
        var definition = context.Document is null || node is null
            ? null
            : HtmlRenderer.ResolveTypeDefinition(
                context.Document.StyleSheet,
                context.AppliesTo,
                ElementKind.Resolve(node));
        if (definition is null || definition.Attributes.Count == 0)
        {
            return new Dictionary<string, GraphValue>(StringComparer.Ordinal)
            {
                ["parts"] = GraphValue.FromParts(parts),
            };
        }

        foreach (var declaration in definition.Attributes)
        {
            var binding = declaration.Binding;
            if (binding is null || binding.IsEmpty)
            {
                continue;
            }

            if (!ElementRenderBindingRules.Validate(context.AppliesTo, declaration, binding).IsValid)
            {
                continue;
            }

            var targetPart = string.IsNullOrWhiteSpace(binding.Part)
                ? PartTokens.Root
                : binding.Part.Trim();
            var raw = context.InstanceText(declaration.Key) ?? declaration.DefaultValue;
            var resolved = ElementRenderBindingRules.ResolveValue(binding.Target, binding.ValueMap, raw);
            if (resolved is null)
            {
                continue;
            }

            parts = BindPart(parts, targetPart, binding.Target, binding.Name, resolved);
        }

        return new Dictionary<string, GraphValue>(StringComparer.Ordinal)
        {
            ["parts"] = GraphValue.FromParts(parts),
        };
    }

    private static IReadOnlyList<GraphPart> BindPart(
        IReadOnlyList<GraphPart> parts,
        string partName,
        ElementAttributeBindingTarget target,
        string name,
        string value)
    {
        var result = new List<GraphPart>(parts.Count);
        foreach (var part in parts)
        {
            var updated = string.Equals(part.Name, partName, StringComparison.Ordinal)
                ? ApplyToPart(part, target, name, value)
                : part;

            if (part.Children.Count > 0)
            {
                updated = updated with
                {
                    Children = BindPart(part.Children, partName, target, name, value),
                };
            }

            result.Add(updated);
        }

        return result;
    }

    private static GraphPart ApplyToPart(
        GraphPart part,
        ElementAttributeBindingTarget target,
        string name,
        string value) => target switch
    {
        ElementAttributeBindingTarget.ClassToken => part with
        {
            CssClass = string.IsNullOrEmpty(part.CssClass) ? value : part.CssClass + " " + value,
        },
        ElementAttributeBindingTarget.CssVar => part with
        {
            BindingCss = string.IsNullOrEmpty(part.BindingCss)
                ? name + ": " + value + ";"
                : part.BindingCss + " " + name + ": " + value + ";",
        },
        ElementAttributeBindingTarget.DataAttribute => part with
        {
            Attributes = (part.Attributes ?? []).Append(new GraphAttribute(name, value)).ToList(),
        },
        _ => part,
    };

    /// <summary>
    /// 样式阶段：根部件的内联声明已由渲染器算好（默认 → 父链 → 类型/kind → 命名 → 实例），
    /// 这里只把绑定阶段产生的变量追加进去，不再自行解析一遍样式。
    /// </summary>
    internal static IReadOnlyDictionary<string, GraphValue> ResolveStyle(GraphEvaluationContext context)
    {
        var partsValue = context.InputValue("parts");
        if (partsValue.IsNone)
        {
            return new Dictionary<string, GraphValue>(StringComparer.Ordinal)
            {
                ["styled"] = GraphValue.FromParts([], styled: true),
            };
        }

        var styled = partsValue.AsParts().Select(StylePart).ToList();

        return new Dictionary<string, GraphValue>(StringComparer.Ordinal)
        {
            ["styled"] = GraphValue.FromParts(styled, styled: true),
        };
    }

    private static GraphPart StylePart(GraphPart part)
    {
        var css = JoinCss(part.StyleCss, part.BindingCss);

        return part with
        {
            StyleCss = string.IsNullOrWhiteSpace(css) ? null : css,
            Children = part.Children.Select(StylePart).ToList(),
        };
    }

    private static string? JoinCss(params string?[] fragments)
    {
        StringBuilder? builder = null;
        foreach (var fragment in fragments)
        {
            if (string.IsNullOrWhiteSpace(fragment))
            {
                continue;
            }

            builder ??= new StringBuilder();
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(fragment.Trim());
        }

        return builder?.ToString();
    }

    /// <summary>
    /// 发射阶段：直接调用唯一渲染器的单块渲染。
    /// 画布检查器、编辑区预览、博客发布因此共用同一样式/模板/图标/结构/签名实现，
    /// 这里不再有第二个 HTML 发射器。
    /// 有阶段被旁路时把旁路映射成渲染器的降级开关：输出按旁路真实变化（实验态），
    /// 无旁路时开关全关，输出与整篇渲染逐字节一致。
    /// </summary>
    internal static IReadOnlyDictionary<string, GraphValue> EmitHtml(GraphEvaluationContext context)
    {
        var node = ResolveRenderNode(context);
        var structureMissing =
            !context.Inputs.TryGetValue("structure", out var structure) || structure.IsNone;

        // 只有“渲染类阶段”被旁路时才允许在上游缺值的情况下出降级输出；
        // 取节点/抽节点值被旁路意味着没有数据，保持“链路没接通就没输出”，不伪造内容。
        var renderingBypass =
            context.BypassedStageIds.Contains(ElementStageCatalog.TemplateStage)
            || context.BypassedStageIds.Contains(ElementStageCatalog.BindingsStage)
            || context.BypassedStageIds.Contains(ElementStageCatalog.StyleStage);

        if (structureMissing && !renderingBypass)
        {
            // 上游真的断了（不是渲染类旁路）：不产出 html，让整图显式报“没有根输出”，
            // 不静默塞空串把断链藏起来。
            return new Dictionary<string, GraphValue>(StringComparer.Ordinal)
            {
                ["html"] = GraphValue.None,
            };
        }

        if (context.Document is null || node is null)
        {
            return new Dictionary<string, GraphValue>(StringComparer.Ordinal)
            {
                ["html"] = GraphValue.FromHtml(string.Empty),
            };
        }

        var options = new RenderFragmentOptions(
            SuppressTemplate: context.BypassedStageIds.Contains(ElementStageCatalog.TemplateStage),
            SuppressBindings: context.BypassedStageIds.Contains(ElementStageCatalog.BindingsStage),
            SuppressInlineStyle: context.BypassedStageIds.Contains(ElementStageCatalog.StyleStage));

        // 结构来自图（结构节点建出来的树）时交给渲染器换根；快照树不换，输出保持原样。
        if (!structureMissing && GraphPartStructure.HasCustomParts(structure.AsParts()))
        {
            options = options with { Structure = GraphPartStructure.ToStructureRoot(structure.AsParts()) };
        }

        var renderer = (HtmlRenderer)new HtmlRendererFactory().CreatePreview(context.Document);
        var html = renderer.RenderFragment(context.Document, node, options);

        return new Dictionary<string, GraphValue>(StringComparer.Ordinal)
        {
            ["html"] = GraphValue.FromHtml(html),
        };
    }

    private static void Walk(MarkdownNode node, NodeType appliesTo, ICollection<MarkdownNode> results)
    {
        if (node.Type == appliesTo)
        {
            results.Add(node);
        }

        foreach (var child in node.Children)
        {
            Walk(child, appliesTo, results);
        }
    }
}
