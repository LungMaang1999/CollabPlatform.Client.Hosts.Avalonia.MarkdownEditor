using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;

public interface IFileService
{
    string GetStyleFilePath(string markdownFilePath);

    /// <summary>
    /// 探测磁盘上实际存在的伴随样式文件（优先新命名 .styles.xml，兼容旧命名 .style.xml）。
    /// 不存在返回 null，不做任何 IO 之外的副作用。
    /// </summary>
    string? FindExistingStyleFilePath(string markdownFilePath);

    Task<DocumentLoadResult> LoadAsync(string markdownFilePath, CancellationToken cancellationToken = default);
    Task<DocumentLoadResult> LoadAsync(string markdownFilePath, bool loadStyle, CancellationToken cancellationToken = default);

    /// <summary>
    /// 直接从内存中的 Markdown 文本加载（浏览器端没有本地文件路径）：
    /// 解析为干净文档，FilePath 为空、快照为 null；后续保存必须走另存通道。
    /// </summary>
    DocumentLoadResult LoadFromText(string markdown);

    /// <summary>
    /// 从内存文本加载并附带伴随样式 XML 文本（浏览器端多文件选择配对通道，
    /// 由调用方传入一并选择的 .styles.xml 内容）。styleXml 为 null/空白时等价于
    /// <see cref="LoadFromText(string)"/>。文档同样无路径、无快照。
    /// </summary>
    DocumentLoadResult LoadFromText(string markdown, string? styleXml);

    /// <summary>导出文档样式包 XML 文本（与 .md.styles.xml 同格式），无样式定制时仍返回完整包。</summary>
    string ExportStyleXml(MarkdownDocument document);

    Task<DocumentFileSnapshot> SaveAsync(MarkdownDocument document, DocumentFileSnapshot? expectedSnapshot = null, CancellationToken cancellationToken = default);
    Task<string> ComputeHashAsync(string filePath, CancellationToken cancellationToken = default);
}
