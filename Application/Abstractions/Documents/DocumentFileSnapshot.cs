namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;

/// <summary>
/// Holds an immutable snapshot of disk file paths, hashes, and last write timestamps.
/// </summary>
public sealed record DocumentFileSnapshot(
    string MarkdownFilePath,
    string StyleFilePath,
    string MarkdownHash,
    string StyleFileHash,
    DateTime MarkdownLastWriteTimeUtc,
    DateTime StyleLastWriteTimeUtc,
    bool StyleFileExists)
{
    public static DocumentFileSnapshot ForMissingStyleFile(string markdownFilePath, string markdownHash, DateTime markdownLastWriteUtc, string styleFilePath) =>
        new(markdownFilePath, styleFilePath, markdownHash, string.Empty, markdownLastWriteUtc, DateTime.MinValue, false);
}