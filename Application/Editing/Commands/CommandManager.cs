namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;

/// <summary>
/// 命令自报占用的近似内存（字节）。命令栈据此对保留的历史做总量裁剪，
/// 避免持有整文档快照的命令（如全文替换）在长会话中把内存撑爆。
/// </summary>
public interface IEditorCommandMemory
{
    long EstimatedBytes { get; }
}

public sealed class CommandManager
{
    public const int DefaultMaxSteps = 200;
    public const long DefaultMaxRetainedBytes = 64L * 1024 * 1024;

    private const long DefaultCommandBytes = 512;

    // LinkedList 支持 O(1) 从最旧端裁剪；First=最新，Last=最旧。
    private readonly LinkedList<IEditorCommand> _undoStack = new();
    private readonly LinkedList<IEditorCommand> _redoStack = new();

    private long _undoBytes;
    private long _redoBytes;

    /// <summary>撤销历史保留的最大步数；超出后丢弃最旧的命令。</summary>
    public int MaxSteps { get; set; } = DefaultMaxSteps;

    /// <summary>撤销+重做历史允许保留的近似总字节数；超出后优先丢弃最旧的撤销历史。</summary>
    public long MaxRetainedBytes { get; set; } = DefaultMaxRetainedBytes;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public int UndoCount => _undoStack.Count;
    public int RedoCount => _redoStack.Count;

    public event EventHandler? CommandStateChanged;

    public void Execute(IEditorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Execute();

        if (command is IEditorCommandState state && !state.HasChanges)
            return;

        _undoStack.AddFirst(command);
        _undoBytes += Weight(command);

        _redoStack.Clear();
        _redoBytes = 0;

        TrimUndo();
        CommandStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo()
    {
        if (_undoStack.Count == 0) return false;

        var node = _undoStack.First!;
        var command = node.Value;

        // 先执行撤销再移动节点：失败时两个栈保持原状。
        command.Undo();

        _undoStack.Remove(node);
        _undoBytes -= Weight(command);

        _redoStack.AddFirst(command);
        _redoBytes += Weight(command);

        TrimToBudget();
        CommandStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (_redoStack.Count == 0) return false;

        var node = _redoStack.First!;
        var command = node.Value;

        command.Execute();
        var changed = command is not IEditorCommandState state || state.HasChanges;

        _redoStack.Remove(node);
        _redoBytes -= Weight(command);

        _undoStack.AddFirst(command);
        _undoBytes += Weight(command);

        TrimToBudget();
        CommandStateChanged?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        _undoBytes = 0;
        _redoBytes = 0;
        CommandStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TrimUndo()
    {
        while (_undoStack.Count > MaxSteps && _undoStack.Last is { } oldest)
        {
            DropUndoOldest(oldest);
        }

        TrimToBudget();
    }

    private void TrimToBudget()
    {
        while (_undoBytes + _redoBytes > MaxRetainedBytes)
        {
            if (_undoStack.Last is { } undoOldest)
            {
                DropUndoOldest(undoOldest);
            }
            else if (_redoStack.Last is { } redoOldest)
            {
                _redoStack.RemoveLast();
                _redoBytes -= Weight(redoOldest.Value);
            }
            else
            {
                break;
            }
        }
    }

    private void DropUndoOldest(LinkedListNode<IEditorCommand> node)
    {
        _undoStack.Remove(node);
        _undoBytes -= Weight(node.Value);
    }

    private static long Weight(IEditorCommand command) =>
        command is IEditorCommandMemory memory && memory.EstimatedBytes > 0
            ? memory.EstimatedBytes
            : DefaultCommandBytes;
}
