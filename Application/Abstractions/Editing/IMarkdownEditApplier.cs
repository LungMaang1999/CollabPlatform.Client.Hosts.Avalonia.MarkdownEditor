using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Editing;

public interface IMarkdownEditApplier
{
    void Apply(MarkdownDocument document, string source);
}