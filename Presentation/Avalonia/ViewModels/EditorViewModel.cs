using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Editing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Parsing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Threading;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.Composition;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.Services;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.ViewModels;

public sealed class EditorViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IDocumentService _documentService;
    private readonly IDocumentRendererFactory _rendererFactory;
    private readonly IMarkdownSourceEditor _sourceEditor;
    private readonly IMarkdownEditApplier _editApplier;
    private readonly IMarkdownParser _parser;
    private readonly IUiThreadDispatcher? _uiDispatcher;
    private readonly IStylePreviewRenderer? _stylePreviewRenderer;
    private readonly MarkdownEditorOptions _editorOptions;
    private readonly CommandManager _commandManager = new();

    private DocumentViewModel? _activeDocument;
    private string _htmlPreview = string.Empty;
    private string? _htmlPatch;
    private IDocumentRenderer? _activeRenderer;
    private bool _isBusy;
    private string? _errorMessage;

    // 1 = 下一帧必须输出全量（页面刷新/JS reset 自愈），由渲染任务消费。
    private int _forceFullPreviewRender;
    // 每次成功发布（全量或补丁）单调递增：宿主据此发现丢帧并请求全量自愈。
    private long _publishedPreviewVersion;

    private readonly object _ctsLock = new();
    private CancellationTokenSource? _previewCts;
    private readonly TimeSpan _previewDebounceDelay = TimeSpan.FromMilliseconds(300);
    private bool _isDisposed;
    private long _previewVersion;

    // 预览渲染串行门：RenderIncremental 的基线读改写不是原子的，且需借此
    // 识别“已渲染但从未发布”的丢弃帧（基线被推进过，下一帧须重置并全量）。
    private readonly SemaphoreSlim _previewRenderGate = new(1, 1);
    private long _lastRenderedPreviewVersion;
    private long _lastPublishedRenderVersion;

    // 后台解析调度：单消费者串行链（不并发跑 Markdig），版本号丢弃过期结果。
    private readonly object _parseChainLock = new();
    private Task _parseChain = Task.CompletedTask;
    private long _parseVersion;

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
                _activeDocument.StyleEditor.StyleCommitted -= ApplyNodeStyle;
                _activeDocument.Dispose();
            }

            _activeDocument = value;

            if (_activeDocument is not null)
            {
                _activeDocument.StyleMutated += OnActiveDocumentStyleMutated;
                _activeDocument.StyleEditor.StyleCommitted += ApplyNodeStyle;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasActiveDocument));
            OnPropertyChanged(nameof(DocumentTitle));
            OnPropertyChanged(nameof(IsModified));
            OnPropertyChanged(nameof(HasFilePath));
            OnPropertyChanged(nameof(RootNode));
            OnPropertyChanged(nameof(ActiveStyleEditor));
            OnPropertyChanged(nameof(CurrentSourceMarkdown));
            OnPropertyChanged(nameof(DocumentRootNode));
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }
    }

    public NodeViewModel? RootNode => _activeDocument?.RootNode;
    public MarkdownNode? DocumentRootNode => _activeDocument?.Document.Root;
    public string CurrentSourceMarkdown => _activeDocument?.Document.SourceMarkdown ?? string.Empty;

    public StyleEditorViewModel? ActiveStyleEditor => _activeDocument?.StyleEditor;

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

    public bool HasActiveDocument => _activeDocument is not null;
    public string DocumentTitle => _activeDocument?.Document.Metadata.Title is { Length: > 0 } title ? title : "Untitled";
    public bool IsModified => _activeDocument?.Document.IsModified ?? false;

    public bool HasFilePath => !string.IsNullOrWhiteSpace(_activeDocument?.FilePath);

    public string HtmlPreview
    {
        get => _htmlPreview;
        private set => SetField(ref _htmlPreview, value);
    }

    /// <summary>
    /// 最近一次发布的增量补丁载荷 JSON；null 表示最近一次发布是全量（HtmlPreview）。
    /// </summary>
    public string? HtmlPatch
    {
        get => _htmlPatch;
        private set => SetField(ref _htmlPatch, value);
    }

    /// <summary>预览发布版本号：每次全量/补丁成功发布到 UI 时单调 +1。</summary>
    public long PublishedPreviewVersion => _publishedPreviewVersion;

    /// <summary>
    /// 请求一帧强制全量预览（WebView 刷新/补丁 reset/丢帧自愈）：
    /// 重置渲染器增量基线并立即调度，下一帧必为全量。
    /// </summary>
    public void RequestFullPreviewRender()
    {
        if (_isDisposed || ActiveDocument is null) return;

        Interlocked.Exchange(ref _forceFullPreviewRender, 1);
        if (_activeRenderer is IIncrementalPreviewRenderer incremental)
        {
            incremental.ResetIncrementalBaseline();
        }

        ScheduleUpdatePreview(immediate: true);
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
        IMarkdownParser parser,
        IUiThreadDispatcher? uiDispatcher = null,
        IStylePreviewRenderer? stylePreviewRenderer = null,
        MarkdownEditorOptions? editorOptions = null)
    {
        _documentService = documentService ?? throw new ArgumentNullException(nameof(documentService));
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
        _sourceEditor = sourceEditor ?? throw new ArgumentNullException(nameof(sourceEditor));
        _editApplier = editApplier ?? throw new ArgumentNullException(nameof(editApplier));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _uiDispatcher = uiDispatcher;
        _stylePreviewRenderer = stylePreviewRenderer;
        _editorOptions = editorOptions ?? new MarkdownEditorOptions();

        _commandManager.CommandStateChanged += OnCommandStateChanged;
        _documentService.CurrentDocumentChanged += OnCurrentDocumentChanged;
        _documentService.HasUnsavedChangesChanged += OnHasUnsavedChangesChanged;
        SelectionService.SelectedNodeChanged += OnSelectionChanged;
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

    public void NewDocument()
    {
        _documentService.NewDocument();
        _commandManager.Clear();
        ErrorMessage = null;

        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
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

            // 屏障：保存前确保在途后台解析已换树，避免 Validate/序列化读到旧 AST。
            await PendingParseCompletionAsync().ConfigureAwait(false);
            await _documentService.SaveAsync(ct).ConfigureAwait(false);
            RunOnUIThread(() =>
            {
                OnPropertyChanged(nameof(IsModified));
                OnPropertyChanged(nameof(HasFilePath));
            });
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

    public async Task SaveDocumentAsAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        try
        {
            RunOnUIThread(() =>
            {
                IsBusy = true;
                ErrorMessage = null;
            });

            await PendingParseCompletionAsync().ConfigureAwait(false);
            await _documentService.SaveAsAsync(filePath, ct).ConfigureAwait(false);
            RunOnUIThread(() =>
            {
                OnPropertyChanged(nameof(IsModified));
                OnPropertyChanged(nameof(HasFilePath));
                OnPropertyChanged(nameof(DocumentTitle));
            });
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
        var useBackground = UseBackgroundParsing(document);
        var sourceChanged = false;

        lock (document)
        {
            var sourceBefore = document.SourceMarkdown;
            _commandManager.Execute(cmd);
            sourceChanged = !string.Equals(sourceBefore, document.SourceMarkdown, StringComparison.Ordinal);

            if (!useBackground)
            {
                // 同步路径：立即解析换树。
                ParseAndCommitInline(document);
                ActiveDocument.RefreshAst();
                document.NormalizeEditorState();

                if (!string.IsNullOrWhiteSpace(document.EditorState.SelectedNodeId))
                {
                    SelectionService.SelectById(document, document.EditorState.SelectedNodeId);
                }

                ResyncSelectionAfterAstChange();
            }
        }

        if (useBackground)
        {
            // source 立即生效，AST 在后台解析提交后的 UI 帧换树；无变化不调度。
            if (sourceChanged)
            {
                RequestBackgroundParse(document);
            }

            // 换树前不刷新预览，提交成功后由 CommitBackgroundParse 调度。
        }
        else
        {
            ScheduleUpdatePreview();
        }

        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(ActiveDocument));
        OnPropertyChanged(nameof(CurrentSourceMarkdown));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));

        if (!useBackground)
        {
            OnPropertyChanged(nameof(RootNode));
            OnPropertyChanged(nameof(DocumentRootNode));
        }
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

        CalculateLineAndColumnRange(currentSource, startOffset, startOffset + oldLength,
            out int startLine, out int startCol, out int endLine, out int endCol);

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

    public void WrapSelection(int selectionStart, int selectionLength, string prefix, string suffix)
    {
        EnsureEditableDocument();
        var doc = ActiveDocument!.Document;
        var source = doc.SourceMarkdown ?? string.Empty;

        if (selectionStart < 0 || selectionStart > source.Length) return;

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

        CalculateLineAndColumnRange(source, safeStart, safeEnd,
            out int startLine, out int startCol, out int endLine, out int endCol);

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

    public MarkdownEditResult ApplySyntax(
        int selectionStart,
        int selectionLength,
        MarkdownSyntaxType syntax)
    {
        EnsureEditableDocument();

        var document = ActiveDocument!.Document;
        string source = document.SourceMarkdown ?? string.Empty;

        var plan = MarkdownSyntaxPlanner.Build(
            source,
            selectionStart,
            selectionLength,
            syntax);

        ExecuteTextChange(plan.Range, plan.Replacement);

        return new MarkdownEditResult(plan.SelectionStart, plan.SelectionLength);
    }

    public void Undo()
    {
        if (ActiveDocument is null) return;
        var doc = ActiveDocument.Document;

        // 撤销/重做必须同步收敛 AST（void 契约，且调用方在锁外立即读树）；
        // 先作废旧版本，在途后台解析的提交会被版本号挡下。
        InvalidatePendingParses();

        lock (doc)
        {
            if (!_commandManager.Undo()) return;
            ParseAndCommitInline(doc);
            ActiveDocument.RefreshAst();
            ResyncSelectionAfterAstChange();
        }

        ScheduleUpdatePreview();
        RaiseSourceAndTreeChanged();
    }

    public void Redo()
    {
        if (ActiveDocument is null) return;
        var doc = ActiveDocument.Document;

        InvalidatePendingParses();

        lock (doc)
        {
            if (!_commandManager.Redo()) return;
            ParseAndCommitInline(doc);
            ActiveDocument.RefreshAst();
            ResyncSelectionAfterAstChange();
        }

        ScheduleUpdatePreview();
        RaiseSourceAndTreeChanged();
    }

    private void RaiseSourceAndTreeChanged()
    {
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
        var renderer = _activeRenderer ??= _rendererFactory.CreatePreview(doc);
        var requireFull = Interlocked.Exchange(ref _forceFullPreviewRender, 0) != 0;

        _ = Task.Run(async () =>
        {
            try
            {
                if (!immediate)
                {
                    await Task.Delay(_previewDebounceDelay, token).ConfigureAwait(false);
                }

                token.ThrowIfCancellationRequested();

                // 串行化渲染：基线读改写在 HtmlRenderer 内部跨方法、无法与取消协作，
                // 只能在门内完成，再由 UI 线程版本检查决定发布。
                await _previewRenderGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();

                    // 上一帧渲染产物未发布（被丢弃或渲染异常），渲染器基线已
                    // 推进到宿主未收到的状态：重置基线强制全量。
                    var effectiveFull = requireFull;
                    if (!effectiveFull
                        && _lastRenderedPreviewVersion != 0
                        && _lastRenderedPreviewVersion != _lastPublishedRenderVersion)
                    {
                        effectiveFull = true;
                        (renderer as IIncrementalPreviewRenderer)?.ResetIncrementalBaseline();
                    }

                    // 渲染不长持文档锁（大文档整篇 HTML 生成可达数百毫秒）。
                    // 安全前提：已发布树只读、Root 原子切换、样式整体替换引用、
                    // StyleResolver 内部自取锁。稳态帧只产块级补丁。
                    string? fullHtml = null;
                    string? patchJson = null;
                    if (!effectiveFull && renderer is IIncrementalPreviewRenderer incrementalRenderer)
                    {
                        var incremental = incrementalRenderer.RenderIncremental(doc);
                        if (incremental.IsPatch)
                        {
                            patchJson = incremental.Patch!.ToPayloadJson();
                        }
                        else
                        {
                            fullHtml = incremental.FullHtml;
                        }
                    }
                    else
                    {
                        fullHtml = renderer.Render(doc).Html;
                    }

                    _lastRenderedPreviewVersion = previewVersion;
                    token.ThrowIfCancellationRequested();

                    RunOnUIThread(() =>
                    {
                        if (token.IsCancellationRequested || previewVersion != Volatile.Read(ref _previewVersion) || _isDisposed)
                            return;

                        _lastPublishedRenderVersion = previewVersion;
                        _publishedPreviewVersion++;
                        if (patchJson is not null)
                        {
                            HtmlPatch = patchJson;
                        }
                        else
                        {
                            HtmlPreview = fullHtml!;
                            HtmlPatch = null;
                        }

                        OnPropertyChanged(nameof(PublishedPreviewVersion));
                    });
                }
                finally
                {
                    _previewRenderGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // 标记本帧“渲染而未发布”：下一帧检测到不一致会重置基线并全量。
                Volatile.Write(ref _lastRenderedPreviewVersion, previewVersion);
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

    private bool UseBackgroundParsing(MarkdownDocument document) =>
        _editorOptions.BackgroundParsing == BackgroundParsingMode.On
        || (_editorOptions.BackgroundParsing == BackgroundParsingMode.Auto
            && document.SourceMarkdown.Length >= _editorOptions.BackgroundParsingThresholdChars);

    private void InvalidatePendingParses() => Interlocked.Increment(ref _parseVersion);

    private void ParseAndCommitInline(MarkdownDocument document)
    {
        var tree = _parser.ParseTree(
            document.SourceMarkdown, document.Root, document.LastParsedSource);
        document.ReplaceParsedRoot(tree, document.SourceMarkdown, markChanged: false);
    }

    private void RequestBackgroundParse(MarkdownDocument document)
    {
        var seq = Interlocked.Increment(ref _parseVersion);

        string source;
        string? previousSource;
        MarkdownNode previousRoot;
        lock (document)
        {
            // Root 与 LastParsedSource 必须在同一把锁内成对快照：二者描述同一棵已提交树。
            source = document.SourceMarkdown;
            previousRoot = document.Root;
            previousSource = document.LastParsedSource;
        }

        // 单消费者串行链：同一时刻最多一个解析在跑，旧轮次提交时被版本号丢弃。
        Task round;
        lock (_parseChainLock)
        {
            round = _parseChain.ContinueWith(
                _ => RunBackgroundParseRound(document, seq, source, previousSource, previousRoot),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
            _parseChain = round;
        }
    }

    private async Task RunBackgroundParseRound(
        MarkdownDocument document,
        long seq,
        string source,
        string? previousSource,
        MarkdownNode previousRoot)
    {
        try
        {
            // 纯函数解析：不触碰 document，可在后台线程安全执行。
            var tree = _parser.ParseTree(source, previousRoot, previousSource);

            // 写文档只能在 UI 线程的 lock 内；Post + TCS 让保存屏障等到树已替换。
            var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            RunOnUIThread(() =>
            {
                try
                {
                    CommitBackgroundParse(document, seq, source, tree);
                    committed.SetResult();
                }
                catch (Exception ex)
                {
                    committed.SetException(ex);
                }
            });

            await committed.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RunOnUIThread(() =>
            {
                if (!_isDisposed)
                {
                    ErrorMessage = $"Parse error: {ex.Message}";
                }
            });
        }
    }

    private void CommitBackgroundParse(
        MarkdownDocument document,
        long seq,
        string source,
        MarkdownNode tree)
    {
        lock (document)
        {
            if (_isDisposed || !ReferenceEquals(_activeDocument?.Document, document)) return;
            if (Volatile.Read(ref _parseVersion) != seq) return;
            if (!string.Equals(document.SourceMarkdown, source, StringComparison.Ordinal)) return;

            // source 已由命令提交（修订号已计），这里只换树。
            document.ReplaceParsedRoot(tree, source, markChanged: false);
            ActiveDocument!.RefreshAst();
            document.NormalizeEditorState();

            if (!string.IsNullOrWhiteSpace(document.EditorState.SelectedNodeId))
            {
                SelectionService.SelectById(document, document.EditorState.SelectedNodeId);
            }

            ResyncSelectionAfterAstChange();
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

    internal Task PendingParseCompletionAsync()
    {
        lock (_parseChainLock)
        {
            return _parseChain;
        }
    }

    private static void CalculateLineAndColumnRange(
        string text,
        int startOffset,
        int endOffset,
        out int startLine,
        out int startColumn,
        out int endLine,
        out int endColumn)
    {
        int start = Math.Clamp(startOffset, 0, text.Length);
        int end = Math.Clamp(endOffset, 0, text.Length);
        if (end < start)
            end = start;

        int line = 1;
        int lastLineBreak = -1;
        for (int i = 0; i < start; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lastLineBreak = i;
            }
        }

        startLine = line;
        startColumn = Math.Max(1, start - lastLineBreak);
        endLine = line;
        endColumn = Math.Max(1, start - lastLineBreak);

        for (int i = start; i < end; i++)
        {
            if (text[i] == '\n')
            {
                endLine++;
                endColumn = 1;
            }
            else
            {
                endColumn++;
            }
        }
    }

    private void EnsureEditableDocument()
    {
        if (ActiveDocument is not null) return;

        NewDocument();
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
            // 旧文档的在途解析提交一律作废（提交时也会再比对文档引用，双保险）。
            InvalidatePendingParses();

            if (doc is null)
            {
                ActiveDocument = null;
                _activeRenderer = null;
                SelectionService.Select(null);
                HtmlPreview = string.Empty;
                HtmlPatch = null;
            }
            else
            {
                ActiveDocument = new DocumentViewModel(doc, _uiDispatcher, _stylePreviewRenderer, ApplyNodeStyle);
                _activeRenderer = _rendererFactory.CreatePreview(doc);
                SelectionService.Select(null);
                ScheduleUpdatePreview(immediate: true);
            }
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        });
    }

    private void OnSelectionChanged(MarkdownNode? node)
    {
        RunOnUIThread(() =>
        {
            if (_isDisposed) return;
            _activeDocument?.StyleEditor.Bind(node);
        });
    }

    private void ApplyNodeStyle(MarkdownNode node, NodeStyle newStyle)
    {
        if (_isDisposed || _activeDocument is null) return;
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(newStyle);

        var document = _activeDocument.Document;

        lock (document)
        {
            var target = document.FindNode(node.Id) ?? node;
            if (StyleEquals(target.LocalStyle, newStyle)) return;

            var command = new ChangeStyleCommand(document, target, newStyle);
            _commandManager.Execute(command);

            _activeDocument.NotifyNodeStyleChanged(target.Id);
        }

        ScheduleUpdatePreview();
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    private void ResyncSelectionAfterAstChange()
    {
        if (_activeDocument is null) return;

        var document = _activeDocument.Document;
        var selected = SelectionService.SelectedNode;

        if (selected is null)
        {
            _activeDocument.StyleEditor.Bind(null);
            return;
        }

        var fresh = document.FindNode(selected.Id);
        if (ReferenceEquals(fresh, selected))
        {
            _activeDocument.StyleEditor.Bind(fresh, forceReload: true);
            _activeDocument.NotifyNodeStyleChanged(fresh!.Id);
        }
        else
        {
            SelectionService.Select(fresh);
        }
    }

    private static bool StyleEquals(NodeStyle a, NodeStyle b) =>
        string.Equals(a.FontFamily, b.FontFamily, StringComparison.Ordinal)
        && a.FontSize == b.FontSize
        && NullableEquals(a.LineHeight, b.LineHeight)
        && string.Equals(a.TextAlign, b.TextAlign, StringComparison.Ordinal)
        && a.Bold == b.Bold
        && a.Italic == b.Italic
        && string.Equals(a.ForegroundColor, b.ForegroundColor, StringComparison.Ordinal)
        && string.Equals(a.BackgroundColor, b.BackgroundColor, StringComparison.Ordinal)
        && string.Equals(a.BorderColor, b.BorderColor, StringComparison.Ordinal)
        && NullableEquals(a.BorderWidth, b.BorderWidth)
        && Equals(a.Margin, b.Margin)
        && Equals(a.Padding, b.Padding)
        && string.Equals(a.CustomCss, b.CustomCss, StringComparison.Ordinal);

    private static bool NullableEquals(double? a, double? b) =>
        a.HasValue == b.HasValue && (!a.HasValue || Math.Abs(a.Value - b!.Value) < 0.000001d);

    private void RunOnUIThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        AvaloniaUiThreadDispatcher.Run(_uiDispatcher, action);
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

            InvalidatePendingParses();

            _previewCts?.Cancel();
            _previewCts?.Dispose();
            _previewCts = null;
        }

        if (_activeDocument is not null)
        {
            _activeDocument.StyleMutated -= OnActiveDocumentStyleMutated;
            _activeDocument.StyleEditor.StyleCommitted -= ApplyNodeStyle;
            _activeDocument.Dispose();
            _activeDocument = null;
        }

        SelectionService.SelectedNodeChanged -= OnSelectionChanged;
        _commandManager.CommandStateChanged -= OnCommandStateChanged;
        _documentService.CurrentDocumentChanged -= OnCurrentDocumentChanged;
        _documentService.HasUnsavedChangesChanged -= OnHasUnsavedChangesChanged;
    }
}
