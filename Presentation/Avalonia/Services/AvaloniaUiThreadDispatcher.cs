using System;
using System.Threading;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Threading;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.Services;

/// <summary>
/// 基于 .NET 标准 SynchronizationContext 的 UI 线程调度器。
/// 无需引用 Avalonia 外部包，天然适配 Avalonia/WPF 等各类 UI 框架。
/// </summary>
public sealed class SynchronizationContextUiThreadDispatcher : IUiThreadDispatcher
{
    private readonly SynchronizationContext? _context;
    private readonly int _uiThreadId;

    public SynchronizationContextUiThreadDispatcher(SynchronizationContext? context = null)
    {
        _context = context ?? SynchronizationContext.Current;
        _uiThreadId = Environment.CurrentManagedThreadId;
    }

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (CheckAccess() || _context is null)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }

    public bool CheckAccess()
    {
        return Environment.CurrentManagedThreadId == _uiThreadId;
    }
}