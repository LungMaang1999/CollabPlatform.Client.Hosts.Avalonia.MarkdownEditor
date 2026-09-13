using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.ViewModels;

/// <summary>
/// Markdown 语法树节点的视图模型（优化闭环版）：
/// 负责处理 UI 双向数据绑定与样式格式转换，完整支持节点样式编辑双向通知联动。
/// </summary>
public sealed class NodeViewModel : INotifyPropertyChanged
{
    private MarkdownNode _node;
    private readonly Action? _onStyleMutated;

    public MarkdownNode Node
    {
        get => _node;
        private set
        {
            if (ReferenceEquals(_node, value)) return;
            _node = value;

            OnPropertyChanged(nameof(Node));
            OnPropertyChanged(nameof(Id));
            OnPropertyChanged(nameof(Type));
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(Level));
            OnPropertyChanged(nameof(StyleId));
            OnPropertyChanged(nameof(RawMarkdown));
            OnPropertyChanged(nameof(LocalStyle));

            // 样式属性变更通知
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
    }

    public string Id => Node.Id;
    public NodeType Type => Node.Type;
    public string Text => Node.Text;
    public int? Level => Node.Level;
    public string? StyleId => Node.StyleId;
    public string RawMarkdown => Node.RawMarkdown;
    public NodeStyle LocalStyle => Node.LocalStyle;

    public ObservableCollection<NodeViewModel> Children { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public NodeViewModel(MarkdownNode node, Action? onStyleMutated = null)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _onStyleMutated = onStyleMutated;
        BuildChildrenTree(node);
    }

    #region 样式属性双向绑定封装（触发 _onStyleMutated 进而刷新主预览与修改状态）

    public string? FontFamily
    {
        get => Node.LocalStyle.FontFamily;
        set
        {
            if (string.Equals(Node.LocalStyle.FontFamily, value, StringComparison.Ordinal)) return;
            Node.LocalStyle.FontFamily = value;
            OnPropertyChanged();
            _onStyleMutated?.Invoke();
        }
    }

    public decimal? FontSize
    {
        get => Node.LocalStyle.FontSize.HasValue ? (decimal)Node.LocalStyle.FontSize.Value : null;
        set
        {
            var targetValue = value.HasValue ? (double)value.Value : (double?)null;
            if (Node.LocalStyle.FontSize == targetValue) return;

            Node.LocalStyle.FontSize = targetValue;
            OnPropertyChanged();
            _onStyleMutated?.Invoke();
        }
    }

    public decimal? LineHeight
    {
        get => Node.LocalStyle.LineHeight.HasValue ? (decimal)Node.LocalStyle.LineHeight.Value : null;
        set
        {
            var targetValue = value.HasValue ? (double)value.Value : (double?)null;
            if (Node.LocalStyle.LineHeight == targetValue) return;

            Node.LocalStyle.LineHeight = targetValue;
            OnPropertyChanged();
            _onStyleMutated?.Invoke();
        }
    }

    public string? TextAlign
    {
        get => Node.LocalStyle.TextAlign;
        set
        {
            if (string.Equals(Node.LocalStyle.TextAlign, value, StringComparison.Ordinal)) return;
            Node.LocalStyle.TextAlign = value;
            OnPropertyChanged();
            _onStyleMutated?.Invoke();
        }
    }

    public bool? Bold
    {
        get => Node.LocalStyle.Bold;
        set
        {
            if (Node.LocalStyle.Bold == value) return;
            Node.LocalStyle.Bold = value;
            OnPropertyChanged();
            _onStyleMutated?.Invoke();
        }
    }

    public bool? Italic
    {
        get => Node.LocalStyle.Italic;
        set
        {
            if (Node.LocalStyle.Italic == value) return;
            Node.LocalStyle.Italic = value;
            OnPropertyChanged();
            _onStyleMutated?.Invoke();
        }
    }

    public string? ForegroundColor
    {
        get => Node.LocalStyle.ForegroundColor;
        set
        {
            if (string.Equals(Node.LocalStyle.ForegroundColor, value, StringComparison.Ordinal)) return;
            Node.LocalStyle.ForegroundColor = value;
            OnPropertyChanged();
            _onStyleMutated?.Invoke();
        }
    }

