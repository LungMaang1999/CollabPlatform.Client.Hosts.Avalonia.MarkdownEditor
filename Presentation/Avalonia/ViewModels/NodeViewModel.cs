using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.ViewModels;

public sealed class NodeViewModel : INotifyPropertyChanged
{
    private MarkdownNode _node;
    public MarkdownNode Node
    {
        get => _node;
        private set
        {
            if (ReferenceEquals(_node, value)) return;
            _node = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Id));
            OnPropertyChanged(nameof(Type));
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(Level));
            OnPropertyChanged(nameof(StyleId));
            OnPropertyChanged(nameof(RawMarkdown));
            OnPropertyChanged(nameof(LocalStyle));
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

    public NodeViewModel(MarkdownNode node)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
        foreach (var child in node.Children)
        {
            Children.Add(new NodeViewModel(child));
        }
    }

    #region 针对 NumericUpDown (decimal?) 与 double? 的安全兼容属性

    public decimal? FontSize
    {
        get => Node.LocalStyle.FontSize.HasValue ? (decimal)Node.LocalStyle.FontSize.Value : null;
        set
        {
            Node.LocalStyle.FontSize = value.HasValue ? (double)value.Value : null;
            OnPropertyChanged();
        }
    }

    public decimal? LineHeight
    {
        get => Node.LocalStyle.LineHeight.HasValue ? (decimal)Node.LocalStyle.LineHeight.Value : null;
        set
        {
            Node.LocalStyle.LineHeight = value.HasValue ? (double)value.Value : null;
            OnPropertyChanged();
        }
    }

    public decimal? BorderWidth
    {
        get => Node.LocalStyle.BorderWidth.HasValue ? (decimal)Node.LocalStyle.BorderWidth.Value : null;
        set
        {
            Node.LocalStyle.BorderWidth = value.HasValue ? (double)value.Value : null;
            OnPropertyChanged();
        }
    }

    #endregion

    #region 针对 Margin / Padding (ThicknessValue 与 string 文本转换)

    public string? Margin
    {
        get => Node.LocalStyle.Margin is { } m ? $"{m.Left},{m.Top},{m.Right},{m.Bottom}" : null;
        set
        {
            Node.LocalStyle.Margin = ParseThickness(value);
            OnPropertyChanged();
        }
    }

    public string? Padding
    {
        get => Node.LocalStyle.Padding is { } p ? $"{p.Left},{p.Top},{p.Right},{p.Bottom}" : null;
        set
        {
            Node.LocalStyle.Padding = ParseThickness(value);
            OnPropertyChanged();
        }
    }

    private static ThicknessValue? ParseThickness(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var uniform))
        {
            return new ThicknessValue(uniform);
        }
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            return new ThicknessValue(h, v);
        }
        if (parts.Length == 4 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var l) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var r) &&
            double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
        {
            return new ThicknessValue(l, t, r, b);
        }

        return null;
    }

    #endregion

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    public void UpdateInternalNode(MarkdownNode newNode)
    {
        Node = newNode;
    }
}