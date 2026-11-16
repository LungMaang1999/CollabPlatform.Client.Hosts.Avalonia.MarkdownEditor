using System.Xml.Linq;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence;

/// <summary>
/// 服务端文章的实现（本期为桩，版本/冲突字段与服务端契约一致）。
/// <para>服务端只存定义段；样式段仍走伴随文件存储，因此一次保存是两次写：
/// 两段各自报告结果，任一段失败都不会谎报整份保存成功。</para>
/// <para>不自行判断权限：<see cref="ElementDefinitionPermissionDeniedException"/> 原样映射为 <c>Forbidden</c>。</para>
/// </summary>
public sealed class GrpcCompanionDocumentStore : ICompanionDocumentStore
{
    private const string VersionPrefix = "article";
    private const string EmptyVersion = "0";

    private readonly IElementDefinitionServiceClient _client;
    private readonly ICompanionDocumentStore? _styleStore;

    public GrpcCompanionDocumentStore(
        IElementDefinitionServiceClient client,
        ICompanionDocumentStore? styleStore = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _styleStore = styleStore;
    }

    public async Task<CompanionSnapshot> LoadAsync(DocumentKey key, CancellationToken cancellationToken = default)
    {
        EnsureServerKey(key);

        var payload = await _client.GetElementDefinitionsAsync(key.Value, cancellationToken).ConfigureAwait(false);
        var parsed = UserBlockElementsSerializer.Deserialize(payload.ElementsXml);
        var diagnostics = new List<DiagnosticMessage>(parsed.Diagnostics);

        XElement? styles = null;
        var stylesVersion = EmptyVersion;
        if (_styleStore is not null)
        {
            try
            {
                var styleSnapshot = await _styleStore.LoadAsync(key, cancellationToken).ConfigureAwait(false);
                styles = styleSnapshot.Document.Styles;
                stylesVersion = styleSnapshot.Version;
                diagnostics.AddRange(styleSnapshot.Diagnostics);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                diagnostics.Add(Warning("SERVER_STYLES_UNAVAILABLE", $"样式段读取失败：{ex.Message}"));
            }
        }
        else
        {
            diagnostics.Add(Warning(
                "SERVER_STYLES_SEPARATE",
                "服务端文章只保存定义段；样式段随文章内容单独存档。"));
        }

        var document = new CompanionDocument
        {
            Format = CompanionDocumentFormat.CompanionV2,
            DocumentId = key.Value,
            Styles = styles,
            Definitions = parsed.Definitions,
            Diagnostics = diagnostics
        };

        return new CompanionSnapshot(
            document,
            CompanionFileExists: true,
            Version: Encode(payload.Version, stylesVersion),
            StylesHash: CompanionDocumentFingerprint.ComputeStylesHash(styles),
            ElementsHash: CompanionDocumentFingerprint.ComputeElementsHash(parsed.Definitions),
            LegacyFiles: [],
            Diagnostics: diagnostics);
    }

    public Task<(string Path, string Reason)?> FindUnreadableInputAsync(
        DocumentKey key,
        CancellationToken cancellationToken = default)
    {
        EnsureServerKey(key);

        // 服务端没有“本地损坏文件”这一状态，拒绝语义由服务端返回。
        return Task.FromResult<(string Path, string Reason)?>(null);
    }

