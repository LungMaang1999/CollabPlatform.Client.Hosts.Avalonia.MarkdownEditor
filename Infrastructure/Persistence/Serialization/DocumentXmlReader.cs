using System;
using System.Linq;
using System.Xml.Linq;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Schema;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

internal static class DocumentXmlReader
{
    public static DocumentPackageDto Read(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var ns = SchemaMigrator.NamespaceName;
        var nsX = XNamespace.Get(ns);
        var root = document.Root ?? throw new InvalidDataException("XML has no root.");

        var dto = new DocumentPackageDto
        {
            SchemaVersion = root.Element(nsX + "SchemaVersion")?.Value ?? SchemaMigrator.CurrentSchemaVersion,
            EditorVersion = root.Element(nsX + "EditorVersion")?.Value ?? SchemaMigrator.CurrentEditorVersion
        };

        var docEl = root.Element(nsX + "Document");
        if (docEl is null) return dto;

        var docDto = new DocumentDto
        {
            Id = docEl.Element(nsX + "Id")?.Value ?? string.Empty,
            FileName = docEl.Element(nsX + "FileName")?.Value ?? string.Empty
        };

        var src = docEl.Element(nsX + "Source");
        if (src is not null)
        {
            docDto.Source.Path = src.Element(nsX + "Path")?.Value ?? string.Empty;
            docDto.Source.Hash = src.Element(nsX + "Hash")?.Value ?? string.Empty;
            docDto.Source.Encoding = src.Element(nsX + "Encoding")?.Value ?? "utf-8";
        }

        var stylesEl = docEl.Element(nsX + "Styles");
        if (stylesEl is not null)
        {
            foreach (var sEl in stylesEl.Elements(nsX + "Style"))
            {
                var s = new StyleDefinitionDto
                {
                    Id = sEl.Attribute("Id")?.Value ?? string.Empty,
                    Name = sEl.Attribute("Name")?.Value ?? string.Empty,
                    ParentStyleId = sEl.Element(nsX + "ParentStyleId")?.Value,
                    AppliesTo = sEl.Element(nsX + "AppliesTo")?.Value
                };

                var valueEl = sEl.Element(nsX + "StyleValue");
                if (valueEl is not null)
                {
                    var nd = new NodeStyleDto
                    {
                        FontFamily = valueEl.Element(nsX + "FontFamily")?.Value,
                        FontSize = TryParseDouble(valueEl.Element(nsX + "FontSize")?.Value),
                        ForegroundColor = valueEl.Element(nsX + "ForegroundColor")?.Value,
                        BackgroundColor = valueEl.Element(nsX + "BackgroundColor")?.Value,
                        Bold = TryParseBool(valueEl.Element(nsX + "Bold")?.Value),
                        Italic = TryParseBool(valueEl.Element(nsX + "Italic")?.Value),
                        LineHeight = TryParseDouble(valueEl.Element(nsX + "LineHeight")?.Value),
                        TextAlign = valueEl.Element(nsX + "TextAlign")?.Value,
                        BorderColor = valueEl.Element(nsX + "BorderColor")?.Value,
                        BorderWidth = TryParseDouble(valueEl.Element(nsX + "BorderWidth")?.Value),
                        CustomCss = valueEl.Element(nsX + "CustomCss")?.Value
                    };

                    var mEl = valueEl.Element(nsX + "Margin");
                    if (mEl is not null)
                    {
                        nd.Margin = new ThicknessValueDto
                        {
                            Left = TryParseDouble(mEl.Element(nsX + "Left")?.Value) ?? 0,
                            Top = TryParseDouble(mEl.Element(nsX + "Top")?.Value) ?? 0,
                            Right = TryParseDouble(mEl.Element(nsX + "Right")?.Value) ?? 0,
                            Bottom = TryParseDouble(mEl.Element(nsX + "Bottom")?.Value) ?? 0
                        };
                    }

                    var pEl = valueEl.Element(nsX + "Padding");
                    if (pEl is not null)
                    {
                        nd.Padding = new ThicknessValueDto
                        {
                            Left = TryParseDouble(pEl.Element(nsX + "Left")?.Value) ?? 0,
                            Top = TryParseDouble(pEl.Element(nsX + "Top")?.Value) ?? 0,
                            Right = TryParseDouble(pEl.Element(nsX + "Right")?.Value) ?? 0,
                            Bottom = TryParseDouble(pEl.Element(nsX + "Bottom")?.Value) ?? 0
                        };
                    }

                    s.Style = nd;
                }

                docDto.Styles.Add(s);
            }
        }

        var rootEl = docEl.Element(nsX + "Root")?.Element(nsX + "Node");
        if (rootEl is not null)
            docDto.Root = ReadNode(rootEl, nsX);

        var esEl = docEl.Element(nsX + "EditorState");
        if (esEl is not null)
        {
            var es = new EditorStateDto
            {
                SelectedNodeId = esEl.Element(nsX + "SelectedNodeId")?.Value,
                CaretOffset = TryParseInt(esEl.Element(nsX + "CaretOffset")?.Value) ?? 0,
                SelectionLength = TryParseInt(esEl.Element(nsX + "SelectionLength")?.Value) ?? 0
            };

            var expanded = esEl.Element(nsX + "ExpandedNodeIds");
            if (expanded is not null)
            {
                foreach (var id in expanded.Elements(nsX + "Id").Select(x => x.Value))
                    es.ExpandedNodeIds.Add(id);
            }

            docDto.EditorState = es;
        }

        var mdEl = docEl.Element(nsX + "Metadata");
        if (mdEl is not null)
        {
            var md = new MetadataDto
            {
                Title = mdEl.Element(nsX + "Title")?.Value ?? string.Empty,
                Author = mdEl.Element(nsX + "Author")?.Value ?? string.Empty,
                Language = mdEl.Element(nsX + "Language")?.Value ?? "en",
                Encoding = mdEl.Element(nsX + "Encoding")?.Value ?? "utf-8",
                CreatedUtc = TryParseDateTime(mdEl.Element(nsX + "CreatedUtc")?.Value) ?? DateTime.UtcNow,
                ModifiedUtc = TryParseDateTime(mdEl.Element(nsX + "ModifiedUtc")?.Value) ?? DateTime.UtcNow
            };

            var propsEl = mdEl.Element(nsX + "Properties");
            if (propsEl is not null)
            {
                foreach (var p in propsEl.Elements(nsX + "Property"))
                {
                    var key = p.Attribute("Key")?.Value ?? string.Empty;
                    var val = p.Attribute("Value")?.Value ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(key)) md.Properties[key] = val;
                }
            }

            docDto.Metadata = md;
        }

