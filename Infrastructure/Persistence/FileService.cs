using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Parsing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

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

    public FileService(IDocumentSerializer serializer, IMarkdownParser parser)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
    }

    public string GetStyleFilePath(string markdownFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownFilePath);
        return Path.GetFullPath(markdownFilePath) + StyleFileSuffix;
    }

    public string? FindExistingStyleFilePath(string markdownFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownFilePath);
        var preferred = Path.GetFullPath(markdownFilePath) + StyleFileSuffix;
        if (File.Exists(preferred)) return preferred;

        var legacy = Path.GetFullPath(markdownFilePath) + LegacyStyleFileSuffix;
        return File.Exists(legacy) ? legacy : null;
    }

    public Task<DocumentLoadResult> LoadAsync(string markdownFilePath, CancellationToken cancellationToken = default) =>
        LoadAsync(markdownFilePath, loadStyle: true, cancellationToken);

    public async Task<DocumentLoadResult> LoadAsync(string markdownFilePath, bool loadStyle, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownFilePath);
        var fullMarkdownPath = Path.GetFullPath(markdownFilePath);
        var preferredStylePath = GetStyleFilePath(fullMarkdownPath);

        if (!File.Exists(fullMarkdownPath))
            throw new FileNotFoundException("Markdown file was not found.", fullMarkdownPath);

        string markdown;
        using (var reader = new StreamReader(fullMarkdownPath, Utf8Encoding, detectEncodingFromByteOrderMarks: true))
        {
            markdown = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var markdownHash = await ComputeHashAsync(fullMarkdownPath, cancellationToken).ConfigureAwait(false);
        var markdownInfo = new FileInfo(fullMarkdownPath);

        // loadStyle=false 表示用户拒绝载入样式：仍解析出干净的纯 Markdown 文档，
        // 但快照必须记住磁盘上样式文件的真实哈希，后续保存时原样保留、绝不覆盖。
        MarkdownDocument? documentFromXml = null;
        var existingStylePath = FindExistingStyleFilePath(fullMarkdownPath);

        if (loadStyle && existingStylePath is not null)
        {
            await using var styleStream = new FileStream(
                existingStylePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            documentFromXml = _serializer.Deserialize(styleStream, markdown, fullMarkdownPath);
        }

        var parseResult = _parser.Parse(markdown, previous: documentFromXml, mode: ParseMode.Loading);
        var document = parseResult.Document;
        document.FilePath = fullMarkdownPath;
        document.MarkSaved();

        if (loadStyle && existingStylePath is not null)
        {
            var styleHash = await ComputeHashAsync(existingStylePath, cancellationToken).ConfigureAwait(false);
            var styleInfo = new FileInfo(existingStylePath);
            var snapshot = new DocumentFileSnapshot(
                fullMarkdownPath, existingStylePath, markdownHash, styleHash,
                markdownInfo.LastWriteTimeUtc, styleInfo.LastWriteTimeUtc,
                StyleFileExists: true, StyleLoadDeclined: false);
            return new DocumentLoadResult(document, snapshot);
        }

        if (existingStylePath is null)
        {
            var missingSnapshot = DocumentFileSnapshot.ForMissingStyleFile(fullMarkdownPath, markdownHash, markdownInfo.LastWriteTimeUtc, preferredStylePath);
            return new DocumentLoadResult(document, missingSnapshot);
        }

        var declinedHash = await ComputeHashAsync(existingStylePath, cancellationToken).ConfigureAwait(false);
        var declinedInfo = new FileInfo(existingStylePath);
        var declinedSnapshot = new DocumentFileSnapshot(
            fullMarkdownPath, existingStylePath, markdownHash, declinedHash,
            markdownInfo.LastWriteTimeUtc, declinedInfo.LastWriteTimeUtc,
            StyleFileExists: true, StyleLoadDeclined: true);
        return new DocumentLoadResult(document, declinedSnapshot);
    }

    /// <summary>
    /// 从内存文本加载（浏览器通道）：只解析 Markdown，文档无文件路径、无快照，
    /// 标记为已保存初始态；与 NewDocument 的区别是内容已解析完毕。
    /// </summary>
    public DocumentLoadResult LoadFromText(string markdown) =>
        LoadFromText(markdown, styleXml: null);

    /// <summary>
    /// 从内存文本加载并附带伴随样式 XML 文本（浏览器端多文件选择配对通道）。
    /// 样式 XML 反序列化后作为解析 previous 参与样式继承，与磁盘打开路径一致；
    /// styleXml 为空时按纯 Markdown 解析。文档无文件路径、无快照。
    /// </summary>
    public DocumentLoadResult LoadFromText(string markdown, string? styleXml)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        MarkdownDocument? documentFromXml = null;
        if (!string.IsNullOrWhiteSpace(styleXml))
        {
            using var styleStream = new MemoryStream(Utf8Encoding.GetBytes(styleXml));
            documentFromXml = _serializer.Deserialize(styleStream, markdown, string.Empty);
        }

        var parseResult = _parser.Parse(markdown, previous: documentFromXml, mode: ParseMode.Loading);
        var document = parseResult.Document;
        document.FilePath = string.Empty;
        document.MarkSaved();

        return new DocumentLoadResult(document, Snapshot: null);
    }

    /// <summary>
    /// 内存导出样式包 XML（不落盘）：与保存 .md.styles.xml 同一序列化口径，
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
        var styleFilePath = GetStyleFilePath(markdownFilePath);
        var directory = Path.GetDirectoryName(markdownFilePath) ?? throw new IOException($"Invalid path '{markdownFilePath}'.");

        Directory.CreateDirectory(directory);
        await EnsureNoExternalModificationAsync(markdownFilePath, styleFilePath, expectedSnapshot, cancellationToken).ConfigureAwait(false);

        byte[] markdownBytes;
        lock (document)
        {
            markdownBytes = Utf8Encoding.GetBytes(document.SourceMarkdown ?? string.Empty);
            document.Metadata.ModifiedUtc = DateTime.UtcNow;
            document.Validate();
        }
        var markdownHash = ComputeSha256(markdownBytes);

        // 拒绝载入且此后未改过任何节点样式时，保存只更新 .md；
        // 磁盘上的样式文件原样保留，避免用空白样式覆盖用户既有样式。
        var preserveStyleFile = expectedSnapshot?.StyleLoadDeclined == true && !document.IsStyleModified;

        byte[]? styleBytes = null;
        var styleHash = string.Empty;
        if (!preserveStyleFile)
        {
            await using var xmlMemoryStream = new MemoryStream();
            lock (document)
            {
                _serializer.Serialize(document, xmlMemoryStream, Path.GetFileName(markdownFilePath), markdownHash);
            }
            styleBytes = xmlMemoryStream.ToArray();
            styleHash = ComputeSha256(styleBytes);
        }

        var markdownTemp = CreateTempPath(directory, Path.GetFileName(markdownFilePath));
        var styleTemp = preserveStyleFile ? string.Empty : CreateTempPath(directory, Path.GetFileName(styleFilePath));

        string? markdownBackup = null;
        string? styleBackup = null;

        try
        {
            await WriteAllBytesAsync(markdownTemp, markdownBytes, cancellationToken).ConfigureAwait(false);
            if (styleBytes is not null)
            {
                await WriteAllBytesAsync(styleTemp, styleBytes, cancellationToken).ConfigureAwait(false);
            }

            await EnsureNoExternalModificationAsync(markdownFilePath, styleFilePath, expectedSnapshot, cancellationToken).ConfigureAwait(false);

            if (File.Exists(markdownFilePath))
            {
                markdownBackup = CreateTempPath(directory, Path.GetFileName(markdownFilePath) + ".bak");
                File.Copy(markdownFilePath, markdownBackup, overwrite: true);
            }

            if (styleBytes is not null && File.Exists(styleFilePath))
            {
                styleBackup = CreateTempPath(directory, Path.GetFileName(styleFilePath) + ".bak");
                File.Copy(styleFilePath, styleBackup, overwrite: true);
            }

            ReplaceFileSafely(markdownTemp, markdownFilePath);
            markdownTemp = string.Empty;

            if (styleBytes is not null)
            {
                ReplaceFileSafely(styleTemp, styleFilePath);
                styleTemp = string.Empty;
            }

            var markdownInfo = new FileInfo(markdownFilePath);

            lock (document)
            {
                document.FilePath = markdownFilePath;
                document.MarkSaved();
            }

            if (preserveStyleFile)
            {
                // 保留场景：目标位置已有样式文件则记录其当前哈希（未改动）；
                // 没有则按缺失记录，保持拒绝载入标记供后续保存延续该语义。
                var preservedPath = FindExistingStyleFilePath(markdownFilePath);
                if (preservedPath is not null)
                {
                    var preservedHash = await ComputeHashAsync(preservedPath, cancellationToken).ConfigureAwait(false);
                    var preservedInfo = new FileInfo(preservedPath);
                    return new DocumentFileSnapshot(
                        markdownFilePath, preservedPath, markdownHash, preservedHash,
                        markdownInfo.LastWriteTimeUtc, preservedInfo.LastWriteTimeUtc,
                        StyleFileExists: true, StyleLoadDeclined: true);
                }

                return new DocumentFileSnapshot(
                    markdownFilePath, styleFilePath, markdownHash, string.Empty,
                    markdownInfo.LastWriteTimeUtc, DateTime.MinValue,
                    StyleFileExists: false, StyleLoadDeclined: true);
            }

            var styleInfo = new FileInfo(styleFilePath);
            return new DocumentFileSnapshot(
                markdownFilePath, styleFilePath, markdownHash, styleHash,
                markdownInfo.LastWriteTimeUtc, styleInfo.LastWriteTimeUtc,
                StyleFileExists: true, StyleLoadDeclined: false);
        }
        catch (Exception primaryEx)
        {
            List<Exception>? restoreErrors = null;

            if (markdownBackup is not null && File.Exists(markdownBackup))
            {
                try { File.Copy(markdownBackup, markdownFilePath, overwrite: true); }
                catch (Exception restoreEx)
                {
                    (restoreErrors ??= []).Add(
                        new IOException($"无法从备份还原 Markdown 文件：{markdownFilePath}", restoreEx));
                }
            }
            if (styleBackup is not null && File.Exists(styleBackup))
            {
                try { File.Copy(styleBackup, styleFilePath, overwrite: true); }
                catch (Exception restoreEx)
                {
                    (restoreErrors ??= []).Add(
                        new IOException($"无法从备份还原样式文件：{styleFilePath}", restoreEx));
                }
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
            DeleteTempFileIfExists(styleTemp);
            DeleteTempFileIfExists(markdownBackup);
            DeleteTempFileIfExists(styleBackup);
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

    private async Task EnsureNoExternalModificationAsync(string markdownPath, string stylePath, DocumentFileSnapshot? expected, CancellationToken cancellationToken)
    {
        if (expected is null) return;

        var actualMarkdownHash = await ComputeHashAsync(markdownPath, cancellationToken).ConfigureAwait(false);
        if (!HashesEqual(expected.MarkdownHash, actualMarkdownHash))
            throw new FileSaveConflictException(markdownPath, expected.MarkdownHash, actualMarkdownHash);

        // 冲突检测始终以快照记录的真实样式路径为准（兼容读取的旧命名文件）。
        var styleCheckPath = expected.StyleFileExists
            ? expected.StyleFilePath
            : stylePath;
        var actualStyleHash = await ComputeHashAsync(styleCheckPath, cancellationToken).ConfigureAwait(false);
        var expectedStyleHash = expected.StyleFileExists ? expected.StyleFileHash : string.Empty;
        if (!HashesEqual(expectedStyleHash, actualStyleHash))
            throw new FileSaveConflictException(styleCheckPath, expectedStyleHash, actualStyleHash);
    }

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
}
