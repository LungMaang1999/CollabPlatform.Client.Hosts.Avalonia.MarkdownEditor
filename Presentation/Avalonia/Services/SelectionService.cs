using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.Services;

/// <summary>
/// Manages the currently selected AST node across UI views.
/// </summary>
public sealed class SelectionService
{
    private MarkdownNode? _selectedNode;
    public MarkdownNode? SelectedNode => _selectedNode;
    public event Action<MarkdownNode?>? SelectedNodeChanged;

    public void Select(MarkdownNode? node)
    {
        if (ReferenceEquals(_selectedNode, node)) return;
        _selectedNode = node;
        SelectedNodeChanged?.Invoke(node);
    }

    public void SelectById(MarkdownDocument document, string? nodeId)
    {
        ArgumentNullException.ThrowIfNull(document);
        Select(string.IsNullOrWhiteSpace(nodeId) ? null : document.FindNode(nodeId));
    }
}