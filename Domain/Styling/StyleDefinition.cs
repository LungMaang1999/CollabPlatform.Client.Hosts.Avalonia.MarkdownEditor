using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

/// <summary>
/// Defines a named style template with optional target node type and inheritance.
/// </summary>
public sealed class StyleDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string? ParentStyleId { get; set; }
    public NodeType? AppliesTo { get; set; }
    public NodeStyle Style { get; set; } = new();
}