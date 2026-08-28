using System.Security.Cryptography;
using System.Text;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Parsing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence;

public sealed class FileService : IFileService
{
    private const string StyleFileSuffix = ".style.xml";
    // 不强制 throwOnInvalidBytes，提升非标准编码文件的容错率
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

    public async Task<DocumentLoadResult> LoadAsync(string markdownFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownFilePath);
        var fullMarkdownPath = Path.GetFullPath(markdownFilePath);
        var styleFilePath = GetStyleFilePath(fullMarkdownPath);

        if (!File.Exists(fullMarkdownPath))
            throw new FileNotFoundException("Markdown file was not found.", fullMarkdownPath);

        // 使用 StreamReader 支持自适应 BOM 读取
        string markdown;
        using (var reader = new StreamReader(fullMarkdownPath, Utf8Encoding, detectEncodingFromByteOrderMarks: true))
        {
            markdown = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var markdownHash = await ComputeHashAsync(fullMarkdownPath, cancellationToken).ConfigureAwait(false);
        var markdownInfo = new FileInfo(fullMarkdownPath);

        MarkdownDocument? documentFromXml = null;
        if (File.Exists(styleFilePath))
        {
            await using var styleStream = new FileStream(
                styleFilePath,
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

        if (!File.Exists(styleFilePath))
        {
            var missingSnapshot = DocumentFileSnapshot.ForMissingStyleFile(fullMarkdownPath, markdownHash, markdownInfo.LastWriteTimeUtc, styleFilePath);
            return new DocumentLoadResult(document, missingSnapshot);
        }

        var styleHash = await ComputeHashAsync(styleFilePath, cancellationToken).ConfigureAwait(false);
        var styleInfo = new FileInfo(styleFilePath);
        var snapshot = new DocumentFileSnapshot(fullMarkdownPath, styleFilePath, markdownHash, styleHash, markdownInfo.LastWriteTimeUtc, styleInfo.LastWriteTimeUtc, StyleFileExists: true);

        return new DocumentLoadResult(document, snapshot);
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

        var markdownBytes = Utf8Encoding.GetBytes(document.SourceMarkdown ?? string.Empty);
        var markdownHash = ComputeSha256(markdownBytes);

        await using var xmlMemoryStream = new MemoryStream();
        document.Metadata.ModifiedUtc = DateTime.UtcNow;
        document.Validate();
        _serializer.Serialize(document, xmlMemoryStream, Path.GetFileName(markdownFilePath), markdownHash);

        var styleBytes = xmlMemoryStream.ToArray();
        var styleHash = ComputeSha256(styleBytes);

        var markdownTemp = CreateTempPath(directory, Path.GetFileName(markdownFilePath));
        var styleTemp = CreateTempPath(directory, Path.GetFileName(styleFilePath));

        // 备份文件路径（两阶段事务保护）
        string? markdownBackup = null;
        string? styleBackup = null;

        try
        {
            await WriteAllBytesAsync(markdownTemp, markdownBytes, cancellationToken).ConfigureAwait(false);
            await WriteAllBytesAsync(styleTemp, styleBytes, cancellationToken).ConfigureAwait(false);

            await EnsureNoExternalModificationAsync(markdownFilePath, styleFilePath, expectedSnapshot, cancellationToken).ConfigureAwait(false);

            // 具备原子回滚能力的两阶段事务文件替换
            if (File.Exists(markdownFilePath))
            {
                markdownBackup = CreateTempPath(directory, Path.GetFileName(markdownFilePath) + ".bak");
                File.Copy(markdownFilePath, markdownBackup, overwrite: true);
            }

            if (File.Exists(styleFilePath))
            {
                styleBackup = CreateTempPath(directory, Path.GetFileName(styleFilePath) + ".bak");
                File.Copy(styleFilePath, styleBackup, overwrite: true);
            }

            ReplaceFileSafely(markdownTemp, markdownFilePath);
            markdownTemp = string.Empty;

            ReplaceFileSafely(styleTemp, styleFilePath);
            styleTemp = string.Empty;

            var markdownInfo = new FileInfo(markdownFilePath);
            var styleInfo = new FileInfo(styleFilePath);

            document.FilePath = markdownFilePath;
            document.MarkSaved();

            return new DocumentFileSnapshot(markdownFilePath, styleFilePath, markdownHash, styleHash, markdownInfo.LastWriteTimeUtc, styleInfo.LastWriteTimeUtc, StyleFileExists: true);
        }
        catch
        {
            // 回滚原始文件
            if (markdownBackup is not null && File.Exists(markdownBackup))
            {
                try { File.Copy(markdownBackup, markdownFilePath, overwrite: true); } catch { }
            }
            if (styleBackup is not null && File.Exists(styleBackup))
            {
                try { File.Copy(styleBackup, styleFilePath, overwrite: true); } catch { }
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

        var actualStyleHash = await ComputeHashAsync(stylePath, cancellationToken).ConfigureAwait(false);
        var expectedStyleHash = expected.StyleFileExists ? expected.StyleFileHash : string.Empty;
        if (!HashesEqual(expectedStyleHash, actualStyleHash))
            throw new FileSaveConflictException(stylePath, expectedStyleHash, actualStyleHash);
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
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}