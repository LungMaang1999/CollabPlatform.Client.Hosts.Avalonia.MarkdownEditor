using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Schema;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

/// <summary>伴随文档的输入来源形态；只影响迁移与诊断，不影响读写语义。</summary>
public enum CompanionDocumentFormat
{
    /// <summary>没有任何伴随文件。</summary>
    None,

    /// <summary>合并后的伴随文档（根 <c>companion</c>，version=2）。</summary>
    CompanionV2,

    /// <summary>早期合并格式（根 <c>companion</c>，version=1，仅样式段）。</summary>
    CompanionV1,

    /// <summary>旧的纯样式文件（根 <c>DocumentPackage</c> 或 <c>Document</c>）。</summary>
    LegacyStyles,

    /// <summary>旧的定义文件（根 <c>elements</c>）。</summary>
    LegacyElements,

    /// <summary>无法识别的 XML；按空文档继续，绝不覆盖原文件。</summary>
    Unknown
}

/// <summary>未知节点所在的段；保存时按原位置写回，避免“升级一次就丢节点”。</summary>
public enum CompanionSection
{
    Root,
    Elements
}

/// <summary>伴随文档里的一个未知节点：记录它原来的位置，写回时插回原处。</summary>
public sealed record CompanionUnknownNode(CompanionSection Section, int Position, XElement Element);

/// <summary>
/// 合并后的伴随文档（<c>&lt;doc&gt;.md.companion.xml</c>）：样式段 + 元素定义段。
/// 两段内部沿用现有序列化器的字段与命名，因此旧文件能逐字段无损迁入。
///
/// 段缺失 = 空段，不报错：只有样式或只有定义都是合法文档。
/// </summary>
public sealed class CompanionDocument
{
    /// <summary>来源形态；<see cref="CompanionDocumentFormat.None"/> 表示内存中新建（首次保存时落盘）。</summary>
    public CompanionDocumentFormat Format { get; init; } = CompanionDocumentFormat.None;

    /// <summary>Markdown 文档 Id；为空时保存不写 document-id 属性。</summary>
    public string? DocumentId { get; init; }

    /// <summary>
    /// 样式段：<c>&lt;styles schema="1"&gt;</c>，其属性即旧样式文件 <c>&lt;Document&gt;</c>
    /// 的属性（Id / FileName / SourcePath / SourceHash / SourceEncoding），子节点保持原命名空间。
    /// 为空表示这段不存在。
    /// </summary>
    public XElement? Styles { get; init; }

    /// <summary>定义段：用户块定义集合；空集合表示这段不存在。</summary>
    public IReadOnlyList<UserBlockDefinition> Definitions { get; init; } = [];

    /// <summary>
    /// 定义段里的组合单元（属性组 / 结构片段）。与 <see cref="Definitions"/> 同存一段：
    /// 元素的 <c>&lt;uses&gt;</c>/<c>&lt;include&gt;</c> 指向这里的 Id。
    /// </summary>
    public UserBlockComposition Composition { get; init; } = UserBlockComposition.Empty;

    /// <summary>未知节点（顶层与定义段内），保存时按原位置写回。</summary>
    public IReadOnlyList<CompanionUnknownNode> UnknownNodes { get; init; } = [];

    /// <summary>读取期诊断；写入不会产生诊断。</summary>
    public IReadOnlyList<DiagnosticMessage> Diagnostics { get; init; } = [];

    /// <summary>文件由更高版本写入：只读打开，禁止覆盖。</summary>
    public bool IsReadOnly { get; init; }

    public string? ReadOnlyReason { get; init; }
}

/// <summary>
/// <c>.md.companion.xml</c> 的读写实现。
///
/// 写入确定性：定义按 Id、属性按 Key 排序（复用 <see cref="UserBlockElementsSerializer"/>），
/// 样式段原样克隆旧包节点，同一个内存状态永远产出同一串字节（<c>meta</c> 除外）。
///
/// 读取容错：任何损坏都转成诊断并按可用内容打开，绝不抛异常；
/// 旧格式（纯样式 / 纯定义 / version=1）都能识别并归一化成同一内存模型，
/// 使首次保存可以一次性把两段合并落盘。
/// </summary>
public static class CompanionDocumentSerializer
{
    public const string FileSuffix = ".md.companion.xml";
    public const string NamespaceName = "urn:collabplatform:markdown-companion";
    public const string WriterName = "desktop";

    /// <summary>当前伴随文档格式版本，写在根元素的 version 属性上。</summary>
    public const int CurrentVersion = 2;

    private const string StylesElementName = "styles";
    private const string ElementsElementName = "elements";
    private const string MetaElementName = "meta";
    private const string SchemaAttributeName = "schema";
    private const string StylesSchema = "1";
    private const string ElementsSchema = "1";

