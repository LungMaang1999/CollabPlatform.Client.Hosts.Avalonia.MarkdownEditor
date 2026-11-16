using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

/// <summary>
/// 伴随文档的<b>分段指纹</b>。两条硬规则：
/// <list type="number">
/// <item>禁止用整份文件哈希 / 文件大小 / 修改时间作为任何缓存键——那是“改一次颜色整篇重解析”的唯一入口；</item>
/// <item>解析层只读定义段指纹（<see cref="ComputeElementsKey"/>，即 <c>ubs2</c>）；
/// 渲染层同时读样式段指纹与定义段指纹。</item>
/// </list>
/// </summary>
public static class CompanionDocumentFingerprint
{
    /// <summary>样式段指纹的格式版本；投影口径变化时递增。</summary>
    public const string StylesVersion = "cstyles2";

    /// <summary>
    /// 样式段的规范化投影哈希。进入哈希的内容：
    /// <c>&lt;Styles&gt;</c> 下的全部定义（按 Id 排序）与 XML 结构里的样式引用/局部样式。
    /// 明确排除：<c>meta</c>、<c>&lt;EditorState&gt;</c>、<c>&lt;Metadata&gt;</c>、
    /// 以及节点里的 <c>Text</c> / <c>RawMarkdown</c>（正文由 markdown 源哈希负责）。
    /// 段缺失返回空串——空段不得与“有内容但哈希为空”混淆。
    /// </summary>
    public static string ComputeStylesHash(XElement? styles)
    {
        if (styles is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(1024);

        var stylesContainer = styles.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "Styles", StringComparison.Ordinal));
        if (stylesContainer is not null)
        {
            var ordered = stylesContainer.Elements()
                .Where(element => string.Equals(element.Name.LocalName, "Style", StringComparison.Ordinal))
                .OrderBy(element => element.Attribute("Id")?.Value ?? string.Empty, StringComparer.Ordinal);

            foreach (var style in ordered)
            {
                var projection = new XElement(style);
                StripInsignificantWhitespace(projection);
                builder.Append(projection.ToString(SaveOptions.DisableFormatting)).Append('\n');
            }
        }

        var rootContainer = styles.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "Root", StringComparison.Ordinal));
        if (rootContainer is not null)
        {
            var projection = new XElement(rootContainer);
            StripNodeContent(projection);
            StripInsignificantWhitespace(projection);
            builder.Append(projection.ToString(SaveOptions.DisableFormatting));
        }

        return StylesVersion + ':' + Sha256(builder.ToString());
    }

    /// <summary>
    /// 定义段的语法键：直接复用 <see cref="UserBlockSyntaxFingerprint.ComputeSet"/>
    /// （版本 <c>ubs2</c>），供解析运行时作为缓存键。样式段内容不得进入这个串。
    /// <para><paramref name="composition"/> 非空时把组合单元也折进来：只改被引用的片段
    /// 也要换掉引用者的键（键不带依赖是 variant / opens 都栽过的老坑）。
    /// 空组合不追加任何内容，历史键逐字节不变。</para>
    /// </summary>
    public static string ComputeElementsKey(
        IReadOnlyList<UserBlockDefinition>? definitions,
        UserBlockComposition? composition = null)
    {
        var key = UserBlockSyntaxFingerprint.ComputeSet(definitions);
        if (composition is null || composition.IsEmpty)
        {
            return key;
        }

        return key + "|composition:" + Sha256(UserBlockElementsSerializer.Serialize([], composition));
    }

    /// <summary>定义段指纹的短哈希：冲突报告与版本串使用，语义等价于 <see cref="ComputeElementsKey"/>。</summary>
    public static string ComputeElementsHash(
        IReadOnlyList<UserBlockDefinition>? definitions,
        UserBlockComposition? composition = null)
    {
        var key = ComputeElementsKey(definitions, composition);
        return key.Length == 0 ? string.Empty : Sha256(key);
    }

    // 去掉正文：Text / RawMarkdown 可能是子元素（新版）也可能是属性（旧版）。
    private static void StripNodeContent(XElement element)
    {
        element.Attributes()
            .Where(attribute => IsContentName(attribute.Name.LocalName))
            .Remove();

        element.Elements()
            .Where(child => IsContentName(child.Name.LocalName))
            .Remove();

        foreach (var child in element.Elements())
        {
            StripNodeContent(child);
        }
    }

    /// <summary>
    /// 去掉只用于排版的空白文本节点。伴随文件带缩进，回读后这些空白会变成真实文本节点；
    /// 若把它们算进指纹，同一份样式“内存里的”和“从文件读回的”就会得到不同哈希，
    /// 进而把一次自己写的定义变更误判成“样式被其他程序修改”。
    /// </summary>
    private static void StripInsignificantWhitespace(XElement element)
    {
        element.Nodes()
            .OfType<XText>()
            .Where(text => string.IsNullOrWhiteSpace(text.Value))
            .Remove();

        foreach (var child in element.Elements())
        {
            StripInsignificantWhitespace(child);
        }
    }

    private static bool IsContentName(string localName) =>
        string.Equals(localName, "Text", StringComparison.Ordinal)
        || string.Equals(localName, "RawMarkdown", StringComparison.Ordinal);

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
