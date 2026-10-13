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
    public static DocumentFileSnapshot ForMissingStyleFile(string markdownFilePath, string markdownHash, DateTime markdownLastWriteUtc, string styleFilePath) =>
        new(markdownFilePath, styleFilePath, markdownHash, string.Empty, markdownLastWriteUtc, DateTime.MinValue, false);
}
