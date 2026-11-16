using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

public sealed class StyleResolver : IStyleResolver
{
    private readonly MarkdownDocument _document;
    private readonly StyleCache _cache;

    private long _cachedSourceRevision = -1;
    private long _cachedStyleRevision = -1;
    private StyleSheet? _indexedStyleSheet;
    private StyleDefinition? _defaultDefinition;
    private Dictionary<NodeType, StyleDefinition>? _typeStyles;
    private Dictionary<NodeType, Dictionary<string, StyleDefinition>>? _kindStyles;
    private Dictionary<string, StyleDefinition>? _stylesById;

    public StyleResolver(MarkdownDocument document, StyleCache? cache = null)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _cache = cache ?? new StyleCache();
    }

    public ComputedStyle Resolve(MarkdownNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        lock (_document)
        {
            EnsureCacheIsCurrent();

            if (_cache.TryGet(node.Id, out var cached))
                return cached;

            var result = new ComputedStyle();

            // 优先级（低 → 高）：渲染器兜底 → 父链继承 → 类型/命名样式 → 节点自身。
            // 即每个可继承属性：自身已定义用自身；否则沿父节点计算值向上查找；
            // 整条链都未定义时才由渲染器 default 兜底。

            // 1) 渲染器兜底：default 是最低优先级的后备值，可被任何祖先设置覆盖。
            result.Apply(_defaultDefinition!.Style);

            // 2) 继承回退：父节点【计算值】中的可继承属性覆盖兜底。
            //    父计算值已递归包含更上层祖先的 LocalStyle，因此列表/引用/表格等
            //    任意祖先上设置的字体、字号、字重、字态、行高、文字色、对齐都会
            //    沿树向下传递，直到被某层直接声明截断。
            if (node.Parent is not null)
            {
                result.ApplyInheritable(Resolve(node.Parent));
            }

            if (_typeStyles!.TryGetValue(node.Type, out var typeStyle))
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                ApplyStyleDefinition(result, typeStyle, visited);
            }

            // 类型定义之上、命名样式之下：带 kind 的类型定义更具体，覆盖不限 kind 的同类型定义。
            var kind = ElementKind.Resolve(node);
            if (kind is not null
                && _kindStyles!.TryGetValue(node.Type, out var kindStyles)
                && kindStyles.TryGetValue(kind, out var kindStyle))
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                ApplyStyleDefinition(result, kindStyle, visited);
            }

            if (!string.IsNullOrWhiteSpace(node.StyleId))
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                ApplyReferencedStyle(result, node.StyleId, visited);
            }

            // 3) 节点自身 LocalStyle 优先级最高。
            result.Apply(node.LocalStyle);

            _cache.Set(node.Id, result);
            return result;
        }
    }

    /// <summary>
    /// 逐属性来源（只读影子重放）。层序与 <see cref="Resolve"/> 完全一致：
    /// 定义默认 → 祖先继承 → 类型定义（含父链）→ kind 定义 → 命名样式 → 节点自身。
    /// 只读：不写缓存、不改任何节点或样式对象，因此不改变输出与块签名。
    /// </summary>
    public IReadOnlyList<StylePropertyProvenance> ResolveProvenance(MarkdownNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        lock (_document)
        {
            EnsureCacheIsCurrent();
            return BuildProvenance(node);
        }
    }

    public void Invalidate(MarkdownNode? node = null)
    {
        lock (_document)
        {
            _cache.Invalidate(node);
            _indexedStyleSheet = null;
            SyncCachedRevisions();
        }
    }

    public void Clear()
    {
        lock (_document)
        {
            _cache.Clear();
            _indexedStyleSheet = null;
            SyncCachedRevisions();
        }
    }

    private void EnsureCacheIsCurrent()
    {
        if (_cachedSourceRevision == _document.SourceRevision
            && _cachedStyleRevision == _document.StyleRevision
            && ReferenceEquals(_indexedStyleSheet, _document.StyleSheet))
        {
            return;
        }

        _cache.Clear();
        RebuildIndexes();
        SyncCachedRevisions();
    }

    private void RebuildIndexes()
    {
        var styleSheet = _document.StyleSheet;
        var defaultDefinition = styleSheet.GetOrCreateDefault();
        var typeStyles = new Dictionary<NodeType, StyleDefinition>();
        var kindStyles = new Dictionary<NodeType, Dictionary<string, StyleDefinition>>();
        var stylesById = new Dictionary<string, StyleDefinition>(StringComparer.Ordinal);

        var styles = styleSheet.Styles;
        for (int i = 0; i < styles.Count; i++)
        {
            var definition = styles[i];
            stylesById.TryAdd(definition.Id, definition);

            if (string.Equals(definition.Id, "default", StringComparison.Ordinal)
                || !definition.AppliesTo.HasValue)
            {
                continue;
            }

            var type = definition.AppliesTo.GetValueOrDefault();
            if (string.IsNullOrWhiteSpace(definition.AppliesToKind))
            {
                typeStyles.TryAdd(type, definition);
                continue;
            }

            // kind 键按 OrdinalIgnoreCase 归一：定义里写 Note 与 note 命中同一档。
            if (!kindStyles.TryGetValue(type, out var byKind))
            {
                byKind = new Dictionary<string, StyleDefinition>(StringComparer.OrdinalIgnoreCase);
                kindStyles[type] = byKind;
            }

            byKind.TryAdd(definition.AppliesToKind.Trim(), definition);
        }

        _indexedStyleSheet = styleSheet;
        _defaultDefinition = defaultDefinition;
        _typeStyles = typeStyles;
        _kindStyles = kindStyles;
        _stylesById = stylesById;
    }

    private void SyncCachedRevisions()
    {
        _cachedSourceRevision = _document.SourceRevision;
        _cachedStyleRevision = _document.StyleRevision;
    }

    private void ApplyReferencedStyle(ComputedStyle target, string styleId, HashSet<string> visited)
    {
        if (!visited.Add(styleId))
            throw new StyleResolutionException($"Style inheritance cycle detected at '{styleId}'.");

        if (!_stylesById!.TryGetValue(styleId, out var definition))
            return;

        ApplyStyleDefinition(target, definition, visited);
    }

    private void ApplyStyleDefinition(ComputedStyle target, StyleDefinition definition, HashSet<string> visited)
    {
        if (!string.IsNullOrWhiteSpace(definition.ParentStyleId))
            ApplyReferencedStyle(target, definition.ParentStyleId!, visited);

        target.Apply(definition.Style);
    }

    // ── 逐属性来源：与 Resolve 同层序的影子重放 ───────────────────────────

    private IReadOnlyList<StylePropertyProvenance> BuildProvenance(MarkdownNode node)
    {
        var layers = new Dictionary<string, List<StylePropertyLayer>>(StringComparer.Ordinal);

        ApplyStyleLayer(layers, _defaultDefinition!.Style, "definition-default", _defaultDefinition.Id, null);

        if (node.Parent is not null)
        {
            // 继承只沿可继承属性；Origin 继续指向祖先真正的胜出层，避免解释停在“父节点”。
            foreach (var entry in BuildProvenance(node.Parent))
            {
                if (!StylePropertySlotsByName.TryGetValue(entry.Property, out var slot) || !slot.Inheritable)
                {
                    continue;
                }

                // 继承层继续把 Origin 指向祖先真正的胜出层，避免解释停在“父节点”。
                var winner = entry.Chain[^1];
                var origin = winner.Layer == "inherited"
                    ? winner.Origin ?? "inherited"
                    : winner.Layer;
                Push(layers, entry.Property, entry.Value, "inherited", null, node.Parent.Id, origin);
            }
        }

        if (_typeStyles!.TryGetValue(node.Type, out var typeStyle))
        {
            ApplyStyleDefinitionProvenance(layers, typeStyle, "type-definition");
        }

        var kind = ElementKind.Resolve(node);
        if (kind is not null
            && _kindStyles!.TryGetValue(node.Type, out var kindStyles)
            && kindStyles.TryGetValue(kind, out var kindStyle))
        {
            ApplyStyleDefinitionProvenance(layers, kindStyle, "kind-definition");
        }

        if (!string.IsNullOrWhiteSpace(node.StyleId))
        {
            ApplyReferencedStyleProvenance(
                layers,
                node.StyleId,
                "named-style",
                new HashSet<string>(StringComparer.Ordinal));
        }

        ApplyStyleLayer(layers, node.LocalStyle, "node-local-style", null, node.Id);

        return BuildProvenanceResult(layers);
    }

    private static void ApplyStyleLayer(
        IDictionary<string, List<StylePropertyLayer>> layers,
        NodeStyle style,
        string layer,
        string? definitionId,
        string? nodeId)
    {
        foreach (var slot in StylePropertySlots)
        {
            var value = slot.Read(style);
            if (value is not null)
            {
                Push(layers, slot.Name, value, layer, definitionId, nodeId);
            }
        }
    }

    private void ApplyStyleDefinitionProvenance(
        IDictionary<string, List<StylePropertyLayer>> layers,
        StyleDefinition definition,
        string layer)
    {
        ApplyStyleDefinitionProvenanceCore(
            layers,
            definition,
            layer,
            new HashSet<string>(StringComparer.Ordinal));
    }

    private void ApplyStyleDefinitionProvenanceCore(
        IDictionary<string, List<StylePropertyLayer>> layers,
        StyleDefinition definition,
        string layer,
        HashSet<string> visited)
    {
        if (!string.IsNullOrWhiteSpace(definition.ParentStyleId))
        {
            ApplyReferencedStyleProvenance(layers, definition.ParentStyleId!, layer, visited);
        }

        ApplyStyleLayer(layers, definition.Style, layer, definition.Id, null);
    }

    private void ApplyReferencedStyleProvenance(
        IDictionary<string, List<StylePropertyLayer>> layers,
        string styleId,
        string layer,
        HashSet<string> visited)
    {
        if (!visited.Add(styleId))
            throw new StyleResolutionException($"Style inheritance cycle detected at '{styleId}'.");

        if (!_stylesById!.TryGetValue(styleId, out var definition))
            return;

        ApplyStyleDefinitionProvenanceCore(layers, definition, layer, visited);
    }

    private static void Push(
        IDictionary<string, List<StylePropertyLayer>> layers,
        string property,
        string value,
        string layer,
        string? definitionId,
        string? nodeId,
        string? origin = null)
    {
        if (!layers.TryGetValue(property, out var chain))
        {
            chain = [];
            layers[property] = chain;
        }
        else if (chain.Count > 0 && chain[^1].OverwrittenBy is null)
        {
            // 后一层写下同属性即覆盖前一层：覆盖关系在这里定死，不靠调用方反推。
            chain[^1] = chain[^1] with { OverwrittenBy = layer };
        }

        chain.Add(new StylePropertyLayer(layer, value, definitionId, nodeId, null, origin));
    }

    private static IReadOnlyList<StylePropertyProvenance> BuildProvenanceResult(
        IReadOnlyDictionary<string, List<StylePropertyLayer>> layers)
    {
        var result = new List<StylePropertyProvenance>(layers.Count);
        foreach (var slot in StylePropertySlots)
        {
            if (!layers.TryGetValue(slot.Name, out var chain) || chain.Count == 0)
            {
                continue;
            }

            var winner = chain[^1];
            result.Add(new StylePropertyProvenance(slot.Name, winner.Value, winner.Layer, chain.ToArray()));
        }

        return result;
    }

    private sealed record StylePropertySlot(string Name, Func<NodeStyle, string?> Read, bool Inheritable);

    private static readonly StylePropertySlot[] StylePropertySlots =
    [
        new("font-family", style => style.FontFamily, Inheritable: true),
        new("font-size", style => FormatNumber(style.FontSize), Inheritable: true),
        new("foreground-color", style => style.ForegroundColor, Inheritable: true),
        new("background-color", style => style.BackgroundColor, Inheritable: false),
        new("bold", style => FormatBool(style.Bold), Inheritable: true),
        new("italic", style => FormatBool(style.Italic), Inheritable: true),
        new("line-height", style => FormatNumber(style.LineHeight), Inheritable: true),
        new("text-align", style => style.TextAlign, Inheritable: true),
        new("vertical-align", style => style.VerticalAlign, Inheritable: true),
        new("margin", style => FormatThickness(style.Margin), Inheritable: false),
        new("padding", style => FormatThickness(style.Padding), Inheritable: false),
        new("border-color", style => style.BorderColor, Inheritable: false),
        new("border-width", style => FormatNumber(style.BorderWidth), Inheritable: false),
        new("border-radius", style => FormatNumber(style.BorderRadius), Inheritable: false),
        new("width", style => FormatNumber(style.Width), Inheritable: false),
        new("gap", style => FormatNumber(style.Gap), Inheritable: false),
        new("custom-css", style => style.CustomCss, Inheritable: false)
    ];

    private static readonly Dictionary<string, StylePropertySlot> StylePropertySlotsByName =
        StylePropertySlots.ToDictionary(slot => slot.Name, StringComparer.Ordinal);

    private static string? FormatNumber(double? value) =>
        value?.ToString("0.###", CultureInfo.InvariantCulture);

    private static string? FormatBool(bool? value) =>
        value is null ? null : value.Value ? "true" : "false";

    private static string? FormatThickness(ThicknessValue? value) =>
        value is null
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{value.Left},{value.Top},{value.Right},{value.Bottom}");
}
