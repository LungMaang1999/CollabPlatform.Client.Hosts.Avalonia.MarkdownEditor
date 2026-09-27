namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

public sealed class EditorState
{
    public string? SelectedNodeId { get; set; }
    public int CaretOffset { get; set; }
    public int SelectionLength { get; set; }
    public HashSet<string> ExpandedNodeIds { get; } = new(StringComparer.Ordinal);
}
