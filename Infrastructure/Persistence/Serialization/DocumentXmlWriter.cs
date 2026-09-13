using System;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Schema;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

internal static class DocumentXmlWriter
{
    private static string FormatInvariant(double value) => value.ToString("G", CultureInfo.InvariantCulture);

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
                element.Add(new XElement(nsX + "Level", node.Level.Value.ToString(CultureInfo.InvariantCulture)));
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

                void AddIf(string name, string? value)
                {
                    if (value is not null)
                    {
                        styleElement.Add(new XElement(nsX + name, value));
                    }
                }

                void AddIfDouble(string name, double? value)
                {
                    if (value.HasValue)
                    {
                        styleElement.Add(new XElement(nsX + name, FormatInvariant(value.Value)));
                    }
                }

                AddIf("FontFamily", node.LocalStyle.FontFamily);
                AddIfDouble("FontSize", node.LocalStyle.FontSize);
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
                AddIfDouble("LineHeight", node.LocalStyle.LineHeight);
                AddIf("TextAlign", node.LocalStyle.TextAlign);
                AddIf("BorderColor", node.LocalStyle.BorderColor);
                AddIfDouble("BorderWidth", node.LocalStyle.BorderWidth);
                AddIf("CustomCss", node.LocalStyle.CustomCss);

                if (node.LocalStyle.Margin is not null)
                {
                    styleElement.Add(
                        new XElement(
                            nsX + "Margin",
                            new XElement(nsX + "Left", FormatInvariant(node.LocalStyle.Margin.Left)),
                            new XElement(nsX + "Top", FormatInvariant(node.LocalStyle.Margin.Top)),
                            new XElement(nsX + "Right", FormatInvariant(node.LocalStyle.Margin.Right)),
                            new XElement(nsX + "Bottom", FormatInvariant(node.LocalStyle.Margin.Bottom))));
                }

                if (node.LocalStyle.Padding is not null)
                {
                    styleElement.Add(
                        new XElement(
                            nsX + "Padding",
                            new XElement(nsX + "Left", FormatInvariant(node.LocalStyle.Padding.Left)),
                            new XElement(nsX + "Top", FormatInvariant(node.LocalStyle.Padding.Top)),
                            new XElement(nsX + "Right", FormatInvariant(node.LocalStyle.Padding.Right)),
                            new XElement(nsX + "Bottom", FormatInvariant(node.LocalStyle.Padding.Bottom))));
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

                void AddIf(string name, string? value)
                {
                    if (value is not null)
                    {
                        styleValueElement.Add(new XElement(nsX + name, value));
                    }
                }

                void AddIfDouble(string name, double? value)
                {
                    if (value.HasValue)
                    {
                        styleValueElement.Add(new XElement(nsX + name, FormatInvariant(value.Value)));
                    }
                }

                AddIf("FontFamily", style.Style.FontFamily);
                AddIfDouble("FontSize", style.Style.FontSize);
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
                AddIfDouble("LineHeight", style.Style.LineHeight);
                AddIf("TextAlign", style.Style.TextAlign);
                AddIf("BorderColor", style.Style.BorderColor);
                AddIfDouble("BorderWidth", style.Style.BorderWidth);
                AddIf("CustomCss", style.Style.CustomCss);

                if (style.Style.Margin is not null)
                {
                    styleValueElement.Add(
                        new XElement(
                            nsX + "Margin",
                            new XElement(nsX + "Left", FormatInvariant(style.Style.Margin.Left)),
                            new XElement(nsX + "Top", FormatInvariant(style.Style.Margin.Top)),
                            new XElement(nsX + "Right", FormatInvariant(style.Style.Margin.Right)),
                            new XElement(nsX + "Bottom", FormatInvariant(style.Style.Margin.Bottom))));
                }

                if (style.Style.Padding is not null)
                {
                    styleValueElement.Add(
                        new XElement(
                            nsX + "Padding",
                            new XElement(nsX + "Left", FormatInvariant(style.Style.Padding.Left)),
                            new XElement(nsX + "Top", FormatInvariant(style.Style.Padding.Top)),
                            new XElement(nsX + "Right", FormatInvariant(style.Style.Padding.Right)),
                            new XElement(nsX + "Bottom", FormatInvariant(style.Style.Padding.Bottom))));
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
                        dto.Document.EditorState.CaretOffset.ToString(CultureInfo.InvariantCulture)),
                    new XElement(
                        nsX + "SelectionLength",
                        dto.Document.EditorState.SelectionLength.ToString(CultureInfo.InvariantCulture)),
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
                        dto.Document.Metadata.CreatedUtc.ToString("o", CultureInfo.InvariantCulture)),
                    new XElement(
                        nsX + "ModifiedUtc",
                        dto.Document.Metadata.ModifiedUtc.ToString("o", CultureInfo.InvariantCulture)),
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