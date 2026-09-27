using System;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Threading;

public interface IUiThreadDispatcher
{
    void Post(Action action);

    bool CheckAccess();
}
