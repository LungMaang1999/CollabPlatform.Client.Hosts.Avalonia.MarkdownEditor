namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax.Factories;

public sealed class SectionBuilder
{
    private readonly NodeFactory _factory;

    public SectionBuilder(NodeFactory factory) => _factory = factory;

    public MarkdownNode Build(IEnumerable<MarkdownNode> blocks) => Build(blocks, null);

    /// <summary>
    /// 按标题层级重建 Section 树。<paramref name="priorTreeNodes"/> 中的块是
    /// 复用自已发布树的共享节点，Parent 覆写延迟到提交换根时（见
    /// MarkdownNode.PendingParentLinks）；集合外节点立即挂接。
    /// </summary>
    public MarkdownNode Build(IEnumerable<MarkdownNode> blocks, HashSet<MarkdownNode>? priorTreeNodes)
    {
        var document = _factory.CreateDocument();
        var rootSection = _factory.CreateSection(0);

        document.AttachChildFromPriorTree(rootSection);

        List<(MarkdownNode Child, MarkdownNode NewParent)>? pending = null;

        void Adopt(MarkdownNode parent, MarkdownNode node)
        {
            if (priorTreeNodes is not null && priorTreeNodes.Contains(node))
            {
                pending ??= document.PendingParentLinks = [];
                parent.AttachSharedChildDeferred(node, pending);
            }
            else
            {
                parent.AttachChildFromPriorTree(node);
            }
        }

        var stack = new Stack<MarkdownNode>();
        stack.Push(rootSection);

        foreach (var node in blocks)
        {
            if (node.Type != NodeType.Heading)
            {
                Adopt(stack.Peek(), node);
                continue;
            }

            var level = Math.Max(1, node.Level ?? 1);
            while (stack.Count > 1 && (stack.Peek().Level ?? 0) >= level)
                stack.Pop();

            var section = _factory.CreateSection(level);
            section.Text = node.Text;
            Adopt(section, node);
            Adopt(stack.Peek(), section);
            stack.Push(section);
        }

        return document;
    }
}
