using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Threading;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.ViewModels;

/// <summary>
/// 样式编辑器 ViewModel（支持统一线程调度器注入与严格生命周期管理）
/// </summary>
internal sealed class StyleEditorViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly StylePreviewRenderer _renderer = new();
    private readonly IUiThreadDispatcher? _uiDispatcher;
    private readonly object _syncLock = new();
    private CancellationTokenSource? _debounceCts;
    private NodeStyleDto _styleDto;
    private string _previewHtml = string.Empty;
    private string? _nodeType;
    private bool _isDisposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal NodeStyleDto StyleDto
    {
        get => _styleDto;
        set
        {
            _styleDto = value ?? new NodeStyleDto();
            OnPropertyChanged();
            ScheduleRender();
        }
    }

    public string? NodeType
    {
        get => _nodeType;
        set
        {
            if (_nodeType == value) return;
            _nodeType = value;
            OnPropertyChanged();
            ScheduleRender();
        }
    }

    public string PreviewHtml
    {
        get => _previewHtml;
        private set
        {
            if (_previewHtml == value) return;
            _previewHtml = value;
            OnPropertyChanged();
        }
    }

    internal StyleEditorViewModel(NodeStyleDto? style = null, string? nodeType = null, IUiThreadDispatcher? uiDispatcher = null)
    {
        _uiDispatcher = uiDispatcher;
        _styleDto = style ?? new NodeStyleDto();
        _nodeType = nodeType;
        PreviewHtml = _renderer.RenderPreviewHtml(_styleDto, _nodeType);
    }

    public void NotifyStyleChanged() => ScheduleRender();

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

            var html = _renderer.RenderPreviewHtml(_styleDto, _nodeType);

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
            // 防抖正常取消
        }
        catch (Exception ex)
        {
            RunOnUIThread(() =>
            {
                if (!_isDisposed)
                {
                    PreviewHtml = $"<div style='color:red;'>Style preview error: {ex.Message}</div>";
                }
            });
        }
    }

    private void RunOnUIThread(Action action)
    {
        if (_isDisposed) return;

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
}