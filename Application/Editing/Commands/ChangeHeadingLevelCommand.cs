using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Editing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;

public sealed class ChangeHeadingLevelCommand : IEditorCommand, IEditorCommandState
{
    private readonly TransformMarkdownSourceCommand _inner;

    public string Description => _inner.Description;
    public bool HasChanges => _inner.HasChanges;

    public ChangeHeadingLevelCommand(MarkdownDocument document, IMarkdownEditApplier editApplier, IMarkdownSourceEditor sourceEditor, MarkdownNode heading, int newLevel)
    {
        ArgumentNullException.ThrowIfNull(heading);
        if (heading.Type != NodeType.Heading) throw new InvalidOperationException("Target node must be a heading.");

        _inner = new TransformMarkdownSourceCommand(document, editApplier, src => sourceEditor.ChangeHeadingLevel(src, heading.Range, newLevel), "Change Heading Level");
    }

    public void Execute() => _inner.Execute();
    public void Undo() => _inner.Undo();
}