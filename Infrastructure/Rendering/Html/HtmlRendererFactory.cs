using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Html;

public sealed class HtmlRendererFactory : IDocumentRendererFactory
{
    public IDocumentRenderer Create(MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var styleCache = new StyleCache();
        var styleResolver = new StyleResolver(document, styleCache);

        return new HtmlRenderer(styleResolver);
    }
}