        dto.Document = docDto;
        return dto;

        NodeDto ReadNode(XElement el, XNamespace nsLocal)
        {
            var node = new NodeDto
            {
                Id = el.Attribute("Id")?.Value ?? Guid.NewGuid().ToString("N"),
                Type = el.Attribute("Type")?.Value ?? string.Empty,
                Category = el.Attribute("Category")?.Value ?? string.Empty,
                IsSynthetic = el.Attribute("IsSynthetic")?.Value == "1"
            };

            node.Text = el.Element(nsLocal + "Text")?.Value ?? string.Empty;
            node.RawMarkdown = el.Element(nsLocal + "RawMarkdown")?.Value ?? string.Empty;
            node.Level = TryParseInt(el.Element(nsLocal + "Level")?.Value);

            node.StyleId = el.Element(nsLocal + "StyleId")?.Value;
            node.IsTableHeader = el.Element(nsLocal + "IsTableHeader")?.Value == "1";

            var localStyleEl = el.Element(nsLocal + "LocalStyle");
            if (localStyleEl is not null)
            {
                var nsDto = new NodeStyleDto
                {
                    FontFamily = localStyleEl.Element(nsLocal + "FontFamily")?.Value,
                    FontSize = TryParseDouble(localStyleEl.Element(nsLocal + "FontSize")?.Value),
                    ForegroundColor = localStyleEl.Element(nsLocal + "ForegroundColor")?.Value,
                    BackgroundColor = localStyleEl.Element(nsLocal + "BackgroundColor")?.Value,
                    Bold = TryParseBool(localStyleEl.Element(nsLocal + "Bold")?.Value),
                    Italic = TryParseBool(localStyleEl.Element(nsLocal + "Italic")?.Value),
                    LineHeight = TryParseDouble(localStyleEl.Element(nsLocal + "LineHeight")?.Value),
                    TextAlign = localStyleEl.Element(nsLocal + "TextAlign")?.Value,
                    BorderColor = localStyleEl.Element(nsLocal + "BorderColor")?.Value,
                    BorderWidth = TryParseDouble(localStyleEl.Element(nsLocal + "BorderWidth")?.Value),
                    CustomCss = localStyleEl.Element(nsLocal + "CustomCss")?.Value
                };

                var mEl = localStyleEl.Element(nsLocal + "Margin");
                if (mEl is not null)
                {
                    nsDto.Margin = new ThicknessValueDto
                    {
                        Left = TryParseDouble(mEl.Element(nsLocal + "Left")?.Value) ?? 0,
                        Top = TryParseDouble(mEl.Element(nsLocal + "Top")?.Value) ?? 0,
                        Right = TryParseDouble(mEl.Element(nsLocal + "Right")?.Value) ?? 0,
                        Bottom = TryParseDouble(mEl.Element(nsLocal + "Bottom")?.Value) ?? 0
                    };
                }

                var pEl = localStyleEl.Element(nsLocal + "Padding");
                if (pEl is not null)
                {
                    nsDto.Padding = new ThicknessValueDto
                    {
                        Left = TryParseDouble(pEl.Element(nsLocal + "Left")?.Value) ?? 0,
                        Top = TryParseDouble(pEl.Element(nsLocal + "Top")?.Value) ?? 0,
                        Right = TryParseDouble(pEl.Element(nsLocal + "Right")?.Value) ?? 0,
                        Bottom = TryParseDouble(pEl.Element(nsLocal + "Bottom")?.Value) ?? 0
                    };
                }

                node.LocalStyle = nsDto;
            }

            node.Attributes.Clear();
            var attrs = el.Element(nsLocal + "Attributes");
            if (attrs is not null)
            {
                foreach (var a in attrs.Elements(nsLocal + "Attribute"))
                {
                    var key = a.Attribute("Key")?.Value ?? string.Empty;
                    var val = a.Attribute("Value")?.Value ?? string.Empty;
                    if (!string.IsNullOrEmpty(key)) node.Attributes[key] = val;
                }
            }

            var childrenEl = el.Element(nsLocal + "Children");
            if (childrenEl is not null)
            {
                foreach (var c in childrenEl.Elements(nsLocal + "Node"))
                    node.Children.Add(ReadNode(c, nsLocal));
            }

            return node;
        }

        static double? TryParseDouble(string? s) => double.TryParse(s, out var v) ? v : null;
        static int? TryParseInt(string? s) => int.TryParse(s, out var v) ? v : null;
        static bool? TryParseBool(string? s) => s switch { "1" => true, "0" => false, "true" => true, "false" => false, _ => null };
        static DateTime? TryParseDateTime(string? s) => DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : null;
    }
}