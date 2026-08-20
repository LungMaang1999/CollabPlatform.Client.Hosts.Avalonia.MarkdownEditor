using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Editing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;

public sealed class ChangeTextCommand : IEditorCommand, IEditorCommandState
{
    private readonly TransformMarkdownSourceCommand _inner;

    public string Description => _inner.Description;
    public bool HasChanges => _inner.HasChanges;

    public ChangeTextCommand(MarkdownDocument document, IMarkdownEditApplier editApplier, IMarkdownSourceEditor sourceEditor, SourceRange range, string? replacement)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(editApplier);
        ArgumentNullException.ThrowIfNull(sourceEditor);
        ArgumentNullException.ThrowIfNull(range);

        replacement ??= string.Empty;
        _inner = new TransformMarkdownSourceCommand(document, editApplier, src => sourceEditor.ReplaceRange(src, range, replacement), "Change Markdown text");
    }

    public void Execute() => _inner.Execute();
    public void Undo() => _inner.Undo();
}