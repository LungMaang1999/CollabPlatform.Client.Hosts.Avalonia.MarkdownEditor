using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;

/// <summary>文档归属：本地文件或服务端文章。所有存储实现都按它分派。</summary>
public enum DocumentLocationKind
{
    LocalFile,
    ServerArticle
}

/// <summary>
/// 文档标识。<see cref="DocumentLocationKind.LocalFile"/> 时 <see cref="Value"/> 是规范化后的
/// Markdown 绝对路径；<see cref="DocumentLocationKind.ServerArticle"/> 时是与 gRPC 归属键一致的文章 Id。
/// </summary>
public readonly record struct DocumentKey(DocumentLocationKind Kind, string Value)
{
    public static DocumentKey LocalFile(string markdownFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownFilePath);
        return new DocumentKey(DocumentLocationKind.LocalFile, Path.GetFullPath(markdownFilePath));
    }

    public static DocumentKey ServerArticle(string articleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(articleId);
        return new DocumentKey(DocumentLocationKind.ServerArticle, articleId);
    }

    public bool IsValid => !string.IsNullOrWhiteSpace(Value);
}

/// <summary>
/// 一次伴随文档读取的结果。
/// <para><see cref="Version"/> 是不透明版本：本地文件为内容哈希编码，服务端为行版本；
/// 保存时原样回传做乐观并发。</para>
/// <para><see cref="StylesHash"/> / <see cref="ElementsHash"/> 是分段指纹，只用于失效与冲突报告，
/// 不得互相污染：样式变化不能让解析层失效。</para>
/// </summary>
public sealed record CompanionSnapshot(
    CompanionDocument Document,
    bool CompanionFileExists,
    string Version,
    string StylesHash,
    string ElementsHash,
    IReadOnlyList<string> LegacyFiles,
    IReadOnlyList<DiagnosticMessage> Diagnostics);

/// <summary>
/// 分段冲突报告：<c>Styles</c> / <c>Elements</c> / 两者同时，绝不是笼统的“文件冲突”。
/// </summary>
public sealed record CompanionChangeReport(
    bool StylesChanged,
    bool ElementsChanged,
    string? RemoteVersion,
    IReadOnlyList<string>? ChangedDefinitionIds);

public enum CompanionSaveStatus
{
    Saved,
    NoChange,
    Conflict,
    Forbidden,
    IoFailure,
    Offline
}

/// <summary>一次伴随文档保存的结果。</summary>
public sealed record CompanionWriteResult(
    CompanionSaveStatus Status,
    string? Version,
    CompanionChangeReport? Conflict,
    IReadOnlyList<DiagnosticMessage> Diagnostics,
    string? Message)
{
    public static CompanionWriteResult Saved(string? version, IReadOnlyList<DiagnosticMessage>? diagnostics = null, string? message = null) =>
        new(CompanionSaveStatus.Saved, version, null, diagnostics ?? [], message);

    public static CompanionWriteResult NoChange(string? version) =>
        new(CompanionSaveStatus.NoChange, version, null, [], "内容没有变化，未写入。");

    public static CompanionWriteResult Failed(CompanionSaveStatus status, string message) =>
        new(status, null, null, [], message);
}

/// <summary>
/// 整份伴随文档（样式 + 定义）的读写门面。上层只依赖它，不感知文件还是服务端。
/// 一次保存必须整份原子写入：不允许出现“样式写成功、定义写失败”的半截状态。
/// </summary>
public interface ICompanionDocumentStore
{
    Task<CompanionSnapshot> LoadAsync(DocumentKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// 探测目标位置是否存在“不可安全覆盖”的既有输入（损坏、结构不认识、版本高于支持）。
    /// 返回精确路径与原因，<c>null</c> 表示可写。保存前必须先检查，避免用空内容覆盖损坏文件。
    /// </summary>
    Task<(string Path, string Reason)?> FindUnreadableInputAsync(
        DocumentKey key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存整份伴随文档。<paramref name="expectedVersion"/> 为上次读取到的版本，
    /// <c>null</c> 表示“无既有内容”（新建/导入覆盖），此时不做基线校验。
    /// </summary>
    Task<CompanionWriteResult> SaveAsync(
        DocumentKey key,
        CompanionDocument document,
        string? expectedVersion,
        CancellationToken cancellationToken = default);
}