    public string? BackgroundColor
    {
        get => Node.LocalStyle.BackgroundColor;
        set
        {
            if (string.Equals(Node.LocalStyle.BackgroundColor, value, StringComparison.Ordinal)) return;
            Node.LocalStyle.BackgroundColor = value;
            OnPropertyChanged();
            _onStyleMutated?.Invoke();
        }
    }

    public decimal? BorderWidth
    {
        get => Node.LocalStyle.BorderWidth.HasValue ? (decimal)Node.LocalStyle.BorderWidth.Value : null;
        set
        {
            var targetValue = value.HasValue ? (double)value.Value : (double?)null;
            if (Node.LocalStyle.BorderWidth == targetValue) return;

            Node.LocalStyle.BorderWidth = targetValue;
            OnPropertyChanged();
            _onStyleMutated?.Invoke();
        }
    }

    public string? BorderColor
    {
        get => Node.LocalStyle.BorderColor;
        set
        {
            if (string.Equals(Node.LocalStyle.BorderColor, value, StringComparison.Ordinal)) return;
            Node.LocalStyle.BorderColor = value;
            OnPropertyChanged();
            _onStyleMutated?.Invoke();
        }
    }

    public string? Margin
    {
        get => Node.LocalStyle.Margin is { } m
            ? string.Create(CultureInfo.InvariantCulture, $"{m.Left},{m.Top},{m.Right},{m.Bottom}")
            : null;
        set
        {
            var parsed = ParseThickness(value);
            if (Equals(Node.LocalStyle.Margin, parsed)) return;

            Node.LocalStyle.Margin = parsed;
            OnPropertyChanged();
            _onStyleMutated?.Invoke();
        }
    }

    public string? Padding
    {
        get => Node.LocalStyle.Padding is { } p
            ? string.Create(CultureInfo.InvariantCulture, $"{p.Left},{p.Top},{p.Right},{p.Bottom}")
            : null;
        set
        {
            var parsed = ParseThickness(value);
            if (Equals(Node.LocalStyle.Padding, parsed)) return;

            Node.LocalStyle.Padding = parsed;
            OnPropertyChanged();
            _onStyleMutated?.Invoke();
        }
    }

    public string? CustomCss
    {
        get => Node.LocalStyle.CustomCss;
        set
        {
            if (string.Equals(Node.LocalStyle.CustomCss, value, StringComparison.Ordinal)) return;
            Node.LocalStyle.CustomCss = value;
            OnPropertyChanged();
            _onStyleMutated?.Invoke();
        }
    }

    #endregion

    public void UpdateInternalNode(MarkdownNode newNode)
    {
        ArgumentNullException.ThrowIfNull(newNode);

        Node = newNode;
        BuildChildrenTree(newNode);
    }

    private void BuildChildrenTree(MarkdownNode node)
    {
        Children.Clear();
        if (node.Children == null || node.Children.Count == 0) return;

        foreach (var child in node.Children)
        {
            Children.Add(new NodeViewModel(child, _onStyleMutated));
        }
    }

    private static ThicknessValue? ParseThickness(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        Span<double> values = stackalloc double[4];
        int count = 0;

        int start = 0;
        for (int i = 0; i <= span.Length; i++)
        {
            if (i == span.Length || span[i] == ',' || span[i] == ' ' || span[i] == ';')
            {
                if (i > start)
                {
                    ReadOnlySpan<char> token = span[start..i].Trim();
                    if (token.Length > 0)
                    {
                        if (count >= 4) return null;

                        // 剥除单位后缀（如 px, pt, em）
                        if (token.EndsWith("px", StringComparison.OrdinalIgnoreCase) ||
                            token.EndsWith("pt", StringComparison.OrdinalIgnoreCase) ||
                            token.EndsWith("em", StringComparison.OrdinalIgnoreCase))
                        {
                            token = token[..^2].Trim();
                        }

                        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedVal))
                        {
                            values[count++] = parsedVal;
                        }
                        else
                        {
                            return null;
                        }
                    }
                }
                start = i + 1;
            }
        }

        return count switch
        {
            1 => new ThicknessValue(values[0]),
            2 => new ThicknessValue(values[0], values[1]),
            4 => new ThicknessValue(values[0], values[1], values[2], values[3]),
            _ => null
        };
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}