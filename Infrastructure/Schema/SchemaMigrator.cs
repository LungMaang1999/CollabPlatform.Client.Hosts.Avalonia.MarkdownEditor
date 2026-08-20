using System.Xml.Linq;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Schema;

internal static class SchemaMigrator
{
    public const string NamespaceName = "urn:markdown-enhanced-editor";
    public const string CurrentSchemaVersion = "1.0";
    public const string CurrentEditorVersion = "1.0.0";

    private static readonly XNamespace Ns = NamespaceName;

    public static XDocument PrepareForReading(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = document.Root ?? throw new InvalidDataException("The XML document has no root element.");

        if (root.Name.LocalName != "DocumentPackage")
            throw new InvalidDataException($"Unexpected XML root element '{root.Name.LocalName}'.");

        var schemaVersion = (string?)root.Attribute("schemaVersion");
        if (string.IsNullOrWhiteSpace(schemaVersion))
            root.SetAttributeValue("schemaVersion", CurrentSchemaVersion);
        else if (!IsSupported(schemaVersion))
            throw new InvalidDataException($"Unsupported document schema version '{schemaVersion}'.");

        if (root.Attribute("editorVersion") is null)
            root.SetAttributeValue("editorVersion", CurrentEditorVersion);

        NormalizeNamespace(root);
        return document;
    }

    public static bool IsSupported(string version) =>
        Version.TryParse(version, out var parsed) && parsed.Major == 1;

    private static void NormalizeNamespace(XElement root)
    {
        if (root.Name.Namespace == XNamespace.None)
        {
            foreach (var element in root.DescendantsAndSelf())
                element.Name = Ns + element.Name.LocalName;
        }
    }
}