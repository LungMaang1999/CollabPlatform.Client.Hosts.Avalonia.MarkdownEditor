using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;

public sealed class ChangeAttributeCommand : IEditorCommand, IEditorCommandState
{
    private readonly MarkdownDocument _document;
    private readonly MarkdownNode _node;
    private readonly string _nodeId;
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

        _nodeId = node.Id;
        _key = key;
        _newValue = value;
        _hadOldValue = node.Attributes.TryGetValue(key, out _oldValue);
    }

    public void Execute()
    {
        var node = ResolveNode();
        if (_newValue is null) node.Attributes.Remove(_key);
        else node.Attributes[_key] = _newValue;
        HasChanges = true;
        _document.MarkStyleChanged();
    }

    public void Undo()
    {
        var node = ResolveNode();
        if (_hadOldValue) node.Attributes[_key] = _oldValue!;
        else node.Attributes.Remove(_key);
        _document.MarkStyleChanged();
    }

    // 撤销/重做会重建 AST（ReplaceParsedRoot 换根），构造期捕获的节点会脱离新树；
    // 每次执行都按 Id 从当前树重新解析，否则重做只作用于游离旧节点而不生效。
    private MarkdownNode ResolveNode() => _document.FindNode(_nodeId) ?? _node;
}
