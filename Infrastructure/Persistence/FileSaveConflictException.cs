namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence;

/// <summary>
/// Thrown when saving a document detects concurrent external changes on disk.
/// </summary>
public sealed class FileSaveConflictException : IOException
{
    public string FilePath { get; }
    public string? ExpectedHash { get; }
    public string? ActualHash { get; }

    public FileSaveConflictException(string filePath, string? expectedHash, string? actualHash)
        : base($"Save conflict detected for '{filePath}'. Expected hash '{expectedHash}', but actual disk hash was '{actualHash}'.")
    {
        FilePath = filePath;
        ExpectedHash = expectedHash;
        ActualHash = actualHash;
    }
}