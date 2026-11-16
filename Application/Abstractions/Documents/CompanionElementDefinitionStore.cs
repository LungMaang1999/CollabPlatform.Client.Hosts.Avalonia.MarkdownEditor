using System.Collections.Concurrent;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;

/// <summary>
/// 定义段门面：把 <see cref="ICompanionDocumentStore"/> 的整份读写收敛成定义段的读写。
/// 不自行判断权限、不自行造冲突语义，只做状态映射与提示文案。
/// </summary>
public sealed class CompanionElementDefinitionStore : IElementDefinitionStore
{
    private readonly ICompanionDocumentStore _documents;
    private readonly IElementDefinitionServiceClient? _client;
    private readonly ConcurrentDictionary<DocumentKey, Baseline> _baselines = new();

    public CompanionElementDefinitionStore(ICompanionDocumentStore documents)
        : this(documents, client: null)
    {
    }

    public CompanionElementDefinitionStore(
        ICompanionDocumentStore documents,
        IElementDefinitionServiceClient? client)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _client = client;
    }

    public event EventHandler<DefinitionInvalidatedEventArgs>? Invalidated;

    public async Task<DefinitionLoadResult> LoadAsync(DocumentKey key, CancellationToken cancellationToken = default)
    {
        // 服务端文章尚未落库（key 为空）时没有任何存储可读：按“没有定义”返回，不抛服务端错误。
        if (!key.IsValid)
        {
            return new DefinitionLoadResult([], null, SourceOf(key), []);
        }

        CompanionSnapshot snapshot;
        try
        {
            snapshot = await _documents.LoadAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (ElementDefinitionServiceUnavailableException)
        {
            return OfflineLoad(key, "当前无法连接服务器，未读取到元素定义。");
        }
        catch (ElementDefinitionPermissionDeniedException ex)
        {
            return new DefinitionLoadResult(
                [],
                null,
                SourceOf(key),
                [ServerDiagnostic("ELEMENT_DEFINITION_FORBIDDEN", ex.Message)]);
        }

        RememberBaseline(key, snapshot);
        return new DefinitionLoadResult(
            snapshot.Document.Definitions,
            snapshot.Version,
            SourceOf(key),
            snapshot.Diagnostics);
    }

    private DefinitionLoadResult OfflineLoad(DocumentKey key, string message) =>
        new([], null, SourceOf(key), [ServerDiagnostic("ELEMENT_DEFINITION_UNAVAILABLE", message)]);

    public async Task<DefinitionWriteResult> SaveAsync(
        DocumentKey key,
        IReadOnlyList<UserBlockDefinition> definitions,
        string? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (key.Kind == DocumentLocationKind.ServerArticle && !key.IsValid)
        {
            return new DefinitionWriteResult(
                DefinitionSaveStatus.NotPersisted,
                null,
                null,
                "请先保存文章再编辑元素定义。");
        }

        ArgumentNullException.ThrowIfNull(definitions);

        CompanionSnapshot snapshot;
        try
        {
            snapshot = await _documents.LoadAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (ElementDefinitionServiceUnavailableException)
        {
            return new DefinitionWriteResult(
                DefinitionSaveStatus.Offline,
                null,
                null,
                "当前无法连接服务器，元素定义未保存；请检查网络后重试。");
        }
        catch (ElementDefinitionPermissionDeniedException ex)
        {
            return new DefinitionWriteResult(
                DefinitionSaveStatus.Forbidden,
                null,
                null,
                string.IsNullOrWhiteSpace(ex.Message)
                    ? "只有作者可以修改该文章的元素定义。"
                    : ex.Message);
        }

        var document = new CompanionDocument
        {
            Format = CompanionDocumentFormat.CompanionV2,
            DocumentId = string.IsNullOrWhiteSpace(snapshot.Document.DocumentId)
                ? key.Value
                : snapshot.Document.DocumentId,
            Styles = snapshot.Document.Styles,
            Definitions = definitions,
            Composition = snapshot.Document.Composition,
            UnknownNodes = snapshot.Document.UnknownNodes
        };

        CompanionWriteResult write;
        try
        {
            write = await _documents
                .SaveAsync(key, document, expectedVersion, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ElementDefinitionServiceUnavailableException)
        {
            return new DefinitionWriteResult(
                DefinitionSaveStatus.Offline,
                null,
                null,
                "当前无法连接服务器，元素定义未保存；请检查网络后重试。");
        }
        catch (ElementDefinitionPermissionDeniedException ex)
        {
            return new DefinitionWriteResult(
                DefinitionSaveStatus.Forbidden,
                null,
                null,
                string.IsNullOrWhiteSpace(ex.Message)
                    ? "只有作者可以修改该文章的元素定义。"
                    : ex.Message);
        }

        switch (write.Status)
        {
            case CompanionSaveStatus.Saved:
            {
                var changedIds = DiffIds(snapshot.Document.Definitions, definitions);
                RememberBaseline(key, snapshot with { Document = document, Version = write.Version ?? snapshot.Version });
                Invalidated?.Invoke(
                    this,
                    new DefinitionInvalidatedEventArgs(key, StylesChanged: false, ElementsChanged: true, changedIds));
                return new DefinitionWriteResult(
                    DefinitionSaveStatus.Saved,
                    write.Version,
                    null,
                    write.Diagnostics.Count == 0 ? "元素定义已保存。" : Describe(write.Diagnostics));
            }

            case CompanionSaveStatus.NoChange:
                return new DefinitionWriteResult(
                    DefinitionSaveStatus.NoChange,
                    write.Version ?? snapshot.Version,
                    null,
                    "没有变化，未写入。");

            case CompanionSaveStatus.Conflict:
                // 冲突按段报告：只改定义段时 StylesChanged=false，绝不谎报整份成功。
                return new DefinitionWriteResult(
                    DefinitionSaveStatus.Conflict,
                    write.Version,
                    write.Conflict ?? new CompanionChangeReport(
                        StylesChanged: false,
                        ElementsChanged: true,
                        RemoteVersion: null,
                        ChangedDefinitionIds: null),
                    "元素定义已被其他处修改，本次未写入。请选择覆盖或放弃并重载。");

            case CompanionSaveStatus.Forbidden:
                return new DefinitionWriteResult(
                    DefinitionSaveStatus.Forbidden,
                    null,
                    null,
                    string.IsNullOrWhiteSpace(write.Message)
                        ? "只有作者可以修改该文章的元素定义。"
                        : write.Message);

            case CompanionSaveStatus.Offline:
                return new DefinitionWriteResult(
                    DefinitionSaveStatus.Offline,
                    null,
                    null,
                    write.Message ?? "当前无法连接服务器，元素定义未保存；请检查网络后重试。");

            default:
                return new DefinitionWriteResult(
                    DefinitionSaveStatus.IoFailure,
                    null,
                    null,
                    write.Message ?? "元素定义保存失败。");
        }
    }

    public async Task<DefinitionWriteResult> ImportAsync(
        DocumentKey key,
        IReadOnlyList<UserBlockDefinition> definitions,
        string? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (key.Kind != DocumentLocationKind.ServerArticle || _client is null)
        {
            return await SaveAsync(key, definitions, expectedVersion, cancellationToken).ConfigureAwait(false);
        }

        if (!key.IsValid)
        {
            return new DefinitionWriteResult(
                DefinitionSaveStatus.NotPersisted,
                null,
                null,
                "请先保存文章再编辑元素定义。");
        }

        var elementsXml = definitions.Count == 0
            ? string.Empty
            : UserBlockElementsSerializer.Serialize(definitions);

        try
        {
            var result = await _client
                .ImportElementDefinitionsAsync(key.Value, elementsXml, expectedVersion, cancellationToken)
                .ConfigureAwait(false);

            switch (result.Status)
            {
                case ImportElementDefinitionsStatus.Unchanged:
                    return new DefinitionWriteResult(
                        DefinitionSaveStatus.NoChange,
                        result.Version ?? result.CurrentVersion,
                        null,
                        "没有变化，未写入。");

                case ImportElementDefinitionsStatus.Saved:
                    Invalidated?.Invoke(
                        this,
                        new DefinitionInvalidatedEventArgs(key, false, true, definitions.Select(d => d.Id).ToArray()));
                    return new DefinitionWriteResult(
                        DefinitionSaveStatus.Saved,
                        result.Version,
                        null,
                        "已导入元素定义。");

                case ImportElementDefinitionsStatus.Conflict:
                    return new DefinitionWriteResult(
                        DefinitionSaveStatus.Conflict,
                        result.Version,
                        new CompanionChangeReport(false, true, result.CurrentVersion, null),
                        "元素定义已被其他处修改，导入未写入。请选择覆盖或放弃并重载。");

                default:
                    return new DefinitionWriteResult(
                        DefinitionSaveStatus.Forbidden,
                        null,
                        null,
                        string.IsNullOrWhiteSpace(result.Message)
                            ? "只有作者可以修改该文章的元素定义。"
                            : result.Message);
            }
        }
        catch (ElementDefinitionServiceUnavailableException)
        {
            return new DefinitionWriteResult(
                DefinitionSaveStatus.Offline,
                null,
                null,
                "当前无法连接服务器，导入未执行；请联网后重试。");
        }
        catch (ElementDefinitionPermissionDeniedException ex)
        {
            return new DefinitionWriteResult(
                DefinitionSaveStatus.Forbidden,
                null,
                null,
                string.IsNullOrWhiteSpace(ex.Message)
                    ? "只有作者可以修改该文章的元素定义。"
                    : ex.Message);
        }
    }

    public async Task<CompanionChangeReport> DetectChangesAsync(        DocumentKey key,
        string? baselineVersion,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _documents.LoadAsync(key, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(baselineVersion) || string.Equals(baselineVersion, snapshot.Version, StringComparison.Ordinal))
        {
            return new CompanionChangeReport(false, false, snapshot.Version, null);
        }

        if (!_baselines.TryGetValue(key, out var baseline)
            || !string.Equals(baseline.Version, baselineVersion, StringComparison.Ordinal))
        {
            // 没有基线内容可比对时只能保守地报“两段都变了”，让界面走重载/覆盖选择。
            return new CompanionChangeReport(true, true, snapshot.Version, null);
        }

        return new CompanionChangeReport(
            !string.Equals(baseline.StylesHash, snapshot.StylesHash, StringComparison.Ordinal),
            !string.Equals(baseline.ElementsHash, snapshot.ElementsHash, StringComparison.Ordinal),
            snapshot.Version,
            null);
    }

    private void RememberBaseline(DocumentKey key, CompanionSnapshot snapshot) =>
        _baselines[key] = new Baseline(
            snapshot.Version,
            snapshot.StylesHash,
            snapshot.ElementsHash,
            snapshot.Document.Definitions.Select(definition => definition.Id).ToArray());

    private static DefinitionSource SourceOf(DocumentKey key) =>
        key.Kind == DocumentLocationKind.ServerArticle ? DefinitionSource.Server : DefinitionSource.LocalFile;

    private static IReadOnlyList<string> DiffIds(
        IReadOnlyList<UserBlockDefinition> before,
        IReadOnlyList<UserBlockDefinition> after)
    {
        var changed = new List<string>();
        var previous = before.ToDictionary(definition => definition.Id, StringComparer.Ordinal);

        foreach (var definition in after)
        {
            if (!previous.TryGetValue(definition.Id, out var old)
                || !string.Equals(
                    UserBlockElementsSerializer.Serialize([old]),
                    UserBlockElementsSerializer.Serialize([definition]),
                    StringComparison.Ordinal))
            {
                changed.Add(definition.Id);
            }
        }

        foreach (var definition in before)
        {
            if (!after.Any(current => string.Equals(current.Id, definition.Id, StringComparison.Ordinal)))
            {
                changed.Add(definition.Id);
            }
        }

        return changed;
    }

    private static string Describe(IReadOnlyList<Application.Abstractions.Diagnostics.DiagnosticMessage> diagnostics) =>
        string.Join("；", diagnostics.Select(diagnostic => diagnostic.Message));

    private static Application.Abstractions.Diagnostics.DiagnosticMessage ServerDiagnostic(string code, string message) =>
        new()
        {
            Severity = Application.Abstractions.Diagnostics.DiagnosticSeverity.Warning,
            Code = code,
            Message = message
        };

    private sealed record Baseline(
        string Version,
        string StylesHash,
        string ElementsHash,
        IReadOnlyList<string> DefinitionIds);
}
