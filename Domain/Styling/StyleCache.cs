using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

/// <summary>
/// Provides thread-safe caching of resolved node styles.
/// </summary>
public sealed class StyleCache
{
    private readonly Dictionary<string, ComputedStyle> _items = new(StringComparer.Ordinal);

    public bool TryGet(string nodeId, out ComputedStyle style) =>
        _items.TryGetValue(nodeId, out style!);

    public void Set(string nodeId, ComputedStyle style)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(style);
        _items[nodeId] = new ComputedStyle(style);
    }

    public void Invalidate(MarkdownNode? node = null)
    {
        if (node is null)
        {
            Clear();
            return;
        }

        _items.Remove(node.Id);
        foreach (var descendant in node.Descendants())
            _items.Remove(descendant.Id);
    }

    public void Clear() => _items.Clear();
}