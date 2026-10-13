using System;
using System.Collections.Generic;
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
        var stylesById = new Dictionary<string, StyleDefinition>(StringComparer.Ordinal);

        var styles = styleSheet.Styles;
        for (int i = 0; i < styles.Count; i++)
        {
            var definition = styles[i];
            stylesById.TryAdd(definition.Id, definition);

            if (!string.Equals(definition.Id, "default", StringComparison.Ordinal)
                && definition.AppliesTo.HasValue)
            {
                typeStyles.TryAdd(definition.AppliesTo.GetValueOrDefault(), definition);
            }
        }

        _indexedStyleSheet = styleSheet;
        _defaultDefinition = defaultDefinition;
        _typeStyles = typeStyles;
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
}
