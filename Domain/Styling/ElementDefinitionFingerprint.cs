using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

/// <summary>
/// 元素定义指纹：把“会影响渲染输出”的定义内容折叠成顺序无关的规范文本。
/// 节点编辑器、渲染缓存与将来的语法层都必须走这一个入口，禁止在别处另算一份。
///
/// 进入指纹的子集：
/// <list type="bullet">
/// <item>适用类型（<see cref="StyleDefinition.AppliesTo"/>）与可选的 kind 限定
/// （<see cref="StyleDefinition.AppliesToKind"/>，仅非空时计入）；</item>
/// <item>展开继承链后的有效 <see cref="NodeStyle"/>（父链先应用、自身后应用，与
/// <see cref="StyleResolver"/> 的优先级一致）；</item>
/// <item>模板结构选择：<see cref="ElementRenderBehavior.TemplateVariant"/> 经类型白名单
/// 回落后的有效变体（结构不同 → 指纹必须不同）；</item>
/// <item>帧策略：<see cref="ElementRenderBehavior.FrameStrategy"/>；</item>
/// <item>真正参与输出的属性声明：只有带非空 Binding 的声明才计入，且只取
/// Key / DefaultValue / Binding.Target / Binding.Name / Binding.Part / Binding.ValueMap。</item>
/// </list>
///
/// 明确排除（改动它们不得改变指纹）：
/// <list type="bullet">
/// <item>设置面板字段：Label、Hint、Group、Options、Presentation（控件、占位、宽度、排序、
/// 条件显示 VisibleWhen、步进、精度、行数、最大长度等）、IsAdvanced；</item>
/// <item>声明编辑字段：ValueKind、IsEnabled、IsDerived、Target（渲染器不读取）；</item>
/// <item>定义身份字段：Id、Name、ParentStyleId（继承已展开为有效样式，不重复计入）；</item>
/// <item>纯交互态样式（hover / active / focus / disabled / selected）：渲染输出不读取。
/// 只有会改变 DOM 结构的状态声明才允许进入指纹，判定依据是“是否影响输出结构”，
/// 而不是“是否属于某状态”。</item>
/// </list>
///
/// 输入约定：调用方传入“解析后的有效定义对象”。当前输入是 <see cref="StyleDefinition"/>，
/// 将来若定义改为声明树，只需替换本类内部的展开逻辑，渲染侧仍只消费同一份指纹。
/// </summary>
public static class ElementDefinitionFingerprint
{
    /// <summary>指纹格式版本；规范文本结构变化时递增。</summary>
    public const string Version = "edf1";

    private const string RootPart = "root";

    /// <summary>
    /// 计算单个定义（含继承链展开）的指纹。属性按内容排序后再折叠，
    /// 因此定义文件里的书写顺序不影响结果。
    /// </summary>
    public static string Compute(
        StyleDefinition definition,
        IReadOnlyDictionary<string, StyleDefinition> stylesById)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(stylesById);

        var builder = new StringBuilder(256);
        builder.Append(Version).Append('{');
        AppendField(builder, "applies", definition.AppliesTo?.ToString());
        // kind 只在显式限定时追加：不限 kind 的定义仍产出与旧版逐字节相同的串。
        if (!string.IsNullOrWhiteSpace(definition.AppliesToKind))
        {
            AppendField(builder, "kind", definition.AppliesToKind.Trim());
        }

