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

            result.Apply(_defaultDefinition!.Style);

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

            ApplySectionAncestorsStyle(result, node.Parent);

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

    private static void ApplySectionAncestorsStyle(ComputedStyle target, MarkdownNode? parent)
    {
        if (parent is null) return;

        if (parent.Parent is not null)
        {
            ApplySectionAncestorsStyle(target, parent.Parent);
        }

        if (parent.Type == NodeType.Section)
        {
            target.Apply(parent.LocalStyle);
        }
    }
}
