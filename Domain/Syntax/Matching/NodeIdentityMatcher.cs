using System.Collections.Generic;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax.Matching;

public sealed class NodeIdentityMatcher : INodeIdentityMatcher
{
    private const int MinimumFuzzyMatchScore = 100;

    public void Match(MarkdownNode previousRoot, MarkdownNode currentRoot)
    {
        ArgumentNullException.ThrowIfNull(previousRoot);
        ArgumentNullException.ThrowIfNull(currentRoot);

        TransferIdentity(previousRoot, currentRoot);

        var previousWrapper = GetSingleTransparentSection(previousRoot);
        var currentWrapper = GetSingleTransparentSection(currentRoot);
        var context = new MatchContext();
        if (previousWrapper is not null && currentWrapper is not null)
        {
            TransferIdentity(previousWrapper, currentWrapper);
            MatchNodeLists(previousWrapper.Children, currentWrapper.Children, context);
        }
        else
        {
            MatchNodeLists(GetComparableChildren(previousRoot), GetComparableChildren(currentRoot), context);
        }

        // 稳态编辑全部节点都能局部匹配，救援扫描（全树两趟）完全跳过；
        // 只有重组类编辑产生未匹配节点时才支付扫描成本。
        if (context.SawUnmatchedCurrent)
        {
            RescueOrphanedNodes(previousRoot, currentRoot);
        }
    }

    // 跟踪本轮匹配是否有当前节点未找到旧节点（救援 pass 的启动条件）。
    private sealed class MatchContext
    {
        public bool SawUnmatchedCurrent;
    }

    // 重组类编辑（标题升降级、段落套引用等）会改变节树嵌套，节点被移入不同父级后
    // 局部匹配永远碰不到旧节点，身份丢失 = 样式与选区丢失。全树兜底：仅当
    // 结构类型+文本 在新旧两侧都唯一时才转移身份，重复内容不抢，避免误配。
    private static void RescueOrphanedNodes(MarkdownNode previousRoot, MarkdownNode currentRoot)
    {
        var currentIds = new HashSet<string>(StringComparer.Ordinal);
        CollectIds(currentRoot, currentIds);

        var lost = new Dictionary<(StructuralKey Key, string Text), List<MarkdownNode>>();
        CollectIdentityLost(previousRoot, currentIds, lost);
        if (lost.Count == 0)
            return;

        var previousIds = new HashSet<string>(StringComparer.Ordinal);
        CollectIds(previousRoot, previousIds);

        var fresh = new Dictionary<(StructuralKey Key, string Text), List<MarkdownNode>>();
        CollectIdentityLost(currentRoot, previousIds, fresh);
        if (fresh.Count == 0)
            return;

        foreach (var pair in fresh)
        {
            if (pair.Value.Count != 1)
                continue;
            if (!lost.TryGetValue(pair.Key, out var candidates) || candidates.Count != 1)
                continue;

            TransferState(candidates[0], pair.Value[0]);
        }
    }

    private static void CollectIds(MarkdownNode node, HashSet<string> ids)
    {
        ids.Add(node.Id);
        foreach (var child in node.Children)
            CollectIds(child, ids);
    }

    // 收集"身份未延续到对面树"的内容节点，按 结构类型+文本 归桶。
    // 文本为空无法可靠配对（Section 等容器 Text 为空），直接跳过。
    private static void CollectIdentityLost(
        MarkdownNode node,
        HashSet<string> otherSideIds,
        Dictionary<(StructuralKey Key, string Text), List<MarkdownNode>> bucket)
    {
        if (!otherSideIds.Contains(node.Id) && !string.IsNullOrEmpty(node.Text))
        {
            var key = (GetStructuralKey(node), node.Text);
            if (!bucket.TryGetValue(key, out var list))
            {
                list = new List<MarkdownNode>();
                bucket[key] = list;
            }
            list.Add(node);
        }

        foreach (var child in node.Children)
            CollectIdentityLost(child, otherSideIds, bucket);
    }

    public void MatchSiblingLists(IList<MarkdownNode> previousTail, IList<MarkdownNode> currentTail)
    {
        ArgumentNullException.ThrowIfNull(previousTail);
        ArgumentNullException.ThrowIfNull(currentTail);

        MatchNodeLists(previousTail, currentTail, null);
    }

    private static MarkdownNode? GetSingleTransparentSection(MarkdownNode root) =>
        root.Children.Count == 1 && IsTransparentSection(root.Children[0]) ? root.Children[0] : null;

