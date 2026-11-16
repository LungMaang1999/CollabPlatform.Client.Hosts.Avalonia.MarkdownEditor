namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;

/// <summary>
/// 元素定义 RPC 的客户端抽象，字段与服务端契约一一对应，不在客户端另造语义。
/// 服务端实现负责把 gRPC 响应/状态码翻译成这些结果；本期先给桩。
/// </summary>
public interface IElementDefinitionServiceClient
{
    /// <summary>读取定义段；无记录时返回空 XML 且 <c>Version = "0"</c>（不是错误）。</summary>
    Task<ElementDefinitionsPayload> GetElementDefinitionsAsync(
        string documentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存定义段。<paramref name="expectedVersion"/> 不匹配时返回
    /// <c>Conflict = true</c> 且带 <c>CurrentVersion</c> / <c>CurrentElementsXml</c>。
    /// </summary>
    Task<SaveElementDefinitionsPayload> SaveElementDefinitionsAsync(
        string documentId,
        string elementsXml,
        string? expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>导入定义段；规范化内容相同时返回 <c>Unchanged</c>，不写库不升版本。</summary>
    Task<ImportElementDefinitionsPayload> ImportElementDefinitionsAsync(
        string documentId,
        string elementsXml,
        string? expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>按文档 Id 批量读取定义集合的元信息（不含定义正文），与服务端 list 契约一致。</summary>
    Task<IReadOnlyList<ElementDefinitionSetPayload>> ListElementDefinitionSetsAsync(
        IReadOnlyCollection<string> documentIds,
        CancellationToken cancellationToken = default);
}

public sealed record ElementDefinitionsPayload(
    string ElementsXml,
    string Version,
    string? AuthorId,
    DateTimeOffset? UpdatedAt,
    string? ContentHash);

public sealed record SaveElementDefinitionsPayload(
    bool Success,
    bool Conflict,
    string? Version,
    string? CurrentVersion,
    string? CurrentElementsXml,
    string? ContentHash,
    string? Message)
{
    public static SaveElementDefinitionsPayload Saved(string version, string? contentHash = null) =>
        new(true, false, version, null, null, contentHash, null);

    public static SaveElementDefinitionsPayload Conflicted(string? currentVersion, string? currentElementsXml) =>
        new(false, true, null, currentVersion, currentElementsXml, null, null);
}

public enum ImportElementDefinitionsStatus
{
    Saved,
    Unchanged,
    Conflict,
    Forbidden
}

public sealed record ImportElementDefinitionsPayload(
    ImportElementDefinitionsStatus Status,
    string? Version,
    string? CurrentVersion,
    string? CurrentElementsXml,
    string? Message);

public sealed record ElementDefinitionSetPayload(
    string DocumentId,
    string? AuthorId,
    string Version,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    string? ContentHash);

/// <summary>服务端拒绝写入（越权/未登录）：客户端只负责映射成可读提示。</summary>
public sealed class ElementDefinitionPermissionDeniedException : Exception
{
    public ElementDefinitionPermissionDeniedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>网络/服务不可用：读走缓存、写返回 Offline，不做离线写队列。</summary>
public sealed class ElementDefinitionServiceUnavailableException : Exception
{
    public ElementDefinitionServiceUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
