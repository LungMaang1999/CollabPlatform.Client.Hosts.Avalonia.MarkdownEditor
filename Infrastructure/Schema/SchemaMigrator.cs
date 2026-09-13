using System;
using System.IO;
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

        // 规范化命名空间，使得未指定命名空间的文档也能与当前命名空间保持一致
        NormalizeNamespace(root);

        // 统一检测 SchemaVersion（优先检测标准子元素，兼容属性写法）
        var schemaVersion = root.Element(Ns + "SchemaVersion")?.Value
                         ?? (string?)root.Attribute("schemaVersion")
                         ?? (string?)root.Attribute("SchemaVersion");

        if (string.IsNullOrWhiteSpace(schemaVersion))
        {
            // 如果不存在，则设置标准子元素
            if (root.Element(Ns + "SchemaVersion") is null)
            {
                root.AddFirst(new XElement(Ns + "SchemaVersion", CurrentSchemaVersion));
            }
        }
        else if (!IsSupported(schemaVersion))
        {
            throw new InvalidDataException($"Unsupported document schema version '{schemaVersion}'.");
        }

        // 统一检测 EditorVersion
        var editorVersion = root.Element(Ns + "EditorVersion")?.Value
                         ?? (string?)root.Attribute("editorVersion")
                         ?? (string?)root.Attribute("EditorVersion");

        if (string.IsNullOrWhiteSpace(editorVersion) && root.Element(Ns + "EditorVersion") is null)
        {
            var schemaEl = root.Element(Ns + "SchemaVersion");
            if (schemaEl is not null)
            {
                schemaEl.AddAfterSelf(new XElement(Ns + "EditorVersion", CurrentEditorVersion));
            }
            else
            {
                root.Add(new XElement(Ns + "EditorVersion", CurrentEditorVersion));
            }
        }

        return document;
    }

    public static bool IsSupported(string version) =>
        Version.TryParse(version, out var parsed) && parsed.Major == 1;

    private static void NormalizeNamespace(XElement root)
    {
        if (root.Name.Namespace == XNamespace.None)
        {
            foreach (var element in root.DescendantsAndSelf())
            {
                element.Name = Ns + element.Name.LocalName;
            }
        }
    }
}