namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;

public sealed class BatchCommand : IEditorCommand, IEditorCommandState
{
    private readonly IReadOnlyList<IEditorCommand> _commands;

    public string Description { get; }
    public bool HasChanges { get; private set; }

    public BatchCommand(string description, IEnumerable<IEditorCommand> commands)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        _commands = (commands ?? throw new ArgumentNullException(nameof(commands))).ToArray();
        Description = description;
    }

    public void Execute()
    {
        HasChanges = false;
        foreach (var cmd in _commands)
        {
            cmd.Execute();
            if (cmd is IEditorCommandState { HasChanges: true }) HasChanges = true;
        }
    }

    public void Undo()
    {
        for (int i = _commands.Count - 1; i >= 0; i--)
            _commands[i].Undo();
    }
}