using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;

public interface IStylePreviewRenderer
{
    string RenderPreviewHtml(NodeStyle? style, string? nodeType = null, string? customContent = null);
}
