using System;
using System.Collections.Generic;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

public sealed class StyleDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string? ParentStyleId { get; set; }
    public NodeType? AppliesTo { get; set; }

    /// <summary>
    /// 可选的 kind 限定：仅当 <see cref="AppliesTo"/> 非空时生效，按节点的
    /// <c>custom-kind</c> 匹配（自定义元素）。为空表示该类型下所有 kind 共用。
    /// 带 kind 的定义比同类型不带 kind 的定义更具体，应用时覆盖后者。
    /// </summary>
    public string? AppliesToKind { get; set; }

    public NodeStyle Style { get; set; } = new();

    /// <summary>
    /// 类型级属性声明：仅当 <see cref="AppliesTo"/> 非空时有意义，
    /// 命名样式忽略该集合。实例值仍存放在节点上。
    /// </summary>
    public List<ElementAttributeDeclaration> Attributes { get; } = new();

    /// <summary>类型级渲染行为：仅当 <see cref="AppliesTo"/> 非空时有意义。</summary>
    public ElementRenderBehavior Behavior { get; set; } = new();

    public ElementAttributeDeclaration? FindAttribute(string key) =>
        Attributes.Find(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal));
}

/// <summary>
/// 节点身份的 kind 部分。kind 的唯一来源是节点属性 <c>custom-kind</c>（由用户块定义 Id
/// 或内置自定义内容块类别写入），样式匹配、模板变体、图标与指纹都从这里取，禁止各处自行解析。
/// </summary>
public static class ElementKind
{
    /// <summary>节点上承载 kind 的属性键。</summary>
    public const string AttributeKey = "custom-kind";

    /// <summary>取节点的 kind；没有该属性的节点返回 <c>null</c>（表示“不限 kind”）。</summary>
    public static string? Resolve(MarkdownNode? node)
    {
        if (node is null
            || !node.Attributes.TryGetValue(AttributeKey, out var value)
            || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    /// <summary>
    /// 取可用于 class/字典键的 kind 规范化形式：只保留字母、数字、<c>-</c>、<c>_</c>。
    /// 含其它字符时返回 <c>null</c>，调用方回退到默认 kind，避免把任意文本拼进 HTML。
    /// </summary>
    public static string? NormalizeToken(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return null;
        }

        var value = kind.Trim();
        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c) && c is not ('-' or '_'))
            {
                return null;
            }
        }

        return value;
    }

    /// <summary>
    /// “类型 + kind”字典键的比较器：类型精确相等、kind 按 <see cref="StringComparison.OrdinalIgnoreCase"/>。
    /// 样式索引、模板白名单与指纹索引共用同一份，避免三处归一规则不一致。
    /// </summary>
    public static IEqualityComparer<(NodeType Type, string Kind)> KeyComparer { get; } = new KindKeyComparer();

    private sealed class KindKeyComparer : IEqualityComparer<(NodeType Type, string Kind)>
    {
        public bool Equals((NodeType Type, string Kind) x, (NodeType Type, string Kind) y) =>
            x.Type == y.Type && string.Equals(x.Kind, y.Kind, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((NodeType Type, string Kind) obj) =>
            HashCode.Combine(obj.Type, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Kind));
    }
}

