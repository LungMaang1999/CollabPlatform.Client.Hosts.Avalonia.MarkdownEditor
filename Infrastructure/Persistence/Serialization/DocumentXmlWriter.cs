using System;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Schema;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

internal static class DocumentXmlWriter
{
    private static string FormatInvariant(double value) => value.ToString("G", CultureInfo.InvariantCulture);

    // 把 Thickness 四值压缩为逗号分隔简写 "l,t,r,b"，省去四个子元素。
    private static string FormatThickness(ThicknessValueDto t) =>
        string.Join(
            ",",
            FormatInvariant(t.Left),
            FormatInvariant(t.Top),
            FormatInvariant(t.Right),
            FormatInvariant(t.Bottom));

    // 将 NodeStyleDto 的全部字段以属性形式写入目标元素，避免 15+ 个子元素开销。
    private static void ApplyStyleAttributes(XElement target, NodeStyleDto? style, XNamespace nsX)
    {
        if (style is null) return;

        void AddAttr(string name, string? value)
        {
            if (value is not null) target.Add(new XAttribute(name, value));
        }

        void AddAttrDouble(string name, double? value)
        {
            if (value.HasValue) target.Add(new XAttribute(name, FormatInvariant(value.Value)));
        }

        AddAttr("FontFamily", style.FontFamily);
        AddAttrDouble("FontSize", style.FontSize);
        AddAttr("ForegroundColor", style.ForegroundColor);
        AddAttr("BackgroundColor", style.BackgroundColor);
        AddAttr("Bold", style.Bold.HasValue ? style.Bold.Value ? "1" : "0" : null);
        AddAttr("Italic", style.Italic.HasValue ? style.Italic.Value ? "1" : "0" : null);
        AddAttrDouble("LineHeight", style.LineHeight);
        AddAttr("TextAlign", style.TextAlign);
        AddAttr("VerticalAlign", style.VerticalAlign);
        AddAttr("BorderColor", style.BorderColor);
        AddAttrDouble("BorderWidth", style.BorderWidth);
        AddAttrDouble("BorderRadius", style.BorderRadius);
        AddAttrDouble("Width", style.Width);
        AddAttrDouble("Gap", style.Gap);
        AddAttr("CustomCss", style.CustomCss);

        if (style.Margin is not null)
            target.Add(new XAttribute("Margin", FormatThickness(style.Margin)));

        if (style.Padding is not null)
            target.Add(new XAttribute("Padding", FormatThickness(style.Padding)));
    }

