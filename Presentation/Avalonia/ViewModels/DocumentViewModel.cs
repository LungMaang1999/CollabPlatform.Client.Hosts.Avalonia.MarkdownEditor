using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.ViewModels;

public sealed class DocumentViewModel
{
    public MarkdownDocument Document { get; }
    public NodeViewModel RootNode { get; }
    public string FilePath => Document.FilePath;
    public bool IsModified => Document.IsModified;

    public DocumentViewModel(MarkdownDocument document)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        RootNode = new NodeViewModel(document.Root);
    }
}