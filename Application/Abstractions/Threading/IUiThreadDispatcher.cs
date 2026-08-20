using System;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Threading;

/// <summary>
/// 抽象 UI 线程调度器，用于 Presentation/ViewModel 层与具体的 Avalonia UI 线程解耦。
/// </summary>
public interface IUiThreadDispatcher
{
    /// <summary>
    /// 将操作分发到 UI 线程异步执行。
    /// </summary>
    void Post(Action action);

    /// <summary>
    /// 检查当前是否运行在 UI 线程上。
    /// </summary>
    bool CheckAccess();
}