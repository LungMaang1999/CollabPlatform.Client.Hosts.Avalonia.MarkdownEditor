using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Editing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Threading;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.Services;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.ViewModels;

/// <summary>
/// 主编辑器视图模型（高可靠并发安全与 Unicode 强化版）
/// </summary>
public sealed class EditorViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IDocumentService _documentService;
    private readonly IDocumentRendererFactory _rendererFactory;
    private readonly IMarkdownSourceEditor _sourceEditor;
    private readonly IMarkdownEditApplier _editApplier;
    private readonly IUiThreadDispatcher? _uiDispatcher;
    private readonly CommandManager _commandManager = new();

    private DocumentViewModel? _activeDocument;
    private string _htmlPreview = string.Empty;
    private IDocumentRenderer? _activeRenderer;
    private bool _isBusy;
    private string? _errorMessage;

    private readonly object _ctsLock = new();
    private CancellationTokenSource? _previewCts;
    private readonly TimeSpan _previewDebounceDelay = TimeSpan.FromMilliseconds(300);
    private bool _isDisposed;
    private long _previewVersion;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal DocumentViewModel? ActiveDocument
    {
        get => _activeDocument;
        private set
        {
            if (ReferenceEquals(_activeDocument, value)) return;

            if (_activeDocument is not null)
            {
                _activeDocument.StyleMutated -= OnActiveDocumentStyleMutated;
                _activeDocument.Dispose();
            }

            _activeDocument = value;

            if (_activeDocument is not null)
            {
                _activeDocument.StyleMutated += OnActiveDocumentStyleMutated;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasActiveDocument));
            OnPropertyChanged(nameof(DocumentTitle));
            OnPropertyChanged(nameof(IsModified));
            OnPropertyChanged(nameof(RootNode));
            OnPropertyChanged(nameof(CurrentSourceMarkdown));
            OnPropertyChanged(nameof(DocumentRootNode));
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }
    }

    #region 公开代理属性与方法

    public NodeViewModel? RootNode => _activeDocument?.RootNode;
    public MarkdownNode? DocumentRootNode => _activeDocument?.Document.Root;
    public string CurrentSourceMarkdown => _activeDocument?.Document.SourceMarkdown ?? string.Empty;

    public void SelectNodeById(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || _activeDocument is null) return;
        SelectionService.SelectById(_activeDocument.Document, nodeId);
    }

    public void SetCaretOffset(int offset)
    {
        if (_activeDocument?.Document is { } doc)
        {
            lock (doc)
            {
                doc.EditorState.CaretOffset = offset;
            }
        }
    }

    #endregion

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
        IUiThreadDispatcher? uiDispatcher = null)
    {
        _documentService = documentService ?? throw new ArgumentNullException(nameof(documentService));
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
        _sourceEditor = sourceEditor ?? throw new ArgumentNullException(nameof(sourceEditor));
        _editApplier = editApplier ?? throw new ArgumentNullException(nameof(editApplier));
        _uiDispatcher = uiDispatcher;

        _commandManager.CommandStateChanged += OnCommandStateChanged;
        _documentService.CurrentDocumentChanged += OnCurrentDocumentChanged;
        _documentService.HasUnsavedChangesChanged += OnHasUnsavedChangesChanged;
    }

    private void OnActiveDocumentStyleMutated()
    {
        RunOnUIThread(() =>
        {
            OnPropertyChanged(nameof(IsModified));
            ScheduleUpdatePreview();
        });
    }

    public async Task OpenDocumentAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            RunOnUIThread(() =>
            {
                IsBusy = true;
                ErrorMessage = null;
            });

            await _documentService.OpenAsync(filePath, ct).ConfigureAwait(false);
            _commandManager.Clear();

            RunOnUIThread(() =>
            {
                OnPropertyChanged(nameof(CanUndo));
                OnPropertyChanged(nameof(CanRedo));
            });
        }
        catch (Exception ex)
        {
            RunOnUIThread(() => ErrorMessage = $"Failed to open file: {ex.Message}");
            throw;
        }
        finally
        {
            RunOnUIThread(() => IsBusy = false);
        }
    }

    public async Task SaveDocumentAsync(CancellationToken ct = default)
    {
        try
        {
            RunOnUIThread(() =>
            {
                IsBusy = true;
                ErrorMessage = null;
            });

            await _documentService.SaveAsync(ct).ConfigureAwait(false);
            RunOnUIThread(() => OnPropertyChanged(nameof(IsModified)));
        }
        catch (Exception ex)
        {
            RunOnUIThread(() => ErrorMessage = $"Failed to save document: {ex.Message}");
            throw;
        }
        finally
        {
            RunOnUIThread(() => IsBusy = false);
        }
    }

    public void ExecuteTextChange(SourceRange range, string? replacement)
    {
        ArgumentNullException.ThrowIfNull(range);
        EnsureEditableDocument();

        var document = ActiveDocument!.Document;
        var cmd = new ChangeTextCommand(document, _editApplier, _sourceEditor, range, replacement ?? string.Empty);

        lock (document)
        {
            _commandManager.Execute(cmd);
            ActiveDocument.RefreshAst();
            document.NormalizeEditorState();

            if (!string.IsNullOrWhiteSpace(document.EditorState.SelectedNodeId))
            {
                SelectionService.SelectById(document, document.EditorState.SelectedNodeId);
            }
        }

        ScheduleUpdatePreview();
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(ActiveDocument));
        OnPropertyChanged(nameof(RootNode));
        OnPropertyChanged(nameof(CurrentSourceMarkdown));
        OnPropertyChanged(nameof(DocumentRootNode));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    public void ApplySourceText(string? source)
    {
        EnsureEditableDocument();

        source ??= string.Empty;
        var document = ActiveDocument!.Document;
        var currentSource = document.SourceMarkdown ?? string.Empty;

        if (string.Equals(currentSource, source, StringComparison.Ordinal))
            return;

        int prefixLen = 0;
        int maxPrefix = Math.Min(currentSource.Length, source.Length);
        while (prefixLen < maxPrefix && currentSource[prefixLen] == source[prefixLen])
        {
            prefixLen++;
        }

        // 避免在 Unicode 代理对中间截断
        if (prefixLen > 0 && prefixLen < currentSource.Length && char.IsLowSurrogate(currentSource[prefixLen]))
        {
            prefixLen--;
        }

        int suffixLen = 0;
        int maxSuffix = Math.Min(currentSource.Length - prefixLen, source.Length - prefixLen);
        while (suffixLen < maxSuffix && currentSource[currentSource.Length - 1 - suffixLen] == source[source.Length - 1 - suffixLen])
        {
            suffixLen++;
        }

        if (suffixLen > 0 && currentSource.Length - suffixLen > 0 && char.IsHighSurrogate(currentSource[currentSource.Length - suffixLen - 1]))
        {
            suffixLen--;
        }

        int startOffset = prefixLen;
        int oldLength = currentSource.Length - prefixLen - suffixLen;
        int newLength = source.Length - prefixLen - suffixLen;

        var replacement = source.Substring(startOffset, newLength);

        var (startLine, startCol) = CalculateLineAndColumn(currentSource, startOffset);
        var (endLine, endCol) = CalculateLineAndColumn(currentSource, startOffset + oldLength);

        var changeRange = new SourceRange
        {
            StartOffset = startOffset,
            Length = oldLength,
            StartLine = startLine,
            StartColumn = startCol,
            EndLine = endLine,
            EndColumn = endCol
        };

        ExecuteTextChange(changeRange, replacement);
    }

    #region Markdown 快捷编辑增强

    public void WrapSelection(int selectionStart, int selectionLength, string prefix, string suffix)
    {
        EnsureEditableDocument();
        var doc = ActiveDocument!.Document;
        var source = doc.SourceMarkdown ?? string.Empty;

        if (selectionStart < 0 || selectionStart > source.Length) return;

        // 对齐代理对边界
        int safeStart = selectionStart;
        if (safeStart > 0 && safeStart < source.Length && char.IsLowSurrogate(source[safeStart]))
        {
            safeStart--;
        }

        int safeEnd = Math.Min(source.Length, safeStart + Math.Max(0, selectionLength));
        if (safeEnd > 0 && safeEnd < source.Length && char.IsHighSurrogate(source[safeEnd - 1]))
        {
            safeEnd++;
        }

        int length = safeEnd - safeStart;
        var selectedText = source.Substring(safeStart, length);
        string replacement;

        if (selectedText.StartsWith(prefix, StringComparison.Ordinal) && selectedText.EndsWith(suffix, StringComparison.Ordinal) &&
            selectedText.Length >= prefix.Length + suffix.Length)
        {
            replacement = selectedText.Substring(prefix.Length, selectedText.Length - prefix.Length - suffix.Length);
        }
        else
        {
            replacement = $"{prefix}{selectedText}{suffix}";
        }

        var (startLine, startCol) = CalculateLineAndColumn(source, safeStart);
        var (endLine, endCol) = CalculateLineAndColumn(source, safeEnd);

        var range = new SourceRange
        {
            StartOffset = safeStart,
            Length = length,
            StartLine = startLine,
            StartColumn = startCol,
            EndLine = endLine,
            EndColumn = endCol
        };

        ExecuteTextChange(range, replacement);
    }

    public void ToggleBold(int selectionStart, int selectionLength) => WrapSelection(selectionStart, selectionLength, "**", "**");
    public void ToggleItalic(int selectionStart, int selectionLength) => WrapSelection(selectionStart, selectionLength, "*", "*");
    public void ToggleCode(int selectionStart, int selectionLength) => WrapSelection(selectionStart, selectionLength, "`", "`");

    #endregion

    public void Undo()
    {
        if (ActiveDocument is null) return;
        var doc = ActiveDocument.Document;

        lock (doc)
        {
            if (!_commandManager.Undo()) return;
            ActiveDocument.RefreshAst();
        }

        ScheduleUpdatePreview();
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(RootNode));
        OnPropertyChanged(nameof(CurrentSourceMarkdown));
        OnPropertyChanged(nameof(DocumentRootNode));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    public void Redo()
    {
        if (ActiveDocument is null) return;
        var doc = ActiveDocument.Document;

        lock (doc)
        {
            if (!_commandManager.Redo()) return;
            ActiveDocument.RefreshAst();
        }

        ScheduleUpdatePreview();
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(RootNode));
        OnPropertyChanged(nameof(CurrentSourceMarkdown));
        OnPropertyChanged(nameof(DocumentRootNode));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    public void ScheduleUpdatePreview(bool immediate = false)
    {
        if (_isDisposed || ActiveDocument is null) return;

        CancellationToken token;
        var previewVersion = Interlocked.Increment(ref _previewVersion);

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

                token.ThrowIfCancellationRequested();

                RenderResult result;
                lock (doc)
                {
                    result = renderer.Render(doc);
                }

                token.ThrowIfCancellationRequested();

                RunOnUIThread(() =>
                {
                    if (token.IsCancellationRequested || previewVersion != Volatile.Read(ref _previewVersion) || _isDisposed)
                        return;

                    HtmlPreview = result.Html;
                });
            }
            catch (OperationCanceledException)
            {
                // 防抖正常取消
            }
            catch (Exception ex)
            {
                RunOnUIThread(() =>
                {
                    if (!_isDisposed && previewVersion == Volatile.Read(ref _previewVersion))
                    {
                        ErrorMessage = $"Render preview error: {ex.Message}";
                    }
                });
            }
        }, token);
    }

    private static (int line, int column) CalculateLineAndColumn(string text, int offset)
    {
        int line = 1;
        int lastLineBreak = -1;
        int clampedOffset = Math.Clamp(offset, 0, text.Length);

        for (int i = 0; i < clampedOffset; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lastLineBreak = i;
            }
        }

        int column = clampedOffset - lastLineBreak;
        return (line, Math.Max(1, column));
    }

    private void EnsureEditableDocument()
    {
        if (ActiveDocument is not null) return;

        var document = new MarkdownDocument { FilePath = string.Empty };
        ActiveDocument = new DocumentViewModel(document, _uiDispatcher);
        _activeRenderer = _rendererFactory.Create(document);
        ErrorMessage = null;
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
                ActiveDocument = new DocumentViewModel(doc, _uiDispatcher);
                _activeRenderer = _rendererFactory.Create(doc);
                ScheduleUpdatePreview(immediate: true);
            }
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        });
    }

    private void RunOnUIThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_uiDispatcher is not null)
        {
            if (_uiDispatcher.CheckAccess())
                action();
            else
                _uiDispatcher.Post(action);
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
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

        if (_activeDocument is not null)
        {
            _activeDocument.StyleMutated -= OnActiveDocumentStyleMutated;
            _activeDocument.Dispose();
            _activeDocument = null;
        }

        _commandManager.CommandStateChanged -= OnCommandStateChanged;
        _documentService.CurrentDocumentChanged -= OnCurrentDocumentChanged;
        _documentService.HasUnsavedChangesChanged -= OnHasUnsavedChangesChanged;
    }
}