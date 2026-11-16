using System.Text;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Documents;

/// <summary>
/// 定义文件在“上一次已知状态”的标记：路径 + 文档实例 + 原始文本。
/// 每次编辑前用它判断文件是否被外部改过；任何写入成功后立刻更新，
/// 撤销/重做也一样，因此标记始终等于磁盘上的真实内容。
/// </summary>
internal sealed class UserBlockElementsFileStamp
{
    private string _path = string.Empty;
    private string _documentId = string.Empty;

    public bool Captured { get; private set; }

    public string? Content { get; private set; }

    public void Capture(string path, string documentId, string? content)
    {
        _path = path;
        _documentId = documentId;
        Content = content;
        Captured = true;
    }

    public bool BelongsTo(string path, string documentId) =>
        Captured
        && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)
        && string.Equals(_documentId, documentId, StringComparison.Ordinal);

    public bool Matches(string? content) =>
        Captured && string.Equals(Content ?? string.Empty, content ?? string.Empty, StringComparison.Ordinal);

    public void Update(string? content) => Content = content;
}

/// <summary>
/// 元素定义的落盘细节。
/// <para>伴随文件 <c>&lt;doc&gt;.md.companion.xml</c> 存在时，定义段是它的一部分，
/// 读写都走同一份伴随文档（一次原子写，样式段原样保留）；</para>
/// <para>伴随文件不存在时退回旧文件 <c>.md.elements.xml</c>，保证迁移前行为不变。</para>
/// <para>伴随文件损坏时读取抛 <see cref="IOException"/>（带路径与原因），写入同样拒绝，
/// 绝不把“解析失败”当成“没有定义”覆盖原文件。</para>
/// </summary>
internal static class UserBlockElementsFileStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>读取原始文本；文件不存在返回 null。IO 异常/伴随文件损坏原样抛出，由调用方转成结构化错误。</summary>
    public static string? Read(string path)
    {
        var companionPath = GetCompanionPath(path);
        if (File.Exists(companionPath))
        {
            var document = LoadCompanionOrThrow(companionPath);
            return document.Definitions.Count == 0 && document.Composition.IsEmpty
                ? null
                : UserBlockElementsSerializer.Serialize(document.Definitions, document.Composition);
        }

        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>按序列化器的确定性口径生成文件内容；无定义返回 null。</summary>
    public static string? BuildContent(
        IReadOnlyList<UserBlockDefinition> definitions,
        UserBlockComposition? composition = null) =>
        definitions.Count == 0
            ? null
            : UserBlockElementsSerializer.Serialize(definitions, composition);

    public static void Write(string path, string? content)
    {
        var companionPath = GetCompanionPath(path);
        if (File.Exists(companionPath))
        {
            var current = LoadCompanionOrThrow(companionPath);
            var parsed = content is null
                ? null
                : UserBlockElementsSerializer.Deserialize(content);

            // 只替换定义段与组合单元段：样式段、未知节点与 document-id 原样保留。
            var updated = new CompanionDocument
            {
                Format = CompanionDocumentFormat.CompanionV2,
                DocumentId = current.DocumentId,
                Styles = current.Styles,
                Definitions = parsed?.Definitions ?? [],
                Composition = parsed?.EffectiveComposition ?? UserBlockComposition.Empty,
                UnknownNodes = current.UnknownNodes
            };

            WriteAtomically(companionPath, CompanionDocumentSerializer.Serialize(updated));
            return;
        }

        if (content is null)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        WriteAtomically(path, content);
    }

    private static string GetCompanionPath(string elementsPath)
    {
        var markdownPath = elementsPath.EndsWith(UserBlockElementsSerializer.FileSuffix, StringComparison.OrdinalIgnoreCase)
            ? elementsPath[..^UserBlockElementsSerializer.FileSuffix.Length]
            : elementsPath;
        return FileCompanionDocumentStore.GetCompanionFilePath(markdownPath);
    }

    private static CompanionDocument LoadCompanionOrThrow(string companionPath)
    {
        var document = CompanionDocumentSerializer.Deserialize(File.ReadAllText(companionPath));
        var error = document.Diagnostics
            .FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        if (document.IsReadOnly || error is not null)
        {
            throw new IOException(
                $"元素定义所在的伴随文件无法读取（{companionPath}）：{error?.Message ?? document.ReadOnlyReason} " +
                "为避免用空定义覆盖原文件，本次操作未写入；请先备份或修复该文件，或显式另存为新文件。");
        }

        return document;
    }

    private static void WriteAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new IOException($"元素定义文件路径无效：{path}");
        }

        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllBytes(tempPath, Utf8NoBom.GetBytes(content));

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // 清理失败不掩盖主异常。
            }
        }
    }
}

/// <summary>定义文件在编辑期间被外部改动：拒绝覆盖，交由界面决定重新打开。</summary>
public sealed class UserBlockElementsConflictException : Exception
{
    public UserBlockElementsConflictException(string message)
        : base(message)
    {
    }
}
