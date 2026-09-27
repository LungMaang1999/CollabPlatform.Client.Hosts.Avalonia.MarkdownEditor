namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax.Matching;

public interface INodeIdentityMatcher
{
    void Match(MarkdownNode previousRoot, MarkdownNode currentRoot);

    /// <summary>
    /// 仅在给定兄弟子序列之间做身份匹配（严格追加尾部拼接快速路径用）：
    /// 两列分别是同一父 Section 下“旧尾部”与“新尾部”直接子节点，
    /// 调用方保证完全未变的前缀不在两列中，从而避免遍历整棵旧树。
    /// </summary>
    void MatchSiblingLists(IList<MarkdownNode> previousTail, IList<MarkdownNode> currentTail);
}
