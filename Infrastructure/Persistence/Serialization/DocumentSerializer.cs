using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Schema;
using System.Text;
using System.Xml.Linq;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

public sealed class DocumentSerializer : IDocumentSerializer
{
    public void Serialize(
        MarkdownDocument document,
        Stream destination,
        string sourceFileName,
        string sourceFileHash)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(destination);

        if (string.IsNullOrWhiteSpace(sourceFileName))
        {
            throw new ArgumentException(
                "sourceFileName cannot be null, empty, or whitespace.",
                nameof(sourceFileName));
        }

        if (string.IsNullOrWhiteSpace(sourceFileHash))
        {
            throw new ArgumentException(
                "sourceFileHash cannot be null, empty, or whitespace.",
                nameof(sourceFileHash));
        }

        var dto = DocumentPackageMapper.ToDto(
            document,
            sourceFileName,
            sourceFileHash);

        var xml = DocumentXmlWriter.Write(dto);

        using var writer = new StreamWriter(
            destination,
            new UTF8Encoding(false),
            bufferSize: 4096,
            leaveOpen: true);

        writer.Write(xml.ToString(SaveOptions.DisableFormatting));
        writer.Flush();
    }

    public MarkdownDocument Deserialize(
        Stream source,
        string sourceMarkdown,
        string sourceFilePath = "")
    {
        ArgumentNullException.ThrowIfNull(source);

        var xml = XDocument.Load(source, LoadOptions.PreserveWhitespace);
        var migrated = SchemaMigrator.PrepareForReading(xml);
        var dto = DocumentXmlReader.Read(migrated);

        return DocumentPackageMapper.ToDomain(
            dto,
            sourceMarkdown,
            sourceFilePath);
    }
}