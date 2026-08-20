using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;

public sealed class ChangeStyleCommand : IEditorCommand, IEditorCommandState
{
    private readonly MarkdownDocument _document;
    private readonly MarkdownNode _node;
    private readonly NodeStyle _oldStyle;
    private readonly NodeStyle _newStyle;

    public string Description => "Change Node Local Style";
    public bool HasChanges { get; private set; }

    public ChangeStyleCommand(MarkdownDocument document, MarkdownNode node, NodeStyle newStyle)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _oldStyle = node.LocalStyle.Clone();
        _newStyle = (newStyle ?? throw new ArgumentNullException(nameof(newStyle))).Clone();
    }

    public void Execute()
    {
        _node.LocalStyle = _newStyle.Clone();
        HasChanges = true;
        _document.MarkStyleChanged();
    }

    public void Undo()
    {
        _node.LocalStyle = _oldStyle.Clone();
        _document.MarkStyleChanged();
    }
}