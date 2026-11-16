using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Parsing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence;

public sealed class FileService : IFileService
{
    // 伴随样式文件命名：与 markdown 文件同名再加 ".styles.xml"（如 note.md → note.md.styles.xml）。
    private const string StyleFileSuffix = ".styles.xml";
    // 旧版命名，仅读取兼容；保存一律写新命名。
    private const string LegacyStyleFileSuffix = ".style.xml";
    private static readonly Encoding Utf8Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private readonly IDocumentSerializer _serializer;
    private readonly IMarkdownParser _parser;
    private readonly ICompanionDocumentStore _companionStore;

    public FileService(IDocumentSerializer serializer, IMarkdownParser parser)
        : this(serializer, parser, new FileCompanionDocumentStore())
    {
    }

    public FileService(
        IDocumentSerializer serializer,
        IMarkdownParser parser,
        ICompanionDocumentStore companionStore)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _companionStore = companionStore ?? throw new ArgumentNullException(nameof(companionStore));
    }

    /// <summary>
    /// 样式所在的伴随文件路径：合并后样式段存放在 <c>&lt;doc&gt;.md.companion.xml</c>。
    /// </summary>
    public string GetStyleFilePath(string markdownFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownFilePath);
        return FileCompanionDocumentStore.GetCompanionFilePath(markdownFilePath);
    }

    /// <summary>
    /// 探测磁盘上实际存在的样式载体：合并伴随文件优先，其次旧命名 <c>.styles.xml</c> / <c>.style.xml</c>。
    /// 只做存在性判断，不解析内容、不写盘。
    /// </summary>
    public string? FindExistingStyleFilePath(string markdownFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownFilePath);
        var fullPath = Path.GetFullPath(markdownFilePath);

        var companionPath = FileCompanionDocumentStore.GetCompanionFilePath(fullPath);
        if (File.Exists(companionPath)) return companionPath;

        var preferred = fullPath + StyleFileSuffix;
        if (File.Exists(preferred)) return preferred;

        var legacy = fullPath + LegacyStyleFileSuffix;
        return File.Exists(legacy) ? legacy : null;
    }

    /// <summary>
    /// 用户块定义伴随文件的历史路径（markdown 全路径 + ".elements.xml"）。
    /// 首次保存迁移后定义并入合并伴随文件，此处仅用于兼容旧文件与界面提示。
    /// </summary>
    public string GetElementsFilePath(string markdownFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownFilePath);
        return Path.GetFullPath(markdownFilePath) + UserBlockElementsSerializer.FileSuffix;
    }

    public Task<DocumentLoadResult> LoadAsync(string markdownFilePath, CancellationToken cancellationToken = default) =>
        LoadAsync(markdownFilePath, loadStyle: true, cancellationToken);

    public async Task<DocumentLoadResult> LoadAsync(string markdownFilePath, bool loadStyle, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownFilePath);
        var fullMarkdownPath = Path.GetFullPath(markdownFilePath);

        if (!File.Exists(fullMarkdownPath))
            throw new FileNotFoundException("Markdown file was not found.", fullMarkdownPath);

        string markdown;
        using (var reader = new StreamReader(fullMarkdownPath, Utf8Encoding, detectEncodingFromByteOrderMarks: true))
        {
            markdown = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var markdownHash = await ComputeHashAsync(fullMarkdownPath, cancellationToken).ConfigureAwait(false);
        var markdownInfo = new FileInfo(fullMarkdownPath);

        // 伴随文档是样式段与定义段的唯一权威来源：读取从不写盘，迁移只在首次保存时发生。
        var snapshot = await _companionStore
            .LoadAsync(DocumentKey.LocalFile(fullMarkdownPath), cancellationToken)
            .ConfigureAwait(false);
        var companion = snapshot.Document;
        var loadDiagnostics = new List<DiagnosticMessage>(snapshot.Diagnostics);

        // loadStyle=false 表示用户拒绝载入样式：仍解析出干净的纯 Markdown 文档，
        // 但快照必须记住磁盘上样式载体的真实哈希，后续保存时原样保留、绝不覆盖。
        MarkdownDocument? documentFromXml = null;
        if (loadStyle && companion.Styles is not null)
        {
            var package = CompanionDocumentSerializer.BuildLegacyStylePackage(companion);
            using var styleStream = new MemoryStream(
                Utf8Encoding.GetBytes(package!.ToString(SaveOptions.DisableFormatting)));
            documentFromXml = _serializer.Deserialize(styleStream, markdown, fullMarkdownPath);
        }

        // 元素级结构图存在 .md.graph.xml，按 element:{id} 挂回定义；没有图就沿用声明结构。
        // 定义保持“来源形态”（ref 原文），解析路径吃“展开形态”：组合在装载期一次展开，
        // 渲染器与解析器永远看不到 ref。
        var attached = ElementGraphFiles.Attach(fullMarkdownPath, companion.Definitions);
        loadDiagnostics.AddRange(attached.Diagnostics);
        var definitions = attached.Definitions;
        var composition = companion.Composition;
        var parseResult = _parser.Parse(
            markdown,
            previous: documentFromXml,
            mode: ParseMode.Loading,
            userBlocks: UserBlockCompositionRules.ExpandAll(definitions, composition),
            sourceUserBlocks: definitions,
            composition: composition);
        loadDiagnostics.AddRange(parseResult.Diagnostics);
        var document = parseResult.Document;
        document.FilePath = fullMarkdownPath;
        document.ApplyGraphStages(attached.Stages);
        document.MarkSaved();

        var style = await ResolveStyleArtifactAsync(fullMarkdownPath, snapshot, cancellationToken).ConfigureAwait(false);
        var loadedSnapshot = new DocumentFileSnapshot(
            fullMarkdownPath, style.Path, markdownHash, style.Hash,
            markdownInfo.LastWriteTimeUtc, style.LastWriteTimeUtc,
            style.Exists, StyleLoadDeclined: !loadStyle)
        {
            CompanionVersion = snapshot.Version
        };

        return new DocumentLoadResult(document, loadedSnapshot) { Diagnostics = loadDiagnostics };
    }

    /// <summary>
    /// 从内存文本加载（浏览器通道）：只解析 Markdown，文档无文件路径、无快照，
    /// 标记为已保存初始态；与 NewDocument 的区别是内容已解析完毕。
    /// </summary>
    public DocumentLoadResult LoadFromText(string markdown) =>
        LoadFromText(markdown, styleXml: null, elementsXml: null);

    /// <summary>
    /// 从内存文本加载并附带伴随样式 XML 文本（浏览器端多文件选择配对通道）。
    /// 样式 XML 反序列化后作为解析 previous 参与样式继承，与磁盘打开路径一致；
    /// styleXml 为空时按纯 Markdown 解析。文档无文件路径、无快照。
    /// </summary>
    public DocumentLoadResult LoadFromText(string markdown, string? styleXml) =>
        LoadFromText(markdown, styleXml, elementsXml: null);

    /// <summary>
    /// 从内存文本加载并附带用户块定义 XML（浏览器端多文件选择配对通道）。
    /// elementsXml 为空/损坏时按可用定义解析，损坏只产生诊断。
    /// </summary>
    public DocumentLoadResult LoadFromText(string markdown, string? styleXml, string? elementsXml)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        MarkdownDocument? documentFromXml = null;
        if (!string.IsNullOrWhiteSpace(styleXml))
        {
            using var styleStream = new MemoryStream(Utf8Encoding.GetBytes(styleXml));
            documentFromXml = _serializer.Deserialize(styleStream, markdown, string.Empty);
        }

        var elements = UserBlockElementsSerializer.Deserialize(elementsXml);
        var parseResult = _parser.Parse(
            markdown,
            previous: documentFromXml,
            mode: ParseMode.Loading,
            userBlocks: UserBlockCompositionRules.ExpandAll(elements.Definitions, elements.EffectiveComposition),
            sourceUserBlocks: elements.Definitions,
            composition: elements.EffectiveComposition);
        var document = parseResult.Document;
        document.FilePath = string.Empty;
        document.MarkSaved();

        var diagnostics = new List<DiagnosticMessage>(elements.Diagnostics);
        diagnostics.AddRange(parseResult.Diagnostics);

        return new DocumentLoadResult(document, Snapshot: null) { Diagnostics = diagnostics };
    }

    /// <summary>
    /// 内存导出样式包 XML（不落盘）：与伴随文件样式段同一序列化口径，
    /// 供无文件路径场景（文章库样式持久化）取用。
    /// </summary>
    public string ExportStyleXml(MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var xmlStream = new MemoryStream();
        lock (document)
        {
            var markdownBytes = Utf8Encoding.GetBytes(document.SourceMarkdown ?? string.Empty);
            var sourceName = string.IsNullOrWhiteSpace(document.FilePath)
                ? "document.md"
                : Path.GetFileName(document.FilePath);
            _serializer.Serialize(document, xmlStream, sourceName, ComputeSha256(markdownBytes));
        }

        return Utf8Encoding.GetString(xmlStream.ToArray());
    }

    public async Task<DocumentFileSnapshot> SaveAsync(MarkdownDocument document, DocumentFileSnapshot? expectedSnapshot = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(document.FilePath))
            throw new ArgumentException("Document.FilePath must be specified before saving.", nameof(document));

        var markdownFilePath = Path.GetFullPath(document.FilePath);
        var companionPath = FileCompanionDocumentStore.GetCompanionFilePath(markdownFilePath);
        var directory = Path.GetDirectoryName(markdownFilePath) ?? throw new IOException($"Invalid path '{markdownFilePath}'.");

        Directory.CreateDirectory(directory);
        await EnsureMarkdownUnmodifiedAsync(markdownFilePath, expectedSnapshot, cancellationToken).ConfigureAwait(false);

        byte[] markdownBytes;
        lock (document)
        {
            markdownBytes = Utf8Encoding.GetBytes(document.SourceMarkdown ?? string.Empty);
            document.Metadata.ModifiedUtc = DateTime.UtcNow;
            document.Validate();
        }
        var markdownHash = ComputeSha256(markdownBytes);

        var key = DocumentKey.LocalFile(markdownFilePath);

        // 先读现状：既用于保留“拒绝载入样式”的样式段，也用于延续未知节点。
        var current = await _companionStore.LoadAsync(key, cancellationToken).ConfigureAwait(false);

        // 损坏/更高版本的文件在写任何字节之前就拒绝：绝不把“解析失败”当成“没有定义”覆盖。
        await EnsureCompanionWritableAsync(key, current, companionPath, cancellationToken).ConfigureAwait(false);

        // 拒绝载入且此后未改过任何节点样式时，样式段原样保留；
        // 磁盘上的既有样式不会被空白样式覆盖。
        var preserveStyleFile = expectedSnapshot?.StyleLoadDeclined == true && !document.IsStyleModified;

        XElement? styles;
        if (preserveStyleFile)
        {
            styles = current.Document.Styles;
        }
        else
        {
            using var xmlMemoryStream = new MemoryStream();
            lock (document)
            {
                _serializer.Serialize(document, xmlMemoryStream, Path.GetFileName(markdownFilePath), markdownHash);
            }

            styles = CompanionDocumentSerializer
                .FromLegacyStyles(Utf8Encoding.GetString(xmlMemoryStream.ToArray()))
                .Styles;
        }

        IReadOnlyList<UserBlockDefinition> definitions;
        UserBlockComposition composition;
        lock (document)
        {
            definitions = document.UserBlockDefinitions.ToArray();
            composition = document.Composition;
        }

        var companion = new CompanionDocument
        {
            Format = CompanionDocumentFormat.CompanionV2,
            DocumentId = string.IsNullOrWhiteSpace(current.Document.DocumentId)
                ? document.Id
                : current.Document.DocumentId,
            Styles = styles,
            Definitions = definitions,
            Composition = composition,
            UnknownNodes = current.Document.UnknownNodes
        };

        var markdownTemp = CreateTempPath(directory, Path.GetFileName(markdownFilePath));
        string? markdownBackup = null;
        var markdownExistedBefore = File.Exists(markdownFilePath);

        try
        {
            await WriteAllBytesAsync(markdownTemp, markdownBytes, cancellationToken).ConfigureAwait(false);
            await EnsureMarkdownUnmodifiedAsync(markdownFilePath, expectedSnapshot, cancellationToken).ConfigureAwait(false);

            if (markdownExistedBefore)
            {
                markdownBackup = CreateTempPath(directory, Path.GetFileName(markdownFilePath) + ".bak");
                File.Copy(markdownFilePath, markdownBackup, overwrite: true);
            }

            ReplaceFileSafely(markdownTemp, markdownFilePath);
            markdownTemp = string.Empty;

            // 样式段与定义段一次原子写入：任一段失败整次保存失败，不出现半份伴随文档。
            var write = await _companionStore
                .SaveAsync(key, companion, expectedSnapshot?.CompanionVersion, cancellationToken)
                .ConfigureAwait(false);

            if (write.Status == CompanionSaveStatus.Conflict)
            {
                throw new FileSaveConflictException(
                    companionPath,
                    expectedSnapshot?.CompanionVersion,
                    write.Conflict?.RemoteVersion,
                    DescribeConflict(write.Conflict));
            }

            if (write.Status is not (CompanionSaveStatus.Saved or CompanionSaveStatus.NoChange))
            {
                throw new IOException(write.Message ?? $"伴随文档保存失败（{companionPath}）。");
            }

            var markdownInfo = new FileInfo(markdownFilePath);
            var companionInfo = new FileInfo(companionPath);

            lock (document)
            {
                document.FilePath = markdownFilePath;
                document.MarkSaved();
            }

            return new DocumentFileSnapshot(
                markdownFilePath, companionPath, markdownHash,
                File.Exists(companionPath) ? await ComputeHashAsync(companionPath, cancellationToken).ConfigureAwait(false) : string.Empty,
                markdownInfo.LastWriteTimeUtc,
                companionInfo.Exists ? companionInfo.LastWriteTimeUtc : DateTime.MinValue,
                StyleFileExists: File.Exists(companionPath),
                StyleLoadDeclined: preserveStyleFile)
            {
                CompanionVersion = write.Version ?? expectedSnapshot?.CompanionVersion
            };
        }
        catch (Exception primaryEx)
        {
            List<Exception>? restoreErrors = null;

            try
            {
                if (markdownBackup is not null && File.Exists(markdownBackup))
                {
                    File.Copy(markdownBackup, markdownFilePath, overwrite: true);
                }
                else if (!markdownExistedBefore && File.Exists(markdownFilePath))
                {
                    // 另存为新文件时伴随写入失败：撤掉刚新建的 markdown，避免留下半份结果。
                    File.Delete(markdownFilePath);
                }
            }
            catch (Exception restoreEx)
            {
                (restoreErrors ??= []).Add(
                    new IOException($"无法从备份还原 Markdown 文件：{markdownFilePath}", restoreEx));
            }

            if (restoreErrors is not null)
            {
                throw new AggregateException(new[] { primaryEx }.Concat(restoreErrors));
            }

            throw;
        }
        finally
        {
            DeleteTempFileIfExists(markdownTemp);
            DeleteTempFileIfExists(markdownBackup);
        }
    }

    public async Task<string> ComputeHashAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath)) return string.Empty;

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>保存前置：损坏/更高版本的伴随文件一律拒绝，返回带路径与原因的可执行提示。</summary>
    private async Task EnsureCompanionWritableAsync(
        DocumentKey key,
        CompanionSnapshot current,
        string companionPath,
        CancellationToken cancellationToken)
    {
        var hasError = current.Document.IsReadOnly
            || current.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        if (!hasError)
        {
            return;
        }

        var unreadable = await _companionStore
            .FindUnreadableInputAsync(key, cancellationToken)
            .ConfigureAwait(false);

        var path = unreadable?.Path ?? companionPath;
        var reason = unreadable?.Reason
            ?? current.Document.ReadOnlyReason
            ?? current.Diagnostics.First(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Message;

        throw new IOException(
            $"伴随文件当前不可安全覆盖，已拒绝保存：{path}。原因：{reason} " +
            "请先备份或修复该文件，或显式选择“另存为新文件”。");
    }

    private async Task<StyleArtifact> ResolveStyleArtifactAsync(
        string markdownFilePath,
        CompanionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.CompanionFileExists)
        {
            var companionPath = FileCompanionDocumentStore.GetCompanionFilePath(markdownFilePath);
            var info = new FileInfo(companionPath);
            var hash = await ComputeHashAsync(companionPath, cancellationToken).ConfigureAwait(false);
            return new StyleArtifact(true, companionPath, hash, info.LastWriteTimeUtc);
        }

        var legacyPath = FindExistingStyleFilePath(markdownFilePath);
        if (legacyPath is not null)
        {
            var info = new FileInfo(legacyPath);
            var hash = await ComputeHashAsync(legacyPath, cancellationToken).ConfigureAwait(false);
            return new StyleArtifact(true, legacyPath, hash, info.LastWriteTimeUtc);
        }

        return new StyleArtifact(false, GetStyleFilePath(markdownFilePath), string.Empty, DateTime.MinValue);
    }

    private async Task EnsureMarkdownUnmodifiedAsync(
        string markdownPath,
        DocumentFileSnapshot? expected,
        CancellationToken cancellationToken)
    {
        if (expected is null) return;

        var actualMarkdownHash = await ComputeHashAsync(markdownPath, cancellationToken).ConfigureAwait(false);
        if (!HashesEqual(expected.MarkdownHash, actualMarkdownHash))
            throw new FileSaveConflictException(markdownPath, expected.MarkdownHash, actualMarkdownHash);

        // 有伴随文档基线时，样式/定义段冲突由 store 按段判定；
        // 没有基线的旧快照仍按文件哈希校验，保持外部调用方语义不变。
        if (expected.CompanionVersion is not null) return;

        var styleCheckPath = expected.StyleFileExists
            ? expected.StyleFilePath
            : GetStyleFilePath(markdownPath);
        var actualStyleHash = await ComputeHashAsync(styleCheckPath, cancellationToken).ConfigureAwait(false);
        var expectedStyleHash = expected.StyleFileExists ? expected.StyleFileHash : string.Empty;
        if (!HashesEqual(expectedStyleHash, actualStyleHash))
            throw new FileSaveConflictException(styleCheckPath, expectedStyleHash, actualStyleHash);
    }

    private static string DescribeConflict(CompanionChangeReport? conflict) => conflict switch
    {
        null => "伴随文档已被其他程序修改，本次未写入。",
        { StylesChanged: true, ElementsChanged: true } => "文本文档样式与元素定义都已被其他程序修改，本次未写入。",
        { StylesChanged: true } => "文本文档样式已被其他程序修改，本次未写入。",
        { ElementsChanged: true } => "元素定义已被其他程序修改，本次未写入。",
        _ => "伴随文档已被其他程序修改，本次未写入。"
    };

    private static async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string ComputeSha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool HashesEqual(string? a, string? b) => string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    private static string CreateTempPath(string directory, string fileName) => Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");

    private static void ReplaceFileSafely(string tempPath, string destPath)
    {
        if (!File.Exists(tempPath)) return;
        if (!File.Exists(destPath))
        {
            File.Move(tempPath, destPath);
            return;
        }

        try
        {
            File.Replace(tempPath, destPath, null, ignoreMetadataErrors: true);
        }
        catch
        {
            var backupPath = destPath + ".bak." + Guid.NewGuid().ToString("N");
            try
            {
                File.Move(destPath, backupPath);
                File.Move(tempPath, destPath);
                File.Delete(backupPath);
            }
            catch
            {
                if (File.Exists(backupPath) && !File.Exists(destPath))
                {
                    File.Move(backupPath, destPath);
                }
                throw;
            }
        }
    }

    private static void DeleteTempFileIfExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private readonly record struct StyleArtifact(bool Exists, string Path, string Hash, DateTime LastWriteTimeUtc);
}