    private static void MatchNodeLists(IList<MarkdownNode> previousNodes, IList<MarkdownNode> currentNodes, MatchContext? context)
    {
        if (previousNodes.Count == 0 || currentNodes.Count == 0)
        {
            // 旧列表为空时当前列表全体都是新增节点，视为未匹配。
            if (currentNodes.Count > 0 && context is not null)
                context.SawUnmatchedCurrent = true;
            return;
        }

        if ((currentNodes.Count == 1 && IsTransparentSection(currentNodes[0]) && previousNodes.Count == 1 && previousNodes[0].Type == NodeType.Section) ||
            (previousNodes.Count == 1 && IsTransparentSection(previousNodes[0]) && currentNodes.Count == 1 && currentNodes[0].Type == NodeType.Section))
        {
            TransferState(previousNodes[0], currentNodes[0]);
            MatchNodeLists(previousNodes[0].Children, currentNodes[0].Children, context);
            return;
        }

        var usedPrevious = new HashSet<int>();
        var matches = new Dictionary<int, int>();

        StrongMatchIndex? index = previousNodes.Count >= StrongMatchIndexThreshold
            ? BuildStrongMatchIndex(previousNodes)
            : null;

        for (int c = 0; c < currentNodes.Count; c++)
        {
            int p = FindStrongMatch(previousNodes, usedPrevious, currentNodes[c], index);
            if (p >= 0) { usedPrevious.Add(p); matches[c] = p; }
        }

        for (int c = 0; c < currentNodes.Count; c++)
        {
            if (matches.ContainsKey(c)) continue;
            int p = FindFuzzyMatch(previousNodes, usedPrevious, currentNodes[c], c);
            if (p >= 0) { usedPrevious.Add(p); matches[c] = p; }
        }

        for (int c = 0; c < currentNodes.Count; c++)
        {
            if (matches.ContainsKey(c)) continue;
            int p = FindPositionalMatch(previousNodes, usedPrevious, currentNodes[c], c);
            if (p >= 0) { usedPrevious.Add(p); matches[c] = p; }
        }

        foreach (var match in matches)
        {
            TransferState(previousNodes[match.Value], currentNodes[match.Key]);
            MatchNodeLists(previousNodes[match.Value].Children, currentNodes[match.Key].Children, context);
        }

        if (context is not null && matches.Count < currentNodes.Count)
            context.SawUnmatchedCurrent = true;
    }

    private static bool IsTransparentSection(MarkdownNode node) => node.Type == NodeType.Section && node.IsSynthetic && node.Level == 0;

    private const int StrongMatchIndexThreshold = 16;

    private readonly record struct StructuralKey(NodeType Type, byte Category);

    private sealed class StrongMatchIndex
    {
        public required Dictionary<(StructuralKey Key, string Value), List<int>> ByRaw { get; init; }
        public required Dictionary<(StructuralKey Key, string Value), List<int>> ByText { get; init; }
    }

    private static StructuralKey GetStructuralKey(MarkdownNode node) =>
        new(node.Type, node.Type == NodeType.Section ? (byte)0 : (byte)node.Category);

    private static StrongMatchIndex BuildStrongMatchIndex(IList<MarkdownNode> previousNodes)
    {
        var byRaw = new Dictionary<(StructuralKey, string), List<int>>();
        var byText = new Dictionary<(StructuralKey, string), List<int>>();

        for (int i = 0; i < previousNodes.Count; i++)
        {
            var node = previousNodes[i];
            var key = GetStructuralKey(node);

            if (!string.IsNullOrEmpty(node.RawMarkdown))
            {
                var bucketKey = (key, node.RawMarkdown);
                if (!byRaw.TryGetValue(bucketKey, out var bucket))
                {
                    bucket = new List<int>();
                    byRaw[bucketKey] = bucket;
                }
                bucket.Add(i);
            }

            if (!string.IsNullOrEmpty(node.Text))
            {
                var bucketKey = (key, node.Text);
                if (!byText.TryGetValue(bucketKey, out var bucket))
                {
                    bucket = new List<int>();
                    byText[bucketKey] = bucket;
                }
                bucket.Add(i);
            }
        }

        return new StrongMatchIndex { ByRaw = byRaw, ByText = byText };
    }

    private static int FindStrongMatch(IList<MarkdownNode> previousNodes, ISet<int> used, MarkdownNode current, StrongMatchIndex? index)
    {
        if (index is null)
            return FindStrongMatchLinear(previousNodes, used, current);

        var key = GetStructuralKey(current);
        var candidates = new List<int>();

        if (!string.IsNullOrEmpty(current.RawMarkdown)
            && index.ByRaw.TryGetValue((key, current.RawMarkdown), out var rawBucket))
        {
            foreach (var i in rawBucket)
                if (!used.Contains(i) && !candidates.Contains(i))
                    candidates.Add(i);
        }

        if (!string.IsNullOrEmpty(current.Text)
            && index.ByText.TryGetValue((key, current.Text), out var textBucket))
        {
            foreach (var i in textBucket)
                if (!used.Contains(i) && !candidates.Contains(i))
                    candidates.Add(i);
        }

        int bestIdx = -1, bestScore = int.MinValue;
        foreach (var i in candidates)
        {
            int score = ScoreStrongMatch(previousNodes[i], current);
            if (score >= 0 && score > bestScore) { bestScore = score; bestIdx = i; }
        }
        return bestIdx;
    }

