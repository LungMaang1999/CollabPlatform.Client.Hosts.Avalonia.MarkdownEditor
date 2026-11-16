using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence;

/// <summary>
/// 缓存装饰器：缓存优先只用于读加速，版本永远以远端为准。
/// <list type="bullet">
/// <item>读成功：整份刷新缓存；若缓存版本与远端不一致，附一条“服务器上有更新版本”提示。</item>
/// <item>读失败且缓存可用：返回缓存版本，并明确标注“离线/缓存”。</item>
/// <item>写：必须在线，离线返回 <c>Offline</c>，不做队列；冲突时丢弃缓存但不动用户内存中的编辑。</item>
/// </list>
/// </summary>
public sealed class CachingCompanionDocumentStore : ICompanionDocumentStore
{
    private readonly ICompanionDocumentStore _inner;
    private readonly ICompanionDocumentCache _cache;

    public CachingCompanionDocumentStore(ICompanionDocumentStore inner, ICompanionDocumentCache cache)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<CompanionSnapshot> LoadAsync(DocumentKey key, CancellationToken cancellationToken = default)
    {
        var hadCache = _cache.TryGet(key, out var cached);

        try
        {
            var snapshot = await _inner.LoadAsync(key, cancellationToken).ConfigureAwait(false);
            _cache.Set(key, new CachedCompanionDocument(
                snapshot.Document,
                snapshot.Version,
                snapshot.StylesHash,
                snapshot.ElementsHash));

            if (hadCache && !string.Equals(cached.Version, snapshot.Version, StringComparison.Ordinal))
            {
                var diagnostics = snapshot.Diagnostics
                    .Append(Warning(
                        "COMPANION_SERVER_NEWER",
                        "服务器上有更新版本，已载入服务器内容；本地未保存的编辑仍在编辑器中，请确认后再保存。"))
                    .ToArray();
                return snapshot with { Diagnostics = diagnostics };
            }

            return snapshot;
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            if (!hadCache)
            {
                throw;
            }

            var diagnostics = cached.Document.Diagnostics
                .Append(Warning(
                    "COMPANION_CACHE_STALE",
                    "当前无法连接服务器，显示的是缓存版本，可能与服务器不一致。"))
                .ToArray();

            return new CompanionSnapshot(
                cached.Document,
                CompanionFileExists: true,
                cached.Version,
                cached.StylesHash,
                cached.ElementsHash,
                LegacyFiles: [],
                Diagnostics: diagnostics);
        }
    }

    public Task<(string Path, string Reason)?> FindUnreadableInputAsync(
        DocumentKey key,
        CancellationToken cancellationToken = default) =>
        _inner.FindUnreadableInputAsync(key, cancellationToken);

    public async Task<CompanionWriteResult> SaveAsync(
        DocumentKey key,
        CompanionDocument document,
        string? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _inner
                .SaveAsync(key, document, expectedVersion, cancellationToken)
                .ConfigureAwait(false);

            switch (result.Status)
            {
                case CompanionSaveStatus.Saved:
                    _cache.Set(key, new CachedCompanionDocument(
                        document,
                        result.Version ?? expectedVersion ?? string.Empty,
                        CompanionDocumentFingerprint.ComputeStylesHash(document.Styles),
                        CompanionDocumentFingerprint.ComputeElementsHash(document.Definitions, document.Composition)));
                    break;

                case CompanionSaveStatus.Conflict:
                    // 远端版本已变：缓存以远端为准，丢弃旧缓存；用户编辑内容不在此层清空。
                    _cache.Remove(key);
                    break;
            }

            return result;
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            return CompanionWriteResult.Failed(
                CompanionSaveStatus.Offline,
                "当前无法连接服务器，元素定义未保存；离线不做排队，请联网后重试。");
        }
    }

    private static bool IsUnavailable(Exception ex) =>
        ex is ElementDefinitionServiceUnavailableException or TimeoutException or HttpRequestException;

    private static DiagnosticMessage Warning(string code, string message) =>
        new() { Severity = DiagnosticSeverity.Warning, Code = code, Message = message };
}
