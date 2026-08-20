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
        MatchNodeLists(GetComparableChildren(previousRoot), GetComparableChildren(currentRoot));
    }

    private static void MatchNodeLists(IReadOnlyList<MarkdownNode> previousNodes, IReadOnlyList<MarkdownNode> currentNodes)
    {
        if (previousNodes.Count == 0 || currentNodes.Count == 0) return;

        if (currentNodes.Count == 1 && IsTransparentSection(currentNodes[0]) && previousNodes.Count == 1 && previousNodes[0].Type == NodeType.Section)
        {
            TransferState(previousNodes[0], currentNodes[0]);
            MatchNodeLists(previousNodes[0].Children, currentNodes[0].Children);
            return;
        }

        if (previousNodes.Count == 1 && IsTransparentSection(previousNodes[0]) && currentNodes.Count == 1 && currentNodes[0].Type == NodeType.Section)
        {
            TransferState(previousNodes[0], currentNodes[0]);
            MatchNodeLists(previousNodes[0].Children, currentNodes[0].Children);
            return;
        }

        var usedPrevious = new HashSet<int>();
        var matches = new Dictionary<int, int>();

        // Phase 1: Exact RawMarkdown or Text Match
        for (int c = 0; c < currentNodes.Count; c++)
        {
            int p = FindStrongMatch(previousNodes, usedPrevious, currentNodes[c]);
            if (p >= 0) { usedPrevious.Add(p); matches[c] = p; }
        }

        // Phase 2: Fuzzy Location & Type Match
        for (int c = 0; c < currentNodes.Count; c++)
        {
            if (matches.ContainsKey(c)) continue;
            int p = FindFuzzyMatch(previousNodes, usedPrevious, currentNodes[c], c);
            if (p >= 0) { usedPrevious.Add(p); matches[c] = p; }
        }

        // Phase 3: Relative Position Match
        for (int c = 0; c < currentNodes.Count; c++)
        {
            if (matches.ContainsKey(c)) continue;
            int p = FindPositionalMatch(previousNodes, usedPrevious, currentNodes[c], c);
            if (p >= 0) { usedPrevious.Add(p); matches[c] = p; }
        }

        foreach (var match in matches)
        {
            TransferState(previousNodes[match.Value], currentNodes[match.Key]);
            MatchNodeLists(previousNodes[match.Value].Children, currentNodes[match.Key].Children);
        }
    }

    private static bool IsTransparentSection(MarkdownNode node) => node.Type == NodeType.Section && node.IsSynthetic && node.Level == 0;

    private static int FindStrongMatch(IReadOnlyList<MarkdownNode> previousNodes, ISet<int> used, MarkdownNode current)
    {
        int bestIdx = -1, bestScore = int.MinValue;
        for (int i = 0; i < previousNodes.Count; i++)
        {
            if (used.Contains(i)) continue;
            var prev = previousNodes[i];
            if (!HasSameStructuralType(prev, current)) continue;

            int score = 0;
            bool strong = false;

            if (!string.IsNullOrEmpty(current.RawMarkdown) && string.Equals(prev.RawMarkdown, current.RawMarkdown, StringComparison.Ordinal))
            { score += 300; strong = true; }

            if (!string.IsNullOrEmpty(current.Text) && string.Equals(prev.Text, current.Text, StringComparison.Ordinal))
            { score += 200; strong = true; }

            if (!strong) continue;
            if (prev.Level == current.Level) score += 50;

            if (score > bestScore) { bestScore = score; bestIdx = i; }
        }
        return bestIdx;
    }

    private static int FindFuzzyMatch(IReadOnlyList<MarkdownNode> previousNodes, ISet<int> used, MarkdownNode current, int currentIdx)
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

    private static int FindPositionalMatch(IReadOnlyList<MarkdownNode> previousNodes, ISet<int> used, MarkdownNode current, int currentIdx)
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
        current.Id = prev.Id;
        if (current.Type == NodeType.Section) return;

        current.StyleId = prev.StyleId;
        current.LocalStyle = prev.LocalStyle.Clone();
        foreach (var attribute in prev.Attributes)
            current.Attributes.TryAdd(attribute.Key, attribute.Value);
    }

    private static IReadOnlyList<MarkdownNode> GetComparableChildren(MarkdownNode root) =>
        (root.Children.Count == 1 && IsTransparentSection(root.Children[0])) ? root.Children[0].Children.ToArray() : root.Children.ToArray();
}