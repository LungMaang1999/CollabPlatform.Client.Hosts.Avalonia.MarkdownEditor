namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

/// <summary>
/// Holds document metadata attributes such as author, title, and timestamps.
/// </summary>
public sealed class DocumentMetadata
{
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Language { get; set; } = "en";
    public string Encoding { get; set; } = "utf-8";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    public Dictionary<string, string> Properties { get; } = new(StringComparer.Ordinal);
}