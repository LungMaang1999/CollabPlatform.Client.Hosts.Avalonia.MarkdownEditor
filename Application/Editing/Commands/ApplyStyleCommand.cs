using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;

public sealed class ApplyStyleCommand : IEditorCommand, IEditorCommandState
{
    private readonly MarkdownDocument _document;
    private readonly IReadOnlyList<MarkdownNode> _nodes;
    private readonly IReadOnlyList<string> _nodeIds;
    private readonly string? _styleId;
    private readonly Dictionary<string, string?> _oldStyleIds = new(StringComparer.Ordinal);

    public string Description => "Apply Style to Nodes";
    public bool HasChanges { get; private set; }

    public ApplyStyleCommand(MarkdownDocument document, IEnumerable<MarkdownNode> nodes, string? styleId)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _nodes = (nodes ?? throw new ArgumentNullException(nameof(nodes))).Distinct().ToArray();
        _nodeIds = _nodes.Select(node => node.Id).ToArray();
        _styleId = string.IsNullOrWhiteSpace(styleId) ? null : styleId;

        foreach (var node in _nodes) _oldStyleIds[node.Id] = node.StyleId;
    }

    public void Execute()
    {
        HasChanges = false;
        for (var i = 0; i < _nodes.Count; i++)
        {
            var node = ResolveNode(i);
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
        for (var i = 0; i < _nodes.Count; i++)
        {
            if (_oldStyleIds.TryGetValue(_nodeIds[i], out var oldId))
                ResolveNode(i).StyleId = oldId;
        }
        _document.MarkStyleChanged();
    }

    // 撤销/重做会重建 AST（ReplaceParsedRoot 换根），构造期捕获的节点会脱离新树；
    // 每次执行都按 Id 从当前树重新解析，否则重做只作用于游离旧节点而不生效。
    private MarkdownNode ResolveNode(int index) =>
        _document.FindNode(_nodeIds[index]) ?? _nodes[index];
}
