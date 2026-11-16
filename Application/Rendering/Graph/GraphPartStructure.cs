using System.Collections.Generic;
using System.Linq;
using System.Net;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Rendering.Graph;

/// <summary>
/// 部件树到结构描述的转换。画布发射与渲染时换根共用同一份映射，
/// 避免两处对标签、槽内容与角色做不同解释。
/// </summary>
public static class GraphPartStructure
{
    /// <summary>任一部件来自结构节点即视为图结构，纯快照树保持原渲染路径。</summary>
    public static bool HasCustomParts(IReadOnlyList<GraphPart> parts) =>
        parts.Any(part => part.IsCustom || HasCustomParts(part.Children));

    public static ElementStructureNode ToStructureRoot(IReadOnlyList<GraphPart> parts)
    {
        if (parts.Count == 1)
        {
            // 自定义元素只有真正的根部件才写得上节点身份属性与强调色。
            return ToStructureNode(parts[0], ElementPartRole.Root);
        }

        // 多个顶层部件需要一个根：用真实元素承载，节点身份属性才不会丢。
        var root = new ElementStructureNode
        {
            Part = "root",
            Tag = "div",
            Role = ElementPartRole.Root,
        };
        foreach (var part in parts)
        {
            root.Children.Add(ToStructureNode(part));
        }

        return root;
    }

    public static ElementStructureNode ToStructureNode(GraphPart part, ElementPartRole? forcedRole = null)
    {
        var tagTemplate = string.IsNullOrWhiteSpace(part.TagTemplate) ? null : part.TagTemplate;
        var node = new ElementStructureNode
        {
            Part = part.Name,
            // 插值原文单独保留，Tag 只放静态标签：渲染期求值不出来时按虚拟部件处理。
            Tag = tagTemplate is null && !string.IsNullOrWhiteSpace(part.Tag) ? part.Tag : null,
            TagTemplate = tagTemplate,
            Class = string.IsNullOrWhiteSpace(part.CssClass) ? null : part.CssClass,
            RawClass = part.RawClass,
            Addressable = part.Addressable,
            When = part.When,
            WhenSource = part.WhenSource,
            Repeat = part.Repeat,
            RepeatMax = part.RepeatMax,
            Generator = part.Generator,
            GeneratorParams = part.GeneratorParams,
            Ref = part.Ref,
            Raw = part.Raw,
            Text = !string.IsNullOrWhiteSpace(part.Text)
                && UserBlockStructureRules.TryParseFieldTemplate(part.Text, out _)
                    ? part.Text.Trim()
                    : null,
            Href = NormalizeBinding(part.Href),
            Src = NormalizeBinding(part.Src),
            Role = forcedRole ?? part.Role,
            SlotHtml = part.SlotHtml
                ?? (string.IsNullOrEmpty(part.SlotText) ? null : WebUtility.HtmlEncode(part.SlotText)),
            StyleCss = part.StyleCss,
        };

        foreach (var child in part.Children)
        {
            node.Children.Add(ToStructureNode(child));
        }

        return node;
    }

    /// <summary>受控 URL 绑定只接受纯洞模板；不是纯洞就按未声明处理，不把原文写进属性。</summary>
    private static string? NormalizeBinding(string? binding) =>
        !string.IsNullOrWhiteSpace(binding)
            && UserBlockStructureRules.TryParseFieldTemplate(binding, out _)
                ? binding.Trim()
                : null;
}