    /// <summary>
    /// 把 XML 1.0 不允许出现在属性值中的控制字符（如时间线标签分隔符 U+001F）
    /// 可逆地编码为 \uXXXX，反斜杠本身转义为 \\。
    /// </summary>
    public static string EncodeValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c == '\\')
            {
                builder.Append("\\\\");
            }
            else if (c < 0x20 && c is not ('\t' or '\n' or '\r'))
            {
                builder.Append("\\u");
                builder.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary><see cref="EncodeValue"/> 的逆运算；旧文件中不存在的反斜杠原样保留。</summary>
    public static string DecodeValue(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.IndexOf('\\') < 0)
        {
            return value ?? string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                var next = value[i + 1];
                if (next == '\\')
                {
                    builder.Append('\\');
                    i++;
                    continue;
                }

                if (next == 'u'
                    && i + 5 < value.Length
                    && int.TryParse(
                        value.AsSpan(i + 2, 4),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out var code))
                {
                    builder.Append((char)code);
                    i += 5;
                    continue;
                }
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

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

            // 标量字段全部用属性输出，省去开闭标签开销；读取端同时兼容旧版子元素。
            if (node.Level.HasValue)
            {
                element.Add(new XAttribute("Level", node.Level.Value.ToString(CultureInfo.InvariantCulture)));
            }

            if (!string.IsNullOrWhiteSpace(node.StyleId))
            {
                element.Add(new XAttribute("StyleId", node.StyleId));
            }

            if (node.IsTableHeader)
            {
                element.Add(new XAttribute("IsTableHeader", "1"));
            }

            if (!string.IsNullOrEmpty(node.Text))
            {
                element.Add(new XElement(nsX + "Text", node.Text));
            }

            if (!string.IsNullOrEmpty(node.RawMarkdown))
            {
                element.Add(new XElement(nsX + "RawMarkdown", node.RawMarkdown));
            }

            if (node.LocalStyle is not null)
            {
                var styleElement = new XElement(nsX + "LocalStyle");
                ApplyStyleAttributes(styleElement, node.LocalStyle, nsX);
                if (styleElement.Attributes().Any())
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
                        {
                            var attributeElement = new XElement(
                                nsX + "Attribute",
                                new XAttribute("Key", attribute.Key ?? string.Empty),
                                new XAttribute("Value", EncodeValue(attribute.Value)));

                            // 缺省来源即 Instance（兼容旧包）；Mapper 只把非 Instance 的来源放进字典。
                            if (node.AttributeSources.TryGetValue(attribute.Key ?? string.Empty, out var source))
                            {
                                attributeElement.Add(new XAttribute("Source", source));
                            }

                            return attributeElement;
                        })));
            }

            if (node.InstanceAttributes.Count > 0)
            {
                element.Add(
                    new XElement(
                        nsX + "InstanceAttributes",
                        node.InstanceAttributes.Select(attribute =>
                            new XElement(
                                nsX + "Attribute",
                                new XAttribute("Key", attribute.Key ?? string.Empty),
                                new XAttribute("Value", EncodeValue(attribute.Value))))));
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
                element.Add(new XAttribute("ParentStyleId", style.ParentStyleId));
            }

            if (!string.IsNullOrWhiteSpace(style.AppliesTo))
            {
                element.Add(new XAttribute("AppliesTo", style.AppliesTo));
            }

            if (!string.IsNullOrWhiteSpace(style.AppliesToKind))
            {
                element.Add(new XAttribute("AppliesToKind", style.AppliesToKind));
            }

            if (style.Style is not null)
            {
                var styleValueElement = new XElement(nsX + "StyleValue");
                ApplyStyleAttributes(styleValueElement, style.Style, nsX);
                if (styleValueElement.Attributes().Any())
                {
                    element.Add(styleValueElement);
                }
            }

            // 类型定义才写声明与行为；命名样式保持旧格式，避免无关文件膨胀。
            if (!string.IsNullOrWhiteSpace(style.AppliesTo) && style.Attributes.Count > 0)
            {
                element.Add(
                    new XElement(
                        nsX + "ElementAttributes",
                        style.Attributes.Select(WriteElementAttribute)));
            }

            if (!string.IsNullOrWhiteSpace(style.AppliesTo) &&
                style.Behavior is not null &&
                (!string.Equals(
                     style.Behavior.FrameStrategy,
                     nameof(ElementFrameStrategy.Auto),
                     StringComparison.Ordinal) ||
                 !string.Equals(
                     style.Behavior.TemplateVariant,
                     nameof(ElementTemplateVariant.Default),
                     StringComparison.Ordinal)))
            {
                var behavior = new XElement(
                    nsX + "Behavior",
                    new XAttribute("FrameStrategy", style.Behavior.FrameStrategy));

                if (!string.Equals(
                        style.Behavior.TemplateVariant,
                        nameof(ElementTemplateVariant.Default),
                        StringComparison.Ordinal))
                {
                    behavior.Add(new XAttribute("TemplateVariant", style.Behavior.TemplateVariant));
                }

                element.Add(behavior);
            }

            return element;
        }

        XElement WriteElementAttribute(ElementAttributeDeclarationDto attribute)
        {
            var element = new XElement(
                nsX + "ElementAttribute",
                new XAttribute("Key", attribute.Key ?? string.Empty),
                new XAttribute("Label", attribute.Label ?? string.Empty),
                new XAttribute("Kind", attribute.ValueKind ?? nameof(ElementAttributeValueKind.Text)));

            if (!string.IsNullOrEmpty(attribute.DefaultValue))
            {
                element.Add(new XAttribute("Default", EncodeValue(attribute.DefaultValue)));
            }

            if (!string.IsNullOrEmpty(attribute.Hint))
            {
                element.Add(new XAttribute("Hint", EncodeValue(attribute.Hint)));
            }

            if (!attribute.IsEnabled)
            {
                element.Add(new XAttribute("Enabled", "0"));
            }

            if (!string.Equals(
                    attribute.Target,
                    nameof(ElementAttributeTarget.DocumentAttribute),
                    StringComparison.Ordinal))
            {
                element.Add(new XAttribute("Target", attribute.Target));
            }

            if (attribute.IsDerived)
            {
                element.Add(new XAttribute("Derived", "1"));
            }

            if (!string.IsNullOrEmpty(attribute.Group))
            {
                element.Add(new XAttribute("Group", EncodeValue(attribute.Group)));
            }

            if (attribute.Options.Count > 0)
            {
                element.Add(
                    new XElement(
                        nsX + "Options",
                        attribute.Options.Select(option =>
                            new XElement(nsX + "Option", new XAttribute("Value", EncodeValue(option))))));
            }

            // 绑定只在配了目标名时写出，避免空绑定污染文件。
            if (!string.IsNullOrWhiteSpace(attribute.BindingName))
            {
                var binding = new XElement(
                    nsX + "Binding",
                    new XAttribute(
                        "Target",
                        attribute.BindingTarget ?? nameof(ElementAttributeBindingTarget.CssVar)),
                    new XAttribute("Name", EncodeValue(attribute.BindingName)));

                if (!string.IsNullOrWhiteSpace(attribute.BindingPart))
                {
                    binding.Add(new XAttribute("Part", EncodeValue(attribute.BindingPart)));
                }

                if (!string.IsNullOrWhiteSpace(attribute.BindingValueMap))
                {
                    binding.Add(new XAttribute("ValueMap", EncodeValue(attribute.BindingValueMap)));
                }

                element.Add(binding);
            }

            // 呈现设置全默认时 Mapper 已置空，这里只为非空（即确有设置）的声明写节点，
            // 保证老文件与“未设置”完全等价。
            if (attribute.Presentation is not null)
            {
                element.Add(WritePresentation(attribute.Presentation));
            }

            return element;
        }

        XElement WritePresentation(ElementAttributePresentationDto presentation)
        {
            var element = new XElement(nsX + "Presentation");

            if (!string.Equals(
                    presentation.Control,
                    nameof(ElementAttributeControlKind.Auto),
                    StringComparison.Ordinal))
            {
                element.Add(new XAttribute("Control", presentation.Control));
            }

            if (!string.Equals(
                    presentation.Width,
                    nameof(ElementAttributeFieldWidth.Auto),
                    StringComparison.Ordinal))
            {
                element.Add(new XAttribute("Width", presentation.Width));
            }

            if (presentation.Order is { } order)
            {
                element.Add(new XAttribute(
                    "Order",
                    order.ToString(CultureInfo.InvariantCulture)));
            }

            if (presentation.IsAdvanced)
            {
                element.Add(new XAttribute("Advanced", "true"));
            }

            if (!string.IsNullOrEmpty(presentation.Placeholder))
            {
                element.Add(new XAttribute("Placeholder", EncodeValue(presentation.Placeholder)));
            }

            if (!string.IsNullOrEmpty(presentation.Unit))
            {
                element.Add(new XAttribute("Unit", EncodeValue(presentation.Unit)));
            }

            if (presentation.Min is { } min)
            {
                element.Add(new XAttribute(
                    "Min",
                    min.ToString(CultureInfo.InvariantCulture)));
            }

            if (presentation.Max is { } max)
            {
                element.Add(new XAttribute(
                    "Max",
                    max.ToString(CultureInfo.InvariantCulture)));
            }

            if (presentation.Step is { } step)
            {
                element.Add(new XAttribute(
                    "Step",
                    step.ToString(CultureInfo.InvariantCulture)));
            }

            if (presentation.Precision is { } precision)
            {
                element.Add(new XAttribute(
                    "Precision",
                    precision.ToString(CultureInfo.InvariantCulture)));
            }

            if (presentation.Rows is { } rows)
            {
                element.Add(new XAttribute(
                    "Rows",
                    rows.ToString(CultureInfo.InvariantCulture)));
            }

            if (presentation.MaxLength is { } maxLength)
            {
                element.Add(new XAttribute(
                    "MaxLength",
                    maxLength.ToString(CultureInfo.InvariantCulture)));
            }

            if (!string.IsNullOrEmpty(presentation.Pattern))
            {
                element.Add(new XAttribute("Pattern", EncodeValue(presentation.Pattern)));
            }

            foreach (var condition in presentation.VisibleWhen)
            {
                element.Add(new XElement(
                    nsX + "VisibleWhen",
                    new XAttribute("Key", EncodeValue(condition.Key)),
                    new XAttribute("Operator", condition.RawOperator ?? condition.Operator),
                    new XAttribute("Value", EncodeValue(condition.Value ?? string.Empty))));
            }

            return element;
        }

        var documentElement = new XElement(
            nsX + "Document",
            new XAttribute("Id", dto.Document.Id ?? string.Empty),
            new XAttribute("FileName", dto.Document.FileName ?? string.Empty),
            new XAttribute("SourcePath", dto.Document.Source.Path ?? string.Empty),
            new XAttribute("SourceHash", dto.Document.Source.Hash ?? string.Empty),
            new XAttribute("SourceEncoding", dto.Document.Source.Encoding ?? "utf-8"),
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
            var es = dto.Document.EditorState;
            var esElement = new XElement(
                nsX + "EditorState",
                new XAttribute("SelectedNodeId", es.SelectedNodeId ?? string.Empty),
                new XAttribute("CaretOffset", es.CaretOffset.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("SelectionLength", es.SelectionLength.ToString(CultureInfo.InvariantCulture)));

            // 展开节点 ID 列表用空格分隔压缩，省去每个 ID 一个子元素。
            if (es.ExpandedNodeIds.Count > 0)
            {
                esElement.Add(new XAttribute("ExpandedNodeIds", string.Join(" ", es.ExpandedNodeIds)));
            }

            documentElement.Add(esElement);
        }

        if (dto.Document.Metadata is not null)
        {
            var md = dto.Document.Metadata;
            var metadataElement = new XElement(
                nsX + "Metadata",
                new XAttribute("Title", md.Title ?? string.Empty),
                new XAttribute("Author", md.Author ?? string.Empty),
                new XAttribute("Language", md.Language ?? "en"),
                new XAttribute("Encoding", md.Encoding ?? "utf-8"),
                new XAttribute("CreatedUtc", md.CreatedUtc.ToString("o", CultureInfo.InvariantCulture)),
                new XAttribute("ModifiedUtc", md.ModifiedUtc.ToString("o", CultureInfo.InvariantCulture)));

            if (md.Properties.Count > 0)
            {
                metadataElement.Add(
                    new XElement(
                        nsX + "Properties",
                        md.Properties.Select(property =>
                            new XElement(
                                nsX + "Property",
                                new XAttribute("Key", property.Key ?? string.Empty),
                                new XAttribute("Value", property.Value ?? string.Empty)))));
            }

            documentElement.Add(metadataElement);
        }

        var packageElement = new XElement(
            nsX + "DocumentPackage",
            new XAttribute("SchemaVersion", dto.SchemaVersion ?? SchemaMigrator.CurrentSchemaVersion),
            new XAttribute("EditorVersion", dto.EditorVersion ?? SchemaMigrator.CurrentEditorVersion),
            documentElement);

        return new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            packageElement);
    }
}
