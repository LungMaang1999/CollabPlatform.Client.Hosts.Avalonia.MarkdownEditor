using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Editing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;

public sealed class DeleteRangeCommand : IEditorCommand, IEditorCommandState
{
    private readonly TransformMarkdownSourceCommand _inner;

    public string Description => _inner.Description;
    public bool HasChanges => _inner.HasChanges;

    public DeleteRangeCommand(MarkdownDocument document, IMarkdownEditApplier editApplier, IMarkdownSourceEditor sourceEditor, SourceRange range)
    {
        _inner = new TransformMarkdownSourceCommand(document, editApplier, src => sourceEditor.DeleteRange(src, range), "Delete Markdown Range");
    }

    public void Execute() => _inner.Execute();
    public void Undo() => _inner.Undo();
}