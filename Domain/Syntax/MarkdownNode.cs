using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

public sealed class MarkdownNode
{
    public string Id { get; set; }
    public NodeType Type { get; set; }
    public NodeCategory Category { get; set; }
    public string Text { get; set; } = string.Empty;
    public string RawMarkdown { get; set; } = string.Empty;
    public MarkdownNode? Parent { get; internal set; }
    public List<MarkdownNode> Children { get; } = [];
    public int? Level { get; set; }
    public SourceRange Range { get; set; } = SourceRange.Empty();
    public string? StyleId { get; set; }
    public bool IsTableHeader { get; set; }
    public NodeStyle LocalStyle { get; set; }
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);
    public bool IsSynthetic { get; set; }

    /// <summary>
    /// 复用自上一棵已发布树的共享节点在新树构建期不覆写 Parent，
    /// “共享子→新父”边先记在此账本（仅挂在新树根上，外部不可达），
    /// 提交换根时在文档锁内统一切换。
    /// </summary>
    internal List<(MarkdownNode Child, MarkdownNode NewParent)>? PendingParentLinks { get; set; }

    public MarkdownNode()
    {
        Id = Guid.NewGuid().ToString("N");
        LocalStyle = new NodeStyle();
    }

    // 克隆热路径：Id/LocalStyle 由调用方指定，避免额外分配。
    internal MarkdownNode(string id, NodeStyle localStyle)
    {
        Id = id;
        LocalStyle = localStyle;
    }

    public void AddChild(MarkdownNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        EnsureCanAdopt(child);

        child.Parent?.RemoveChild(child);
        child.Parent = this;
        Children.Add(child);
    }

    /// <summary>
    /// 挂接新建且尚未发布的节点（新 Section 壳、探测块、克隆块）：
    /// 只加入 Children、不从旧父摘除；同一轮构建中每个节点只挂接一次。
    /// 复用自已发布树的共享节点用 <see cref="AttachSharedChildDeferred"/>。
    /// </summary>
    internal void AttachChildFromPriorTree(MarkdownNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        EnsureCanAdopt(child);

        child.Parent = this;
        Children.Add(child);
    }

    /// <summary>
    /// 延迟挂接复用自已发布树的共享节点：构建期不覆写 Parent，
    /// 只加入新父 Children 并记入 pending，提交换根时统一切换；不从旧父摘除。
    /// </summary>
    internal void AttachSharedChildDeferred(
        MarkdownNode child,
        List<(MarkdownNode Child, MarkdownNode NewParent)> pending)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(pending);
        EnsureCanAdopt(child);

        Children.Add(child);
        pending.Add((child, this));
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

    public MarkdownNode? FindById(string id)
    {
        var pending = new List<MarkdownNode> { this };
        while (pending.Count > 0)
        {
            int lastIndex = pending.Count - 1;
            var current = pending[lastIndex];
            pending.RemoveAt(lastIndex);

            if (string.Equals(current.Id, id, StringComparison.Ordinal))
                return current;

            for (int i = current.Children.Count - 1; i >= 0; i--)
                pending.Add(current.Children[i]);
        }

        return null;
    }

    private void EnsureCanAdopt(MarkdownNode child)
    {
        if (ReferenceEquals(child, this))
            throw new InvalidOperationException(
                "A node cannot contain itself.");

        if (IsDescendantOf(child))
            throw new InvalidOperationException(
                "A node cannot contain one of its ancestors.");
    }

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
