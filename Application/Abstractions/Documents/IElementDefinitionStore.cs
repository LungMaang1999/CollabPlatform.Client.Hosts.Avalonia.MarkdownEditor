using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;

/// <summary>定义段来源：本地文件、服务端文章、浏览器/弱网缓存。</summary>
public enum DefinitionSource
{
    LocalFile,
    Server,
    Cache
}

/// <summary>定义段读取结果。<see cref="Version"/> 原样来自存储层，用于后续乐观并发。</summary>
public sealed record DefinitionLoadResult(
    IReadOnlyList<UserBlockDefinition> Definitions,
    string? Version,
    DefinitionSource Source,
    IReadOnlyList<DiagnosticMessage>? Diagnostics)
{
    /// <summary>服务端无记录时返回 version=0 + 空 XML：这是“还没有定义”，不是错误。</summary>
    public bool IsEmpty => Definitions.Count == 0;
}

/// <summary>
/// 定义段保存状态。
/// <para><see cref="NoChange"/> 对应服务端 <c>Unchanged</c>，界面必须显示“没有变化”，不能显示成“已保存”。</para>
/// <para><see cref="Forbidden"/> 对应服务端 <c>PermissionDenied</c>，权限判定始终在服务端。</para>
/// <para><see cref="NotPersisted"/> 表示文档尚未落库，尚未发出任何服务端调用。</para>
/// </summary>
public enum DefinitionSaveStatus
{
    Saved,
    NoChange,
    Conflict,
    Forbidden,
    NotPersisted,
    IoFailure,
    Offline
}

/// <summary>定义段写入结果；冲突时 <see cref="Conflict"/> 按段报告，不谎报整份保存成功。</summary>
public sealed record DefinitionWriteResult(
    DefinitionSaveStatus Status,
    string? Version,
    CompanionChangeReport? Conflict,
    string? Message)
{
    public bool IsSuccess => Status is DefinitionSaveStatus.Saved or DefinitionSaveStatus.NoChange;
}

public sealed record DefinitionInvalidatedEventArgs(
    DocumentKey Key,
    bool StylesChanged,
    bool ElementsChanged,
    IReadOnlyList<string>? ChangedDefinitionIds);

/// <summary>
/// 编辑面板/命令层只依赖这个门面，不感知“本地文件还是服务端文章”。
/// 读路径永不写盘；定义段的写入必须带上次读取到的版本。
/// </summary>
public interface IElementDefinitionStore
{
    /// <summary>读取定义段；styles 段由伴随文档读取器单独提供给样式层。</summary>
    Task<DefinitionLoadResult> LoadAsync(DocumentKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// 只提交定义段；<paramref name="expectedVersion"/> 为上次读取到的版本，
    /// <c>null</c> 表示“新建/无既有基线”。
    /// </summary>
    Task<DefinitionWriteResult> SaveAsync(
        DocumentKey key,
        IReadOnlyList<UserBlockDefinition> definitions,
        string? expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>外部改动探测：分别报告样式段与定义段。</summary>
    Task<CompanionChangeReport> DetectChangesAsync(
        DocumentKey key,
        string? baselineVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 从文件/外部来源导入定义段。默认等同于保存；服务端实现应走 Import RPC，
    /// 规范化内容相同时返回 <see cref="DefinitionSaveStatus.NoChange"/>（界面显示“没有变化”）。
    /// </summary>
    Task<DefinitionWriteResult> ImportAsync(
        DocumentKey key,
        IReadOnlyList<UserBlockDefinition> definitions,
        string? expectedVersion,
        CancellationToken cancellationToken = default) =>
        SaveAsync(key, definitions, expectedVersion, cancellationToken);

    /// <summary>缓存/文件监听触发的失效通知（不承担写回）。</summary>
    event EventHandler<DefinitionInvalidatedEventArgs>? Invalidated;
}
