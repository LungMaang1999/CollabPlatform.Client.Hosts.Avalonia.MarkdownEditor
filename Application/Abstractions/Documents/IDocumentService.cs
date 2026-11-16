using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;

public interface IDocumentService
{
    MarkdownDocument? CurrentDocument { get; }
    string? CurrentFilePath { get; }
    bool HasUnsavedChanges { get; }

    /// <summary>
    /// 最近一次打开产生的诊断（当前主要是 .md.elements.xml 的损坏/跳过条目）。
    /// 正常打开为空；文档本身始终完整解析，不是空文档。
    /// </summary>
    IReadOnlyList<DiagnosticMessage> CurrentLoadDiagnostics { get; }

    /// <summary>
    /// 最近一次打开产生的聚合摘要（如“3 个元素定义问题、2 处内容违规”）；
    /// 无诊断时为 null。明细仍通过 <see cref="CurrentLoadDiagnostics"/> 获取。
    /// </summary>
    string? CurrentLoadDiagnosticsSummary { get; }

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

    /// <summary>
    /// 从内存 Markdown 文本载入并同时附带样式段与元素定义段（服务端文章通道）。
    /// 定义段参与解析，决定自定义块是否按声明渲染。
    /// </summary>
    Task<MarkdownDocument> OpenFromTextAsync(
        string markdown,
        string? styleXml,
        string? elementsXml,
        CancellationToken cancellationToken = default);

    /// <summary>导出当前文档的样式包 XML；无打开文档时返回空串。</summary>
    string ExportCurrentStyleXml();

    Task SaveAsync(CancellationToken cancellationToken = default);
    Task SaveAsAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 标记当前文档为已保存（浏览器端外部通道：内容已直接写入用户选定的
    /// 可写流，不产生文件快照），并广播未保存状态变化。
    /// </summary>
    void MarkCurrentSaved();
    Task<bool> HasExternalChangesAsync(MarkdownDocument document, CancellationToken cancellationToken = default);
}
