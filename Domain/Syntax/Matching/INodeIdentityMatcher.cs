namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax.Matching;

public interface INodeIdentityMatcher
{
    void Match(MarkdownNode previousRoot, MarkdownNode currentRoot);
}