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

    /// <summary>
    /// 当前生效的属性值（渲染层只读这里）。同一键的三种来源已经按
    /// <see cref="AttributeValueSource"/> 的优先级合并完毕。
    /// </summary>
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 生效值对应的来源。缺省（未记录）视为 <see cref="AttributeValueSource.Instance"/>，
    /// 兼容没有来源信息的旧树/旧文档包。
    /// </summary>
    public Dictionary<string, AttributeValueSource> AttributeSources { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 实例层：面板/命令写入的值。即使当前被源码值覆盖也保留在这里，
    /// 用于“删除源码值后回落到实例值”和“能看出实例值被覆盖”。
    /// 不参与渲染，渲染仍只读 <see cref="Attributes"/>。
    /// </summary>
    public Dictionary<string, string> InstanceAttributes { get; } = new(StringComparer.Ordinal);

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

    /// <summary>取生效值的来源；未记录来源的旧数据按实例层处理。</summary>
    public AttributeValueSource GetAttributeSource(string key) =>
        AttributeSources.TryGetValue(key, out var source) ? source : AttributeValueSource.Instance;

    /// <summary>写入源码内联值：源码层优先级最高，直接成为生效值。</summary>
    public void SetSourceAttribute(string key, string value)
    {
        Attributes[key] = value;
        AttributeSources[key] = AttributeValueSource.Source;
    }

    /// <summary>
    /// 写入“由解析器从语法结构推导”的属性（admonition、custom-kind、language、timeline-* 等）：
    /// 值进入生效属性，来源固定记 Source。解析代码新增派生属性必须走这里，
    /// 不要直接写 <see cref="Attributes"/>，否则展示层只能反过来猜哪些值来自源码。
    /// </summary>
    public void SetDerivedAttribute(string key, string value) => SetSourceAttribute(key, value);

    /// <summary>
    /// 写入实例层值并记录来源。源码已声明该键时只保留实例层（作为源码值被删除后的回落），
    /// 生效值仍由源码决定；否则实例值立即成为生效值。
    /// </summary>
    public void SetInstanceAttribute(string key, string value)
    {
        InstanceAttributes[key] = value;
        if (GetAttributeSource(key) == AttributeValueSource.Source)
        {
            return;
        }

        Attributes[key] = value;
        AttributeSources[key] = AttributeValueSource.Instance;
    }

    /// <summary>注入定义默认值：只在既无源码值也无实例值时生效。</summary>
    public void SetDefinitionDefaultAttribute(string key, string value)
    {
        if (Attributes.ContainsKey(key))
        {
            return;
        }

        Attributes[key] = value;
        AttributeSources[key] = AttributeValueSource.DefinitionDefault;
    }

    /// <summary>撤销/重做恢复生效值及其来源（value 为 null 表示该键不存在）。</summary>
    public void RestoreAttribute(string key, string? value, AttributeValueSource source)
    {
        if (value is null)
        {
            Attributes.Remove(key);
            AttributeSources.Remove(key);
            return;
        }

        Attributes[key] = value;
        AttributeSources[key] = source;
    }

    /// <summary>撤销/重做恢复实例层条目（value 为 null 表示该键原先没有实例值）。</summary>
    public void RestoreInstanceAttribute(string key, string? value)
    {
        if (value is null)
        {
            InstanceAttributes.Remove(key);
            return;
        }

        InstanceAttributes[key] = value;
    }

    /// <summary>删除实例层条目；生效值不变（是否回落默认由调用方决定）。</summary>
    public bool RemoveInstanceAttribute(string key) => InstanceAttributes.Remove(key);

    public bool RemoveAttribute(string key)
    {
        AttributeSources.Remove(key);
        return Attributes.Remove(key);
    }

    /// <summary>
    /// 实例层里存在、但被源码值盖住的回退值。用于向用户说明
    /// “面板设过的值没有被静默丢弃，只是当前让位于源码”。
    /// </summary>
    public bool TryGetOverriddenInstanceValue(string key, out string value)
    {
        value = string.Empty;
        if (!InstanceAttributes.TryGetValue(key, out var instance)
            || !Attributes.TryGetValue(key, out var effective))
        {
            return false;
        }

        if (GetAttributeSource(key) != AttributeValueSource.Source
            || string.Equals(instance, effective, StringComparison.Ordinal))
        {
            return false;
        }

        value = instance;
        return true;
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
