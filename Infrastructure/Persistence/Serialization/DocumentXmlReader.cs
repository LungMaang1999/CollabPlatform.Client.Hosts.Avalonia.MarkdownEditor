using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Schema;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

internal static class DocumentXmlReader
{
    // 读取标量值：优先属性，回退到同名子元素（兼容旧版 XML）。
    private static string? AttrOrElement(XElement el, XNamespace ns, string name) =>
        el.Attribute(name)?.Value ?? el.Element(ns + name)?.Value;

    // 解析 "l,t,r,b" 简写；旧版用 Left/Top/Right/Bottom 四个子元素，两端兼容。
    private static ThicknessValueDto? ReadThickness(XElement parent, XNamespace ns, string name)
    {
        var shorthand = parent.Attribute(name)?.Value;
        if (!string.IsNullOrEmpty(shorthand))
        {
            var parts = shorthand.Split(',');
            if (parts.Length == 4)
            {
                return new ThicknessValueDto
                {
                    Left = TryParseDouble(parts[0]) ?? 0,
                    Top = TryParseDouble(parts[1]) ?? 0,
                    Right = TryParseDouble(parts[2]) ?? 0,
                    Bottom = TryParseDouble(parts[3]) ?? 0
                };
            }
        }

        var container = parent.Element(ns + name);
        if (container is null) return null;

        return new ThicknessValueDto
        {
            Left = TryParseDouble(container.Element(ns + "Left")?.Value) ?? 0,
            Top = TryParseDouble(container.Element(ns + "Top")?.Value) ?? 0,
            Right = TryParseDouble(container.Element(ns + "Right")?.Value) ?? 0,
            Bottom = TryParseDouble(container.Element(ns + "Bottom")?.Value) ?? 0
        };
    }

    // 从元素读取 NodeStyleDto：字段全部优先走属性，回退到子元素。
    private static NodeStyleDto ReadStyleDto(XElement el, XNamespace ns)
    {
        var dto = new NodeStyleDto
        {
            FontFamily = AttrOrElement(el, ns, "FontFamily"),
            FontSize = TryParseDouble(AttrOrElement(el, ns, "FontSize")),
            ForegroundColor = AttrOrElement(el, ns, "ForegroundColor"),
            BackgroundColor = AttrOrElement(el, ns, "BackgroundColor"),
            Bold = TryParseBool(AttrOrElement(el, ns, "Bold")),
            Italic = TryParseBool(AttrOrElement(el, ns, "Italic")),
            LineHeight = TryParseDouble(AttrOrElement(el, ns, "LineHeight")),
            TextAlign = AttrOrElement(el, ns, "TextAlign"),
            VerticalAlign = AttrOrElement(el, ns, "VerticalAlign"),
            BorderColor = AttrOrElement(el, ns, "BorderColor"),
            BorderWidth = TryParseDouble(AttrOrElement(el, ns, "BorderWidth")),
            BorderRadius = TryParseDouble(AttrOrElement(el, ns, "BorderRadius")),
            Width = TryParseDouble(AttrOrElement(el, ns, "Width")),
            Gap = TryParseDouble(AttrOrElement(el, ns, "Gap")),
            CustomCss = AttrOrElement(el, ns, "CustomCss"),
            Margin = ReadThickness(el, ns, "Margin"),
            Padding = ReadThickness(el, ns, "Padding")
        };
        return dto;
    }

