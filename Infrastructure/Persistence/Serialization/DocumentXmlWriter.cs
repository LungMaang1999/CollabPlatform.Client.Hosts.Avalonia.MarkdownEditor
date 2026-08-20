using System;
using System.Linq;
using System.Xml.Linq;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Schema;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

internal static class DocumentXmlWriter
{
    public static XDocument Write(DocumentPackageDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var ns = SchemaMigrator.NamespaceName;
        var nsX = XNamespace.Get(ns);

        XElement WriteNode(NodeDto node)
        {
            var element = new XElement(
                nsX + "Node",
                new XAttribute("Id", node.Id ?? string.Empty),
                new XAttribute("Type", node.Type ?? string.Empty),
                new XAttribute("Category", node.Category ?? string.Empty),
                new XAttribute("IsSynthetic", node.IsSynthetic ? "1" : "0"));

            if (!string.IsNullOrEmpty(node.Text))
            {
                element.Add(new XElement(nsX + "Text", node.Text));
            }

            if (!string.IsNullOrEmpty(node.RawMarkdown))
            {
                element.Add(new XElement(nsX + "RawMarkdown", node.RawMarkdown));
            }

            if (node.Level.HasValue)
            {
                element.Add(new XElement(nsX + "Level", node.Level.Value));
            }

            if (!string.IsNullOrWhiteSpace(node.StyleId))
            {
                element.Add(new XElement(nsX + "StyleId", node.StyleId));
            }

            if (node.IsTableHeader)
            {
                element.Add(new XElement(nsX + "IsTableHeader", "1"));
            }

            if (node.LocalStyle is not null)
            {
                var styleElement = new XElement(nsX + "LocalStyle");

                void AddIf<T>(string name, T? value)
                {
                    if (value is not null)
                    {
                        styleElement.Add(new XElement(nsX + name, value));
                    }
                }

                AddIf("FontFamily", node.LocalStyle.FontFamily);
                AddIf("FontSize", node.LocalStyle.FontSize);
                AddIf("ForegroundColor", node.LocalStyle.ForegroundColor);
                AddIf("BackgroundColor", node.LocalStyle.BackgroundColor);
                AddIf(
                    "Bold",
                    node.LocalStyle.Bold.HasValue
                        ? node.LocalStyle.Bold.Value ? "1" : "0"
                        : null);
                AddIf(
                    "Italic",
                    node.LocalStyle.Italic.HasValue
                        ? node.LocalStyle.Italic.Value ? "1" : "0"
                        : null);
                AddIf("LineHeight", node.LocalStyle.LineHeight);
                AddIf("TextAlign", node.LocalStyle.TextAlign);
                AddIf("BorderColor", node.LocalStyle.BorderColor);
                AddIf("BorderWidth", node.LocalStyle.BorderWidth);
                AddIf("CustomCss", node.LocalStyle.CustomCss);

                if (node.LocalStyle.Margin is not null)
                {
                    styleElement.Add(
                        new XElement(
                            nsX + "Margin",
                            new XElement(nsX + "Left", node.LocalStyle.Margin.Left),
                            new XElement(nsX + "Top", node.LocalStyle.Margin.Top),
                            new XElement(nsX + "Right", node.LocalStyle.Margin.Right),
                            new XElement(nsX + "Bottom", node.LocalStyle.Margin.Bottom)));
                }

                if (node.LocalStyle.Padding is not null)
                {
                    styleElement.Add(
                        new XElement(
                            nsX + "Padding",
                            new XElement(nsX + "Left", node.LocalStyle.Padding.Left),
                            new XElement(nsX + "Top", node.LocalStyle.Padding.Top),
                            new XElement(nsX + "Right", node.LocalStyle.Padding.Right),
                            new XElement(nsX + "Bottom", node.LocalStyle.Padding.Bottom)));
                }

                if (styleElement.HasElements)
                {
                    element.Add(styleElement);
                }
            }

            if (node.Attributes.Count > 0)
            {
                element.Add(
                    new XElement(
                        nsX + "Attributes",
                        node.Attributes.Select(attribute =>
                            new XElement(
                                nsX + "Attribute",
                                new XAttribute("Key", attribute.Key ?? string.Empty),
                                new XAttribute("Value", attribute.Value ?? string.Empty)))));
            }

            if (node.Children.Count > 0)
            {
                element.Add(
                    new XElement(
                        nsX + "Children",
                        node.Children.Select(WriteNode)));
            }

            return element;
        }

        XElement WriteStyle(StyleDefinitionDto style)
        {
            var element = new XElement(
                nsX + "Style",
                new XAttribute("Id", style.Id ?? string.Empty),
                new XAttribute("Name", style.Name ?? string.Empty));

            if (!string.IsNullOrWhiteSpace(style.ParentStyleId))
            {
                element.Add(
                    new XElement(nsX + "ParentStyleId", style.ParentStyleId));
            }

            if (!string.IsNullOrWhiteSpace(style.AppliesTo))
            {
                element.Add(
                    new XElement(nsX + "AppliesTo", style.AppliesTo));
            }

            if (style.Style is not null)
            {
                var styleValueElement = new XElement(nsX + "StyleValue");

                void AddIf<T>(string name, T? value)
                {
                    if (value is not null)
                    {
                        styleValueElement.Add(new XElement(nsX + name, value));
                    }
                }

                AddIf("FontFamily", style.Style.FontFamily);
                AddIf("FontSize", style.Style.FontSize);
                AddIf("ForegroundColor", style.Style.ForegroundColor);
                AddIf("BackgroundColor", style.Style.BackgroundColor);
                AddIf(
                    "Bold",
                    style.Style.Bold.HasValue
                        ? style.Style.Bold.Value ? "1" : "0"
                        : null);
                AddIf(
                    "Italic",
                    style.Style.Italic.HasValue
                        ? style.Style.Italic.Value ? "1" : "0"
                        : null);
                AddIf("LineHeight", style.Style.LineHeight);
                AddIf("TextAlign", style.Style.TextAlign);
                AddIf("BorderColor", style.Style.BorderColor);
                AddIf("BorderWidth", style.Style.BorderWidth);
                AddIf("CustomCss", style.Style.CustomCss);

                if (style.Style.Margin is not null)
                {
                    styleValueElement.Add(
                        new XElement(
                            nsX + "Margin",
                            new XElement(nsX + "Left", style.Style.Margin.Left),
                            new XElement(nsX + "Top", style.Style.Margin.Top),
                            new XElement(nsX + "Right", style.Style.Margin.Right),
                            new XElement(nsX + "Bottom", style.Style.Margin.Bottom)));
                }

                if (style.Style.Padding is not null)
                {
                    styleValueElement.Add(
                        new XElement(
                            nsX + "Padding",
                            new XElement(nsX + "Left", style.Style.Padding.Left),
                            new XElement(nsX + "Top", style.Style.Padding.Top),
                            new XElement(nsX + "Right", style.Style.Padding.Right),
                            new XElement(nsX + "Bottom", style.Style.Padding.Bottom)));
                }

                if (styleValueElement.HasElements)
                {
                    element.Add(styleValueElement);
                }
            }

            return element;
        }

        var documentElement = new XElement(
            nsX + "Document",
            new XElement(nsX + "Id", dto.Document.Id ?? string.Empty),
            new XElement(nsX + "FileName", dto.Document.FileName ?? string.Empty),
            new XElement(
                nsX + "Source",
                new XElement(
                    nsX + "Path",
                    dto.Document.Source.Path ?? string.Empty),
                new XElement(
                    nsX + "Hash",
                    dto.Document.Source.Hash ?? string.Empty),
                new XElement(
                    nsX + "Encoding",
                    dto.Document.Source.Encoding ?? "utf-8")),
            new XElement(
                nsX + "Styles",
                dto.Document.Styles.Select(WriteStyle)));

        if (dto.Document.Root is not null)
        {
            documentElement.Add(
                new XElement(
                    nsX + "Root",
                    WriteNode(dto.Document.Root)));
        }

        if (dto.Document.EditorState is not null)
        {
            documentElement.Add(
                new XElement(
                    nsX + "EditorState",
                    new XElement(
                        nsX + "SelectedNodeId",
                        dto.Document.EditorState.SelectedNodeId ?? string.Empty),
                    new XElement(
                        nsX + "CaretOffset",
                        dto.Document.EditorState.CaretOffset),
                    new XElement(
                        nsX + "SelectionLength",
                        dto.Document.EditorState.SelectionLength),
                    new XElement(
                        nsX + "ExpandedNodeIds",
                        dto.Document.EditorState.ExpandedNodeIds.Select(
                            id => new XElement(nsX + "Id", id)))));
        }

        if (dto.Document.Metadata is not null)
        {
            documentElement.Add(
                new XElement(
                    nsX + "Metadata",
                    new XElement(
                        nsX + "Title",
                        dto.Document.Metadata.Title ?? string.Empty),
                    new XElement(
                        nsX + "Author",
                        dto.Document.Metadata.Author ?? string.Empty),
                    new XElement(
                        nsX + "Language",
                        dto.Document.Metadata.Language ?? "en"),
                    new XElement(
                        nsX + "Encoding",
                        dto.Document.Metadata.Encoding ?? "utf-8"),
                    new XElement(
                        nsX + "CreatedUtc",
                        dto.Document.Metadata.CreatedUtc.ToString("o")),
                    new XElement(
                        nsX + "ModifiedUtc",
                        dto.Document.Metadata.ModifiedUtc.ToString("o")),
                    new XElement(
                        nsX + "Properties",
                        dto.Document.Metadata.Properties.Select(property =>
                            new XElement(
                                nsX + "Property",
                                new XAttribute(
                                    "Key",
                                    property.Key ?? string.Empty),
                                new XAttribute(
                                    "Value",
                                    property.Value ?? string.Empty))))));
        }

        var packageElement = new XElement(
            nsX + "DocumentPackage",
            new XElement(
                nsX + "SchemaVersion",
                dto.SchemaVersion ?? SchemaMigrator.CurrentSchemaVersion),
            new XElement(
                nsX + "EditorVersion",
                dto.EditorVersion ?? SchemaMigrator.CurrentEditorVersion),
            documentElement);

        return new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            packageElement);
    }
}