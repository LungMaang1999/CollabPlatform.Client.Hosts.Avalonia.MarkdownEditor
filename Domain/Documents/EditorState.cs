namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

/// <summary>
/// Stores UI editor state such as cursor position, text selection, and expanded outline nodes.
/// </summary>
public sealed class EditorState
{
    public string? SelectedNodeId { get; set; }
    public int CaretOffset { get; set; }
    public int SelectionLength { get; set; }
    public HashSet<string> ExpandedNodeIds { get; } = new(StringComparer.Ordinal);
}