    public static DocumentPackageDto Read(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var ns = SchemaMigrator.NamespaceName;
        var nsX = XNamespace.Get(ns);
        var root = document.Root ?? throw new InvalidDataException("XML has no root.");

        var dto = new DocumentPackageDto
        {
            SchemaVersion = AttrOrElement(root, nsX, "SchemaVersion") ?? SchemaMigrator.CurrentSchemaVersion,
            EditorVersion = AttrOrElement(root, nsX, "EditorVersion") ?? SchemaMigrator.CurrentEditorVersion
        };

        var docEl = root.Element(nsX + "Document");
        if (docEl is null) return dto;

        var docDto = new DocumentDto
        {
            Id = docEl.Attribute("Id")?.Value ?? docEl.Element(nsX + "Id")?.Value ?? string.Empty,
            FileName = docEl.Attribute("FileName")?.Value ?? docEl.Element(nsX + "FileName")?.Value ?? string.Empty
        };

        // Source：新版用 SourcePath/SourceHash/SourceEncoding 属性，旧版用 <Source> 子元素。
        docDto.Source.Path = docEl.Attribute("SourcePath")?.Value
            ?? docEl.Element(nsX + "Source")?.Element(nsX + "Path")?.Value
            ?? string.Empty;
        docDto.Source.Hash = docEl.Attribute("SourceHash")?.Value
            ?? docEl.Element(nsX + "Source")?.Element(nsX + "Hash")?.Value
            ?? string.Empty;
        docDto.Source.Encoding = docEl.Attribute("SourceEncoding")?.Value
            ?? docEl.Element(nsX + "Source")?.Element(nsX + "Encoding")?.Value
            ?? "utf-8";

        var stylesEl = docEl.Element(nsX + "Styles");
        if (stylesEl is not null)
        {
            foreach (var sEl in stylesEl.Elements(nsX + "Style"))
            {
                var s = new StyleDefinitionDto
                {
                    Id = sEl.Attribute("Id")?.Value ?? string.Empty,
                    Name = sEl.Attribute("Name")?.Value ?? string.Empty,
                    ParentStyleId = AttrOrElement(sEl, nsX, "ParentStyleId"),
                    AppliesTo = AttrOrElement(sEl, nsX, "AppliesTo")
                };

                var valueEl = sEl.Element(nsX + "StyleValue");
                if (valueEl is not null)
                {
                    s.Style = ReadStyleDto(valueEl, nsX);
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
                SelectedNodeId = AttrOrElement(esEl, nsX, "SelectedNodeId"),
                CaretOffset = TryParseInt(AttrOrElement(esEl, nsX, "CaretOffset")) ?? 0,
                SelectionLength = TryParseInt(AttrOrElement(esEl, nsX, "SelectionLength")) ?? 0
            };

            // 展开节点：新版空格分隔属性，旧版 <ExpandedNodeIds><Id>...</Id></ExpandedNodeIds>
            var expandedAttr = esEl.Attribute("ExpandedNodeIds")?.Value;
            if (!string.IsNullOrWhiteSpace(expandedAttr))
            {
                foreach (var id in expandedAttr.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    es.ExpandedNodeIds.Add(id);
            }
            else
            {
                var expanded = esEl.Element(nsX + "ExpandedNodeIds");
                if (expanded is not null)
                {
                    foreach (var id in expanded.Elements(nsX + "Id").Select(x => x.Value))
                        es.ExpandedNodeIds.Add(id);
                }
            }

            docDto.EditorState = es;
        }

        var mdEl = docEl.Element(nsX + "Metadata");
        if (mdEl is not null)
        {
            var md = new MetadataDto
            {
                Title = AttrOrElement(mdEl, nsX, "Title") ?? string.Empty,
                Author = AttrOrElement(mdEl, nsX, "Author") ?? string.Empty,
                Language = AttrOrElement(mdEl, nsX, "Language") ?? "en",
                Encoding = AttrOrElement(mdEl, nsX, "Encoding") ?? "utf-8",
                CreatedUtc = TryParseDateTime(AttrOrElement(mdEl, nsX, "CreatedUtc")) ?? DateTime.UtcNow,
                ModifiedUtc = TryParseDateTime(AttrOrElement(mdEl, nsX, "ModifiedUtc")) ?? DateTime.UtcNow
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

            // 标量字段：新版写在属性上，旧版写在子元素里，两端兼容。
            node.Level = TryParseInt(el.Attribute("Level")?.Value ?? el.Element(nsLocal + "Level")?.Value);
            node.StyleId = el.Attribute("StyleId")?.Value ?? el.Element(nsLocal + "StyleId")?.Value;
            node.IsTableHeader = (el.Attribute("IsTableHeader")?.Value ?? el.Element(nsLocal + "IsTableHeader")?.Value) == "1";

            var localStyleEl = el.Element(nsLocal + "LocalStyle");
            if (localStyleEl is not null)
            {
                node.LocalStyle = ReadStyleDto(localStyleEl, nsLocal);
            }

            node.Attributes.Clear();
            var attrs = el.Element(nsLocal + "Attributes");
            if (attrs is not null)
            {
                foreach (var a in attrs.Elements(nsLocal + "Attribute"))
                {
                    var key = a.Attribute("Key")?.Value ?? string.Empty;
                    var val = DocumentXmlWriter.DecodeValue(a.Attribute("Value")?.Value);
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
    }

    private static double? TryParseDouble(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static int? TryParseInt(string? s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static bool? TryParseBool(string? s) => s switch
    {
        "1" => true,
        "0" => false,
        "true" or "True" => true,
        "false" or "False" => false,
        _ => null
    };

    private static DateTime? TryParseDateTime(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt : null;
}
