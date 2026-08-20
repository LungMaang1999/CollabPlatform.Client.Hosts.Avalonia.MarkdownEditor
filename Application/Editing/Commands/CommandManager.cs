namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;

public sealed class CommandManager
{
    private readonly Stack<IEditorCommand> _undoStack = new();
    private readonly Stack<IEditorCommand> _redoStack = new();

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public event EventHandler? CommandStateChanged;

    public void Execute(IEditorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Execute();

        if (command is IEditorCommandState state && !state.HasChanges)
            return;

        _undoStack.Push(command);
        _redoStack.Clear();
        CommandStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo()
    {
        if (_undoStack.Count == 0) return false;
        var command = _undoStack.Pop();
        try
        {
            command.Undo();
            _redoStack.Push(command);
            CommandStateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch
        {
            _undoStack.Push(command);
            throw;
        }
    }

    public bool Redo()
    {
        if (_redoStack.Count == 0) return false;
        var command = _redoStack.Pop();
        try
        {
            command.Execute();
            if (command is IEditorCommandState state && !state.HasChanges)
                return false;

            _undoStack.Push(command);
            CommandStateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch
        {
            _redoStack.Push(command);
            throw;
        }
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        CommandStateChanged?.Invoke(this, EventArgs.Empty);
    }
}