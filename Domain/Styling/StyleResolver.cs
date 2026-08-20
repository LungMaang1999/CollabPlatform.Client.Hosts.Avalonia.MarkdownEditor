using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

/// <summary>
/// 高性能样式级联解析器（线程安全与零堆内存分配祖先回溯）
/// </summary>
public sealed class StyleResolver : IStyleResolver
{
    private readonly MarkdownDocument _document;
    private readonly StyleCache _cache;
    private long _cacheRevision = -1;
    private readonly object _syncLock = new();

    public StyleResolver(MarkdownDocument document, StyleCache? cache = null)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _cache = cache ?? new StyleCache();
    }

    public ComputedStyle Resolve(MarkdownNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        lock (_syncLock)
        {
            EnsureCacheIsCurrent();

            if (_cache.TryGet(node.Id, out var cached))
                return cached;

            var result = new ComputedStyle();

            // 1. Default Style
            var defaultStyle = _document.StyleSheet.GetOrCreateDefault();
            result.Apply(defaultStyle.Style);

            // 2. Node Type Style
            ApplyTypeStyle(result, node.Type);

            // 3. Referenced Style
            if (!string.IsNullOrWhiteSpace(node.StyleId))
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                ApplyReferencedStyle(result, node.StyleId, visited);
            }

            // 4. Section Local Styles (由外向内递归回溯，零堆分配)
            ApplySectionAncestorsStyle(result, node.Parent);

            // 5. Node Local Style
            result.Apply(node.LocalStyle);

            _cache.Set(node.Id, result);
            return result;
        }
    }

    public void Invalidate(MarkdownNode? node = null)
    {
        lock (_syncLock)
        {
            _cache.Invalidate(node);
            _cacheRevision = _document.Revision;
        }
    }

    public void Clear()
    {
        lock (_syncLock)
        {
            _cache.Clear();
            _cacheRevision = _document.Revision;
        }
    }

    private void EnsureCacheIsCurrent()
    {
        if (_cacheRevision == _document.Revision)
            return;

        _cache.Clear();
        _cacheRevision = _document.Revision;
    }

    private void ApplyTypeStyle(ComputedStyle target, NodeType nodeType)
    {
        var styles = _document.StyleSheet.Styles;
        for (int i = 0; i < styles.Count; i++)
        {
            var style = styles[i];
            if (!string.Equals(style.Id, "default", StringComparison.Ordinal) && style.AppliesTo == nodeType)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                ApplyStyleDefinition(target, style, visited);
                break;
            }
        }
    }

    private void ApplyReferencedStyle(ComputedStyle target, string styleId, HashSet<string> visited)
    {
        if (!visited.Add(styleId))
            throw new StyleResolutionException($"Style inheritance cycle detected at '{styleId}'.");

        var definition = _document.StyleSheet.FindById(styleId);
        if (definition is null) return;

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

        // 递归传递至最顶层祖先
        if (parent.Parent is not null)
        {
            ApplySectionAncestorsStyle(target, parent.Parent);
        }

        // 归程执行：由外向内覆盖
        if (parent.Type == NodeType.Section)
        {
            target.Apply(parent.LocalStyle);
        }
    }
}