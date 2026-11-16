using System.Globalization;
using System.Text;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

/// <summary>
/// 用户块定义的<b>语法指纹</b>：折叠“会改变解析结果”的字段，与渲染侧
/// <c>ElementDefinitionFingerprint</c>（样式/模板/绑定）完全分开。
///
/// 进入指纹的字段：
/// <list type="bullet">
/// <item>Id：最终写入 <c>custom-kind</c>，改 Id 等于删旧增新；</item>
/// <item>Extends：继承目标换人等于换定义（父链展开结果与解析产物都变）；</item>
/// <item>Name / Aliases / FenceChar / FenceLength：决定哪一行能开围栏；</item>
/// <item>ItemMarker：决定容器内哪一行开新条目（未声明时为 <c>@@</c>）；</item>
/// <item>Opens：决定本定义的首词开启哪个块类型（未声明时不贡献语法形态）；</item>
/// <item>Priority：同名定义之间的胜负，改变胜者就改变 <c>custom-kind</c>；</item>
/// <item>属性声明与内容模型：自约束生效起，它们会改变节点属性（默认值注入、
/// 非法值回退）并影响该块的诊断与违规标记，因此属于解析结果的一部分。</item>
/// </list>
///
/// 仍然与“渲染指纹”分开：这里只关心解析产物（属性/结构/诊断），
/// 样式、模板与绑定变化不会让任何块失效。
/// </summary>
public static class UserBlockSyntaxFingerprint
{
    /// <summary>指纹格式版本；规范文本结构变化时递增。</summary>
    public const string Version = "ubs2";

    /// <summary>单条定义的语法指纹。字段长度前缀编码，避免拼接歧义。</summary>
    public static string Compute(UserBlockDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var builder = new StringBuilder(192);
        builder.Append(Version).Append('{');
        AppendField(builder, "id", definition.Id);
        // 继承目标进指纹：父链换人等于换定义；只在声明时追加，历史指纹逐字节不变。
        if (!string.IsNullOrEmpty(definition.Extends))
        {
            AppendField(builder, "extends", definition.Extends.ToLowerInvariant());
        }
        AppendField(builder, "name", definition.Name.ToLowerInvariant());
        // 别名同属“能开围栏的关键字”：只在声明时追加，旧定义指纹逐字节不变。
        if (definition.Aliases.Count > 0)
        {
            AppendField(
                builder,
                "aliases",
                string.Join('\u001e', definition.Aliases.Select(alias => alias.ToLowerInvariant())));
        }
        AppendField(builder, "fence-char", definition.FenceChar.ToString());
        // 多字符声明只在真正多字符时追加，旧单字符定义的指纹保持原样。
        if (definition.FenceChars.Count > 1)
        {
            AppendField(builder, "fence-chars", string.Join(",", definition.FenceChars));
        }
        AppendField(builder, "fence-length", definition.FenceLength.ToString(CultureInfo.InvariantCulture));
        AppendField(builder, "priority", definition.Priority.ToString(CultureInfo.InvariantCulture));
        // 条目标记决定哪些行切开条目，属于解析产物；只在声明时追加，
        // 未声明该字段的旧定义指纹保持逐字节不变。
        if (!string.IsNullOrEmpty(definition.ItemMarker))
        {
            AppendField(builder, "item-marker", definition.ItemMarker);
        }
        // 首词分派决定哪一行、以哪个目标开围栏，属于解析产物；同样只在声明时追加。
        if (!string.IsNullOrEmpty(definition.Opens))
        {
            AppendField(builder, "opens", definition.Opens.ToLowerInvariant());
        }

        // 行内 HTML 白名单决定一段行内标签产不产节点，属于解析产物；
        // 只在声明时追加，未声明该节的旧定义指纹逐字节不变。
        if (definition.InlineHtml is not null)
        {
            AppendField(builder, "inline-html", DescribeInlineHtml(definition.InlineHtml));
        }

        // 块级 HTML 白名单与行内分开声明、分开进指纹：同一份声明不会因为写在哪一节而互相影响。
        if (definition.BlockHtml is not null)
        {
            AppendField(builder, "block-html", DescribeInlineHtml(definition.BlockHtml));
        }

        // 组合引用集合进指纹：只改被引用的属性组/片段，引用者必须换缓存键。
        // 只在存在引用时追加，历史定义指纹逐字节不变。
        if (definition.CompositionRefs.Count > 0)
        {
            AppendField(
                builder,
                "refs",
                string.Join('\u001e', definition.CompositionRefs.Select(id => id.ToLowerInvariant())));
        }

        foreach (var attribute in definition.Attributes.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var descriptor = string.Join(
                '\u001f',
                attribute.Key,
                attribute.Kind.ToString(),
                attribute.DefaultValue ?? "\u0000",
                attribute.Required ? "1" : "0",
                string.Join('\u001e', attribute.AllowedValues));
            AppendField(builder, "attribute", descriptor);
        }

        var content = string.Join(
            '\u001f',
            string.Join(',', definition.Content.AllowedBlocks.OrderBy(value => (int)value)),
            string.Join(',', definition.Content.AllowedInlines.OrderBy(value => (int)value)));
        AppendField(builder, "content", content);

        builder.Append('}');
        return builder.ToString();
    }

