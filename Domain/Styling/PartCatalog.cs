using System;
using System.Collections.Generic;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

/// <summary>部件在元素结构里的角色。只用于分类与展示，不参与寻址。</summary>
public enum ElementPartRole
{
    Root,
    Header,
    Title,
    Icon,
    Content,
    Meta,
    Rail,
    Dot,
    Tail,
    Wrapper,
}

/// <summary>
/// 一个可独立寻址的部件：<c>{NodeType}/{Part}</c> 全局唯一，
/// <c>Part</c> 在单个块内唯一，映射到稳定的 <c>data-part</c> 取值。
/// </summary>
public sealed record PartDescriptor(
    NodeType NodeType,
    string Part,
    string CssClass,
    ElementPartRole Role,
    bool SupportsBinding,
    string Description);

/// <summary>
/// 部件目录：渲染器、模板、客户端 key、声明绑定共用的唯一权威源。
/// 新增部件必须先登记，禁止在别处写 part 字面量。
/// </summary>
public static class PartCatalog
{
    public static IReadOnlyList<PartDescriptor> All { get; } =
    [
        // 提示块
        new(NodeType.Quote, PartTokens.Root, "admonition", ElementPartRole.Root, true, "提示块容器"),
        new(NodeType.Quote, PartTokens.Title, "admonition-title", ElementPartRole.Header, true, "提示块头部"),
        new(NodeType.Quote, PartTokens.Icon, "admonition-icon", ElementPartRole.Icon, true, "提示块图标"),
        new(NodeType.Quote, PartTokens.TitleText, "admonition-title-text", ElementPartRole.Title, false, "提示块标题文字"),
        new(NodeType.Quote, PartTokens.Content, "admonition-body", ElementPartRole.Content, true, "提示块正文"),

        // 自定义块
        new(NodeType.Custom, PartTokens.Root, "custom-block", ElementPartRole.Root, true, "自定义块容器"),
        new(NodeType.Custom, PartTokens.Icon, "custom-block-icon", ElementPartRole.Icon, true, "自定义块图标"),
        new(NodeType.Custom, PartTokens.Body, "custom-block-body", ElementPartRole.Content, true, "自定义块正文"),

        // 时间线
        new(NodeType.Timeline, PartTokens.Root, "md-timeline", ElementPartRole.Root, true, "时间线容器"),
        new(NodeType.Timeline, PartTokens.Axis, "md-timeline-axis", ElementPartRole.Wrapper, true, "中轴包裹层"),
        new(NodeType.TimelineItem, PartTokens.Root, "md-tl-item", ElementPartRole.Root, true, "时间线条目"),
        new(NodeType.TimelineItem, PartTokens.Rail, "md-tl-rail", ElementPartRole.Rail, true, "条目连线"),
        new(NodeType.TimelineItem, PartTokens.Dot, "md-tl-dot", ElementPartRole.Dot, true, "条目节点"),
        new(NodeType.TimelineItem, PartTokens.Content, "md-tl-content", ElementPartRole.Content, true, "条目内容容器"),
        new(NodeType.TimelineItem, PartTokens.Meta, "md-tl-meta", ElementPartRole.Meta, true, "条目元信息"),
        new(NodeType.TimelineItem, PartTokens.Time, "md-tl-time", ElementPartRole.Meta, true, "条目时间"),
        new(NodeType.TimelineItem, PartTokens.Tags, "md-tl-tags", ElementPartRole.Meta, true, "条目标签"),
        new(NodeType.TimelineItem, PartTokens.Body, "md-tl-body", ElementPartRole.Content, true, "条目正文"),
    ];

    public static PartDescriptor? Find(NodeType nodeType, string part)
    {
        foreach (var descriptor in All)
        {
            if (descriptor.NodeType == nodeType
                && string.Equals(descriptor.Part, part, StringComparison.Ordinal))
            {
                return descriptor;
            }
        }

        return null;
    }

    public static IReadOnlyList<PartDescriptor> ForType(NodeType nodeType)
    {
        var result = new List<PartDescriptor>();
        foreach (var descriptor in All)
        {
            if (descriptor.NodeType == nodeType)
            {
                result.Add(descriptor);
            }
        }

        return result;
    }

    public static bool IsBindable(NodeType nodeType, string? part)
    {
        if (string.IsNullOrEmpty(part))
        {
            return true;
        }

        var descriptor = Find(nodeType, part);
        return descriptor is not null && descriptor.SupportsBinding;
    }
}

/// <summary>部件 token 常量。渲染器、模板与测试只允许引用这里。</summary>
public static class PartTokens
{
    public const string Root = "root";
    public const string Title = "title";
    public const string TitleText = "title-text";
    public const string Icon = "icon";
    public const string Content = "content";
    public const string Meta = "meta";
    public const string Time = "time";
    public const string Tags = "tags";
    public const string Body = "body";
    /// <summary>结构尾部槽位：可寻址的结构容器，内容只能走 <c>text</c>/<c>raw</c>/子部件。</summary>
    public const string Footer = "footer";
    /// <summary>徽章槽位：可寻址的结构容器，常用于短文本标记。</summary>
    public const string Badge = "badge";
    public const string Rail = "rail";
    public const string Dot = "dot";
    public const string Axis = "axis";

    // 以下 token 只用于内置类型描述，暂未进入 PartCatalog；
    // 待描述派生部件目录（缺口 B）落地后统一定义“可寻址与否”。
    public const string Wrapper = "wrapper";
    public const string Header = "header";
    public const string Control = "control";
    public const string Anchor = "anchor";
    public const string Index = "index";
    public const string Row = "row";
    public const string Tag = "tag";
    public const string Author = "author";
    public const string Dynasty = "dynasty";
    public const string Form = "form";
    public const string Stanza = "stanza";
    public const string Line = "line";
    public const string Source = "source";
    public const string Note = "note";
    public const string Chart = "chart";
    /// <summary>脚注回链图标：只用于内置 Footnote 描述，不进 PartCatalog。</summary>
    public const string BackrefIcon = "backref-icon";
}
