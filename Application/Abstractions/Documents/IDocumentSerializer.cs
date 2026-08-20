using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;

public interface IDocumentSerializer
{
    void Serialize(MarkdownDocument document, Stream destination, string sourceFileName, string sourceFileHash);
    MarkdownDocument Deserialize(Stream source, string sourceMarkdown, string sourceFilePath = "");
}