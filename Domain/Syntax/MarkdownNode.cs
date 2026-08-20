using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using System.Collections.ObjectModel;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

/// <summary>
/// Represents an Abstract Syntax Tree (AST) node in a Markdown document.
/// </summary>
public sealed class MarkdownNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public NodeType Type { get; set; }
    public NodeCategory Category { get; set; }
    public string Text { get; set; } = string.Empty;
    public string RawMarkdown { get; set; } = string.Empty;
    public MarkdownNode? Parent { get; internal set; }
    public ObservableCollection<MarkdownNode> Children { get; } = new();
    public int? Level { get; set; }
    public SourceRange Range { get; set; } = SourceRange.Empty();
    public string? StyleId { get; set; }
    public bool IsTableHeader { get; set; }
    public NodeStyle LocalStyle { get; set; } = new();
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);
    public bool IsSynthetic { get; set; }

    public void AddChild(MarkdownNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        EnsureCanAdopt(child);

        child.Parent?.RemoveChild(child);
        child.Parent = this;
        Children.Add(child);
    }

    public void InsertChild(int index, MarkdownNode child)
    {
        ArgumentNullException.ThrowIfNull(child);

        if (index < 0 || index > Children.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        EnsureCanAdopt(child);

        child.Parent?.RemoveChild(child);
        child.Parent = this;
        Children.Insert(index, child);
    }

    public bool RemoveChild(MarkdownNode child)
    {
        if (!Children.Remove(child))
            return false;

        child.Parent = null;
        return true;
    }

    public IEnumerable<MarkdownNode> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;

            foreach (var descendant in child.Descendants())
                yield return descendant;
        }
    }

    public MarkdownNode? FindById(string id) =>
        string.Equals(Id, id, StringComparison.Ordinal)
            ? this
            : Descendants().FirstOrDefault(
                x => string.Equals(x.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Ensures that adding <paramref name="child"/> would not create an AST cycle.
    /// </summary>
    private void EnsureCanAdopt(MarkdownNode child)
    {
        if (ReferenceEquals(child, this))
            throw new InvalidOperationException(
                "A node cannot contain itself.");

        // 当前节点若已位于 child 的后代链中，则 child 是当前节点的祖先。
        // 将祖先挂到后代下会形成循环：
        //
        // root -> child -> grandchild
        // grandchild.AddChild(root)  // forbidden
        //
        if (IsDescendantOf(child))
            throw new InvalidOperationException(
                "A node cannot contain one of its ancestors.");
    }

    /// <summary>
    /// Determines whether the current node is a descendant of <paramref name="node"/>.
    /// </summary>
    private bool IsDescendantOf(MarkdownNode node)
    {
        for (var current = Parent; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, node))
                return true;
        }

        return false;
    }
}