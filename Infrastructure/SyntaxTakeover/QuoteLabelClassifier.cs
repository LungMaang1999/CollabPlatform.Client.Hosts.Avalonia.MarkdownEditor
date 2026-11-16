using System.Text.RegularExpressions;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.SyntaxTakeover;

/// <summary>
/// 引用块首行标签类别。识别发生在块装配阶段，节点按类别一次性创建，
/// 不再创建后改 <c>Type</c>。
/// </summary>
public enum QuoteLabelKind
{
    /// <summary>无标签，保持普通引用块。</summary>
    None,

    /// <summary>自定义内容块（<c>kind: 正文</c>）。</summary>
    CustomBlock,

    /// <summary>提示块（<c>[!NOTE] 标题</c>）。</summary>
    Admonition
}

/// <summary>
/// 首行标签的识别结果。标签到类别名的映射仍由 <see cref="AdmonitionCatalog"/> 声明；
/// <see cref="PrefixLength"/> 是需要在首个段落中消费掉的前缀长度。
/// </summary>
public sealed record QuoteLabelClassification(
    QuoteLabelKind Kind,
    string MappedKind,
    string Title,
    int PrefixLength)
{
    public static readonly QuoteLabelClassification None =
        new(QuoteLabelKind.None, string.Empty, string.Empty, 0);
}

/// <summary>
/// 引用块首行标签识别器。输入是与既有实现完全一致的首行文本
/// （仅拼接 <c>Text</c> 子节点、遇到换行即止），因此识别口径逐字不变。
/// </summary>
public static class QuoteLabelClassifier
{
    private static readonly Regex CustomBlockLabelRegex = new(
        @"^(?<kind>[A-Za-z][A-Za-z0-9_-]{0,20})[ \t]*:[ \t]*(?<body>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex AdmonitionLabelRegex = new(
        @"^\[!(?<label>[A-Za-z][A-Za-z0-9_-]{0,20})\](?:[ \t]+(?<title>.+?))?[ \t]*$",
        RegexOptions.Compiled);

    public static QuoteLabelClassification Classify(string? firstLineText)
    {
        if (string.IsNullOrEmpty(firstLineText))
        {
            return QuoteLabelClassification.None;
        }

        var custom = CustomBlockLabelRegex.Match(firstLineText);
        if (custom.Success &&
            AdmonitionCatalog.TryMapCustomBlockLabel(custom.Groups["kind"].Value, out var customKind))
        {
            return new QuoteLabelClassification(
                QuoteLabelKind.CustomBlock,
                customKind,
                string.Empty,
                custom.Groups["body"].Index);
        }

        var admonition = AdmonitionLabelRegex.Match(firstLineText);
        if (admonition.Success &&
            AdmonitionCatalog.TryMapAdmonitionLabel(admonition.Groups["label"].Value, out var admonitionKind))
        {
            var title = admonition.Groups["title"].Success
                ? admonition.Groups["title"].Value.Trim()
                : AdmonitionCatalog.GetDefaultTitle(admonitionKind);

            return new QuoteLabelClassification(
                QuoteLabelKind.Admonition,
                admonitionKind,
                title,
                admonition.Length);
        }

        return QuoteLabelClassification.None;
    }
}
