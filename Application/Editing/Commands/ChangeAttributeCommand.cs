using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;

public sealed class ChangeAttributeCommand : IEditorCommand, IEditorCommandState
{
    private readonly MarkdownDocument _document;
    private readonly MarkdownNode _node;
    private readonly string _key;
    private readonly string? _newValue;
    private readonly bool _hadOldValue;
    private readonly string? _oldValue;

    public string Description => $"Change attribute '{_key}'";
    public bool HasChanges { get; private set; }

    public ChangeAttributeCommand(MarkdownDocument document, MarkdownNode node, string key, string? value)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _node = node ?? throw new ArgumentNullException(nameof(node));
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _key = key;
        _newValue = value;
        _hadOldValue = node.Attributes.TryGetValue(key, out _oldValue);
    }

    public void Execute()
    {
        if (_newValue is null) _node.Attributes.Remove(_key);
        else _node.Attributes[_key] = _newValue;
        HasChanges = true;
        _document.MarkStyleChanged();
    }

    public void Undo()
    {
        if (_hadOldValue) _node.Attributes[_key] = _oldValue!;
        else _node.Attributes.Remove(_key);
        _document.MarkStyleChanged();
    }
}