    private static readonly XNamespace Ns = NamespaceName;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 序列化整份伴随文档。<paramref name="includeMeta"/> 为 false 时省略 <c>meta</c> 段，
    /// 供“内容是否变化”的逐字节比较使用（时间戳不参与比较）。
    /// </summary>
    public static string Serialize(CompanionDocument document, bool includeMeta = true)
    {
        ArgumentNullException.ThrowIfNull(document);

        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
            Encoding = Utf8NoBom,
            OmitXmlDeclaration = false
        };

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("companion", NamespaceName);
            writer.WriteAttributeString("version", CurrentVersion.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(document.DocumentId))
            {
                writer.WriteAttributeString("document-id", document.DocumentId);
            }

            if (includeMeta)
            {
                writer.WriteStartElement(MetaElementName, NamespaceName);
                writer.WriteAttributeString(
                    "modified-utc",
                    DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                writer.WriteAttributeString("writer", WriterName);
                writer.WriteEndElement();
            }

            WriteStyles(writer, document.Styles);
            WriteElements(writer, document);

            foreach (var unknown in document.UnknownNodes
                         .Where(node => node.Section == CompanionSection.Root)
                         .OrderBy(node => node.Position))
            {
                unknown.Element.WriteTo(writer);
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return Utf8NoBom.GetString(stream.ToArray());
    }

    /// <summary>解析任意来源的伴随内容；任何损坏都转成诊断，不抛异常。</summary>
    public static CompanionDocument Deserialize(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return new CompanionDocument { Format = CompanionDocumentFormat.None };
        }

        XDocument parsed;
        try
        {
            parsed = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            return Corrupt(
                CompanionDocumentFormat.Unknown,
                "COMPANION_INVALID_XML",
                $"伴随文档不是合法 XML（第 {ex.LineNumber} 行第 {ex.LinePosition} 列）：{ex.Message}");
        }

        var root = parsed.Root;
        if (root is null)
        {
            return Corrupt(
                CompanionDocumentFormat.Unknown,
                "COMPANION_ROOT_MISSING",
                "伴随文档没有根元素");
        }

        return string.Equals(root.Name.LocalName, "companion", StringComparison.Ordinal)
            ? ReadCompanion(root)
            : ReadLegacy(root);
    }

    /// <summary>把旧纯样式文件（<c>DocumentPackage</c>/<c>Document</c>）归一化成“只有样式段”的伴随文档。</summary>
    public static CompanionDocument FromLegacyStyles(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return new CompanionDocument { Format = CompanionDocumentFormat.LegacyStyles };
        }

