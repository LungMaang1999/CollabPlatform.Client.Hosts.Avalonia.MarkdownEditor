using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Editing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;

public sealed class TransformMarkdownSourceCommand : IEditorCommand, IEditorCommandState
{
    private readonly MarkdownDocument _document;
    private readonly Func<string, string> _transform;
    private readonly string _oldSource;
    private readonly IMarkdownEditApplier _editApplier;
    private string? _newSource;

    public string Description { get; }
    public bool HasChanges { get; private set; }

    public TransformMarkdownSourceCommand(MarkdownDocument document, IMarkdownEditApplier editApplier, Func<string, string> transform, string description)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _editApplier = editApplier ?? throw new ArgumentNullException(nameof(editApplier));
        _transform = transform ?? throw new ArgumentNullException(nameof(transform));
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        Description = description;
        _oldSource = document.SourceMarkdown;
    }

    public void Execute()
    {
        _newSource ??= _transform(_oldSource);
        HasChanges = Apply(_newSource);
    }

    public void Undo() => HasChanges = Apply(_oldSource);

    private bool Apply(string source)
    {
        if (string.Equals(_document.SourceMarkdown, source, StringComparison.Ordinal))
            return false;

        _editApplier.Apply(_document, source);
        return true;
    }
}