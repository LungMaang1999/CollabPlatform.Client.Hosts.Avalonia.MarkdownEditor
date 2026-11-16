using System;
using System.Collections.Generic;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

/// <summary>
/// 类型级模板变体。只改结构（部件顺序、包裹、标签、class），
/// 内容仍由同一批填充器产出。
/// </summary>
public enum ElementTemplateVariant
{
    /// <summary>标准结构；未选择或变体非法时一律回落到它。</summary>
    Default = 0,

    /// <summary>提示块：卡片式（header 头 + body 包裹）。</summary>
    Card = 1,

    /// <summary>自定义块：极简结构（不输出图标槽）。</summary>
    Minimal = 20,

    /// <summary>时间线：全部靠左。</summary>
    Left = 40,

    /// <summary>时间线：全部靠右。</summary>
    Right = 41,

    /// <summary>时间线：中轴居中。</summary>
    Center = 42,
}

public sealed record TemplateVariantOption(
    ElementTemplateVariant Value,
    string Label,
    string Description);

/// <summary>变体解析结果：<see cref="Effective"/> 是实际使用的变体。</summary>
public readonly record struct TemplateVariantResolution(
    ElementTemplateVariant Requested,
    ElementTemplateVariant Effective,
    bool IsFallback);

/// <summary>
/// 每个类型允许的模板变体白名单。未登记的类型只有 <see cref="ElementTemplateVariant.Default"/>。
/// 宿主页面从这里取下拉项，渲染器从这里做回落校验。
/// </summary>
public static class TemplateVariantCatalog
{
    private static readonly TemplateVariantOption DefaultOption = new(
        ElementTemplateVariant.Default,
        "标准",
        "沿用当前默认结构");

    private static readonly Dictionary<NodeType, IReadOnlyList<TemplateVariantOption>> Variants = new()
    {
        [NodeType.Quote] =
        [
            DefaultOption,
            new(ElementTemplateVariant.Card, "卡片", "标题改为 header，正文包一层 body"),
        ],
        [NodeType.Custom] =
        [
            DefaultOption,
            new(ElementTemplateVariant.Minimal, "极简", "不输出图标槽，仅保留正文"),
        ],
        [NodeType.Timeline] =
        [
            DefaultOption,
            new(ElementTemplateVariant.Left, "靠左", "单侧时间轴，连线位于内容左侧"),
            new(ElementTemplateVariant.Right, "靠右", "单侧时间轴，连线位于内容右侧"),
            new(ElementTemplateVariant.Center, "居中", "中轴包裹层，条目按侧别分布"),
        ],
    };

    public static IReadOnlyList<TemplateVariantOption> ForType(NodeType nodeType) =>
        Variants.TryGetValue(nodeType, out var options)
            ? options
            : [DefaultOption];

    /// <summary>
    /// kind 专属白名单。未登记时退化为类型级白名单（与旧行为完全一致）。
    /// 只能在渲染开始前调用；kind 为空等同撤销登记。
    /// </summary>
    public static void SetKindVariants(
        NodeType nodeType,
        string? kind,
        IReadOnlyList<TemplateVariantOption>? options)
    {
        var key = (nodeType, kind?.Trim() ?? string.Empty);
        if (options is null)
        {
            KindVariants.Remove(key);
            return;
        }

        KindVariants[key] = options;
    }

    public static IReadOnlyList<TemplateVariantOption> ForType(NodeType nodeType, string? kind)
    {
        if (!string.IsNullOrWhiteSpace(kind)
            && KindVariants.TryGetValue((nodeType, kind.Trim()), out var options))
        {
            return options;
        }

        return ForType(nodeType);
    }

    public static bool IsSupported(NodeType nodeType, ElementTemplateVariant variant) =>
        IsSupported(ForType(nodeType), variant);

    public static bool IsSupported(NodeType nodeType, string? kind, ElementTemplateVariant variant) =>
        IsSupported(ForType(nodeType, kind), variant);

    private static bool IsSupported(
        IReadOnlyList<TemplateVariantOption> options,
        ElementTemplateVariant variant)
    {
        foreach (var option in options)
        {
            if (option.Value == variant)
            {
                return true;
            }
        }

        return false;
    }

    public static TemplateVariantResolution Resolve(
        NodeType nodeType,
        ElementTemplateVariant requested) =>
        Resolve(nodeType, kind: null, requested);

    /// <summary>
    /// kind 为空或该 kind 未登记专属白名单时，结果与 <c>Resolve(nodeType, requested)</c> 完全一致。
    /// </summary>
    public static TemplateVariantResolution Resolve(
        NodeType nodeType,
        string? kind,
        ElementTemplateVariant requested)
    {
        if (IsSupported(nodeType, kind, requested))
        {
            return new TemplateVariantResolution(requested, requested, false);
        }

        return new TemplateVariantResolution(requested, ElementTemplateVariant.Default, true);
    }

    public static string GetLabel(NodeType nodeType, ElementTemplateVariant variant) =>
        GetLabel(ForType(nodeType), variant);

    public static string GetLabel(NodeType nodeType, string? kind, ElementTemplateVariant variant) =>
        GetLabel(ForType(nodeType, kind), variant);

    private static string GetLabel(
        IReadOnlyList<TemplateVariantOption> options,
        ElementTemplateVariant variant)
    {
        foreach (var option in options)
        {
            if (option.Value == variant)
            {
                return option.Label;
            }
        }

        return variant.ToString();
    }

    /// <summary>kind 键按 OrdinalIgnoreCase 归一，与 <see cref="StyleResolver"/> 的 kind 匹配一致。</summary>
    private static readonly Dictionary<(NodeType, string), IReadOnlyList<TemplateVariantOption>> KindVariants =
        new(ElementKind.KeyComparer);
}
