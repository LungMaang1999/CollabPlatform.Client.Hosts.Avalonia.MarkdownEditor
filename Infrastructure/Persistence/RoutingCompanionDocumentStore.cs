using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence;

/// <summary>
/// 按 <see cref="DocumentKey.Kind"/> 分派到具体存储：本地文件走伴随文件，
/// 服务端文章走 gRPC（可再包一层缓存）。
/// <para>门面本身不判断权限、不造版本语义，只做选择；未接入服务端存储时明确拒绝，
/// 绝不悄悄回退到本地文件。</para>
/// </summary>
public sealed class RoutingCompanionDocumentStore : ICompanionDocumentStore
{
    private readonly ICompanionDocumentStore _local;
    private readonly ICompanionDocumentStore? _server;

    public RoutingCompanionDocumentStore(
        ICompanionDocumentStore local,
        ICompanionDocumentStore? server = null)
    {
        _local = local ?? throw new ArgumentNullException(nameof(local));
        _server = server;
    }

    public Task<CompanionSnapshot> LoadAsync(DocumentKey key, CancellationToken cancellationToken = default) =>
        Resolve(key).LoadAsync(key, cancellationToken);

    public Task<(string Path, string Reason)?> FindUnreadableInputAsync(
        DocumentKey key,
        CancellationToken cancellationToken = default) =>
        Resolve(key).FindUnreadableInputAsync(key, cancellationToken);

    public Task<CompanionWriteResult> SaveAsync(
        DocumentKey key,
        CompanionDocument document,
        string? expectedVersion,
        CancellationToken cancellationToken = default) =>
        Resolve(key).SaveAsync(key, document, expectedVersion, cancellationToken);

    private ICompanionDocumentStore Resolve(DocumentKey key) => key.Kind switch
    {
        DocumentLocationKind.LocalFile => _local,
        DocumentLocationKind.ServerArticle => _server ?? throw new NotSupportedException(
            "服务端文章的元素定义存储尚未接入，本次读写已拒绝。"),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key.Kind, "未知的文档归属。")
    };
}
