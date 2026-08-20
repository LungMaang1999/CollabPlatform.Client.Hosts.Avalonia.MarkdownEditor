using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;

public interface IDocumentRendererFactory
{
    IDocumentRenderer Create(MarkdownDocument document);
}