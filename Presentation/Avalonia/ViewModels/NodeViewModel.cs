using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using System.Collections.ObjectModel;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.ViewModels;

public sealed class NodeViewModel
{
    public MarkdownNode Node { get; }
    public string Id => Node.Id;
    public NodeType Type => Node.Type;
    public string Text => Node.Text;
    public ObservableCollection<NodeViewModel> Children { get; } = new();

    public NodeViewModel(MarkdownNode node)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
        foreach (var child in node.Children)
            Children.Add(new NodeViewModel(child));
    }
}