namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

/// <summary>
/// 领域节点 <see cref="MarkdownNode.Attributes"/> 使用的保留键。
/// </summary>
public static class MarkdownAttributes
{
    /// <summary>
    /// 值为 "true" 表示该块在源码末尾仍未闭合（流式输入中的半截围栏/公式），
    /// 渲染层据此发射 data-block-unclosed 标记，前端跳过高亮/图表/公式处理。
    /// </summary>
    public const string Unclosed = "unclosed";

    /// <summary>
    /// 全部由解析器按当前源码派生的属性键。重解析后身份匹配只继承用户自定义
    /// 属性（ChangeAttributeCommand/持久化恢复），这些键必须以新树解析结果
    /// 为准，不能从旧树继承（如围栏 info string 被编辑后 language 要能消失）。
    /// </summary>
    public static readonly IReadOnlySet<string> SourceDerivedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        Unclosed,
        "checked",
        "index",
        "id",
        "language",
        "src",
        "url",
        "href",
        "custom-kind",
        "admonition",
        "admonition-title"
    };
}