    /// <summary>
    /// 行内 HTML 白名单的规范串：只描述“允许什么”，顺序稳定（标签/属性/协议均排序），
    /// 因此同一份声明无论书写顺序如何都得到同一个解析缓存键。
    /// </summary>
    private static string DescribeInlineHtml(InlineHtmlDefinition definition)
    {
        var builder = new StringBuilder(160);
        builder.Append((int)definition.DefaultDisposition)
            .Append('\u001f').Append(definition.MaxDepth.ToString(CultureInfo.InvariantCulture))
            .Append('\u001f').Append(definition.MaxAttributes.ToString(CultureInfo.InvariantCulture))
            .Append('\u001f').Append(definition.MaxAttributeLength.ToString(CultureInfo.InvariantCulture))
            .Append('\u001f').Append(definition.MaxTagNameLength.ToString(CultureInfo.InvariantCulture));

        foreach (var tag in definition.Tags.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            builder.Append('\u001e')
                .Append(tag.Name)
                .Append('\u001f').Append((int)tag.Disposition)
                .Append('\u001f').Append(tag.IsVoid ? "1" : "0");

            // style 开关只在开启时追加：历史定义的规范串逐字节不变。
            if (tag.StyleLayout)
            {
                builder.Append('\u001c').Append("style-layout");
            }

            foreach (var attribute in tag.Attributes.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                builder.Append('\u001d')
                    .Append(attribute.Name)
                    .Append('\u001f').Append(attribute.Kind)
                    .Append('\u001f').Append(attribute.MaxLength.ToString(CultureInfo.InvariantCulture))
                    .Append('\u001f')
                    .Append(string.Join(',', attribute.Protocols.OrderBy(item => item, StringComparer.Ordinal)));

                // 扩展字段只在声明时追加：历史定义（text/url）的规范串逐字节不变。
                if (attribute.IsInteger)
                {
                    builder.Append('\u001c').Append(attribute.Min.ToString(CultureInfo.InvariantCulture))
                        .Append('\u001f').Append(attribute.Max.ToString(CultureInfo.InvariantCulture));
                }

                if (attribute.IsConstant)
                {
                    builder.Append('\u001c').Append("constant")
                        .Append('\u001f').Append(attribute.Constant)
                        .Append('\u001f').Append(attribute.ConstantWhen ?? string.Empty);
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>整组定义的聚合指纹，用作解析运行时（管线/转换器）的缓存键。</summary>
    public static string ComputeSet(IEnumerable<UserBlockDefinition>? definitions)    {
        if (definitions is null) return string.Empty;

        var ordered = new List<UserBlockDefinition>(definitions);
        if (ordered.Count == 0) return string.Empty;

        // 定义文件的书写顺序不影响语法：按 Id 排序后再折叠。
        ordered.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));

        var builder = new StringBuilder(ordered.Count * 96);
        builder.Append(Version).Append('[');
        foreach (var definition in ordered)
        {
            // 长度前缀保证不同定义的拼接不会碰撞。
            var fingerprint = Compute(definition);
            builder.Append(fingerprint.Length).Append(':').Append(fingerprint).Append(';');
        }

        builder.Append(']');
        return builder.ToString();
    }

    /// <summary>
    /// 比较新旧定义集合，返回语法指纹发生变化的 Id（含新增与删除）。
    /// Id 按忽略大小写对齐，返回值保持新定义的书写形式。
    /// </summary>
    public static IReadOnlySet<string> ComputeChangedIds(
        IEnumerable<UserBlockDefinition>? previous,
        IEnumerable<UserBlockDefinition>? current)
    {
        var before = IndexById(previous);
        var after = IndexById(current);

        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in after)
        {
            if (!before.TryGetValue(pair.Key, out var oldDefinition)
                || !string.Equals(Compute(oldDefinition), Compute(pair.Value), StringComparison.Ordinal))
            {
                changed.Add(pair.Value.Id);
            }
        }

        foreach (var pair in before)
        {
            if (!after.ContainsKey(pair.Key))
            {
                changed.Add(pair.Value.Id);
            }
        }

        return changed;
    }

    private static Dictionary<string, UserBlockDefinition> IndexById(IEnumerable<UserBlockDefinition>? definitions)
    {
        var index = new Dictionary<string, UserBlockDefinition>(StringComparer.OrdinalIgnoreCase);
        if (definitions is null) return index;

        foreach (var definition in definitions)
        {
            index.TryAdd(definition.Id, definition);
        }

        return index;
    }

    private static void AppendField(StringBuilder builder, string name, string value)
    {
        builder.Append(name).Append('=').Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':').Append(value).Append(';');
    }
}
