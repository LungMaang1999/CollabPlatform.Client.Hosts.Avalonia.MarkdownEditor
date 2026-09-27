using System;
using Avalonia.Threading;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Threading;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.Services;

public sealed class AvaloniaUiThreadDispatcher : IUiThreadDispatcher
{
    public static void Run(IUiThreadDispatcher? dispatcher, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (dispatcher is not null)
        {
            if (dispatcher.CheckAccess())
                action();
            else
                dispatcher.Post(action);
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = Dispatcher.UIThread;
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Post(action);
        }
    }

    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();
}
