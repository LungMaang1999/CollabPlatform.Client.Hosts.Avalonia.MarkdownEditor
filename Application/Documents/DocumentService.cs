using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Documents;

public sealed class DocumentService : IDocumentService, IDisposable
{
    private readonly IFileService _fileService;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private MarkdownDocument? _currentDocument;
    private DocumentFileSnapshot? _currentSnapshot;
    private string? _currentFilePath;

    public event EventHandler<MarkdownDocument?>? CurrentDocumentChanged;
    public event EventHandler<bool>? HasUnsavedChangesChanged;

    public DocumentService(IFileService fileService)
    {
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
    }

    public MarkdownDocument? CurrentDocument => _currentDocument;
    public string? CurrentFilePath => _currentFilePath;
    public bool HasUnsavedChanges => _currentDocument?.IsModified == true;

    public async Task<MarkdownDocument> OpenAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loadResult = await _fileService.LoadAsync(fullPath, cancellationToken).ConfigureAwait(false);
            _currentDocument = loadResult.Document;
            _currentSnapshot = loadResult.Snapshot;
            _currentFilePath = fullPath;

            _currentDocument.FilePath = fullPath;
            _currentDocument.MarkSaved();

            CurrentDocumentChanged?.Invoke(this, _currentDocument);
            HasUnsavedChangesChanged?.Invoke(this, HasUnsavedChanges);

            return _currentDocument;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var doc = _currentDocument ?? throw new InvalidOperationException("No document is currently open.");
            var snapshot = _currentSnapshot ?? throw new InvalidOperationException("Current document has no file snapshot.");

            if (string.IsNullOrWhiteSpace(doc.FilePath))
                throw new InvalidOperationException("Current document has no file path. Use SaveAsAsync instead.");

            var newSnapshot = await _fileService.SaveAsync(doc, snapshot, cancellationToken).ConfigureAwait(false);
            _currentSnapshot = newSnapshot;
            _currentFilePath = doc.FilePath;
            doc.MarkSaved();

            HasUnsavedChangesChanged?.Invoke(this, HasUnsavedChanges);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var doc = _currentDocument ?? throw new InvalidOperationException("No document is currently open.");
            var previousPath = doc.FilePath;

            doc.FilePath = fullPath;
            try
            {
                var newSnapshot = await _fileService.SaveAsync(doc, expectedSnapshot: null, cancellationToken).ConfigureAwait(false);
                _currentSnapshot = newSnapshot;
                _currentFilePath = fullPath;
                doc.MarkSaved();

                HasUnsavedChangesChanged?.Invoke(this, HasUnsavedChanges);
            }
            catch
            {
                doc.FilePath = previousPath;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> HasExternalChangesAsync(MarkdownDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!ReferenceEquals(document, _currentDocument) || _currentSnapshot is null) return false;

        var currentMdHash = await _fileService.ComputeHashAsync(_currentSnapshot.MarkdownFilePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(_currentSnapshot.MarkdownHash, currentMdHash, StringComparison.OrdinalIgnoreCase))
            return true;

        var currentStyleHash = await _fileService.ComputeHashAsync(_currentSnapshot.StyleFilePath, cancellationToken).ConfigureAwait(false);
        var expectedStyleHash = _currentSnapshot.StyleFileExists ? _currentSnapshot.StyleFileHash : string.Empty;

        return !string.Equals(expectedStyleHash, currentStyleHash, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}