        try
        {
            var parsed = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            var root = parsed.Root
                       ?? throw new InvalidDataException("样式文件没有根元素。");
            return FromLegacyStyles(root);
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException)
        {
            return Corrupt(
                CompanionDocumentFormat.LegacyStyles,
                "COMPANION_LEGACY_STYLES_INVALID",
                $"旧样式文件无法解析：{ex.Message}");
        }
    }

    /// <summary>把旧定义文件（<c>&lt;elements&gt;</c>）归一化成“只有定义段”的伴随文档。</summary>
    public static CompanionDocument FromLegacyElements(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return new CompanionDocument { Format = CompanionDocumentFormat.LegacyElements };
        }

        var file = UserBlockElementsSerializer.Deserialize(xml);
        var firstError = file.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return new CompanionDocument
        {
            Format = CompanionDocumentFormat.LegacyElements,
            Definitions = file.Definitions,
            Composition = file.EffectiveComposition,
            Diagnostics = file.Diagnostics,
            IsReadOnly = firstError is not null,
            ReadOnlyReason = firstError is null
                ? null
                : $"{firstError.Message} 为避免用空定义覆盖原文件，已进入只读：请先备份或修复该文件，或显式另存为新文件。"
        };
    }
    /// <summary>
    /// 把样式段还原成旧样式包 <c>&lt;DocumentPackage&gt;</c>，供样式层（<c>DocumentSerializer</c>）
    /// 复用同一套读取代码。没有样式段返回 null。
    /// </summary>
    public static XDocument? BuildLegacyStylePackage(CompanionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Styles is null)
        {
            return null;
        }

        var ns = XNamespace.Get(SchemaMigrator.NamespaceName);
        var documentElement = new XElement(ns + "Document");

        // <styles> 上的属性就是旧 <Document> 的属性；schema 是我们自己的段标记，不写回。
        foreach (var attribute in document.Styles.Attributes())
        {
            if (!string.Equals(attribute.Name.LocalName, SchemaAttributeName, StringComparison.Ordinal)
                && attribute.Name.Namespace == XNamespace.None)
            {
                documentElement.Add(new XAttribute(attribute.Name.LocalName, attribute.Value));
            }
        }

        foreach (var child in document.Styles.Elements())
        {
            documentElement.Add(new XElement(child));
        }

        return new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement(
                ns + "DocumentPackage",
                new XAttribute("SchemaVersion", SchemaMigrator.CurrentSchemaVersion),
                new XAttribute("EditorVersion", SchemaMigrator.CurrentEditorVersion),
                documentElement));
    }

    private static CompanionDocument ReadCompanion(XElement root)
    {
        var diagnostics = new List<DiagnosticMessage>();
        var versionText = root.Attribute("version")?.Value;
        var version = int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedVersion)
            ? parsedVersion
            : CurrentVersion;

        if (version > CurrentVersion)
        {
            // 更高版本：只读打开，禁止用旧语义覆盖新内容。
            return new CompanionDocument
            {
                Format = CompanionDocumentFormat.CompanionV2,
                DocumentId = root.Attribute("document-id")?.Value,
                IsReadOnly = true,
                ReadOnlyReason = $"伴随文档由更新版本写入（version={versionText}），已进入只读。",
                Diagnostics = [Diagnostic(
                    DiagnosticSeverity.Warning,
                    "COMPANION_VERSION_NEWER",
                    $"伴随文档版本 '{versionText}' 高于当前支持的 {CurrentVersion}，已只读打开。")]
            };
        }

        var format = version >= CurrentVersion
            ? CompanionDocumentFormat.CompanionV2
            : CompanionDocumentFormat.CompanionV1;

        if (format == CompanionDocumentFormat.CompanionV1)
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Info,
                "COMPANION_VERSION_UPGRADED",
                $"伴随文档版本 '{versionText}' 将在下次保存时升级为 {CurrentVersion}。"));
        }

        XElement? styles = null;
        IReadOnlyList<UserBlockDefinition> definitions = [];
        var composition = UserBlockComposition.Empty;
        var unknowns = new List<CompanionUnknownNode>();
        var rootPosition = 0;
        var elementsPosition = 0;
        var stylesSeen = false;
        var elementsSeen = false;

        foreach (var child in root.Elements())
        {
            switch (child.Name.LocalName)
            {
                case MetaElementName:
                    // meta 不参与任何指纹，读入即丢弃。
                    continue;

                case StylesElementName when !stylesSeen:
                    stylesSeen = true;
                    styles = new XElement(
                        Ns + StylesElementName,
                        child.Attributes(),
                        child.Elements().Select(element => new XElement(element)));
                    continue;
                case ElementsElementName when !elementsSeen:
                    elementsSeen = true;
                    foreach (var item in child.Elements())
                    {
                        if (IsElementSectionChild(item))
                        {
                            continue;
                        }

                        unknowns.Add(new CompanionUnknownNode(
                            CompanionSection.Elements,
                            elementsPosition,
                            new XElement(item)));
                        elementsPosition++;
                    }

                    var normalized = new XElement(
                        "elements",
                        new XAttribute("version", child.Attribute(SchemaAttributeName)?.Value ?? ElementsSchema),
                        child.Elements()
                            .Where(IsElementSectionChild)
                            .Select(item => new XElement(item)));

                    var file = UserBlockElementsSerializer.Deserialize(normalized.ToString(SaveOptions.DisableFormatting));
                    definitions = file.Definitions;
                    composition = file.EffectiveComposition;
                    diagnostics.AddRange(file.Diagnostics);
                    continue;
                default:
                    unknowns.Add(new CompanionUnknownNode(
                        CompanionSection.Root,
                        rootPosition,
                        new XElement(child)));
                    rootPosition++;
                    continue;
            }
        }

        var firstError = diagnostics.FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return new CompanionDocument
        {
            Format = format,
            DocumentId = root.Attribute("document-id")?.Value,
            Styles = styles,
            Definitions = definitions,
            Composition = composition,
            UnknownNodes = unknowns,
            Diagnostics = diagnostics,
            IsReadOnly = firstError is not null,
            ReadOnlyReason = firstError is null
                ? null
                : $"{firstError.Message} 为避免用空内容覆盖原文件，已进入只读：请先备份或修复该文件，或显式另存为新文件。"
        };
    }

    private static CompanionDocument ReadLegacy(XElement root)
    {
        if (string.Equals(root.Name.LocalName, "elements", StringComparison.Ordinal))
        {
            var legacy = FromLegacyElements(root.ToString(SaveOptions.DisableFormatting));
            return legacy;
        }

        if (string.Equals(root.Name.LocalName, "DocumentPackage", StringComparison.Ordinal)
            || string.Equals(root.Name.LocalName, "Document", StringComparison.Ordinal))
        {
            return FromLegacyStyles(root);
        }

        return Corrupt(
            CompanionDocumentFormat.Unknown,
            "COMPANION_ROOT_UNKNOWN",
            $"无法识别的伴随文档根元素 <{root.Name.LocalName}>");
    }

    private static CompanionDocument FromLegacyStyles(XElement root)
    {
        var diagnostics = new List<DiagnosticMessage>();
        XElement? documentElement;
        if (string.Equals(root.Name.LocalName, "Document", StringComparison.Ordinal))
        {
            documentElement = root;
        }
        else
        {
            documentElement = root.Elements()
                .FirstOrDefault(child => string.Equals(child.Name.LocalName, "Document", StringComparison.Ordinal));
        }

        if (documentElement is null)
        {
            return Corrupt(
                CompanionDocumentFormat.LegacyStyles,
                "COMPANION_LEGACY_STYLES_ROOT_INVALID",
                "旧样式文件缺少 <Document> 根节点");
        }

        var styles = new XElement(
            Ns + StylesElementName,
            new XAttribute(SchemaAttributeName, StylesSchema),
            documentElement.Attributes().Select(attribute => new XAttribute(attribute.Name.LocalName, attribute.Value)),
            documentElement.Elements().Select(element => new XElement(element)));

        return new CompanionDocument
        {
            Format = CompanionDocumentFormat.LegacyStyles,
            DocumentId = documentElement.Attribute("Id")?.Value,
            Styles = styles,
            Diagnostics = diagnostics
        };
    }

    private static void WriteStyles(XmlWriter writer, XElement? styles)
    {
        if (styles is null)
        {
            return;
        }

        writer.WriteStartElement(StylesElementName, NamespaceName);
        writer.WriteAttributeString(SchemaAttributeName, StylesSchema);

        // 属性顺序固定：先 Id，其余按名称排序，保证同一内存状态产出同一串字节。
        var attributes = styles.Attributes()
            .Where(attribute => !string.Equals(attribute.Name.LocalName, SchemaAttributeName, StringComparison.Ordinal))
            .OrderBy(attribute => string.Equals(attribute.Name.LocalName, "Id", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal);

        foreach (var attribute in attributes)
        {
            writer.WriteAttributeString(attribute.Name.LocalName, attribute.Value);
        }

        foreach (var child in styles.Elements())
        {
            child.WriteTo(writer);
        }

        writer.WriteEndElement();
    }

    private static void WriteElements(XmlWriter writer, CompanionDocument document)
    {
        if (document.Definitions.Count == 0
            && document.Composition.IsEmpty
            && document.UnknownNodes.All(node => node.Section != CompanionSection.Elements))
        {
            return;
        }

        var section = new XElement(Ns + ElementsElementName, new XAttribute(SchemaAttributeName, ElementsSchema));
        if (document.Definitions.Count > 0 || !document.Composition.IsEmpty)
        {
            var xml = UserBlockElementsSerializer.Serialize(document.Definitions, document.Composition);
            var parsed = XDocument.Parse(xml).Root!;
            section.Add(parsed.Elements().Select(element => new XElement(element)));
        }

        foreach (var unknown in document.UnknownNodes
                     .Where(node => node.Section == CompanionSection.Elements)
                     .OrderBy(node => node.Position))
        {
            section.Add(new XElement(unknown.Element));
        }

        section.WriteTo(writer);
    }

    /// <summary>
    /// 定义段里由引擎认领的子节点：元素定义与组合单元。
    /// 其它子节点一律按未知节点保留，写回原位置。
    /// </summary>
    private static bool IsElementSectionChild(XElement element) =>
        string.Equals(element.Name.LocalName, "element", StringComparison.Ordinal)
        || string.Equals(element.Name.LocalName, "attribute-group", StringComparison.Ordinal)
        || string.Equals(element.Name.LocalName, "fragment", StringComparison.Ordinal);

    private static CompanionDocument Corrupt(CompanionDocumentFormat format, string code, string reason) => new()
    {
        Format = format,
        IsReadOnly = true,
        ReadOnlyReason = $"{reason}。为避免用空内容覆盖原文件，已进入只读：请先备份或修复该文件，或显式另存为新文件。",
        Diagnostics =
        [
            Diagnostic(
                DiagnosticSeverity.Error,
                code,
                $"{reason}。已按空内容打开，原文件不会被覆盖；保存会被拒绝，请先备份或修复该文件，或显式另存为新文件。")
        ]
    };

    private static DiagnosticMessage Diagnostic(DiagnosticSeverity severity, string code, string message) => new()
    {
        Severity = severity,
        Code = code,
        Message = message,
        Range = SourceRange.Empty()
    };
}
