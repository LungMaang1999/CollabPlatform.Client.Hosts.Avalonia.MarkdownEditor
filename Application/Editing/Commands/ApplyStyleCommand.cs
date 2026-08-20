using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;


public sealed class ApplyStyleCommand : IEditorCommand, IEditorCommandState
{
    private readonly MarkdownDocument _document;
    private readonly IReadOnlyList<MarkdownNode> _nodes;
    private readonly string? _styleId;
    private readonly Dictionary<string, string?> _oldStyleIds = new(StringComparer.Ordinal);

    public string Description => "Apply Style to Nodes";
    public bool HasChanges { get; private set; }

    public ApplyStyleCommand(MarkdownDocument document, IEnumerable<MarkdownNode> nodes, string? styleId)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _nodes = (nodes ?? throw new ArgumentNullException(nameof(nodes))).Distinct().ToArray();
        _styleId = string.IsNullOrWhiteSpace(styleId) ? null : styleId;

        foreach (var node in _nodes) _oldStyleIds[node.Id] = node.StyleId;
    }

    public void Execute()
    {
        HasChanges = false;
        foreach (var node in _nodes)
        {
            if (!string.Equals(node.StyleId, _styleId, StringComparison.Ordinal))
            {
                node.StyleId = _styleId;
                HasChanges = true;
            }
        }
        if (HasChanges) _document.MarkStyleChanged();
    }

    public void Undo()
    {
        foreach (var node in _nodes)
        {
            if (_oldStyleIds.TryGetValue(node.Id, out var oldId))
                node.StyleId = oldId;
        }
        _document.MarkStyleChanged();
    }
}