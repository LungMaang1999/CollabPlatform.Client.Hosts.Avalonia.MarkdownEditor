using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;

public interface IDocumentService
{
    MarkdownDocument? CurrentDocument { get; }
    string? CurrentFilePath { get; }
    bool HasUnsavedChanges { get; }

    event EventHandler<MarkdownDocument?>? CurrentDocumentChanged;
    event EventHandler<bool>? HasUnsavedChangesChanged;

    Task<MarkdownDocument> OpenAsync(string filePath, CancellationToken cancellationToken = default);
    Task SaveAsync(CancellationToken cancellationToken = default);
    Task SaveAsAsync(string filePath, CancellationToken cancellationToken = default);
    Task<bool> HasExternalChangesAsync(MarkdownDocument document, CancellationToken cancellationToken = default);
}