    public async Task<CompanionWriteResult> SaveAsync(
        DocumentKey key,
        CompanionDocument document,
        string? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        EnsureServerKey(key);
        ArgumentNullException.ThrowIfNull(document);

        var (expectedDefinitionVersion, expectedStylesVersion) = Decode(expectedVersion);

        var current = await _client.GetElementDefinitionsAsync(key.Value, cancellationToken).ConfigureAwait(false);
        var elementsXml = Canonical(document.Definitions);
        var definitionsUnchanged = string.Equals(
            Canonical(DeserializeOrEmpty(current.ElementsXml)),
            elementsXml,
            StringComparison.Ordinal);

        var newDefinitionVersion = current.Version;
        if (!definitionsUnchanged)
        {
            SaveElementDefinitionsPayload saved;
            try
            {
                saved = await _client
                    .SaveElementDefinitionsAsync(key.Value, elementsXml, expectedDefinitionVersion, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ElementDefinitionPermissionDeniedException ex)
            {
                return Forbidden(ex.Message);
            }

            if (saved.Conflict)
            {
                return new CompanionWriteResult(
                    CompanionSaveStatus.Conflict,
                    null,
                    new CompanionChangeReport(
                        StylesChanged: false,
                        ElementsChanged: true,
                        RemoteVersion: saved.CurrentVersion,
                        ChangedDefinitionIds: null),
                    [],
                    "元素定义已被其他处修改，本次未写入。");
            }

            if (!saved.Success)
            {
                return CompanionWriteResult.Failed(
                    CompanionSaveStatus.IoFailure,
                    saved.Message ?? "元素定义保存失败。");
            }

            newDefinitionVersion = saved.Version ?? current.Version;
        }

        var stylesVersion = expectedStylesVersion;
        if (document.Styles is not null)
        {
            if (_styleStore is null)
            {
                return new CompanionWriteResult(
                    CompanionSaveStatus.IoFailure,
                    null,
                    null,
                    [],
                    definitionsUnchanged
                        ? "样式段没有可用的存储，本次未写入。"
                        : "元素定义已保存，但样式段没有可用的存储，本次样式未保存；两段需分别处理。");
            }

            var stylesDocument = new CompanionDocument
            {
                Format = document.Format,
                DocumentId = document.DocumentId,
                Styles = document.Styles,
                Definitions = [],
                UnknownNodes = []
            };

            CompanionWriteResult styleWrite;
            try
            {
                styleWrite = await _styleStore
                    .SaveAsync(key, stylesDocument, stylesVersion, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ElementDefinitionPermissionDeniedException ex)
            {
                return Forbidden(ex.Message);
            }

            if (styleWrite.Status is CompanionSaveStatus.Conflict or CompanionSaveStatus.Forbidden
                or CompanionSaveStatus.IoFailure or CompanionSaveStatus.Offline)
            {
                var prefix = definitionsUnchanged
                    ? "样式段未保存"
                    : "元素定义已保存，但样式段未保存";
                return styleWrite.Status == CompanionSaveStatus.Conflict
                    ? new CompanionWriteResult(
                        CompanionSaveStatus.Conflict,
                        null,
                        styleWrite.Conflict ?? new CompanionChangeReport(
                            StylesChanged: true,
                            ElementsChanged: !definitionsUnchanged,
                            RemoteVersion: null,
                            ChangedDefinitionIds: null),
                        styleWrite.Diagnostics,
                        $"{prefix}：文中样式已被其他处修改；两段需分别处理。")
                    : new CompanionWriteResult(
                        styleWrite.Status,
                        null,
                        null,
                        styleWrite.Diagnostics,
                        $"{prefix}：{styleWrite.Message ?? "未知原因"}");
            }

            stylesVersion = styleWrite.Version ?? stylesVersion;
        }

        var newVersion = Encode(newDefinitionVersion, stylesVersion);
        return definitionsUnchanged && document.Styles is null
            ? CompanionWriteResult.NoChange(newVersion)
            : CompanionWriteResult.Saved(newVersion, [], document.Styles is null ? null : "元素定义与样式段均已保存。");
    }

    private static void EnsureServerKey(DocumentKey key)
    {
        if (key.Kind != DocumentLocationKind.ServerArticle)
        {
            throw new ArgumentException("GrpcCompanionDocumentStore 只处理服务端文章。", nameof(key));
        }

        if (!key.IsValid)
        {
            throw new ArgumentException("文档尚未落库，无法访问元素定义。", nameof(key));
        }
    }

    private static (string DefinitionVersion, string StylesVersion) Decode(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return (EmptyVersion, EmptyVersion);

        var parts = version.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0] != VersionPrefix) return (version!, EmptyVersion);

        var definitionVersion = EmptyVersion;
        var stylesVersion = EmptyVersion;
        foreach (var part in parts.Skip(1))
        {
            if (part.StartsWith("d=", StringComparison.Ordinal)) definitionVersion = part[2..];
            else if (part.StartsWith("s=", StringComparison.Ordinal)) stylesVersion = part[2..];
        }

        return (definitionVersion, stylesVersion);
    }

    private static string Encode(string definitionVersion, string stylesVersion) =>
        $"{VersionPrefix}|d={definitionVersion}|s={stylesVersion}";

    private static IReadOnlyList<Domain.Syntax.UserBlockDefinition> DeserializeOrEmpty(string? xml) =>
        string.IsNullOrWhiteSpace(xml)
            ? []
            : UserBlockElementsSerializer.Deserialize(xml).Definitions;

    private static string Canonical(IReadOnlyList<Domain.Syntax.UserBlockDefinition> definitions) =>
        definitions.Count == 0 ? string.Empty : UserBlockElementsSerializer.Serialize(definitions);

    private static CompanionWriteResult Forbidden(string? message) =>
        CompanionWriteResult.Failed(
            CompanionSaveStatus.Forbidden,
            string.IsNullOrWhiteSpace(message) ? "只有作者可以修改该文章的元素定义。" : message);

    private static DiagnosticMessage Warning(string code, string message) =>
        new() { Severity = DiagnosticSeverity.Warning, Code = code, Message = message };
}
