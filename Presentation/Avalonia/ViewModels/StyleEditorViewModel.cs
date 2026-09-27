using System;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Threading;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.Services;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.ViewModels;

public sealed class StyleEditorViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IStylePreviewRenderer _renderer;
    private readonly IUiThreadDispatcher? _uiDispatcher;
    private readonly object _syncLock = new();
    private CancellationTokenSource? _debounceCts;
    private NodeStyle _draft = new();
    private string _previewHtml = string.Empty;
    private string? _nodeType;
    private MarkdownNode? _boundNode;
    private bool _isLoading;
    private bool _isDisposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action<MarkdownNode, NodeStyle>? StyleCommitted;

    public bool HasBoundNode => _boundNode is not null;

    public string? NodeType
    {
        get => _nodeType;
        private set => SetField(ref _nodeType, value);
    }

    public string PreviewHtml
    {
        get => _previewHtml;
        private set => SetField(ref _previewHtml, value);
    }

    public StyleEditorViewModel(
        IUiThreadDispatcher? uiDispatcher = null,
        IStylePreviewRenderer? previewRenderer = null)
    {
        _uiDispatcher = uiDispatcher;
        _renderer = previewRenderer ?? new UnstyledStylePreviewRenderer();
        PreviewHtml = _renderer.RenderPreviewHtml(_draft, _nodeType);
    }

    public void Bind(MarkdownNode? node, bool forceReload = false)
    {
        if (_isDisposed) return;
        if (_isLoading) return;
        if (!forceReload && ReferenceEquals(_boundNode, node)) return;

        _boundNode = node;
        LoadFromBoundNode();
    }

    public string? FontFamily
    {
        get => _draft.FontFamily;
        set
        {
            if (string.Equals(_draft.FontFamily, value, StringComparison.Ordinal)) return;
            _draft.FontFamily = value;
            OnPropertyChanged();
            RequestCommit();
        }
    }

    public decimal? FontSize
    {
        get => _draft.FontSize.HasValue ? (decimal)_draft.FontSize.Value : null;
        set
        {
            var target = value.HasValue ? (double)value.Value : (double?)null;
            if (_draft.FontSize == target) return;
            _draft.FontSize = target;
            OnPropertyChanged();
            RequestCommit();
        }
    }

    public decimal? LineHeight
    {
        get => _draft.LineHeight.HasValue ? (decimal)_draft.LineHeight.Value : null;
        set
        {
            var target = value.HasValue ? (double)value.Value : (double?)null;
            if (_draft.LineHeight == target) return;
            _draft.LineHeight = target;
            OnPropertyChanged();
            RequestCommit();
        }
    }

    public string? TextAlign
    {
        get => _draft.TextAlign;
        set
        {
            if (string.Equals(_draft.TextAlign, value, StringComparison.Ordinal)) return;
            _draft.TextAlign = value;
            OnPropertyChanged();
            RequestCommit();
        }
    }

    public bool? Bold
    {
        get => _draft.Bold;
        set
        {
            if (_draft.Bold == value) return;
            _draft.Bold = value;
            OnPropertyChanged();
            RequestCommit();
        }
    }

    public bool? Italic
    {
        get => _draft.Italic;
        set
        {
            if (_draft.Italic == value) return;
            _draft.Italic = value;
            OnPropertyChanged();
            RequestCommit();
        }
    }

    public string? ForegroundColor
    {
        get => _draft.ForegroundColor;
        set
        {
            if (string.Equals(_draft.ForegroundColor, value, StringComparison.Ordinal)) return;
            _draft.ForegroundColor = value;
            OnPropertyChanged();
            RequestCommit();
        }
    }

    public string? BackgroundColor
    {
        get => _draft.BackgroundColor;
        set
        {
            if (string.Equals(_draft.BackgroundColor, value, StringComparison.Ordinal)) return;
            _draft.BackgroundColor = value;
            OnPropertyChanged();
            RequestCommit();
        }
    }

    public string? BorderColor
    {
        get => _draft.BorderColor;
        set
        {
            if (string.Equals(_draft.BorderColor, value, StringComparison.Ordinal)) return;
            _draft.BorderColor = value;
            OnPropertyChanged();
            RequestCommit();
        }
    }

    public decimal? BorderWidth
    {
        get => _draft.BorderWidth.HasValue ? (decimal)_draft.BorderWidth.Value : null;
        set
        {
            var target = value.HasValue ? (double)value.Value : (double?)null;
            if (_draft.BorderWidth == target) return;
            _draft.BorderWidth = target;
            OnPropertyChanged();
            RequestCommit();
        }
    }

    public string? Margin
    {
        get => ThicknessStringConverter.Format(_draft.Margin);
        set
        {
            var parsed = ThicknessStringConverter.Parse(value);
            if (Equals(_draft.Margin, parsed)) return;

            _draft.Margin = parsed;
            OnPropertyChanged();
            RequestCommit();
        }
    }

    public string? Padding
    {
        get => ThicknessStringConverter.Format(_draft.Padding);
        set
        {
            var parsed = ThicknessStringConverter.Parse(value);
            if (Equals(_draft.Padding, parsed)) return;

            _draft.Padding = parsed;
            OnPropertyChanged();
            RequestCommit();
        }
    }

    public string? CustomCss
    {
        get => _draft.CustomCss;
        set
        {
            if (string.Equals(_draft.CustomCss, value, StringComparison.Ordinal)) return;
            _draft.CustomCss = value;
            OnPropertyChanged();
            RequestCommit();
        }
    }

    private void LoadFromBoundNode()
    {
        _isLoading = true;
        try
        {
            _draft = _boundNode?.LocalStyle.Clone() ?? new NodeStyle();
            _nodeType = _boundNode?.Type.ToString();

            OnPropertyChanged(nameof(HasBoundNode));
            OnPropertyChanged(nameof(NodeType));
            OnPropertyChanged(nameof(FontFamily));
            OnPropertyChanged(nameof(FontSize));
            OnPropertyChanged(nameof(LineHeight));
            OnPropertyChanged(nameof(TextAlign));
            OnPropertyChanged(nameof(Bold));
            OnPropertyChanged(nameof(Italic));
            OnPropertyChanged(nameof(ForegroundColor));
            OnPropertyChanged(nameof(BackgroundColor));
            OnPropertyChanged(nameof(BorderColor));
            OnPropertyChanged(nameof(BorderWidth));
            OnPropertyChanged(nameof(Margin));
            OnPropertyChanged(nameof(Padding));
            OnPropertyChanged(nameof(CustomCss));
        }
        finally
        {
            _isLoading = false;
        }

        PreviewHtml = _renderer.RenderPreviewHtml(_draft, _nodeType);
    }

    private void RequestCommit()
    {
        ScheduleRender();

        if (_isLoading || _isDisposed || _boundNode is null) return;

        StyleCommitted?.Invoke(_boundNode, _draft.Clone());
    }

    private void ScheduleRender()
    {
        lock (_syncLock)
        {
            if (_isDisposed) return;

            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;

            _ = RenderPreviewAsync(token);
        }
    }

    private async Task RenderPreviewAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(150, token).ConfigureAwait(false);
            if (token.IsCancellationRequested || _isDisposed) return;

            var html = _renderer.RenderPreviewHtml(_draft, _nodeType);

            if (token.IsCancellationRequested || _isDisposed) return;

            RunOnUIThread(() =>
            {
                if (!token.IsCancellationRequested && !_isDisposed)
                {
                    PreviewHtml = html;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RunOnUIThread(() =>
            {
                if (!_isDisposed)
                {
                    PreviewHtml = $"<div style='color:red;'>Style preview error: {WebUtility.HtmlEncode(ex.Message)}</div>";
                }
            });
        }
    }

    private void RunOnUIThread(Action action)
    {
        if (_isDisposed) return;
        AvaloniaUiThreadDispatcher.Run(_uiDispatcher, action);
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private sealed class UnstyledStylePreviewRenderer : IStylePreviewRenderer
    {
        public string RenderPreviewHtml(NodeStyle? style, string? nodeType = null, string? customContent = null)
        {
            var sample = string.IsNullOrWhiteSpace(customContent)
                ? nodeType switch
                {
                    "Heading" => "标题样式预览 (Heading Preview)",
                    "CodeBlock" => "// 示例代码\npublic class MarkdownEditor { }",
                    _ => "这是一段用于预览节点样式的正文文本。"
                }
                : customContent;

            return "<div class=\"preview-container\" style=\"box-sizing:border-box; width:100%; font-family:sans-serif;\"><p>"
                + WebUtility.HtmlEncode(sample).Replace("\n", "<br />")
                + "</p></div>";
        }
    }
}
