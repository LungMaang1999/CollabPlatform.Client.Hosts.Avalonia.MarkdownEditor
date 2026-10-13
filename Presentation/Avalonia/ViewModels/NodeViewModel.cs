using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.ViewModels;

public sealed class NodeViewModel : INotifyPropertyChanged
{
    private MarkdownNode _node;
    private readonly Action<MarkdownNode, NodeStyle>? _onStyleCommitted;
    private readonly Action<MarkdownNode, string, string?>? _onAttributeCommitted;

    public MarkdownNode Node => _node;

    public string Id => Node.Id;
    public NodeType Type => Node.Type;
    public int? Level => Node.Level;
    public string? StyleId => Node.StyleId;
    public string RawMarkdown => Node.RawMarkdown;
    public NodeStyle LocalStyle => Node.LocalStyle;

    /// <summary>
    /// 大纲树显示文本：节点自身 Text 为空时拼接子节点 DisplayText 摘要
    /// （" · " 分隔，最多 3 个）。
    /// </summary>
    public string DisplayText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Node.Text))
                return Node.Text;

            var parts = new List<string>();
            foreach (var child in Children)
            {
                var dt = child.DisplayText;
                if (!string.IsNullOrWhiteSpace(dt))
                {
                    parts.Add(dt);
                    if (parts.Count >= 3)
                        break;
                }
            }

            return parts.Count > 0
                ? string.Join(" · ", parts)
                : Node.Text;
        }
    }

    /// <summary>兼容旧绑定：Text 仍返回原始 AST 文本。</summary>
    public string Text => Node.Text;

    public ObservableCollection<NodeViewModel> Children { get; } = new();

    /// <summary>
    /// 大纲树用子节点视图（仅展示层过滤，不影响 AST 结构）：Section/Document/
    /// FootnoteGroup 纯容器拼接不显示自身；标题行下的 Text/换行子行与标题重复故隐藏。
    /// </summary>
    public IEnumerable<NodeViewModel> BlockChildren
    {
        get
        {
            foreach (var child in Children)
            {
                if (child.Node.Type is NodeType.Section
                    or NodeType.Document
                    or NodeType.FootnoteGroup)
                {
                    foreach (var descendant in child.BlockChildren)
                    {
                        yield return descendant;
                    }

                    continue;
                }

                // 标题下的文本/换行子行与标题重复
                if (Node.Type == NodeType.Heading &&
                    child.Node.Type is NodeType.Text or NodeType.LineBreak)
                {
                    continue;
                }

                yield return child;
            }
        }
    }

    private bool _isCurrent;

    /// <summary>编辑器光标当前落点对应的树节点（大纲"当前位置"高亮）。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            OnPropertyChanged();
        }
    }

    public event Action<MarkdownNode, NodeStyle>? StyleCommitRequested;

    /// <summary>属性面板提交块参数（如图标）时触发，由编辑器包装成可撤销命令。</summary>
    public event Action<MarkdownNode, string, string?>? AttributeCommitRequested;

    public NodeViewModel(
        MarkdownNode node,
        Action<MarkdownNode, NodeStyle>? onStyleCommitted = null,
        Action<MarkdownNode, string, string?>? onAttributeCommitted = null)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _onStyleCommitted = onStyleCommitted;
        _onAttributeCommitted = onAttributeCommitted;
        StyleCommitRequested += onStyleCommitted;
        AttributeCommitRequested += onAttributeCommitted;

        // 必须在 BuildChildrenTree 之前订阅：构建期加入的子节点也要挂上
        // 冒泡监听。子树的增删/同步会改变 BlockChildren 投影与 DisplayText，
        // TreeView 需收到通知才能刷新展开箭头与子项（如古诗按行细分）。
        Children.CollectionChanged += OnChildrenCollectionChanged;
        BuildChildrenTree(node);
    }

    private void OnChildrenCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (NodeViewModel child in e.OldItems)
            {
                child.PropertyChanged -= OnChildPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (NodeViewModel child in e.NewItems)
            {
                child.PropertyChanged += OnChildPropertyChanged;
            }
        }

        RaiseOutlineProjectionChanged();
    }

    private void OnChildPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // BlockChildren 会展开纯容器（Section/Document/FootnoteGroup），
        // 这些子节点的投影变化等价于本节点投影变化；DisplayText 同理
        // （容器摘要来自子节点 DisplayText）。
        if (e.PropertyName is nameof(BlockChildren) or nameof(DisplayText))
        {
            RaiseOutlineProjectionChanged();
        }
    }

    private void RaiseOutlineProjectionChanged()
    {
        OnPropertyChanged(nameof(BlockChildren));
        OnPropertyChanged(nameof(DisplayText));
    }

    /// <summary>读取节点属性（解析派生或用户自定义），不存在返回 null。</summary>
    public string? GetAttribute(string key) =>
        Node.Attributes.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// 提交一次属性修改：value 为 null 表示删除该属性。
    /// 仅负责唤起命令管线，模型本身由命令修改，避免绕过撤销栈。
    /// </summary>
    public void SetAttribute(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        AttributeCommitRequested?.Invoke(Node, key, value);
    }

    /// <summary>外部（撤销/重做等）改动属性后通知面板回读。</summary>
    public void RaiseAttributesChanged() => OnPropertyChanged("Attributes");

    public string? FontFamily
    {
        get => Node.LocalStyle.FontFamily;
        set
        {
            if (string.Equals(Node.LocalStyle.FontFamily, value, StringComparison.Ordinal)) return;
            CommitStyle(style => style.FontFamily = value);
        }
    }

    public decimal? FontSize
    {
        get => Node.LocalStyle.FontSize.HasValue ? (decimal)Node.LocalStyle.FontSize.Value : null;
        set => CommitStyle(style =>
        {
            var targetValue = value.HasValue ? (double)value.Value : (double?)null;
            if (Node.LocalStyle.FontSize == targetValue) return false;
            style.FontSize = targetValue;
            return true;
        });
    }

    public decimal? LineHeight
    {
        get => Node.LocalStyle.LineHeight.HasValue ? (decimal)Node.LocalStyle.LineHeight.Value : null;
        set => CommitStyle(style =>
        {
            var targetValue = value.HasValue ? (double)value.Value : (double?)null;
            if (Node.LocalStyle.LineHeight == targetValue) return false;
            style.LineHeight = targetValue;
            return true;
        });
    }

    public string? TextAlign
    {
        get => Node.LocalStyle.TextAlign;
        set
        {
            if (string.Equals(Node.LocalStyle.TextAlign, value, StringComparison.Ordinal)) return;
            CommitStyle(style => style.TextAlign = value);
        }
    }

    public string? VerticalAlign
    {
        get => Node.LocalStyle.VerticalAlign;
        set
        {
            if (string.Equals(Node.LocalStyle.VerticalAlign, value, StringComparison.Ordinal)) return;
            CommitStyle(style => style.VerticalAlign = value);
        }
    }

    public bool? Bold
    {
        get => Node.LocalStyle.Bold;
        set
        {
            if (Node.LocalStyle.Bold == value) return;
            CommitStyle(style => style.Bold = value);
        }
    }

    public bool? Italic
    {
        get => Node.LocalStyle.Italic;
        set
        {
            if (Node.LocalStyle.Italic == value) return;
            CommitStyle(style => style.Italic = value);
        }
    }

    public string? ForegroundColor
    {
        get => Node.LocalStyle.ForegroundColor;
        set
        {
            if (string.Equals(Node.LocalStyle.ForegroundColor, value, StringComparison.Ordinal)) return;
            CommitStyle(style => style.ForegroundColor = value);
        }
    }

    public string? BackgroundColor
    {
        get => Node.LocalStyle.BackgroundColor;
        set
        {
            if (string.Equals(Node.LocalStyle.BackgroundColor, value, StringComparison.Ordinal)) return;
            CommitStyle(style => style.BackgroundColor = value);
        }
    }

    public decimal? BorderWidth
    {
        get => Node.LocalStyle.BorderWidth.HasValue ? (decimal)Node.LocalStyle.BorderWidth.Value : null;
        set => CommitStyle(style =>
        {
            var targetValue = value.HasValue ? (double)value.Value : (double?)null;
            if (Node.LocalStyle.BorderWidth == targetValue) return false;
            style.BorderWidth = targetValue;
            return true;
        });
    }

    public string? BorderColor
    {
        get => Node.LocalStyle.BorderColor;
        set
        {
            if (string.Equals(Node.LocalStyle.BorderColor, value, StringComparison.Ordinal)) return;
            CommitStyle(style => style.BorderColor = value);
        }
    }

    public decimal? BorderRadius
    {
        get => Node.LocalStyle.BorderRadius.HasValue ? (decimal)Node.LocalStyle.BorderRadius.Value : null;
        set => CommitStyle(style =>
        {
            var targetValue = value.HasValue ? (double)value.Value : (double?)null;
            if (Node.LocalStyle.BorderRadius == targetValue) return false;
            style.BorderRadius = targetValue;
            return true;
        });
    }

    public decimal? Width
    {
        get => Node.LocalStyle.Width.HasValue ? (decimal)Node.LocalStyle.Width.Value : null;
        set => CommitStyle(style =>
        {
            var targetValue = value.HasValue ? (double)value.Value : (double?)null;
            if (Node.LocalStyle.Width == targetValue) return false;
            style.Width = targetValue;
            return true;
        });
    }

    public decimal? Gap
    {
        get => Node.LocalStyle.Gap.HasValue ? (decimal)Node.LocalStyle.Gap.Value : null;
        set => CommitStyle(style =>
        {
            var targetValue = value.HasValue ? (double)value.Value : (double?)null;
            if (Node.LocalStyle.Gap == targetValue) return false;
            style.Gap = targetValue;
            return true;
        });
    }

    public string? Margin
    {
        get => ThicknessStringConverter.Format(Node.LocalStyle.Margin);
        set
        {
            var parsed = ThicknessStringConverter.Parse(value);
            if (Equals(Node.LocalStyle.Margin, parsed)) return;
            CommitStyle(style => style.Margin = parsed);
        }
    }

    public string? Padding
    {
        get => ThicknessStringConverter.Format(Node.LocalStyle.Padding);
        set
        {
            var parsed = ThicknessStringConverter.Parse(value);
            if (Equals(Node.LocalStyle.Padding, parsed)) return;
            CommitStyle(style => style.Padding = parsed);
        }
    }

    public string? CustomCss
    {
        get => Node.LocalStyle.CustomCss;
        set
        {
            if (string.Equals(Node.LocalStyle.CustomCss, value, StringComparison.Ordinal)) return;
            CommitStyle(style => style.CustomCss = value);
        }
    }

    public void SyncTo(MarkdownNode newNode)
    {
        ArgumentNullException.ThrowIfNull(newNode);
        if (ReferenceEquals(_node, newNode)) return;

        var oldNode = _node;
        _node = newNode;
        RaiseChangedNodeProperties(oldNode, newNode);
        ReconcileChildren(newNode);
    }

    internal void RaiseStylePropertiesChanged()
    {
        OnPropertyChanged(nameof(LocalStyle));
        OnPropertyChanged(nameof(FontFamily));
        OnPropertyChanged(nameof(FontSize));
        OnPropertyChanged(nameof(LineHeight));
        OnPropertyChanged(nameof(TextAlign));
        OnPropertyChanged(nameof(VerticalAlign));
        OnPropertyChanged(nameof(Bold));
        OnPropertyChanged(nameof(Italic));
        OnPropertyChanged(nameof(ForegroundColor));
        OnPropertyChanged(nameof(BackgroundColor));
        OnPropertyChanged(nameof(BorderWidth));
        OnPropertyChanged(nameof(BorderColor));
        OnPropertyChanged(nameof(BorderRadius));
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Gap));
        OnPropertyChanged(nameof(Margin));
        OnPropertyChanged(nameof(Padding));
        OnPropertyChanged(nameof(CustomCss));
    }

    private void CommitStyle(Func<NodeStyle, bool> mutate)
    {
        var candidate = Node.LocalStyle.Clone();
        if (!mutate(candidate)) return;

        StyleCommitRequested?.Invoke(Node, candidate);

        RaiseStylePropertiesChanged();
    }

    private void CommitStyle(Action<NodeStyle> mutate)
    {
        var candidate = Node.LocalStyle.Clone();
        mutate(candidate);
        StyleCommitRequested?.Invoke(Node, candidate);
        RaiseStylePropertiesChanged();
    }

    private void BuildChildrenTree(MarkdownNode node)
    {
        Children.Clear();
        foreach (var child in node.Children)
        {
            Children.Add(new NodeViewModel(child, _onStyleCommitted, _onAttributeCommitted));
        }
    }

    private void ReconcileChildren(MarkdownNode node)
    {
        // 热路径：子节点数量与 Id 顺序一致时（按键输入影响一两个块，
        // 其余绝大多数层级都属于此情形）原地同步，不分配字典/列表、
        // 不触发 ObservableCollection 增删移动。
        if (Children.Count == node.Children.Count)
        {
            var sameOrder = true;
            for (var i = 0; i < Children.Count; i++)
            {
                if (!string.Equals(Children[i].Id, node.Children[i].Id, StringComparison.Ordinal))
                {
                    sameOrder = false;
                    break;
                }
            }

            if (sameOrder)
            {
                for (var i = 0; i < Children.Count; i++)
                {
                    Children[i].SyncTo(node.Children[i]);
                }
                return;
            }
        }

        var existingById = new Dictionary<string, NodeViewModel>(StringComparer.Ordinal);
        foreach (var child in Children)
        {
            existingById[child.Id] = child;
        }

        var desired = new List<NodeViewModel>(node.Children.Count);
        foreach (var child in node.Children)
        {
            if (existingById.Remove(child.Id, out var childVm))
            {
                childVm.SyncTo(child);
                desired.Add(childVm);
            }
            else
            {
                desired.Add(new NodeViewModel(child, _onStyleCommitted, _onAttributeCommitted));
            }
        }

        foreach (var stale in existingById.Values)
        {
            Children.Remove(stale);
        }

        for (int i = 0; i < desired.Count; i++)
        {
            if (i < Children.Count)
            {
                if (!ReferenceEquals(Children[i], desired[i]))
                {
                    int currentIndex = -1;
                    for (int j = i + 1; j < Children.Count; j++)
                    {
                        if (ReferenceEquals(Children[j], desired[i]))
                        {
                            currentIndex = j;
                            break;
                        }
                    }

                    if (currentIndex >= 0)
                    {
                        Children.Move(currentIndex, i);
                    }
                    else
                    {
                        Children.Insert(i, desired[i]);
                    }
                }
            }
            else
            {
                Children.Add(desired[i]);
            }
        }

        while (Children.Count > desired.Count)
        {
            Children.RemoveAt(Children.Count - 1);
        }
    }

    // AST 每次编辑都是整树重建（匹配器只把 Id/样式拷到新实例），
    // 这里逐属性比对，只通知真正变化的属性：大文档协调时可把
    // PropertyChanged 事件量从“节点数 × 14”降到实际变化的属性数。
    private void RaiseChangedNodeProperties(MarkdownNode oldNode, MarkdownNode newNode)
    {
        OnPropertyChanged(nameof(Node));

        if (!string.Equals(oldNode.Id, newNode.Id, StringComparison.Ordinal))
            OnPropertyChanged(nameof(Id));
        if (oldNode.Type != newNode.Type)
            OnPropertyChanged(nameof(Type));
        if (!string.Equals(oldNode.Text, newNode.Text, StringComparison.Ordinal))
            OnPropertyChanged(nameof(Text));
        if (!Nullable.Equals(oldNode.Level, newNode.Level))
            OnPropertyChanged(nameof(Level));
        if (!string.Equals(oldNode.StyleId, newNode.StyleId, StringComparison.Ordinal))
            OnPropertyChanged(nameof(StyleId));
        if (!string.Equals(oldNode.RawMarkdown, newNode.RawMarkdown, StringComparison.Ordinal))
            OnPropertyChanged(nameof(RawMarkdown));
        if (!StyleEquals(oldNode.LocalStyle, newNode.LocalStyle))
            RaiseStylePropertiesChanged();
    }

    private static bool StyleEquals(NodeStyle? a, NodeStyle? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;

        return string.Equals(a.FontFamily, b.FontFamily, StringComparison.Ordinal)
            && Nullable.Equals(a.FontSize, b.FontSize)
            && string.Equals(a.ForegroundColor, b.ForegroundColor, StringComparison.Ordinal)
            && string.Equals(a.BackgroundColor, b.BackgroundColor, StringComparison.Ordinal)
            && Nullable.Equals(a.Bold, b.Bold)
            && Nullable.Equals(a.Italic, b.Italic)
            && Nullable.Equals(a.LineHeight, b.LineHeight)
            && string.Equals(a.TextAlign, b.TextAlign, StringComparison.Ordinal)
            && string.Equals(a.VerticalAlign, b.VerticalAlign, StringComparison.Ordinal)
            && Equals(a.Margin, b.Margin)
            && Equals(a.Padding, b.Padding)
            && string.Equals(a.BorderColor, b.BorderColor, StringComparison.Ordinal)
            && Nullable.Equals(a.BorderWidth, b.BorderWidth)
            && Nullable.Equals(a.BorderRadius, b.BorderRadius)
            && Nullable.Equals(a.Width, b.Width)
            && Nullable.Equals(a.Gap, b.Gap)
            && string.Equals(a.CustomCss, b.CustomCss, StringComparison.Ordinal);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public event PropertyChangedEventHandler? PropertyChanged;
}
