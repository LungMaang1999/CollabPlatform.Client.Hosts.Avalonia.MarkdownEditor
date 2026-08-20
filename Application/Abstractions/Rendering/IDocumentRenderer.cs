using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;

public interface IDocumentRenderer
{
    RenderResult Render(MarkdownDocument document);
}