        AppendField(builder, "variant", ResolveVariant(definition).ToString());
        AppendField(builder, "frame", ResolveFrameStrategy(definition).ToString());
        AppendField(builder, "style", DescribeEffectiveStyle(definition, stylesById));
        AppendField(builder, "attrs", DescribeBoundAttributes(definition));
        builder.Append('}');
        return builder.ToString();
    }

    /// <summary>
    /// 计算整张样式表的渲染指纹集合：文档级指纹（放进缓存纪元前缀）+
    /// 按节点类型的指纹 + 按定义 Id 的样式指纹（供节点 StyleId 折叠）+
    /// 按自定义元素 kind 的结构指纹（供含该 kind 的块单独失效）。
    /// </summary>
    public static ElementDefinitionFingerprintSet ComputeSet(
        StyleSheet sheet,
        IReadOnlyList<UserBlockDefinition>? userBlocks = null,
        IReadOnlyList<CompositeStageDefinition>? graphStages = null)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        // 与 StyleResolver 保持一致：default 是整个文档的兜底定义，缺省时按同一规则补齐，
        // 避免“首帧创建前/后”算出两份不同的文档级指纹。
        var defaultDefinition = sheet.GetOrCreateDefault();

        var byId = new Dictionary<string, StyleDefinition>(StringComparer.Ordinal);
        foreach (var definition in sheet.Styles)
        {
            byId.TryAdd(definition.Id, definition);
        }

        var styleById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in byId)
        {
            styleById[pair.Key] = DescribeEffectiveStyle(pair.Value, byId);
        }

        // 每个类型只取第一条定义，与 StyleResolver / BuildTypeDefinitionIndex 的 TryAdd 语义一致。
        var byType = new Dictionary<NodeType, string>();
        var byKind = new Dictionary<(NodeType, string), string>(ElementKind.KeyComparer);
        foreach (var definition in sheet.Styles)
        {
            if (string.Equals(definition.Id, "default", StringComparison.Ordinal)
                || definition.AppliesTo is not { } type
                || type == NodeType.Document)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(definition.AppliesToKind))
            {
                byType.TryAdd(type, Compute(definition, byId));
                continue;
            }

            byKind.TryAdd((type, definition.AppliesToKind.Trim()), Compute(definition, byId));
        }

        var documentLevel = new StringBuilder(160);
        documentLevel.Append(Version).Append("{default=");
        // default 只应用自身 Style，不展开 ParentStyleId，与 StyleResolver 完全一致。
        documentLevel.Append(DescribeStyle(defaultDefinition.Style));

        var documentDefinition = FindDocumentTypeDefinition(sheet);
        if (documentDefinition is not null)
        {
            // Document 类型定义对整篇生效，放进文档级前缀，让所有块一起失效。
            documentLevel.Append(";document=").Append(Compute(documentDefinition, byId));
        }

        documentLevel.Append('}');

        var byStructure = ComputeStructures(userBlocks, graphStages);
        var aggregate = BuildAggregate(documentLevel.ToString(), byType, byKind, styleById, byStructure);
        return new ElementDefinitionFingerprintSet(
            documentLevel.ToString(),
            aggregate,
            byType,
            byKind,
            styleById,
            byStructure);
    }

    /// <summary>
    /// 按 kind 计算结构声明指纹。结构只挂在自定义元素定义上（定义 Id 即 custom-kind），
    /// 因此键就是 kind；重复 Id 保留首条，与解析器的胜出规则一致。
    /// 写入顺序不影响结果：按 Id 排序后折叠。
    /// <para>
    /// 图签名按“展开后规范形”取哈希：引用子图与手写内联展开形必须得到同一个签名。
    /// 没有复合节点时 <see cref="GraphCompositeExpander.Expand"/> 原样返回原图，
    /// 因此签名与旧算法逐字节相同。
    /// </para>
    /// </summary>
    private static Dictionary<string, string> ComputeStructures(
        IReadOnlyList<UserBlockDefinition>? userBlocks,
        IReadOnlyList<CompositeStageDefinition>? graphStages)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (userBlocks is null || userBlocks.Count == 0)
        {
            return result;
        }

        var ordered = new List<UserBlockDefinition>(userBlocks);
        ordered.Sort(static (left, right) => string.CompareOrdinal(left.Id, right.Id));

        foreach (var definition in ordered)
        {
            if (!result.ContainsKey(definition.Id))
            {
                // 先净化再取规范串：非法标签/属性不进入指纹，也不进入渲染。
                var structure = UserBlockStructureRules.Sanitize(definition.Structure).Structure;
                var graph = definition.StructureGraph;
                if (!structure.IsEmpty || graph is not null)
                {
                    // 图参与签名：只改图不改声明时，块缓存也必须失效。
                    result[definition.Id] = (structure.IsEmpty ? "-" : DescribeStructure(structure))
                        + (graph is null ? string.Empty : "|graph=" + ComputeGraphSignature(graph, graphStages));
                }
            }
        }

        return result;
    }

    /// <summary>口径 A：展开后规范形取哈希；未发生展开时直接沿用原图哈希。</summary>
    private static string ComputeGraphSignature(
        NodeGraph graph,
        IReadOnlyList<CompositeStageDefinition>? graphStages)
    {
        var hash = graph.ComputeRenderHash();
        if (graphStages is not { Count: > 0 })
        {
            return hash;
        }

        var expansion = GraphCompositeExpander.Expand(graph, graphStages);
        return expansion.Expanded ? expansion.Graph.ComputeRenderHash() : hash;
    }

    private static string DescribeStructure(UserBlockStructure structure) =>
        string.Concat("structure=", UserBlockStructureRules.Describe(structure));

    /// <summary>
    /// 把节点自身样式（局部样式）折叠成稳定文本，供块签名使用。
    /// 与定义指纹共用同一套字段顺序与数字格式，避免两处规范不一致。
    /// </summary>
    public static string DescribeNodeStyle(NodeStyle? style) =>
        style is null ? string.Empty : DescribeStyle(style);

    private static ElementTemplateVariant ResolveVariant(StyleDefinition definition)
    {
        // 命名样式不消费行为；类型定义才按白名单回落。
        if (definition.AppliesTo is not { } type)
        {
            return ElementTemplateVariant.Default;
        }

        return TemplateVariantCatalog
            .Resolve(type, definition.AppliesToKind, definition.Behavior.TemplateVariant)
            .Effective;
    }

    private static ElementFrameStrategy ResolveFrameStrategy(StyleDefinition definition) =>
        definition.AppliesTo is null ? ElementFrameStrategy.Auto : definition.Behavior.FrameStrategy;

    private static StyleDefinition? FindDocumentTypeDefinition(StyleSheet sheet)
    {
        foreach (var definition in sheet.Styles)
        {
            if (!string.Equals(definition.Id, "default", StringComparison.Ordinal)
                && definition.AppliesTo == NodeType.Document)
            {
                return definition;
            }
        }

        return null;
    }

    private static string DescribeEffectiveStyle(
        StyleDefinition definition,
        IReadOnlyDictionary<string, StyleDefinition> stylesById)
    {
        var chain = new List<StyleDefinition>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        var current = definition;
        while (current is not null && visited.Add(current.Id))
        {
            chain.Add(current);
            current = !string.IsNullOrWhiteSpace(current.ParentStyleId)
                      && stylesById.TryGetValue(current.ParentStyleId, out var parent)
                ? parent
                : null;
        }

        // 与 StyleResolver 一致：祖先先应用，自身最后应用。
        var effective = new ComputedStyle();
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            effective.Apply(chain[i].Style);
        }

        return DescribeStyle(effective);
    }

    private static string DescribeStyle(NodeStyle style)
    {
        var builder = new StringBuilder(224);
        AppendField(builder, "font-family", style.FontFamily);
        AppendNumber(builder, "font-size", style.FontSize);
        AppendField(builder, "foreground", style.ForegroundColor);
        AppendField(builder, "background", style.BackgroundColor);
        AppendFlag(builder, "bold", style.Bold);
        AppendFlag(builder, "italic", style.Italic);
        AppendNumber(builder, "line-height", style.LineHeight);
        AppendField(builder, "text-align", style.TextAlign);
        AppendField(builder, "vertical-align", style.VerticalAlign);
        AppendThickness(builder, "margin", style.Margin);
        AppendThickness(builder, "padding", style.Padding);
        AppendField(builder, "border-color", style.BorderColor);
        AppendNumber(builder, "border-width", style.BorderWidth);
        AppendNumber(builder, "border-radius", style.BorderRadius);
        AppendNumber(builder, "width", style.Width);
        AppendNumber(builder, "gap", style.Gap);
        AppendField(builder, "custom-css", style.CustomCss);
        return builder.ToString();
    }

    private static string DescribeBoundAttributes(StyleDefinition definition)
    {
        if (definition.Attributes.Count == 0)
        {
            return string.Empty;
        }

        var entries = new List<string>();
        foreach (var attribute in definition.Attributes)
        {
            var binding = attribute.Binding;
            if (binding is null || binding.IsEmpty)
            {
                continue;
            }

            entries.Add(DescribeAttribute(attribute, binding));
        }

        if (entries.Count == 0)
        {
            return string.Empty;
        }

        // 整条声明按内容排序：key 相同（同名多声明）时也有确定顺序。
        entries.Sort(StringComparer.Ordinal);

        var builder = new StringBuilder(entries.Count * 72);
        foreach (var entry in entries)
        {
            builder.Append(entry).Append('\n');
        }

        return builder.ToString();
    }

    private static string DescribeAttribute(
        ElementAttributeDeclaration attribute,
        ElementAttributeRenderBinding binding)
    {
        var builder = new StringBuilder(96);
        AppendField(builder, "key", attribute.Key);
        AppendField(builder, "default", NormalizeDefaultValue(attribute.DefaultValue));
        AppendField(builder, "target", binding.Target.ToString());
        AppendField(builder, "name", binding.Name);
        AppendField(builder, "part", NormalizePart(binding.Part));
        AppendField(builder, "value-map", NormalizeValueMap(binding.ValueMap));
        return builder.ToString();
    }

    private static string? NormalizeDefaultValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizePart(string? part) =>
        string.IsNullOrWhiteSpace(part) ? RootPart : part.Trim();

    private static string? NormalizeValueMap(string? valueMap)
    {
        if (string.IsNullOrWhiteSpace(valueMap))
        {
            return null;
        }

        if (!ElementRenderBindingRules.TryParseValueMap(valueMap, out var entries, out _))
        {
            return valueMap.Trim();
        }

        // 值映射是字典语义，书写顺序不影响输出，排序后再折叠。
        var builder = new StringBuilder(valueMap.Length);
        foreach (var pair in entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append(pair.Key).Append('=').Append(pair.Value).Append(';');
        }

        return builder.ToString();
    }

    private static string BuildAggregate(
        string documentLevel,
        Dictionary<NodeType, string> byType,
        Dictionary<(NodeType, string), string> byKind,
        Dictionary<string, string> styleById,
        Dictionary<string, string> byStructure)
    {
        // 聚合指纹只用于“定义是否被改过”的备忘判定：任何定义变化都让它变化，
        // 从而重新计算块签名；块是否真正失效仍由各自的块签名决定。
        var builder = new StringBuilder(320);
        builder.Append(documentLevel).Append('|');

        foreach (var pair in byType.OrderBy(pair => (int)pair.Key))
        {
            builder.Append((int)pair.Key).Append('=').Append(pair.Value).Append('|');
        }

        foreach (var pair in byKind.OrderBy(pair => (int)pair.Key.Item1)
                     .ThenBy(pair => pair.Key.Item2, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append((int)pair.Key.Item1)
                .Append('#')
                .Append(pair.Key.Item2)
                .Append('=')
                .Append(pair.Value)
                .Append('|');
        }

        foreach (var pair in styleById.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append(pair.Key).Append('=').Append(pair.Value).Append('|');
        }

        foreach (var pair in byStructure.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("structure#").Append(pair.Key).Append('=').Append(pair.Value).Append('|');
        }

        return builder.ToString();
    }

    private static void AppendField(StringBuilder builder, string name, string? value)
    {
        builder.Append(name).Append('=');
        if (value is null)
        {
            builder.Append("-1;");
            return;
        }

        // 长度前缀消除分隔符歧义，保证不同字段组合不会拼出同一串。
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append(';');
    }

    private static void AppendNumber(StringBuilder builder, string name, double? value)
    {
        builder.Append(name).Append('=');
        builder.Append(value.HasValue
            ? value.Value.ToString("R", CultureInfo.InvariantCulture)
            : "-");
        builder.Append(';');
    }

    private static void AppendFlag(StringBuilder builder, string name, bool? value)
    {
        builder.Append(name).Append('=');
        builder.Append(value.HasValue ? (value.Value ? '1' : '0') : '-');
        builder.Append(';');
    }

    private static void AppendThickness(StringBuilder builder, string name, ThicknessValue? value)
    {
        builder.Append(name).Append('=');
        if (value is null)
        {
            builder.Append("-;");
            return;
        }

        builder.Append(value.Left.ToString("R", CultureInfo.InvariantCulture)).Append(',')
            .Append(value.Top.ToString("R", CultureInfo.InvariantCulture)).Append(',')
            .Append(value.Right.ToString("R", CultureInfo.InvariantCulture)).Append(',')
            .Append(value.Bottom.ToString("R", CultureInfo.InvariantCulture)).Append(';');
    }
}

