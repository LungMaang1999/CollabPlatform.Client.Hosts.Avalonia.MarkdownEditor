namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;

public interface IEditorCommand
{
    string Description { get; }
    void Execute();
    void Undo();
}