using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax.Factories;

public sealed class NodeFactory
{
    public MarkdownNode Create(NodeType type, NodeCategory category, string text = "", SourceRange? range = null, string rawMarkdown = "") => new()
    {
        Type = type,
        Category = category,
        Text = text,
        RawMarkdown = rawMarkdown,
        Range = range ?? SourceRange.Empty()
    };

    public MarkdownNode CreateDocument() => Create(NodeType.Document, NodeCategory.Synthetic, "Document");

    public MarkdownNode CreateSection(int level) => new()
    {
        Type = NodeType.Section,
        Category = NodeCategory.Container,
        Level = level,
        Text = $"Section {level}",
        IsSynthetic = true
    };
}