/// <summary>
/// 一张样式表在某次渲染开始时的元素定义指纹快照。渲染期间只读，
/// 由 <see cref="ElementDefinitionFingerprint.ComputeSet"/> 统一产出。
/// </summary>
public sealed class ElementDefinitionFingerprintSet
{
    private readonly Dictionary<NodeType, string> _byType;
    private readonly Dictionary<(NodeType, string), string> _byKind;
    private readonly Dictionary<string, string> _byId;
    private readonly Dictionary<string, string> _byStructure;

    internal ElementDefinitionFingerprintSet(
        string documentLevel,
        string aggregate,
        Dictionary<NodeType, string> byType,
        Dictionary<(NodeType, string), string> byKind,
        Dictionary<string, string> byId,
        Dictionary<string, string> byStructure)
    {
        DocumentLevel = documentLevel;
        Aggregate = aggregate;
        _byType = byType;
        _byKind = byKind;
        _byId = byId;
        _byStructure = byStructure;
    }

    /// <summary>文档级定义指纹（default 与 Document 类型定义），进入块签名前缀。</summary>
    public string DocumentLevel { get; }

    /// <summary>整张表定义内容的聚合指纹，仅用于判断定义是否变化。</summary>
    public string Aggregate { get; }

    public bool TryGetTypeFingerprint(NodeType type, out string fingerprint) =>
        TryGetTypeFingerprint(type, kind: null, out fingerprint);

