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

    public MarkdownNode Node => _node;

    public string Id => Node.Id;
    public NodeType Type => Node.Type;
    public string Text => Node.Text;
    public int? Level => Node.Level;
    public string? StyleId => Node.StyleId;
    public string RawMarkdown => Node.RawMarkdown;
    public NodeStyle LocalStyle => Node.LocalStyle;

    public ObservableCollection<NodeViewModel> Children { get; } = new();

    public event Action<MarkdownNode, NodeStyle>? StyleCommitRequested;

    public NodeViewModel(MarkdownNode node, Action<MarkdownNode, NodeStyle>? onStyleCommitted = null)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _onStyleCommitted = onStyleCommitted;
        StyleCommitRequested += onStyleCommitted;
        BuildChildrenTree(node);
    }

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
        OnPropertyChanged(nameof(Bold));
        OnPropertyChanged(nameof(Italic));
        OnPropertyChanged(nameof(ForegroundColor));
        OnPropertyChanged(nameof(BackgroundColor));
        OnPropertyChanged(nameof(BorderWidth));
        OnPropertyChanged(nameof(BorderColor));
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
            Children.Add(new NodeViewModel(child, _onStyleCommitted));
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
                desired.Add(new NodeViewModel(child, _onStyleCommitted));
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
            && Equals(a.Margin, b.Margin)
            && Equals(a.Padding, b.Padding)
            && string.Equals(a.BorderColor, b.BorderColor, StringComparison.Ordinal)
            && Nullable.Equals(a.BorderWidth, b.BorderWidth)
            && string.Equals(a.CustomCss, b.CustomCss, StringComparison.Ordinal);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public event PropertyChangedEventHandler? PropertyChanged;
}
