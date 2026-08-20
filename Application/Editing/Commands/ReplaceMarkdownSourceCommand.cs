using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Editing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;

public sealed class ReplaceMarkdownSourceCommand : IEditorCommand, IEditorCommandState
{
    private readonly MarkdownDocument _document;
    private readonly string _oldSource;
    private readonly string _newSource;
    private readonly IMarkdownEditApplier _editApplier;

    public string Description => "Replace Markdown Source";
    public bool HasChanges { get; private set; }

    public ReplaceMarkdownSourceCommand(MarkdownDocument document, IMarkdownEditApplier editApplier, string? newSource)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _editApplier = editApplier ?? throw new ArgumentNullException(nameof(editApplier));
        _oldSource = document.SourceMarkdown;
        _newSource = newSource ?? string.Empty;
    }

    public void Execute() => HasChanges = Apply(_newSource);
    public void Undo() => HasChanges = Apply(_oldSource);

    private bool Apply(string source)
    {
        if (string.Equals(_document.SourceMarkdown, source, StringComparison.Ordinal)) return false;
        _editApplier.Apply(_document, source);
        return true;
    }
}