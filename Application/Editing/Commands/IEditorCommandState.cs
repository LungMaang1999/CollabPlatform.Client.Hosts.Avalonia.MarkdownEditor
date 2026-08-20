namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;

public interface IEditorCommandState
{
    bool HasChanges { get; }
}