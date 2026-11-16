using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Rendering.Graph;

/// <summary>内置类型默认投影无法完整表达、需要人工校正的一项。</summary>
public sealed record ElementManualCorrection(NodeType NodeType, string Reason, string Expected);

/// <summary>
/// 一个内置类型的默认投影：可序列化的阶段链图 + 渲染器结构快照 + 稳定结构签名。
/// 只读数据，不参与渲染路径，也不写回任何文件。
/// </summary>
public sealed record ElementDefaultProjection(
    NodeType NodeType,
    NodeGraph Graph,
    ElementTemplateSnapshot? Template,
    string StructureSignature,
    ElementManualCorrection? Correction);

/// <summary>
/// 内置类型默认图派生。结构取自 <c>DescribeTemplate</c> 快照，图取自既有阶段链投影；
/// 两者都是只读事实源，不新增第二份模板表。
/// </summary>
public static class ElementDefaultGraphProjection
{
    /// <summary>
    /// 无法从单张快照等价还原的类型：快照只反映样张实例，结构随层级/变体/列数变化。
    /// 这里的 <see cref="ElementManualCorrection.Expected"/> 是 R1 之后要补的参数形态。
    /// </summary>
    public static IReadOnlyList<ElementManualCorrection> ManualCorrections { get; } =
    [
        new(NodeType.Document,
            "文档根不是元素模板，渲染器不提供部件快照。",
            "不生成结构部件；整篇输出由文档级渲染决定。"),
        new(NodeType.Heading,
            "根标签随层级在 h1..h6 间变化，快照只反映样张层级。",
            "tag-from=level（模式 h{n}），并声明 level 为 1..6 的源码派生属性。"),
        new(NodeType.Timeline,
            "Default/Left/Right/Center 变体改变根类名与 rail/dot/tail 部件构成。",
            "按 TemplateVariant 各生成一张图，四张图共享 fields 段。"),
        new(NodeType.ImageMatrix,
            "列数由内容推导，快照只反映样张列数。",
            "columns-from 内容字段，结构按列数展开。"),
        new(NodeType.Radar,
            "轴数与层级由内容推导，快照只反映样张。",
            "axes-from / levels-from 内容字段，结构按轴数展开。"),
        new(NodeType.Quote,
            "admonition 类别改变根类名与 icon/title 槽，快照只反映样张类别。",
            "按 kind 维度各生成一张图，kind 级定义共享结构。"),
        new(NodeType.PoemBlock,
            "诗体部件的 title/author/dynasty/form/stanza/line/source/note 由诗体规则生成，不是直接标签映射。",
            "按诗体规则生成固定部件树，槽位内容由源字段填充。"),
        new(NodeType.Custom,
            "没有内置模板，快照来自样张自定义块，不代表任何具体定义。",
            "结构必须由定义段的 <structure> 声明提供，编译为 structure 端口常量。"),
    ];

    /// <summary>阶段链默认图；与画布和求值共用同一条投影路径。</summary>
    public static NodeGraph BuildGraph(NodeType type, GraphNodeRegistry? registry = null) =>
        NodeGraphMigration.CreateDefaultProjection(type, registry);

    /// <summary>有一个该类型的实例时派生完整投影；没有实例时返回 null（不伪造样张）。</summary>
    public static ElementDefaultProjection? TryBuild(
        MarkdownDocument document,
        MarkdownNode node,
        GraphNodeRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(node);

        var renderer = new Infrastructure.Rendering.Html.HtmlRendererFactory()
            .CreatePreview(document) as Infrastructure.Rendering.Html.HtmlRenderer;
        var snapshot = renderer?.DescribeTemplate(document, node);
        var parts = snapshot is null
            ? []
            : NodeGraphStageLibrary.BuildRendererParts(document, node);

        return new ElementDefaultProjection(
            node.Type,
            BuildGraph(node.Type, registry),
            snapshot,
            DescribeParts(parts),
            FindCorrection(node.Type) ?? CreateContainerNote(node.Type, snapshot));
    }

    /// <summary>渲染器不提供独立模板的容器/子部件类型，只能随宿主渲染，没有可编辑结构。</summary>
    private static ElementManualCorrection? CreateContainerNote(NodeType type, ElementTemplateSnapshot? snapshot) =>
        snapshot is null
            ? new ElementManualCorrection(
                type,
                "渲染器不为该类型提供独立模板（容器或子部件）。",
                "不生成独立结构图，随宿主类型渲染。")
            : null;

    /// <summary>文档里每种出现过的类型各取首个实例派生一张默认图。</summary>
    public static IReadOnlyList<ElementDefaultProjection> BuildAll(
        MarkdownDocument document,
        GraphNodeRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        registry ??= NodeGraphStageLibrary.CreateRegistry();

        var result = new List<ElementDefaultProjection>();
        var seen = new HashSet<NodeType>();
        foreach (var node in Walk(document.Root))
        {
            if (!seen.Add(node.Type))
            {
                continue;
            }

            var projection = TryBuild(document, node, registry);
            if (projection is not null)
            {
                result.Add(projection);
            }
        }

        return result;
    }

    /// <summary>部件树的规范签名：顺序、标签、类名、槽位都参与，用于对照与快照。</summary>
    public static string DescribeParts(IReadOnlyList<GraphPart>? parts)
    {
        if (parts is null || parts.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(160);
        foreach (var part in parts)
        {
            AppendPart(builder, part, depth: 0);
        }

        return builder.ToString();
    }

    private static void AppendPart(StringBuilder builder, GraphPart part, int depth)
    {
        if (builder.Length > 0)
        {
            builder.Append('|');
        }

        builder.Append(depth)
            .Append(':').Append(part.Name)
            .Append(':').Append(part.Tag ?? "-")
            .Append(':').Append(string.IsNullOrEmpty(part.CssClass) ? "-" : part.CssClass);

        foreach (var child in part.Children)
        {
            AppendPart(builder, child, depth + 1);
        }
    }

    private static ElementManualCorrection? FindCorrection(NodeType type)
    {
        foreach (var correction in ManualCorrections)
        {
            if (correction.NodeType == type)
            {
                return correction;
            }
        }

        return null;
    }

    private static IEnumerable<MarkdownNode> Walk(MarkdownNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }
}
