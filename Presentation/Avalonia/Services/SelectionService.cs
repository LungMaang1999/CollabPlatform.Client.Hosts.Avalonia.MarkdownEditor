using System;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.Services;

public sealed class SelectionService
{
    private readonly object _syncLock = new();
    private MarkdownNode? _selectedNode;

    public MarkdownNode? SelectedNode
    {
        get { lock (_syncLock) return _selectedNode; }
    }

    public event Action<MarkdownNode?>? SelectedNodeChanged;

    public void Select(MarkdownNode? node)
    {
        lock (_syncLock)
        {
            if (ReferenceEquals(_selectedNode, node)) return;
            _selectedNode = node;
        }

        SelectedNodeChanged?.Invoke(node);
    }

    public void SelectById(MarkdownDocument document, string? nodeId)
    {
        ArgumentNullException.ThrowIfNull(document);

        lock (document)
        {
            Select(string.IsNullOrWhiteSpace(nodeId) ? null : document.FindNode(nodeId));
        }
    }
}