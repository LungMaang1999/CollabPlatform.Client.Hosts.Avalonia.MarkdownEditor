namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

/// <summary>
/// 用户块的内容模型：允许哪些块级元素与行内元素。空集合表示不作限制，
/// 与“内容由 Markdig 递归解析”的现有行为一致。
/// </summary>
public sealed record UserBlockContentModel
{
    public static UserBlockContentModel Empty { get; } = new();

    /// <summary>允许的块级节点类型；空表示任意块。</summary>
    public IReadOnlyList<NodeType> AllowedBlocks { get; init; } = [];

    /// <summary>允许的行内节点类型（按 <see cref="NodeType"/> 名称）；空表示任意行内。</summary>
    public IReadOnlyList<NodeType> AllowedInlines { get; init; } = [];

    public bool AllowsAnyBlock => AllowedBlocks.Count == 0;

    public bool AllowsAnyInline => AllowedInlines.Count == 0;
}
