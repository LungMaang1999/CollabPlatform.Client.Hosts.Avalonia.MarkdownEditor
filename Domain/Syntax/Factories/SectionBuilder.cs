using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax.Factories;

public sealed class SectionBuilder
{
    private readonly NodeFactory _factory;

    public SectionBuilder(NodeFactory factory) => _factory = factory;

    public MarkdownNode Build(IEnumerable<MarkdownNode> blocks)
    {
        var document = _factory.CreateDocument();
        var rootSection = _factory.CreateSection(0);

        document.AddChild(rootSection);

        var stack = new Stack<MarkdownNode>();
        stack.Push(rootSection);

        foreach (var node in blocks)
        {
            if (node.Type != NodeType.Heading)
            {
                stack.Peek().AddChild(node);
                continue;
            }

            var level = Math.Max(1, node.Level ?? 1);
            while (stack.Count > 1 && (stack.Peek().Level ?? 0) >= level)
                stack.Pop();

            var section = _factory.CreateSection(level);
            section.Text = node.Text;
            section.AddChild(node);
            stack.Peek().AddChild(section);
            stack.Push(section);
        }

        return document;
    }
}