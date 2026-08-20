using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Editing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Parsing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing;

public sealed class MarkdownEditApplier : IMarkdownEditApplier
{
    private readonly IMarkdownParser _parser;

    public MarkdownEditApplier(IMarkdownParser parser) =>
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));

    public void Apply(MarkdownDocument document, string source)
    {
        ArgumentNullException.ThrowIfNull(document);
        _parser.Parse(source ?? string.Empty, document, ParseMode.Editing);
    }
}