using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

public interface IStyleResolver
{
    ComputedStyle Resolve(MarkdownNode node);
    void Invalidate(MarkdownNode? node = null);
    void Clear();
}