    /// <summary>
    /// 取“类型 + kind”的有效定义指纹。kind 命中时把类型级、kind 级与结构声明三份指纹
    /// 一起折叠，保证改 kind 定义或改该 kind 的结构只影响该 kind 的块；
    /// kind 为空、未登记且无结构声明时与旧行为逐字节一致。
    /// </summary>
    public bool TryGetTypeFingerprint(NodeType type, string? kind, out string fingerprint)
    {
        var hasType = _byType.TryGetValue(type, out var typeFingerprint);

        string? kindFingerprint = null;
        var hasKind = string.IsNullOrWhiteSpace(kind) || !_byKind.TryGetValue((type, kind.Trim()), out kindFingerprint)
            ? false
            : true;

        string? structureFingerprint = null;
        var hasStructure = type == NodeType.Custom
                           && !string.IsNullOrWhiteSpace(kind)
                           && _byStructure.TryGetValue(kind.Trim(), out structureFingerprint);

        if (!hasType && !hasKind && !hasStructure)
        {
            fingerprint = string.Empty;
            return false;
        }

        if (!hasKind && !hasStructure)
        {
            // 旧路径：只有类型级指纹时原样返回，不追加任何分隔符。
            fingerprint = typeFingerprint!;
            return true;
        }

        var builder = new StringBuilder(96);
        if (hasType)
        {
            builder.Append(typeFingerprint);
        }

        if (hasKind)
        {
            builder.Append('\u0000').Append(kindFingerprint);
        }

        if (hasStructure)
        {
            builder.Append('\u0000').Append(structureFingerprint);
        }

        fingerprint = builder.ToString();
        return true;
    }

    public bool TryGetStyleFingerprint(string? styleId, out string fingerprint)
    {
        if (!string.IsNullOrWhiteSpace(styleId))
        {
            return _byId.TryGetValue(styleId, out fingerprint!);
        }

        fingerprint = string.Empty;
        return false;
    }
}