    private static int FindStrongMatchLinear(IList<MarkdownNode> previousNodes, ISet<int> used, MarkdownNode current)
    {
        int bestIdx = -1, bestScore = int.MinValue;
        for (int i = 0; i < previousNodes.Count; i++)
        {
            if (used.Contains(i)) continue;

            int score = ScoreStrongMatch(previousNodes[i], current);
            if (score < 0) continue;

            if (score > bestScore) { bestScore = score; bestIdx = i; }
        }
        return bestIdx;
    }

    private static int ScoreStrongMatch(MarkdownNode prev, MarkdownNode current)
    {
        if (!HasSameStructuralType(prev, current)) return -1;

        int score = 0;
        bool strong = false;

        if (!string.IsNullOrEmpty(current.RawMarkdown) && string.Equals(prev.RawMarkdown, current.RawMarkdown, StringComparison.Ordinal))
        { score += 300; strong = true; }

        if (!string.IsNullOrEmpty(current.Text) && string.Equals(prev.Text, current.Text, StringComparison.Ordinal))
        { score += 200; strong = true; }

        if (!strong) return -1;
        if (prev.Level == current.Level) score += 50;

        return score;
    }

    private static int FindFuzzyMatch(IList<MarkdownNode> previousNodes, ISet<int> used, MarkdownNode current, int currentIdx)
    {
        int bestIdx = -1, bestScore = int.MinValue;
        for (int i = 0; i < previousNodes.Count; i++)
        {
            if (used.Contains(i)) continue;
            var prev = previousNodes[i];
            if (!HasSameStructuralType(prev, current)) continue;

            int score = CalculateFuzzyScore(prev, current, i, currentIdx);
            if (score > bestScore) { bestScore = score; bestIdx = i; }
        }
        return bestScore >= MinimumFuzzyMatchScore ? bestIdx : -1;
    }

    private static int FindPositionalMatch(IList<MarkdownNode> previousNodes, ISet<int> used, MarkdownNode current, int currentIdx)
    {
        int bestIdx = -1, bestDist = int.MaxValue;
        for (int i = 0; i < previousNodes.Count; i++)
        {
            if (used.Contains(i)) continue;
            var prev = previousNodes[i];
            if (!HasSameStructuralType(prev, current)) continue;

            int dist = Math.Abs(i - currentIdx);
            if (dist < bestDist) { bestDist = dist; bestIdx = i; }
        }
        return bestIdx;
    }

    private static bool HasSameStructuralType(MarkdownNode prev, MarkdownNode current) =>
        prev.Type == current.Type && (prev.Type == NodeType.Section || prev.Category == current.Category);

    private static int CalculateFuzzyScore(MarkdownNode prev, MarkdownNode current, int prevIdx, int currentIdx)
    {
        int score = 100;
        if (prev.Level == current.Level) score += 25;
        score += Math.Max(0, 40 - Math.Min(Math.Abs(prevIdx - currentIdx) * 10, 40));
        score += string.Equals(prev.Text, current.Text, StringComparison.Ordinal) ? 100 : -20;
        if (string.Equals(prev.RawMarkdown, current.RawMarkdown, StringComparison.Ordinal)) score += 50;
        return score;
    }

    private static void TransferIdentity(MarkdownNode prev, MarkdownNode current) => current.Id = prev.Id;

    private static void TransferState(MarkdownNode prev, MarkdownNode current)
    {
        // 增量追加解析会直接复用前缀块对象（current 与 prev 同引用）。
        // 旧树发布后只读：对同一对象写回 Id/样式克隆会破坏并发不变量，直接跳过。
        if (ReferenceEquals(prev, current)) return;

        current.Id = prev.Id;
        if (current.Type == NodeType.Section) return;

        current.StyleId = prev.StyleId;
        current.LocalStyle = prev.LocalStyle.Clone();
        foreach (var attribute in prev.Attributes)
        {
            // 解析器源码派生的属性（unclosed/language/href/index 等）以新树
            // 为准，不继承；仅保留用户自定义属性。
            if (MarkdownAttributes.SourceDerivedKeys.Contains(attribute.Key)) continue;
            current.Attributes.TryAdd(attribute.Key, attribute.Value);
        }
    }

    private static IList<MarkdownNode> GetComparableChildren(MarkdownNode root) =>
        (root.Children.Count == 1 && IsTransparentSection(root.Children[0])) ? root.Children[0].Children : root.Children;
}
