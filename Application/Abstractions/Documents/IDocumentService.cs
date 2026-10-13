using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;

public interface IDocumentService
{
    MarkdownDocument? CurrentDocument { get; }
    string? CurrentFilePath { get; }
    bool HasUnsavedChanges { get; }

    event EventHandler<MarkdownDocument?>? CurrentDocumentChanged;
    event EventHandler<bool>? HasUnsavedChangesChanged;

    MarkdownDocument NewDocument();

    /// <summary>伴随样式文件的标准路径（markdown 全路径 + ".styles.xml"）。</summary>
    string GetStyleFilePath(string markdownFilePath);

    /// <summary>
    /// 探测磁盘上与指定 markdown 同名的伴随样式文件；存在返回实际路径，
    /// 不存在返回 null。UI 层据此决定是否弹出“是否载入样式”确认框。
    /// </summary>
    string? FindStyleCompanion(string markdownFilePath);

    Task<MarkdownDocument> OpenAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 打开文档；loadStyle=false 表示用户拒绝载入同名伴随样式文件，
    /// 文档按纯 Markdown 解析，后续保存会保留磁盘上的样式文件不覆盖。
    /// </summary>
    Task<MarkdownDocument> OpenAsync(string filePath, bool loadStyle, CancellationToken cancellationToken = default);

    /// <summary>
    /// 从内存 Markdown 文本载入（浏览器端没有可访问的本地路径）。
    /// 载入后文档无文件路径，保存必须走另存通道。
    /// </summary>
    Task<MarkdownDocument> OpenFromTextAsync(string markdown, CancellationToken cancellationToken = default);

    /// <summary>
    /// 从内存 Markdown 文本载入并附带伴随样式 XML 文本（浏览器端多文件
    /// 选择配对通道）。styleXml 为 null 时等价于纯文本载入。
    /// </summary>
    Task<MarkdownDocument> OpenFromTextAsync(string markdown, string? styleXml, CancellationToken cancellationToken = default);
    Task SaveAsync(CancellationToken cancellationToken = default);
    Task SaveAsAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 标记当前文档为已保存（浏览器端外部通道：内容已直接写入用户选定的
    /// 可写流，不产生文件快照），并广播未保存状态变化。
    /// </summary>
    void MarkCurrentSaved();
    Task<bool> HasExternalChangesAsync(MarkdownDocument document, CancellationToken cancellationToken = default);
}
