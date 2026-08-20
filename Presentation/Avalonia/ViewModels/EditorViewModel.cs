using System.ComponentModel;
using System.Runtime.CompilerServices;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Editing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.Services;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.ViewModels;

public sealed class EditorViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IDocumentService _documentService;
    private readonly IDocumentRendererFactory _rendererFactory;
    private readonly IMarkdownSourceEditor _sourceEditor;
    private readonly IMarkdownEditApplier _editApplier;
    private readonly Action<Action>? _uiDispatcherInvoker;
    private readonly CommandManager _commandManager = new();

    private DocumentViewModel? _activeDocument;
    private string _htmlPreview = string.Empty;
    private IDocumentRenderer? _activeRenderer;
    private bool _isBusy;
    private string? _errorMessage;

    private readonly object _ctsLock = new();
    private CancellationTokenSource? _previewCts;
    private readonly TimeSpan _previewDebounceDelay = TimeSpan.FromMilliseconds(150);
    private bool _isDisposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DocumentViewModel? ActiveDocument
    {
        get => _activeDocument;
        private set
        {
            if (SetField(ref _activeDocument, value))
            {
                OnPropertyChanged(nameof(HasActiveDocument));
                OnPropertyChanged(nameof(DocumentTitle));
                OnPropertyChanged(nameof(IsModified));
            }
        }
    }

    public bool HasActiveDocument => _activeDocument is not null;
    public string DocumentTitle => _activeDocument?.Document.Metadata.Title is { Length: > 0 } title ? title : "Untitled";
    public bool IsModified => _activeDocument?.Document.IsModified ?? false;

    public string HtmlPreview
    {
        get => _htmlPreview;
        private set => SetField(ref _htmlPreview, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetField(ref _errorMessage, value);
    }

    public CommandManager CommandManager => _commandManager;
    public SelectionService SelectionService { get; } = new();

    public bool CanUndo => _commandManager.CanUndo;
    public bool CanRedo => _commandManager.CanRedo;

    public EditorViewModel(
        IDocumentService documentService,
        IDocumentRendererFactory rendererFactory,
        IMarkdownSourceEditor sourceEditor,
        IMarkdownEditApplier editApplier,
        Action<Action>? uiDispatcherInvoker = null)
    {
        _documentService = documentService ?? throw new ArgumentNullException(nameof(documentService));
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
        _sourceEditor = sourceEditor ?? throw new ArgumentNullException(nameof(sourceEditor));
        _editApplier = editApplier ?? throw new ArgumentNullException(nameof(editApplier));
        _uiDispatcherInvoker = uiDispatcherInvoker;

        _commandManager.CommandStateChanged += OnCommandStateChanged;
        _documentService.CurrentDocumentChanged += OnCurrentDocumentChanged;
        _documentService.HasUnsavedChangesChanged += OnHasUnsavedChangesChanged;
    }

    private void OnCommandStateChanged(object? sender, EventArgs e)
    {
        RunOnUIThread(() =>
        {
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            OnPropertyChanged(nameof(IsModified));
        });
    }

    private void OnHasUnsavedChangesChanged(object? sender, bool hasChanges)
    {
        RunOnUIThread(() => OnPropertyChanged(nameof(IsModified)));
    }

    private void OnCurrentDocumentChanged(object? sender, MarkdownDocument? doc)
    {
        RunOnUIThread(() =>
        {
            if (doc is null)
            {
                ActiveDocument = null;
                _activeRenderer = null;
                HtmlPreview = string.Empty;
            }
            else
            {
                ActiveDocument = new DocumentViewModel(doc);
                _activeRenderer = _rendererFactory.Create(doc);
                ScheduleUpdatePreview(immediate: true);
            }
        });
    }

    public async Task OpenDocumentAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            var doc = await _documentService.OpenAsync(filePath, ct).ConfigureAwait(false);
            _commandManager.Clear();

            RunOnUIThread(() =>
            {
                ActiveDocument = new DocumentViewModel(doc);
                _activeRenderer = _rendererFactory.Create(doc);
                ScheduleUpdatePreview(immediate: true);
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to open file: {ex.Message}";
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SaveDocumentAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            await _documentService.SaveAsync(ct).ConfigureAwait(false);
            RunOnUIThread(() => OnPropertyChanged(nameof(IsModified)));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save document: {ex.Message}";
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ExecuteTextChange(SourceRange range, string replacement)
    {
        if (ActiveDocument is null) return;
        var cmd = new ChangeTextCommand(ActiveDocument.Document, _editApplier, _sourceEditor, range, replacement);
        _commandManager.Execute(cmd);
        ScheduleUpdatePreview();
    }

    public void ExecuteHeadingChange(MarkdownNode headingNode, int newLevel)
    {
        if (ActiveDocument is null) return;
        var cmd = new ChangeHeadingLevelCommand(ActiveDocument.Document, _editApplier, _sourceEditor, headingNode, newLevel);
        _commandManager.Execute(cmd);
        ScheduleUpdatePreview();
    }

    public void Undo()
    {
        if (_commandManager.Undo())
            ScheduleUpdatePreview();
    }

    public void Redo()
    {
        if (_commandManager.Redo())
            ScheduleUpdatePreview();
    }

    public void ScheduleUpdatePreview(bool immediate = false)
    {
        if (_isDisposed || ActiveDocument is null) return;

        CancellationToken token;
        lock (_ctsLock)
        {
            if (_isDisposed) return;
            _previewCts?.Cancel();
            _previewCts?.Dispose();
            _previewCts = new CancellationTokenSource();
            token = _previewCts.Token;
        }

        var doc = ActiveDocument.Document;
        var renderer = _activeRenderer ??= _rendererFactory.Create(doc);

        _ = Task.Run(async () =>
        {
            try
            {
                if (!immediate)
                {
                    await Task.Delay(_previewDebounceDelay, token).ConfigureAwait(false);
                }

                if (token.IsCancellationRequested) return;

                var result = renderer.Render(doc);

                if (!token.IsCancellationRequested)
                {
                    RunOnUIThread(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            HtmlPreview = result.Html;
                        }
                    });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                RunOnUIThread(() => ErrorMessage = $"Render error: {ex.Message}");
            }
        }, token);
    }

    private void RunOnUIThread(Action action)
    {
        if (_uiDispatcherInvoker is not null)
            _uiDispatcherInvoker(action);
        else
            action();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public void Dispose()
    {
        lock (_ctsLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _previewCts?.Cancel();
            _previewCts?.Dispose();
            _previewCts = null;
        }

        _commandManager.CommandStateChanged -= OnCommandStateChanged;
        _documentService.CurrentDocumentChanged -= OnCurrentDocumentChanged;
        _documentService.HasUnsavedChangesChanged -= OnHasUnsavedChangesChanged;
    }
}