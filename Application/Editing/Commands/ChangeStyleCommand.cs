using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;

public sealed class ChangeStyleCommand : IEditorCommand, IEditorCommandState
{
    private readonly MarkdownDocument _document;
    private readonly MarkdownNode _node;
    private readonly string _nodeId;
    private readonly NodeStyle _oldStyle;
    private readonly NodeStyle _newStyle;

    public string Description => "Change Node Local Style";
    public bool HasChanges { get; private set; }

    public ChangeStyleCommand(MarkdownDocument document, MarkdownNode node, NodeStyle newStyle)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _nodeId = node.Id;
        _oldStyle = node.LocalStyle.Clone();
        _newStyle = (newStyle ?? throw new ArgumentNullException(nameof(newStyle))).Clone();
    }

    public void Execute()
    {
        ResolveNode().LocalStyle = _newStyle.Clone();
        HasChanges = true;
        _document.MarkStyleChanged();
    }

    public void Undo()
    {
        ResolveNode().LocalStyle = _oldStyle.Clone();
        _document.MarkStyleChanged();
    }

    // 撤销/重做会重建 AST（ReplaceParsedRoot 换根），构造期捕获的节点会脱离新树；
    // 每次执行都按 Id 从当前树重新解析，否则重做只作用于游离旧节点而不生效。
    private MarkdownNode ResolveNode() => _document.FindNode(_nodeId) ?? _node;
}
