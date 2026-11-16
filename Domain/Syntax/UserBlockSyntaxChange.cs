namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

/// <summary>
/// 一次用户块定义变更：新定义集合 + 语法指纹变化的 Id 集合。
/// 解析器据此只重解析“引用了这些定义”的块。
/// </summary>
public sealed record UserBlockSyntaxChange(
    IReadOnlyList<UserBlockDefinition> Definitions,
    IReadOnlySet<string> ChangedIds)
{
    public bool HasChanges => ChangedIds.Count > 0;

    /// <summary>由新旧定义集合构造变更描述；未变化的定义不进入 <see cref="ChangedIds"/>。</summary>
    public static UserBlockSyntaxChange Between(
        IEnumerable<UserBlockDefinition>? previous,
        IEnumerable<UserBlockDefinition>? current)
    {
        var definitions = current is null ? [] : new List<UserBlockDefinition>(current);
        return new UserBlockSyntaxChange(
            definitions,
            UserBlockSyntaxFingerprint.ComputeChangedIds(previous, definitions));
    }
}
