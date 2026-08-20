using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;

public interface IFileService
{
    string GetStyleFilePath(string markdownFilePath);
    Task<DocumentLoadResult> LoadAsync(string markdownFilePath, CancellationToken cancellationToken = default);
    Task<DocumentFileSnapshot> SaveAsync(MarkdownDocument document, DocumentFileSnapshot? expectedSnapshot = null, CancellationToken cancellationToken = default);
    Task<string> ComputeHashAsync(string filePath, CancellationToken cancellationToken = default);
}