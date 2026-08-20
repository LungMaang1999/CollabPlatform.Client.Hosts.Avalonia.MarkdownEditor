namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

/// <summary>
/// Categorizes document nodes based on structural and syntactic layout roles.
/// </summary>
public enum NodeCategory
{
    Container,
    Block,
    Inline,
    Leaf,
    Synthetic
}