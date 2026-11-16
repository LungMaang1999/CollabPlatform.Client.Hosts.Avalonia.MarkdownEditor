namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;

public sealed record DocumentFileSnapshot(
    string MarkdownFilePath,
    string StyleFilePath,
    string MarkdownHash,
    string StyleFileHash,
    DateTime MarkdownLastWriteTimeUtc,
    DateTime StyleLastWriteTimeUtc,
    bool StyleFileExists,
    bool StyleLoadDeclined = false)
{
    /// <summary>
    /// 读取时拿到的伴随文档不透明版本，保存时原样回传给 <see cref="ICompanionDocumentStore"/> 做乐观并发。
    /// 为空表示该快照没有伴随文档基线（旧调用方/新建快照），保存退回按文件哈希校验。
    /// </summary>
    public string? CompanionVersion { get; init; }

    public static DocumentFileSnapshot ForMissingStyleFile(string markdownFilePath, string markdownHash, DateTime markdownLastWriteUtc, string styleFilePath) =>
        new(markdownFilePath, styleFilePath, markdownHash, string.Empty, markdownLastWriteUtc, DateTime.MinValue, false);
}
