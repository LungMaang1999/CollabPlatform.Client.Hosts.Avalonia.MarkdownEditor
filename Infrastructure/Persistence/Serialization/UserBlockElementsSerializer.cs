using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

/// <summary>
/// <c>.md.elements.xml</c>（用户块定义伴随文件）的独立读写实现。
/// 与图序列化器、样式包序列化器互不复用：本文件的唯一职责是表达
/// “允许用户定义哪些围栏块”，格式变化不牵动其它伴随文件。
///
/// 写入确定性：元素按 Id、属性声明按 Key、内容模型按枚举值排序，
/// 属性/子元素的书写顺序固定，同一份定义永远产出同一串字节。
/// 读取容错：缺失文件 = 无定义；XML 损坏 = 一条错误诊断 + 空定义；
/// 单条定义非法 = 跳过该条并保留其余定义，绝不让文档打不开。
/// </summary>
public static class UserBlockElementsSerializer
{
    public const string FileSuffix = ".elements.xml";

    /// <summary>当前定义文件格式版本，写在根元素的 version 属性上。</summary>
    public const int CurrentVersion = 1;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 把定义集合序列化成确定性的 XML 文本。
    /// 组合单元（属性组 / 结构片段）按 Id 排序写在元素之后；没有单元时不写任何节点，
    /// 因此历史定义文件的字节表示保持不变。
    /// </summary>
    public static string Serialize(
        IEnumerable<UserBlockDefinition>? definitions,
        UserBlockComposition? composition = null)
    {
        var ordered = definitions?.ToList() ?? [];
        ordered.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));

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
            writer.WriteStartElement("elements");
            writer.WriteAttributeString("version", CurrentVersion.ToString(CultureInfo.InvariantCulture));

            foreach (var definition in ordered)
            {
                WriteDefinition(writer, definition);
            }

            WriteComposition(writer, composition);

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return Utf8NoBom.GetString(stream.ToArray());
    }

    /// <summary>读取定义文件；文件不存在时返回空集合且不产生诊断。</summary>
    public static UserBlockElementsFile LoadFile(string elementsFilePath)
    {
        if (string.IsNullOrWhiteSpace(elementsFilePath) || !File.Exists(elementsFilePath))
        {
            return UserBlockElementsFile.Empty;
        }

        string xml;
        try
        {
            xml = File.ReadAllText(elementsFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UserBlockElementsFile(
                [],
                [Diagnostic(
                    DiagnosticSeverity.Error,
                    "USER_ELEMENTS_READ_FAILED",
                    $"用户元素定义文件读取失败（{elementsFilePath}）：{ex.Message}。已按无自定义元素打开。")]);
        }

        return Deserialize(xml);
    }

    /// <summary>解析定义 XML 文本；任何损坏都转成诊断，不抛异常。</summary>
    public static UserBlockElementsFile Deserialize(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return UserBlockElementsFile.Empty;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            return new UserBlockElementsFile(
                [],
                [Diagnostic(
                    DiagnosticSeverity.Error,
                    "USER_ELEMENTS_INVALID_XML",
                    $"用户元素定义文件不是合法 XML：{ex.Message}。已按无自定义元素打开。")]);
        }

        var diagnostics = new List<DiagnosticMessage>();
        var root = document.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "elements", StringComparison.Ordinal))
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Error,
                "USER_ELEMENTS_ROOT_INVALID",
                "<elements> 根元素缺失或名称不符。已按无自定义元素打开。"));

            return new UserBlockElementsFile([], diagnostics);
        }

        var versionText = root.Attribute("version")?.Value;
        if (!int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
            || version != CurrentVersion)
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Warning,
                "USER_ELEMENTS_VERSION_UNSUPPORTED",
                $"用户元素定义文件版本 '{versionText}' 不在支持范围（当前 {CurrentVersion}），按当前版本尽力解析。"));
        }

        var definitions = new List<UserBlockDefinition>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keywords = new KeywordOccupancy();

        // 组合单元先读：元素定义展开/校验都要用到它们。单元本身坏掉只丢该单元，不影响元素定义。
        var composition = ReadComposition(root, diagnostics);

        foreach (var element in root.Elements())
        {
            if (!string.Equals(element.Name.LocalName, "element", StringComparison.Ordinal))
            {
                continue;
            }

            var definition = ReadDefinition(element, ids, keywords, diagnostics);
            if (definition is not null)
            {
                definitions.Add(definition);
            }
        }

        // 装载策略：环、缺引用、部件名碰撞、展开后结构非法 → 丢该条定义 + 诊断，其余定义照常装载。
        // 保存策略要走同一个校验函数（UserBlockDefinitionValidator），但那边是拒绝保存。
        var normalized = ApplyCompositionLoadPolicy(definitions, composition, diagnostics);
        return new UserBlockElementsFile(normalized, diagnostics, composition);
    }

    /// <summary>
    /// 装载期的组合/继承策略：逐条校验，通过的保留“来源形态”（ref/extends 原文不动，供 save→load→save），
    /// 不通过的丢弃并给固定诊断码。级联：父定义被丢后，继承它的定义也以 MISSING_REF 丢掉。
    /// </summary>
    private static IReadOnlyList<UserBlockDefinition> ApplyCompositionLoadPolicy(
        IReadOnlyList<UserBlockDefinition> definitions,
        UserBlockComposition composition,
        List<DiagnosticMessage> diagnostics)
    {
        if (composition.IsEmpty && definitions.All(definition =>
                definition.AttributeGroups.Count == 0
                && !UserBlockCompositionRules.UsesInheritance(definition)
                && !UserBlockCompositionRules.ContainsInclude(definition.Structure.Root)))
        {
            // 没有组合/继承的历史文件：不做任何额外校验，读入字节与行为完全不变。
            return definitions;
        }

        var kept = new List<UserBlockDefinition>(definitions.Count);
        foreach (var definition in definitions)
        {
            if (definition.AttributeGroups.Count == 0
                && !UserBlockCompositionRules.UsesInheritance(definition)
                && !UserBlockCompositionRules.ContainsInclude(definition.Structure.Root))
            {
                kept.Add(definition);
                continue;
            }

            // 与保存期是同一个重载：本地定义集合就是父链的解析域。
            var issues = UserBlockCompositionRules.Validate(definition, composition, definitions);
            if (issues.Count == 0)
            {
                issues = ValidateExpandedStructure(definition, composition, definitions);
            }

            if (issues.Count > 0)
            {
                foreach (var issue in issues)
                {
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Error,
                        issue.Code,
                        $"用户元素 '{definition.Id}'：{issue.Message}已跳过该定义。"));
                }

                continue;
            }

            kept.Add(definition with
            {
                CompositionRefs = UserBlockCompositionRules.CollectReferenceIds(
                    definition,
                    composition,
                    definitions)
            });
        }

        return DropOrphanedInheritors(kept, diagnostics);
    }

    /// <summary>
    /// 继承的级联剔除：某条定义因自身问题被丢后，<c>extends</c> 它的定义也以 MISSING_REF 丢掉，
    /// 避免留下指向不存在父定义的“半继承”定义。反复剔除直到保留集合不再变化（不动点）。
    /// </summary>
    private static IReadOnlyList<UserBlockDefinition> DropOrphanedInheritors(
        List<UserBlockDefinition> kept,
        List<DiagnosticMessage> diagnostics)
    {
        if (!kept.Any(UserBlockCompositionRules.UsesInheritance))
        {
            return kept;
        }

        var keptIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in kept)
        {
            keptIds.Add(definition.Id);
        }

        var dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var definition in kept)
            {
                if (dropped.Contains(definition.Id)) continue;
                if (definition.Extends is not { Length: > 0 } parentId) continue;
                if (keptIds.Contains(parentId)) continue;

                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Error,
                    UserBlockCompositionRules.MissingRefCode,
                    $"用户元素 '{definition.Id}'：继承的父定义 '{parentId}' 不存在，无法展开。已跳过该定义。"));
                dropped.Add(definition.Id);
                keptIds.Remove(definition.Id);
                changed = true;
            }
        }

        return dropped.Count == 0
            ? kept
            : kept.Where(definition => !dropped.Contains(definition.Id)).ToList();
    }

    /// <summary>
    /// 展开后才有意义的校验：属性组把属性合并进来，片段把部件拼进来，继承把父定义铺平，
    /// 因此标签插值/条件/重复/生成器/引用/策略只能在展开结果上判。
    /// </summary>
    private static IReadOnlyList<UserBlockStructureIssue> ValidateExpandedStructure(
        UserBlockDefinition definition,
        UserBlockComposition composition,
        IReadOnlyList<UserBlockDefinition>? scope = null)
    {
        var expanded = UserBlockCompositionRules.Expand(definition, composition, scope);
        var attributes = expanded.Attributes;
        var issues = new List<UserBlockStructureIssue>();

        issues.AddRange(UserBlockStructureRules.ValidateWhenSourceFields(expanded.Structure));
        issues.AddRange(UserBlockStructureRules.ValidateTagFields(expanded.Structure, attributes));
        issues.AddRange(UserBlockStructureRules.ValidateWhenFields(expanded.Structure, attributes));
        issues.AddRange(UserBlockStructureRules.ValidateRepeatFields(expanded.Structure, expanded.StructureKey));
        issues.AddRange(UserBlockStructureRules.ValidateGeneratorFields(expanded.Structure));
        issues.AddRange(UserBlockStructureRules.ValidateRefFields(expanded.Structure));
        issues.AddRange(UserBlockStructureRules.ValidatePolicyFields(expanded.Structure));

        // 正文槽位：围栏定义必须能放正文（继承/组合展开后重新判，父部件也可能提供 body）。
        if (expanded.AppliesTo is null && !UserBlockStructureRules.HasContentSlot(expanded.Structure))
        {
            issues.Add(new UserBlockStructureIssue(
                "USER_ELEMENTS_STRUCTURE_NO_CONTENT",
                "结构缺少正文槽位（body/content）。"));
        }

        if (issues.Count > 0)
        {
            return issues;
        }

        // 片段里的自定义槽位必须由引用者在 slots 里登记：展开后按引用者的声明面再净化一次。
        var sanitized = UserBlockStructureRules.Sanitize(expanded.Structure);
        return sanitized.Issues;
    }

    private static void WriteDefinition(XmlWriter writer, UserBlockDefinition definition)
    {
        writer.WriteStartElement("element");
        writer.WriteAttributeString("id", definition.Id);
        // extends 固定在 id 之后、name 之前：位置稳定，save→load→save 不漂移。
        if (definition.Extends is not null)
        {
            writer.WriteAttributeString("extends", definition.Extends);
        }

        writer.WriteAttributeString("name", definition.Name);
        // 别名只在声明时写，历史定义文件的字节表示保持不变。
        if (definition.Aliases.Count > 0)
        {
            writer.WriteAttributeString("keywords", string.Join(",", definition.Aliases));
        }

        // 内置类型重写定义没有围栏形态：写 applies-to 即可，围栏字段一律省略。
        if (definition.AppliesTo is not null)
        {
            writer.WriteAttributeString("applies-to", definition.AppliesTo);

            // 只在显式声明时写：缺省语义（参与渲染、无结构键、无选择器）不落字节。
            if (definition.RenderEnabled is { } renderEnabled)
            {
                writer.WriteAttributeString("render", renderEnabled ? "true" : "false");
            }

            if (definition.StructureKey is not null)
            {
                writer.WriteAttributeString("structure-key", definition.StructureKey);
            }

            if (definition.Selector is not null)
            {
                writer.WriteAttributeString("selector", definition.Selector);
            }

            if (definition.Variant is not null)
            {
                writer.WriteAttributeString("variant", definition.Variant);
            }

            if (definition.ItemMarker is not null)
            {
                writer.WriteAttributeString("item-marker", definition.ItemMarker);
            }

            // 首词分派声明与被打开的块类型成对出现：缺围栏字符或关键字则不成立。
            if (definition.Opens is not null)
            {
                writer.WriteAttributeString("opens", definition.Opens);

                // 继承定义没写围栏字符时不落字节：父定义的围栏字符要在重载后继续生效。
                if (definition.Extends is null
                    || IsDeclared(definition, "fence-char")
                    || IsDeclared(definition, "fence-chars")
                    || definition.FenceChars.Count > 1)
                {
                    WriteFenceCharacters(writer, definition);
                }
            }
        }
        else if (definition.FenceChars.Count > 1
                 || (definition.FenceChars.Count == 1 && IsDeclared(definition, "fence-chars")))
        {
            // 多字符定义写 fence-chars（逗号分隔）；显式声明 fence-chars 时即使单字符也原样写回，
            // 否则重载后 fence-chars="@" 会退化成 fence-char，覆盖被静默改写。
            writer.WriteAttributeString("fence-chars", string.Join(",", definition.FenceChars));
        }
        else if (!IsDeclared(definition, "fence-char") && definition.Extends is not null)
        {
            // 继承定义没写 fence-char：写出来会变成“显式覆盖父值”，破坏 save→load→save 的继承语义。
        }
        else
        {
            writer.WriteAttributeString("fence-char", definition.FenceChar.ToString());
        }

        if (definition.AppliesTo is null)
        {
            // 同上：继承定义没写的围栏长度/优先级不落字节，否则缺省值会在往返后变成显式覆盖。
            if (definition.Extends is null || IsDeclared(definition, "fence-length"))
            {
                writer.WriteAttributeString("fence-length", definition.FenceLength.ToString(CultureInfo.InvariantCulture));
            }

            if (definition.Extends is null || IsDeclared(definition, "priority"))
            {
                writer.WriteAttributeString("priority", definition.Priority.ToString(CultureInfo.InvariantCulture));
            }
        }

        // 属性组引用按声明顺序写在属性声明之前：读回时顺序不变，save→load→save 稳定。
        foreach (var groupId in definition.AttributeGroups)
        {
            writer.WriteStartElement("uses");
            writer.WriteAttributeString("ref", groupId);
            writer.WriteEndElement();
        }

        WriteAttributes(writer, definition.Attributes);

        if (!definition.Content.AllowsAnyBlock || !definition.Content.AllowsAnyInline)
        {
            writer.WriteStartElement("content");
            foreach (var block in definition.Content.AllowedBlocks.OrderBy(value => (int)value))
            {
                writer.WriteStartElement("block");
                writer.WriteAttributeString("type", block.ToString());
                writer.WriteEndElement();
            }

            foreach (var inline in definition.Content.AllowedInlines.OrderBy(value => (int)value))
            {
                writer.WriteStartElement("inline");
                writer.WriteAttributeString("type", inline.ToString());
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        WriteStructure(writer, definition.Structure, allowUndeclaredSlots: definition.Extends is not null);

        WriteHtmlAllowlist(writer, definition.InlineHtml, InlineHtmlSectionName);
        WriteHtmlAllowlist(writer, definition.BlockHtml, BlockHtmlSectionName);

        writer.WriteEndElement();
    }

    /// <summary>围栏字符的规范写法：多字符用 <c>fence-chars</c>，单字符用 <c>fence-char</c>。</summary>
    private static void WriteFenceCharacters(XmlWriter writer, UserBlockDefinition definition)
    {
        if (definition.FenceChars.Count > 1)
        {
            writer.WriteAttributeString("fence-chars", string.Join(",", definition.FenceChars));
        }
        else
        {
            writer.WriteAttributeString("fence-char", definition.FenceChar.ToString());
        }
    }

    /// <summary>
    /// 读组合单元。<c>&lt;attribute-group id="…"&gt;</c> 内是标准 <c>&lt;attributes&gt;</c> 段；
    /// <c>&lt;fragment id="…"&gt;</c> 内是一棵部件树。非法 Id 只丢该单元并给固定码，
    /// 不影响元素定义与其它的单元。
    /// </summary>
    private static UserBlockComposition ReadComposition(XElement root, List<DiagnosticMessage> diagnostics)
    {
        var groups = new List<UserBlockAttributeGroupDefinition>();
        var fragments = new List<UserBlockStructureFragmentDefinition>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var child in root.Elements())
        {
            var localName = child.Name.LocalName;
            if (!string.Equals(localName, "attribute-group", StringComparison.Ordinal)
                && !string.Equals(localName, "fragment", StringComparison.Ordinal))
            {
                continue;
            }

            var id = (child.Attribute("id")?.Value ?? string.Empty).Trim();
            if (!UserBlockRegistry.IsValidIdentifier(id))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Error,
                    "USER_ELEMENTS_ID_INVALID",
                    $"组合单元 <{localName}> 的 Id '{id}' 非法。已跳过该单元。"));
                continue;
            }

            if (!ids.Add(id))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Error,
                    "USER_ELEMENTS_ID_DUPLICATE",
                    $"组合单元 Id '{id}' 重复。已跳过该单元，保留首次出现的声明。"));
                continue;
            }

            if (string.Equals(localName, "attribute-group", StringComparison.Ordinal))
            {
                groups.Add(new UserBlockAttributeGroupDefinition
                {
                    Id = id,
                    Attributes = ReadAttributes(child, id, diagnostics)
                });
                continue;
            }

            var structure = ReadStructureContainer(
                child,
                id,
                [],
                diagnostics,
                requireContentSlot: false,
                structureKey: null,
                deferValidation: true,
                declaredSlotsOverride: InferFragmentCustomSlots(child));

            fragments.Add(new UserBlockStructureFragmentDefinition
            {
                Id = id,
                Root = structure.Root
            });
        }

        return groups.Count == 0 && fragments.Count == 0
            ? UserBlockComposition.Empty
            : new UserBlockComposition { AttributeGroups = groups, Fragments = fragments };
    }

    /// <summary>
    /// 片段的自定义槽位由片段自身声明面推断：片段是封闭子树，别人用到的槽名都在它的部件上。
    /// 推断只为让片段能独立读入；引用者仍必须在自己 <c>slots</c> 里登记，展开后按引用者的声明面复检。
    /// </summary>
    private static IReadOnlyList<string> InferFragmentCustomSlots(XElement fragment) =>
        fragment.Descendants()
            .Where(node => string.Equals(node.Name.LocalName, "part", StringComparison.Ordinal))
            .Select(node => node.Attribute("slot")?.Value?.Trim())
            .Where(slot => !string.IsNullOrEmpty(slot))
            .Select(slot => slot!)
            .Where(slot => !UserBlockStructureRules.TryNormalizePart(slot, Array.Empty<string>(), out _))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>写一组属性声明（元素自有与属性组共用同一段字节口径）。</summary>
    private static void WriteAttributes(XmlWriter writer, IReadOnlyList<UserBlockAttributeDeclaration> attributes)
    {
        if (attributes.Count == 0)
        {
            return;
        }

        writer.WriteStartElement("attributes");
        foreach (var attribute in attributes.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            writer.WriteStartElement("attribute");
            writer.WriteAttributeString("key", attribute.Key);
            writer.WriteAttributeString("kind", attribute.Kind.ToString());
            if (attribute.DefaultValue is not null)
            {
                writer.WriteAttributeString("default", attribute.DefaultValue);
            }

            writer.WriteAttributeString("required", attribute.Required ? "true" : "false");

            // 允许值按声明顺序书写；无允许值时 XmlWriter 仍输出自闭合标签，
            // 既有定义文件的字节表示保持不变。
            foreach (var allowedValue in attribute.AllowedValues)
            {
                writer.WriteStartElement("allowed");
                writer.WriteAttributeString("value", allowedValue);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    /// <summary>写组合单元：属性组在前、结构片段在后，各自按 Id 排序；空组合不写节点。</summary>
    private static void WriteComposition(XmlWriter writer, UserBlockComposition? composition)
    {
        if (composition is null || composition.IsEmpty)
        {
            return;
        }

        foreach (var group in composition.AttributeGroups.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            writer.WriteStartElement("attribute-group");
            writer.WriteAttributeString("id", group.Id);
            WriteAttributes(writer, group.Attributes);
            writer.WriteEndElement();
        }

        foreach (var fragment in composition.Fragments.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            writer.WriteStartElement("fragment");
            writer.WriteAttributeString("id", fragment.Id);
            if (fragment.Root is not null)
            {
                WritePart(writer, fragment.Root);
            }

            writer.WriteEndElement();
        }
    }

    /// <summary>
    /// 写结构声明。未声明结构时不输出任何节点——历史定义文件的字节表示保持不变。
    /// 部件事先净化并按声明顺序输出，保证同一份定义永远产出同一串字节。
    /// </summary>
    private static void WriteStructure(
        XmlWriter writer,
        UserBlockStructure structure,
        bool allowUndeclaredSlots = false)
    {
        // 继承定义的自定义槽位可能只声明在父定义里，净化面不能比读取时更窄，否则部件会被写丢。
        var sanitized = UserBlockStructureRules.Sanitize(structure, allowUndeclaredSlots).Structure;
        if (sanitized.Root is null)
        {
            return;
        }

        writer.WriteStartElement("structure");

        // 自定义槽位只在声明时写，且保持声明顺序；历史定义没有该属性，字节不变。
        if (sanitized.Slots.Count > 0)
        {
            writer.WriteAttributeString("slots", string.Join(',', sanitized.Slots));
        }

        WritePart(writer, sanitized.Root);
        writer.WriteEndElement();
    }

    private static void WritePart(XmlWriter writer, UserBlockPart part)
    {
        // 片段引用按来源形态写回：save→load→save 不会把展开后的部件树写进去。
        if (part.Include is not null)
        {
            writer.WriteStartElement("include");
            writer.WriteAttributeString("ref", part.Include);
            writer.WriteEndElement();
            return;
        }

        // 继承语义是“子没写才沿用父”，所以源 XML 里显式声明过的字段即使等于缺省值也必须写回，
        // 否则重载后会退化成“没写”→ 静默继承父值，显式覆盖丢失。
        // DeclaredFields 为 null（代码内手工构造）时保持最小字节输出，历史行为不变。
        var declared = part.DeclaredFields;
        bool Explicit(string field) => declared is not null && declared.Contains(field);

        writer.WriteStartElement("part");
        writer.WriteAttributeString("slot", part.Part);

        // 透明部件（仅内置重写定义）不写 tag：读到缺省 tag 时按同一 flag 还原。
        if (part.Tag is not null)
        {
            writer.WriteAttributeString("tag", part.Tag);
        }
        else if (Explicit("tag"))
        {
            // tag="" 是“透明部件”的真实取值，显式声明过就必须写出来。
            writer.WriteAttributeString("tag", string.Empty);
        }

        if (part.Class is not null)
        {
            writer.WriteAttributeString("class", part.Class);
        }

        if (part.Style is not null)
        {
            writer.WriteAttributeString("style", part.Style);
        }

        // 只在显式关闭时写：v1 定义的字节表示与历史文件保持不变。
        if (!part.Addressable)
        {
            writer.WriteAttributeString("addressable", "false");
        }
        else if (Explicit("addressable"))
        {
            writer.WriteAttributeString("addressable", "true");
        }

        // 无条件时不写：历史定义的字节表示保持不变。
        if (part.When is not null)
        {
            writer.WriteAttributeString("when", part.When);
        }

        // 源数据条件只在声明时写：历史定义字节不变。
        if (part.WhenSource is not null)
        {
            writer.WriteAttributeString("when-source", part.WhenSource);
        }

        // 重复与上限只在声明了重复时写；上限等于缺省值时不写，保持最小字节表示。
        if (part.Repeat is not null)
        {
            writer.WriteAttributeString("repeat", part.Repeat);
            if (part.RepeatMax != UserBlockStructureRules.DefaultRepeatMax || Explicit("repeat-max"))
            {
                writer.WriteAttributeString(
                    "repeat-max",
                    part.RepeatMax.ToString(CultureInfo.InvariantCulture));
            }
        }

        // 生成器与参数：只在声明了生成器时写；参数已按注册表规范化、按键排序。
        if (part.Generator is not null)
        {
            writer.WriteAttributeString("generator", part.Generator);
            if (part.GeneratorParams is not null)
            {
                writer.WriteAttributeString("generator-params", part.GeneratorParams);
            }
        }

        // 引用索引与安全策略：只在声明时写，历史定义字节不变。
        if (part.Ref is not null)
        {
            writer.WriteAttributeString("ref", part.Ref);
        }

        if (part.Policy is not null)
        {
            writer.WriteAttributeString("policy", part.Policy);
        }

        // 原文槽与槽位文本：只在显式声明时写，历史定义字节不变。
        if (part.Raw)
        {
            writer.WriteAttributeString("raw", "true");
        }
        else if (Explicit("raw"))
        {
            writer.WriteAttributeString("raw", "false");
        }

        if (part.Text is not null)
        {
            writer.WriteAttributeString("text", part.Text);
        }

        // 受控 URL 绑定只在声明时写：历史定义字节不变。
        if (part.Href is not null)
        {
            writer.WriteAttributeString(UserBlockStructureRules.HrefAttribute, part.Href);
        }

        if (part.Src is not null)
        {
            writer.WriteAttributeString(UserBlockStructureRules.SrcAttribute, part.Src);
        }

        // schema v3 原语：只在声明时写，历史定义字节不变。
        if (part.Content is not null)
        {
            writer.WriteAttributeString("content", part.Content);
        }

        if (part.Css is not null)
        {
            writer.WriteAttributeString("css", part.Css);
        }

        if (part.ClassRecipe is not null)
        {
            writer.WriteAttributeString("class-recipe", part.ClassRecipe);
        }

        if (part.TagCondition is not null)
        {
            writer.WriteAttributeString("tag-if", part.TagCondition);
        }

        if (part.Role is not null)
        {
            writer.WriteAttributeString("role", part.Role);
        }

        if (part.AttributesAfterIdentity)
        {
            writer.WriteAttributeString("attrs-after-identity", "true");
        }
        else if (Explicit("attrs-after-identity"))
        {
            writer.WriteAttributeString("attrs-after-identity", "false");
        }

        if (part.SourceCondition is not null)
        {
            writer.WriteAttributeString("source-condition", part.SourceCondition);
        }

        if (part.ChildCollection is not null)
        {
            writer.WriteAttributeString("child-collection", part.ChildCollection);
        }

        if (part.EmitWhenCollection is not null)
        {
            writer.WriteAttributeString("emit-when-collection", part.EmitWhenCollection);
        }

        if (part.EmptyCollection is not null)
        {
            writer.WriteAttributeString("empty-collection", part.EmptyCollection);
        }

        if (!part.EmitNodeIdentity)
        {
            writer.WriteAttributeString("no-identity", "true");
        }
        else if (Explicit("no-identity"))
        {
            writer.WriteAttributeString("no-identity", "false");
        }

        if (part.SelfClosing)
        {
            writer.WriteAttributeString("self-closing", "true");
        }
        else if (Explicit("self-closing"))
        {
            writer.WriteAttributeString("self-closing", "false");
        }

        if (part.SuppressNodeStyle)
        {
            writer.WriteAttributeString("suppress-style", "true");
        }
        else if (Explicit("suppress-style"))
        {
            writer.WriteAttributeString("suppress-style", "false");
        }

        if (part.EmitNodeStyle)
        {
            writer.WriteAttributeString("emit-node-style", "true");
        }
        else if (Explicit("emit-node-style"))
        {
            writer.WriteAttributeString("emit-node-style", "false");
        }

        if (part.EmitNone)
        {
            writer.WriteAttributeString("emit", "none");
        }
        else if (Explicit("emit"))
        {
            // 读取端 emit/emit="none" 都认；显式声明过就写回，避免重载后退回父值。
            writer.WriteAttributeString("emit", "emit");
        }

        if (part.Attributes.Count > 0)
        {
            writer.WriteAttributeString(
                "attr",
                string.Join(' ', part.Attributes.Select(item => $"{item.Source}@{item.Name}")));
        }

        foreach (var child in part.Children)
        {
            WritePart(writer, child);
        }

        writer.WriteEndElement();
    }

    private static UserBlockDefinition? ReadDefinition(
        XElement element,
        HashSet<string> ids,
        KeywordOccupancy keywords,
        List<DiagnosticMessage> diagnostics)
    {
        var id = (element.Attribute("id")?.Value ?? string.Empty).Trim();
        if (!UserBlockRegistry.IsValidIdentifier(id))
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Error,
                "USER_ELEMENTS_ID_INVALID",
                $"用户元素定义的 Id '{id}' 非法：需字母开头，仅含字母、数字、下划线或连字符，最长 64。已跳过该定义。"));
            return null;
        }

        if (!ids.Add(id))
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Error,
                "USER_ELEMENTS_ID_DUPLICATE",
                $"用户元素定义 Id '{id}' 重复。已跳过重复项，保留首次出现的定义。"));
            return null;
        }

        var name = (element.Attribute("name")?.Value ?? string.Empty).Trim();

        // 继承：extends 指向同一文件里的另一条元素定义。父链的缺引用/成环由组合校验统一判，
        // 这里只读原文（非法 Id 找不到父定义，会以 MISSING_REF 报出来）。
        var extends = (element.Attribute("extends")?.Value ?? string.Empty).Trim();
        if (extends.Length == 0)
        {
            extends = null;
        }

        // 声明面：继承合并要区分“子写了默认值”与“子压根没写”。
        var declaredFields = ReadDefinitionDeclaredFields(element);

        // 两个分支（applies-to / 围栏定义）共用的声明：避免跨分支作用域冲突。
        string? itemMarker = null;
        string? opens = null;

        // 内置类型重写定义：applies-to 指向 NodeType 枚举名，不参与围栏关键字竞争，
        // 也不写 name/fence/priority —— 它只提供结构描述。
        var appliesToText = (element.Attribute("applies-to")?.Value ?? string.Empty).Trim();
        if (appliesToText.Length > 0)
        {
            if (!Enum.TryParse<NodeType>(appliesToText, ignoreCase: true, out var target)
                || !Enum.IsDefined(target))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Error,
                    "USER_ELEMENTS_APPLIES_TO_INVALID",
                    $"元素定义 '{id}' 的 applies-to='{appliesToText}' 不是可重写的内置类型。已跳过该定义。"));
                return null;
            }

            bool? renderEnabled = null;
            var renderText = (element.Attribute("render")?.Value ?? string.Empty).Trim();
            if (renderText.Length > 0)
            {
                if (bool.TryParse(renderText, out var renderValue))
                {
                    renderEnabled = renderValue;
                }
                else
                {
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Error,
                        "USER_ELEMENTS_RENDER_INVALID",
                        $"元素定义 '{id}' 的 render='{renderText}' 不是 true/false。已跳过该定义。"));
                    return null;
                }
            }

            string? structureKey = null;
            var structureKeyText = (element.Attribute("structure-key")?.Value ?? string.Empty).Trim();
            if (structureKeyText.Length > 0)
            {
                if (!UserBlockStructureRules.TryNormalizeStructureKey(structureKeyText, out var normalizedKey))
                {
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Error,
                        "USER_ELEMENTS_STRUCTURE_KEY_INVALID",
                        $"元素定义 '{id}' 的 structure-key='{structureKeyText}' 不在允许的枚举内。已跳过该定义。"));
                    return null;
                }

                structureKey = normalizedKey;
            }

            string? selector = null;
            var selectorText = (element.Attribute("selector")?.Value ?? string.Empty).Trim();
            if (selectorText.Length > 0)
            {
                if (!UserBlockStructureRules.TryNormalizeSelector(selectorText, out var normalizedSelector))
                {
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Error,
                        "USER_ELEMENTS_SELECTOR_INVALID",
                        $"元素定义 '{id}' 的 selector='{selectorText}' 不在允许的枚举内。已跳过该定义。"));
                    return null;
                }

                selector = normalizedSelector;
            }

            string? variant = null;
            var variantText = (element.Attribute("variant")?.Value ?? string.Empty).Trim();
            if (variantText.Length > 0)
            {
                if (!UserBlockStructureRules.TryNormalizeVariant(variantText, out var normalizedVariant))
                {
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Error,
                        "USER_ELEMENTS_VARIANT_INVALID",
                        $"元素定义 '{id}' 的 variant='{variantText}' 不在允许的枚举内。已跳过该定义。"));
                    return null;
                }

                variant = normalizedVariant;
            }

            // 条目切分标记：非法只忽略不整条拒载。
            var itemMarkerText = (element.Attribute("item-marker")?.Value ?? string.Empty).Trim();
            if (itemMarkerText.Length > 0)
            {
                if (UserBlockStructureRules.TryNormalizeItemMarker(itemMarkerText, out var normalizedMarker))
                {
                    itemMarker = normalizedMarker;
                }
                else
                {
                    // 只影响条目切分，不整条丢弃定义：报错后按缺省 @@ 继续。
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Error,
                        "USER_ELEMENTS_ITEM_MARKER_INVALID",
                        $"元素定义 '{id}' 的 item-marker='{itemMarkerText}' 不合法（需 ≤4 个非空白、非字母数字字符）。已忽略该字段。"));
                }
            }

            // 首词分派：opens 声明本定义的 keywords 在哪些围栏字符下开启哪个块类型。
            // 只影响语法形态，不整条丢弃定义：任一项不成立就忽略该字段，结构仍按原样生效。
            IReadOnlyList<string> opensKeywords = [];
            IReadOnlyList<char> opensFenceChars = [];
            var opensText = (element.Attribute("opens")?.Value ?? string.Empty).Trim();
            if (opensText.Length > 0)
            {
                if (!FenceOpenTargets.TryNormalize(opensText, out var normalizedOpens))
                {
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Error,
                        "USER_ELEMENTS_OPENS_INVALID",
                        $"元素定义 '{id}' 的 opens='{opensText}' 不在可声明的块类型内"
                        + $"（允许：{string.Join(" / ", FenceOpenTargets.Names)}）。已忽略该字段。"));
                }
                else if (!FenceOpenTargets.IsCustom(normalizedOpens)
                    && TryReadOpensDeclaration(
                        element,
                        id,
                        normalizedOpens,
                        diagnostics,
                        // 继承定义可以只声明 opens、把围栏字符留给父定义：读入时先受理，合并后再生效。
                        inheritFenceChars: extends is not null,
                        out opensKeywords,
                        out opensFenceChars)
                    && TryClaimOpensKeywords(id, opensFenceChars, opensKeywords, keywords, diagnostics))
                {
                    opens = normalizedOpens;
                }
            }

            // 结构里的标签插值要对着属性声明校验，因此属性必须先读出来。
            var builtInAttributes = ReadAttributes(element, id, diagnostics);
            var builtInGroups = ReadAttributeGroupRefs(element, id, diagnostics);
            var builtInInlineHtml = ReadHtmlAllowlist(element, id, diagnostics, InlineHtmlSectionName);
            var builtInBlockHtml = ReadHtmlAllowlist(element, id, diagnostics, BlockHtmlSectionName);
            var builtInStructure = ReadStructure(
                element,
                id,
                builtInAttributes,
                diagnostics,
                requireContentSlot: false,
                structureKey: structureKey,
                deferValidation: builtInGroups.Count > 0
                    || StructureDeclaresInclude(element)
                    || extends is not null);

            // 既没有结构根、也没有任何行为声明：这类定义装进来也永远不会生效（哑状态），
            // 属于最难查的一类错，宁可当场报错、只丢这一条。
            // “行为声明”按原始数据判，不看类型名：<inline-html>/<block-html> 节、opens 首词、显式 render="false"
            // 都让定义本身有效（只给既有类型加围栏首词、或关掉某类型的渲染）。
            // 无效值也照样算“声明过”——由各自的警告处理，不在这里连坐。
            // 有 extends 时结构可能由父定义补齐，故不在此判定。
            var hasBehavior = builtInInlineHtml is not null
                || builtInBlockHtml is not null
                || !string.IsNullOrWhiteSpace((string?)element.Attribute("opens"))
                || string.Equals((string?)element.Attribute("render"), "false", StringComparison.OrdinalIgnoreCase);
            if (extends is null && builtInStructure.Root is null && !hasBehavior)
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Error,
                    "USER_ELEMENTS_NO_STRUCTURE_OR_BEHAVIOR",
                    $"元素定义 '{id}' 既没有结构根（<structure>），也没有任何行为节（如 <inline-html>/<block-html>）。"
                    + "该定义永远不会生效，已跳过该定义。"));
                return null;
            }

            return new UserBlockDefinition
            {
                Id = id,
                Extends = extends,
                DeclaredFields = declaredFields,
                Name = target.ToString(),
                AppliesTo = target.ToString(),
                RenderEnabled = renderEnabled,
                StructureKey = structureKey,
                Selector = selector,
                Variant = variant,
                ItemMarker = itemMarker,
                // 未声明 opens 时保持历史缺省（无别名、冒号围栏），指纹与运行时键逐字节不变。
                Opens = opens,
                Aliases = opens is null ? [] : opensKeywords,
                // 继承定义只写 opens、把围栏字符留给父定义时 opensFenceChars 为空，先按缺省占位。
                FenceChar = opensFenceChars.Count > 0 ? opensFenceChars[0] : ':',
                FenceChars = opensFenceChars,
                Attributes = builtInAttributes,
                AttributeGroups = builtInGroups,
                Content = ReadContent(element, id, diagnostics),
                InlineHtml = builtInInlineHtml,
                BlockHtml = builtInBlockHtml,
                Structure = builtInStructure,
            };
        }

        if (!UserBlockRegistry.IsValidIdentifier(name))
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Error,
                "USER_ELEMENTS_NAME_INVALID",
                $"用户元素 '{id}' 缺少合法名称（name='{name}'）：需字母开头，仅含字母、数字、下划线或连字符，最长 64。已跳过该定义。"));
            return null;
        }

        var legacyFenceCharText = element.Attribute("fence-char")?.Value?.Trim();
        var multiFenceCharsText = element.Attribute("fence-chars")?.Value;

        char fenceChar;
        IReadOnlyList<char> fenceChars;
        if (multiFenceCharsText is not null)
        {
            var raw = multiFenceCharsText.Trim();
            if (raw.Length == 0)
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Error,
                    "USER_ELEMENTS_FENCE_CHARS_EMPTY",
                    $"用户元素 '{id}' 的 fence-chars 为空；要么省略该属性，要么至少声明一个围栏字符。已跳过该定义。"));
                return null;
            }

            var parsed = new List<char>();
            foreach (var part in raw.Split(','))
            {
                var token = part.Trim();
                if (token.Length == 0)
                {
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Error,
                        "USER_ELEMENTS_FENCE_CHARS_EMPTY",
                        $"用户元素 '{id}' 的 fence-chars='{multiFenceCharsText}' 含空项。已跳过该定义。"));
                    return null;
                }

                if (token.Length != 1)
                {
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Error,
                        UserBlockRegistry.InvalidFenceCharCode,
                        $"用户元素 '{id}' 的围栏字符 '{token}' 不是单个字符；{UserBlockRegistry.FenceCharRule}。已跳过该定义。"));
                    return null;
                }

                if (!UserBlockRegistry.TryGetFenceCharIssue(token[0], out var fenceCode, out var fenceDetail))
                {
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Error,
                        fenceCode,
                        $"用户元素 '{id}' 的围栏字符不可用；{fenceDetail}。已跳过该定义。"));
                    return null;
                }

                if (parsed.Contains(token[0]))
                {
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Error,
                        "USER_ELEMENTS_FENCE_CHARS_DUPLICATE",
                        $"用户元素 '{id}' 的 fence-chars='{multiFenceCharsText}' 含重复字符 '{token[0]}'。已跳过该定义。"));
                    return null;
                }

                parsed.Add(token[0]);
            }

            // 两个字段同时出现时必须一致，避免同一份定义出现互相矛盾的事实来源。
            if (!string.IsNullOrEmpty(legacyFenceCharText) && legacyFenceCharText[0] != parsed[0])
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Error,
                    "USER_ELEMENTS_FENCE_CHARS_CONFLICT",
                    $"用户元素 '{id}' 的 fence-chars 首项 '{parsed[0]}' "
                    + $"与 fence-char='{legacyFenceCharText}' 不一致。已跳过该定义。"));
                return null;
            }

            fenceChars = parsed;
            fenceChar = parsed[0];
        }
        else
        {
            var fenceCharText = legacyFenceCharText ?? ":";
            if (fenceCharText.Length != 1)
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Error,
                    UserBlockRegistry.InvalidFenceCharCode,
                    $"用户元素 '{id}' 的围栏字符 '{fenceCharText}' 不是单个字符；{UserBlockRegistry.FenceCharRule}。已跳过该定义。"));
                return null;
            }

            if (!UserBlockRegistry.TryGetFenceCharIssue(fenceCharText[0], out var fenceCode, out var fenceDetail))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Error,
                    fenceCode,
                    $"用户元素 '{id}' 的围栏字符不可用；{fenceDetail}。已跳过该定义。"));
                return null;
            }

            fenceChar = fenceCharText[0];
            fenceChars = [];
        }

        // 内置关键字是保留字：手改文件写了同名定义也不能顶掉内置围栏。
        var effectiveFenceChars = fenceChars.Count > 0 ? fenceChars : [fenceChar];
        if (effectiveFenceChars.Any(candidate =>
                UserBlockRegistry.FamilyOf(candidate) is { } family
                && FenceKeywordRegistry.BuiltIn.IsReservedKeyword(family, name)))
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Error,
                "USER_ELEMENTS_NAME_RESERVED",
                $"用户元素 '{id}' 的名称 '{name}' 是内置围栏关键字，不能作为元素名称。已跳过该定义。"));
            return null;
        }

        var fenceLength = ReadInt(element, "fence-length", 3);
        if (fenceLength is < UserBlockRegistry.MinFenceLength or > UserBlockRegistry.MaxFenceLength)
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Error,
                "USER_ELEMENTS_FENCE_LENGTH_OUT_OF_RANGE",
                $"用户元素 '{id}' 的围栏长度 {fenceLength} 越界：允许 {UserBlockRegistry.MinFenceLength}~{UserBlockRegistry.MaxFenceLength}。已跳过该定义。"));
            return null;
        }

        var priority = ReadInt(element, "priority", 0);
        if (priority is < UserBlockRegistry.MinPriority or > UserBlockRegistry.MaxPriority)
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Error,
                "USER_ELEMENTS_PRIORITY_OUT_OF_RANGE",
                $"用户元素 '{id}' 的优先级 {priority} 越界：允许 {UserBlockRegistry.MinPriority}~{UserBlockRegistry.MaxPriority}。已跳过该定义。"));
            return null;
        }

        // 条目切分标记：≤4 个非空白、非字母数字字符；非法只忽略不整条拒载。
        var itemMarkerTextFence = (element.Attribute("item-marker")?.Value ?? string.Empty).Trim();
        if (itemMarkerTextFence.Length > 0)
        {
            if (UserBlockStructureRules.TryNormalizeItemMarker(itemMarkerTextFence, out var normalizedMarker))
            {
                itemMarker = normalizedMarker;
            }
            else
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Error,
                    "USER_ELEMENTS_ITEM_MARKER_INVALID",
                    $"用户元素 '{id}' 的 item-marker='{itemMarkerTextFence}' 不合法（需 ≤4 个非空白、非字母数字字符）。已忽略该字段。"));
            }
        }

        // 首词分派目标：非法值直接拒载——opens 是结构意图的一部分，错了整条定义就没有意义。
        var opensTextFence = (element.Attribute("opens")?.Value ?? string.Empty).Trim();
        if (opensTextFence.Length > 0)
        {
            if (!FenceOpenTargets.TryNormalize(opensTextFence, out var normalizedOpens))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Error,
                    "USER_ELEMENTS_OPENS_INVALID",
                    $"用户元素 '{id}' 的 opens='{opensTextFence}' 不在可声明的块类型内"
                    + $"（允许：{string.Join(" / ", FenceOpenTargets.Names)}）。已跳过该定义。"));
                return null;
            }

            opens = normalizedOpens;
        }

        // 结构里的标签插值要对着属性声明校验，因此属性必须先读出来。
        var attributes = ReadAttributes(element, id, diagnostics);
        var attributeGroups = ReadAttributeGroupRefs(element, id, diagnostics);

        // 别名：格式/自指/定义内重复/撞内置都是硬错误；任一不满足即跳过整条定义，
        // 不保留“只生效一半别名”的中间态。
        if (!TryReadAliases(element, id, name, effectiveFenceChars, diagnostics, out var aliases))
        {
            return null;
        }

        // 别名的定义间唯一性：与已出现定义的任意关键字同族重复即拒绝后出现的定义，
        // 不允许后者静默覆盖前者（本名之间的重复仍由 priority 仲裁，行为不变）。
        foreach (var alias in aliases)
        {
            foreach (var candidate in effectiveFenceChars)
            {
                if (keywords.TryFindKeyword(candidate, alias, out var owner))
                {
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Error,
                        "USER_ELEMENTS_KEYWORD_DUPLICATE",
                        $"用户元素 '{id}' 的别名 '{alias}' 与已有定义 '{owner}' 的关键字重复。已跳过该定义。"));
                    return null;
                }
            }
        }

        // 反向：新定义的本名撞上已声明的别名，同样拒绝，避免别名悄悄占住别人的名字。
        foreach (var candidate in effectiveFenceChars)
        {
            if (keywords.TryFindAlias(candidate, name, out var owner))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Error,
                    "USER_ELEMENTS_KEYWORD_DUPLICATE",
                    $"用户元素 '{id}' 的名称 '{name}' 与已有定义 '{owner}' 的别名重复。已跳过该定义。"));
                return null;
            }
        }

        foreach (var candidate in effectiveFenceChars)
        {
            keywords.ClaimName(candidate, name, id);
        }

        foreach (var alias in aliases)
        {
            foreach (var candidate in effectiveFenceChars)
            {
                keywords.ClaimAlias(candidate, alias, id);
            }
        }

        var structure = ReadStructure(
            element,
            id,
            attributes,
            diagnostics,
            deferValidation: attributeGroups.Count > 0
                || StructureDeclaresInclude(element)
                || extends is not null,
            // 继承定义的部件可以省略 tag：读入时先按透明部件保留，合并时再从父部件继承。
            inheritTag: extends is not null);

        // 围栏定义原始 XML 有 <structure> 但被 ReadStructure 校验成 Empty，
        // 且缺的是 body 槽 → 整条拒载，避免静默回退内置模板。
        // 其他原因（如 Policy 拒绝 script 标签）是合法的安全降级，保留定义但结构回退。
        if (structure.Root is null
            && extends is null
            && element.Elements().Any(child => string.Equals(child.Name.LocalName, "structure", StringComparison.Ordinal))
            && diagnostics.Any(item => item.Code == "USER_ELEMENTS_STRUCTURE_NO_CONTENT"))
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Error,
                "USER_ELEMENTS_STRUCTURE_INVALID",
                $"用户元素 '{id}' 的结构缺少正文槽位，已跳过该定义。"));
            return null;
        }

        return new UserBlockDefinition
        {
            Id = id,
            Extends = extends,
            DeclaredFields = declaredFields,
            Name = name,
            Aliases = aliases,
            FenceChar = fenceChar,
            FenceChars = fenceChars,
            FenceLength = fenceLength,
            Priority = priority,
            ItemMarker = itemMarker,
            Opens = opens,
            Attributes = attributes,
            AttributeGroups = attributeGroups,
            Content = ReadContent(element, id, diagnostics),
            Structure = structure
        };
    }

    /// <summary>
    /// 读 <c>&lt;uses ref="…"/&gt;</c> 属性组引用。非法 Id 只丢该条引用；
    /// 缺引用/属性键碰撞由组合校验统一判（保存拒绝、装载丢定义）。
    /// </summary>
    private static IReadOnlyList<string> ReadAttributeGroupRefs(
        XElement element,
        string definitionId,
        List<DiagnosticMessage> diagnostics)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in element.Elements().Where(item =>
                     string.Equals(item.Name.LocalName, "uses", StringComparison.Ordinal)))
        {
            var reference = (child.Attribute("ref")?.Value ?? string.Empty).Trim();
            if (!UserBlockRegistry.IsValidIdentifier(reference))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Warning,
                    UserBlockCompositionRules.MissingRefCode,
                    $"用户元素 '{definitionId}' 的 uses 引用 '{reference}' 非法，已忽略该引用。"));
                continue;
            }

            if (seen.Add(reference))
            {
                result.Add(reference);
            }
        }

        return result;
    }

    /// <summary>结构声明里是否出现 <c>&lt;include&gt;</c>：有则把字段校验推迟到展开后。</summary>
    private static bool StructureDeclaresInclude(XElement element) =>
        element.Elements()
            .FirstOrDefault(child => string.Equals(child.Name.LocalName, "structure", StringComparison.Ordinal))
            ?.Descendants()
            .Any(node => string.Equals(node.Name.LocalName, "include", StringComparison.Ordinal))
        ?? false;

    /// <summary>
    /// 元素定义在源 XML 里实际声明的字段：元素属性名 + 直接子元素名（小写）。
    /// 继承合并靠它判断“子写了这个字段”，<c>null</c> 只用于代码内手工构造的定义。
    /// </summary>
    private static IReadOnlySet<string> ReadDefinitionDeclaredFields(XElement element)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in element.Attributes())
        {
            if (!attribute.IsNamespaceDeclaration)
            {
                fields.Add(attribute.Name.LocalName.ToLowerInvariant());
            }
        }

        foreach (var child in element.Elements())
        {
            fields.Add(child.Name.LocalName.ToLowerInvariant());
        }

        return fields;
    }

    /// <summary>源 XML 是否显式声明了该字段（<c>DeclaredFields</c> 为 null 时按“已声明”处理）。</summary>
    private static bool IsDeclared(UserBlockDefinition definition, string field) =>
        definition.DeclaredFields is not { } declared || declared.Contains(field);

    /// <summary>
    /// 读取 <c>keywords="a,b"</c> 别名声明。缺省属性 = 无别名（历史定义字节与行为不变）；
    /// 显式给出时逐项校验，任一项非法即整条定义跳过并给固定诊断码。
    /// </summary>
    private static bool TryReadAliases(
        XElement element,
        string definitionId,
        string name,
        IReadOnlyList<char> effectiveFenceChars,
        List<DiagnosticMessage> diagnostics,
        out IReadOnlyList<string> aliases)
    {
        aliases = [];

        var text = element.Attribute("keywords")?.Value;
        if (text is null)
        {
            return true;
        }

        var rawItems = text.Split(',');
        if (rawItems.Length > UserBlockDefinition.MaxAliases)
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Error,
                "USER_ELEMENTS_KEYWORD_COUNT_EXCEEDED",
                $"用户元素 '{definitionId}' 声明了 {rawItems.Length} 个别名，超过上限 "
                + $"{UserBlockDefinition.MaxAliases}。已跳过该定义。"));
            return false;
        }

        var parsed = new List<string>(rawItems.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in rawItems)
        {
            var alias = item.Trim();
            if (!UserBlockRegistry.IsValidIdentifier(alias))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Error,
                    "USER_ELEMENTS_KEYWORD_INVALID",
                    $"用户元素 '{definitionId}' 的别名 '{alias}' 非法：需字母开头，仅含字母、数字、下划线或连字符，最长 64。已跳过该定义。"));
                return false;
            }

            if (string.Equals(alias, name, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Error,
                    "USER_ELEMENTS_KEYWORD_SELF_REFERENCE",
                    $"用户元素 '{definitionId}' 的别名 '{alias}' 与本名相同，必须移除该别名。已跳过该定义。"));
                return false;
            }

            if (!seen.Add(alias))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Error,
                    "USER_ELEMENTS_KEYWORD_DUPLICATE",
                    $"用户元素 '{definitionId}' 的别名 '{alias}' 定义内重复。已跳过该定义。"));
                return false;
            }

            if (effectiveFenceChars.Any(candidate =>
                    UserBlockRegistry.FamilyOf(candidate) is { } family
                    && FenceKeywordRegistry.BuiltIn.IsReservedKeyword(family, alias)))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Error,
                    "USER_ELEMENTS_KEYWORD_RESERVED",
                    $"用户元素 '{definitionId}' 的别名 '{alias}' 是内置围栏关键字，不能作为别名。已跳过该定义。"));
                return false;
            }

            parsed.Add(alias);
        }

        aliases = parsed;
        return true;
    }

    /// <summary>
    /// 读取 <c>opens</c> 的配套声明：<c>keywords</c>（本定义要开的首词）与
    /// <c>fence-char</c>/<c>fence-chars</c>（允许的围栏族）。任一项不成立即整体忽略 opens，
    /// 定义本身保留，行为与该字段不存在时一致。
    /// </summary>
    private static bool TryReadOpensDeclaration(
        XElement element,
        string definitionId,
        string opens,
        List<DiagnosticMessage> diagnostics,
        bool inheritFenceChars,
        out IReadOnlyList<string> keywords,
        out IReadOnlyList<char> fenceChars)
    {
        keywords = [];
        fenceChars = [];

        var keywordText = element.Attribute("keywords")?.Value;
        if (string.IsNullOrWhiteSpace(keywordText))
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Error,
                "USER_ELEMENTS_OPENS_INVALID",
                $"元素定义 '{definitionId}' 声明了 opens='{opens}' 但缺少 keywords。已忽略该字段。"));
            return false;
        }

        var rawKeywords = keywordText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rawKeywords.Length == 0 || rawKeywords.Length > UserBlockDefinition.MaxAliases)
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Error,
                "USER_ELEMENTS_OPENS_INVALID",
                $"元素定义 '{definitionId}' 的 opens 关键字数量需在 1~{UserBlockDefinition.MaxAliases} 之间。已忽略该字段。"));
            return false;
        }

        var parsedKeywords = new List<string>(rawKeywords.Length);
        foreach (var keyword in rawKeywords)
        {
            if (!UserBlockRegistry.IsValidIdentifier(keyword))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Error,
                    "USER_ELEMENTS_OPENS_INVALID",
                    $"元素定义 '{definitionId}' 的 opens 关键字 '{keyword}' 非法：需字母开头，"
                    + "仅含字母、数字、下划线或连字符，最长 64。已忽略该字段。"));
                return false;
            }

            parsedKeywords.Add(keyword);
        }

        var multiText = element.Attribute("fence-chars")?.Value;
        var singleText = element.Attribute("fence-char")?.Value;

        // 继承定义可以省略围栏字符：由父定义提供。此时先受理 opens，占用登记与配对校验留到合并后。
        if (inheritFenceChars && multiText is null && singleText is null)
        {
            keywords = parsedKeywords;
            fenceChars = [];
            return true;
        }

        var rawFenceChars = multiText is not null
            ? multiText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [(singleText ?? string.Empty).Trim()];

        var parsedFenceChars = new List<char>(rawFenceChars.Length);
        foreach (var text in rawFenceChars)
        {
            if (text.Length != 1 || !FenceOpenTargets.SupportsFenceChar(opens, text[0]))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Error,
                    "USER_ELEMENTS_OPENS_INVALID",
                    $"元素定义 '{definitionId}' 的围栏字符 '{text}' 不能开启 {opens}："
                    + "三单引号族只开诗词/时间线/雷达图/图片矩阵，代码围栏族只开数学/流程图/图片矩阵/二维码。"
                    + "已忽略该字段。"));
                return false;
            }

            if (!parsedFenceChars.Contains(text[0]))
            {
                parsedFenceChars.Add(text[0]);
            }
        }

        if (parsedFenceChars.Count == 0)
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Error,
                "USER_ELEMENTS_OPENS_INVALID",
                $"元素定义 '{definitionId}' 声明了 opens='{opens}' 但缺少可用的 fence-char/fence-chars。已忽略该字段。"));
            return false;
        }

        // 内置首词是保留字：声明不能静默顶掉已有形态（同族同词一律拒绝）。
        foreach (var fenceChar in parsedFenceChars)
        {
            if (UserBlockRegistry.FamilyOf(fenceChar) is not { } family)
            {
                continue;
            }

            foreach (var keyword in parsedKeywords)
            {
                if (FenceKeywordRegistry.BuiltIn.IsReservedKeyword(family, keyword))
                {
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Error,
                        "USER_ELEMENTS_OPENS_INVALID",
                        $"元素定义 '{definitionId}' 的 opens 关键字 '{keyword}' 已被内置形态占用。已忽略该字段。"));
                    return false;
                }
            }
        }

        keywords = parsedKeywords;
        fenceChars = parsedFenceChars;
        return true;
    }

    /// <summary>
    /// 首词占用：同族同词只能有一个归属。先声明者占住后出现者即报错，
    /// 不允许后出现的定义静默顶掉前面的形态。
    /// </summary>
    private static bool TryClaimOpensKeywords(
        string definitionId,
        IReadOnlyList<char> fenceChars,
        IReadOnlyList<string> keywords,
        KeywordOccupancy occupancy,
        List<DiagnosticMessage> diagnostics)
    {
        foreach (var fenceChar in fenceChars)
        {
            foreach (var keyword in keywords)
            {
                if (occupancy.TryFindKeyword(fenceChar, keyword, out var owner))
                {
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Error,
                        "USER_ELEMENTS_OPENS_INVALID",
                        $"元素定义 '{definitionId}' 的首词 '{keyword}' 已由定义 '{owner}' 占用。已忽略该字段。"));
                    return false;
                }
            }
        }

        foreach (var fenceChar in fenceChars)
        {
            foreach (var keyword in keywords)
            {
                occupancy.ClaimAlias(fenceChar, keyword, definitionId);
            }
        }

        return true;
    }

    /// <summary>
    /// 关键字占用表：按「围栏字符 + 关键字」记录归属。本名允许重复（priority 仲裁），
    /// 别名与任何已有本名/别名重复都拒绝，保证“后出现者不静默覆盖”。
    /// </summary>
    private sealed class KeywordOccupancy
    {
        private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

        public bool TryFindKeyword(char fenceChar, string keyword, out string owner) =>
            _names.TryGetValue(Key(fenceChar, keyword), out owner!)
            || _aliases.TryGetValue(Key(fenceChar, keyword), out owner!);

        public bool TryFindAlias(char fenceChar, string keyword, out string owner) =>
            _aliases.TryGetValue(Key(fenceChar, keyword), out owner!);

        public void ClaimName(char fenceChar, string keyword, string definitionId) =>
            _names[Key(fenceChar, keyword)] = definitionId;

        public void ClaimAlias(char fenceChar, string keyword, string definitionId) =>
            _aliases[Key(fenceChar, keyword)] = definitionId;

        private static string Key(char fenceChar, string keyword) => fenceChar + "\u0000" + keyword;
    }

    /// <summary>
    /// 读取结构声明。部件属性只接受 <c>slot</c> / <c>tag</c> / <c>class</c> / <c>style</c> /
    /// <c>addressable</c> / <c>when</c> / <c>repeat</c> / <c>repeat-max</c> / <c>generator</c> / <c>generator-params</c> /
    /// <c>ref</c> / <c>policy</c>，其余（如 <c>onclick</c>）一律忽略并报问题；
    /// 标签与类名再经 <see cref="UserBlockStructureRules"/> 净化。
    /// </summary>
    private static UserBlockStructure ReadStructure(
        XElement element,
        string definitionId,
        IReadOnlyList<UserBlockAttributeDeclaration> attributes,
        List<DiagnosticMessage> diagnostics,
        bool requireContentSlot = true,
        string? structureKey = null,
        bool deferValidation = false,
        bool inheritTag = false)
    {
        var container = element.Elements()
            .FirstOrDefault(child => string.Equals(child.Name.LocalName, "structure", StringComparison.Ordinal));
        if (container is null)
        {
            return UserBlockStructure.Empty;
        }

        return ReadStructureContainer(
            container,
            definitionId,
            attributes,
            diagnostics,
            requireContentSlot,
            structureKey,
            deferValidation,
            inheritTag: inheritTag);
    }

    /// <summary>
    /// 从 <c>&lt;structure&gt;</c>/<c>&lt;fragment&gt;</c> 容器读部件树。
    /// <paramref name="deferValidation"/> 为 true 时只做标签/槽位净化，
    /// 把依赖合并后属性的校验（插值、条件、重复、生成器、引用、策略）留给展开后统一判。
    /// </summary>
    private static UserBlockStructure ReadStructureContainer(
        XElement container,
        string definitionId,
        IReadOnlyList<UserBlockAttributeDeclaration> attributes,
        List<DiagnosticMessage> diagnostics,
        bool requireContentSlot,
        string? structureKey,
        bool deferValidation,
        IReadOnlyList<string>? declaredSlotsOverride = null,
        bool inheritTag = false)
    {
        // 定义级自定义槽位声明先规范化：非法/保留名/重复/超限逐条给固定诊断码，
        // 部件引用被丢弃的槽名时按“未声明”再报一次。
        var slotsIssue = new List<UserBlockStructureIssue>();
        var declaredSlots = UserBlockStructureRules.NormalizeSlotDeclarations(
            declaredSlotsOverride ?? SplitSlotDeclarations(container.Attribute("slots")?.Value),
            slotsIssue);
        foreach (var issue in slotsIssue)
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Warning,
                issue.Code,
                $"用户元素 '{definitionId}'：{issue.Message}"));
        }

        var parts = new List<UserBlockPart>();
        // 继承定义允许部件省略 tag（由父部件补上），因此按“可透明”读入；
        // 声明面会记住 tag 是否出现，合并时再决定是继承还是透明。
        var allowTransparent = !requireContentSlot || inheritTag;
        foreach (var item in container.Elements())
        {
            var part = ReadPart(
                item,
                definitionId,
                diagnostics,
                declaredSlots,
                allowTransparent,
                allowUndeclaredSlots: inheritTag);
            if (part is not null)
            {
                parts.Add(part);
            }
        }

        // 只保留第一个根部件：结构是单根树，多余的同级根部件无法表达层级。
        if (parts.Count > 1)
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Warning,
                "USER_ELEMENTS_STRUCTURE_MULTI_ROOT",
                $"用户元素 '{definitionId}' 的结构声明了 {parts.Count} 个根部件，只保留第一个，其余已丢弃。"));
        }

        if (parts.Count == 0)
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Warning,
                "USER_ELEMENTS_STRUCTURE_EMPTY",
                $"用户元素 '{definitionId}' 的结构声明没有可用部件，已回退内置模板。"));
            return UserBlockStructure.Empty;
        }

        var raw = new UserBlockStructure
        {
            Root = parts[0],
            Slots = declaredSlots,
            AllowTransparentParts = allowTransparent,
        };

        // 一个部件只能有一个条件槽位：字段条件与源数据条件并存时整条结构回退，不做隐式 AND。
        // 先于净化判定，避免净化“丢掉源数据条件”后被误当成合法结构。
        var whenSourceFieldIssues = UserBlockStructureRules.ValidateWhenSourceFields(raw);
        if (whenSourceFieldIssues.Count > 0)
        {
            foreach (var issue in whenSourceFieldIssues)
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Warning,
                    issue.Code,
                    $"用户元素 '{definitionId}'：{issue.Message}已回退内置模板。"));
            }

            return UserBlockStructure.Empty;
        }

        // 继承定义的自定义槽位可能只声明在父定义里：先按格式受理，合并后再用父链槽位并集统一校验。
        var sanitized = UserBlockStructureRules.Sanitize(raw, allowUndeclaredSlots: inheritTag);
        foreach (var issue in sanitized.Issues)
        {
            diagnostics.Add(Diagnostic(DiagnosticSeverity.Warning, issue.Code, $"用户元素 '{definitionId}'：{issue.Message}"));
        }

        if (sanitized.Structure.Root is null)
        {
            return UserBlockStructure.Empty;
        }

        // 组合定义与片段：插值/条件等校验要用合并后的属性与展开后的部件树，这里先放过。
        if (deferValidation)
        {
            return sanitized.Structure;
        }

        // 标签插值只在保存/读取时校验：手改文件绕过编辑校验时，整条结构拒绝并回退内置模板。
        var tagFieldIssues = UserBlockStructureRules.ValidateTagFields(sanitized.Structure, attributes);
        if (tagFieldIssues.Count > 0)
        {
            foreach (var issue in tagFieldIssues)
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Warning,
                    issue.Code,
                    $"用户元素 '{definitionId}'：{issue.Message}已回退内置模板。"));
            }

            return UserBlockStructure.Empty;
        }

        // 条件同样只允许“已声明枚举字段 + 枚举值”：读入即校验，非法整条结构回退。
        var whenFieldIssues = UserBlockStructureRules.ValidateWhenFields(sanitized.Structure, attributes);
        if (whenFieldIssues.Count > 0)
        {
            foreach (var issue in whenFieldIssues)
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Warning,
                    issue.Code,
                    $"用户元素 '{definitionId}'：{issue.Message}已回退内置模板。"));
            }

            return UserBlockStructure.Empty;
        }

        // 重复集合、上限与嵌套约束同样读入即校验，非法整条结构回退。
        var repeatFieldIssues = UserBlockStructureRules.ValidateRepeatFields(sanitized.Structure, structureKey);
        if (repeatFieldIssues.Count > 0)
        {
            foreach (var issue in repeatFieldIssues)
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Warning,
                    issue.Code,
                    $"用户元素 '{definitionId}'：{issue.Message}已回退内置模板。"));
            }

            return UserBlockStructure.Empty;
        }

        // 生成器与参数同样读入即校验，非法整条结构回退。
        var generatorFieldIssues = UserBlockStructureRules.ValidateGeneratorFields(sanitized.Structure);
        if (generatorFieldIssues.Count > 0)
        {
            foreach (var issue in generatorFieldIssues)
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Warning,
                    issue.Code,
                    $"用户元素 '{definitionId}'：{issue.Message}已回退内置模板。"));
            }

            return UserBlockStructure.Empty;
        }

        // 引用索引与安全策略同样读入即校验，非法整条结构回退（安全边界宁可拒绝）。
        foreach (var issues in new[]
                 {
                     UserBlockStructureRules.ValidateRefFields(sanitized.Structure),
                     UserBlockStructureRules.ValidatePolicyFields(sanitized.Structure),
                 })
        {
            if (issues.Count == 0)
            {
                continue;
            }

            foreach (var issue in issues)
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Warning,
                    issue.Code,
                    $"用户元素 '{definitionId}'：{issue.Message}已回退内置模板。"));
            }

            return UserBlockStructure.Empty;
        }

        // 槽位文本引用未知字段时只告警不整条回退：未知字段渲染为空内容，不影响结构安全。
        foreach (var issue in UserBlockStructureRules.ValidateTextField(sanitized.Structure, attributes))
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Warning,
                issue.Code,
                $"用户元素 '{definitionId}'：{issue.Message}"));
        }

        // 受控 URL 绑定引用未知字段同样只告警：运行时取不到值就不写该属性，不会产生可执行地址。
        foreach (var issue in UserBlockStructureRules.ValidateBindingFields(sanitized.Structure, attributes))
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Warning,
                issue.Code,
                $"用户元素 '{definitionId}'：{issue.Message}"));
        }

        if (requireContentSlot && !UserBlockStructureRules.HasContentSlot(sanitized.Structure))
        {
            // 没有正文槽位时正文无处可放；围栏定义必须有正文槽才能装载，否则直接拒载。
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Error,
                "USER_ELEMENTS_STRUCTURE_NO_CONTENT",
                $"用户元素 '{definitionId}' 的结构缺少正文槽位（body/content），已跳过该定义。"));
            return UserBlockStructure.Empty;
        }

        return sanitized.Structure;
    }

    /// <summary>拆分 <c>slots="a,b"</c> 声明；缺省属性返回空表，空段保留给格式校验报错。</summary>
    private static IReadOnlyList<string> SplitSlotDeclarations(string? value) =>
        value is null ? Array.Empty<string>() : value.Split(',');

    /// <summary>
    /// 部件在源 XML 里实际声明的属性名（小写）。继承合并必须按“源 XML 里出现过哪个属性”
    /// 判断覆盖，而不是按值是否为默认（例如 <c>addressable="false"</c> 必须能覆盖父的 <c>true</c>）。
    /// </summary>
    private static IReadOnlySet<string> ReadPartDeclaredFields(XElement item)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in item.Attributes())
        {
            if (!attribute.IsNamespaceDeclaration)
            {
                fields.Add(attribute.Name.LocalName.ToLowerInvariant());
            }
        }

        return fields;
    }

    private static UserBlockPart? ReadPart(
        XElement item,
        string definitionId,
        List<DiagnosticMessage> diagnostics,
        IReadOnlyList<string> declaredSlots,
        bool allowTransparent,
        bool allowUndeclaredSlots = false)
    {
        if (!string.Equals(item.Name.LocalName, "part", StringComparison.Ordinal))
        {
            // 片段引用是结构里的合法占位节点：它不参与槽位/标签判定，展开后整节点被替换。
            if (string.Equals(item.Name.LocalName, "include", StringComparison.Ordinal))
            {
                var includeRef = (item.Attribute("ref")?.Value ?? string.Empty).Trim();
                if (!UserBlockRegistry.IsValidIdentifier(includeRef))
                {
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Warning,
                        UserBlockCompositionRules.MissingRefCode,
                        $"用户元素 '{definitionId}' 的结构里 include 的片段引用 '{includeRef}' 非法，已忽略该节点。"));
                    return null;
                }

                return new UserBlockPart
                {
                    Part = UserBlockStructureRules.IncludePartToken,
                    Include = includeRef
                };
            }

            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Warning,
                "USER_ELEMENTS_STRUCTURE_NODE_INVALID",
                $"用户元素 '{definitionId}' 的结构里出现未知节点 <{item.Name.LocalName}>，已忽略。"));
            return null;
        }

        // 属性名白名单：不在表内的一律不进入模型，从根上断掉 on* / href 等注入面；
        // style 虽在表内，但随后只保留白名单盒子属性并规范化，不做原文透传。
        foreach (var attribute in item.Attributes())
        {
            if (!UserBlockStructureRules.AllowedPartAttributes.Contains(attribute.Name.LocalName))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Warning,
                    "USER_ELEMENTS_STRUCTURE_ATTRIBUTE_REJECTED",
                    $"用户元素 '{definitionId}' 的结构部件含不支持的属性 '{attribute.Name.LocalName}'，已忽略。"));
            }
        }

        var slotText = item.Attribute("slot")?.Value;
        // 继承定义的槽位可能只声明在父定义里：读取面不能比合并后的校验面更窄，
        // 否则父定义的 marker 槽位在子定义里会被当成“未声明”丢掉。
        if (!UserBlockStructureRules.TryNormalizePart(
                slotText,
                declaredSlots,
                allowUndeclared: allowUndeclaredSlots,
                out var slot))
        {
            var undeclared = UserBlockStructureRules.TryNormalizeSlotName(slotText, out _);
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Warning,
                undeclared
                    ? "USER_ELEMENTS_STRUCTURE_SLOT_UNDECLARED"
                    : "USER_ELEMENTS_STRUCTURE_SLOT_INVALID",
                undeclared
                    ? $"用户元素 '{definitionId}' 的结构部件槽位 '{slotText}' 未在 slots 中声明，已丢弃该部件及其子部件。"
                    : $"用户元素 '{definitionId}' 的结构部件槽位 '{slotText}' 非法，已丢弃该部件及其子部件。"));
            return null;
        }

        // 安全策略先于标签：受控标签（a/img/input）只有匹配策略才放行；
        // 脚本/样式/嵌入/表单类标签任何策略都不放行，直接丢弃该部件。
        var policyText = item.Attribute("policy")?.Value;
        string? policy = null;
        if (!string.IsNullOrWhiteSpace(policyText))
        {
            if (!UserBlockStructureRules.TryNormalizePolicy(policyText, out var normalizedPolicy))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Warning,
                    "USER_ELEMENTS_STRUCTURE_POLICY_UNKNOWN",
                    $"用户元素 '{definitionId}' 的结构部件安全策略 '{policyText}' 不在允许的枚举内，已丢弃该策略。"));
            }
            else
            {
                policy = normalizedPolicy;
            }
        }

        var tagText = item.Attribute("tag")?.Value;
        var transparent = string.IsNullOrWhiteSpace(tagText) && allowTransparent;
        if (transparent)
        {
            tagText = null;
        }

        if (!transparent && UserBlockStructureRules.IsForbiddenTag(tagText))
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Warning,
                "USER_ELEMENTS_STRUCTURE_POLICY_TAG_FORBIDDEN",
                $"用户元素 '{definitionId}' 的结构标签 '{tagText}' 被安全策略禁止，已丢弃该部件及其子部件。"));
            return null;
        }

        string? tag = null;
        if (!transparent)
        {
            if (!UserBlockStructureRules.TryNormalizeTag(tagText, policy, out var normalizedTag))
            {
                var controlled = UserBlockStructureRules.IsControlledTag(tagText);
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Warning,
                    controlled
                        ? "USER_ELEMENTS_STRUCTURE_POLICY_TAG_MISMATCH"
                        : "USER_ELEMENTS_STRUCTURE_TAG_INVALID",
                    controlled
                        ? $"用户元素 '{definitionId}' 的受控标签 '{tagText}' 缺少匹配的安全策略，已丢弃该部件及其子部件。"
                        : $"用户元素 '{definitionId}' 的结构部件标签 '{tagText}' 不在白名单内，已丢弃该部件及其子部件。"));
                return null;
            }

            tag = normalizedTag;
        }

        if (tag is not null && policy is not null && !UserBlockStructureRules.PolicyAllowsTag(policy, tag))
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Warning,
                "USER_ELEMENTS_STRUCTURE_POLICY_TAG_MISMATCH",
                $"用户元素 '{definitionId}' 的策略 '{policy}' 与标签 '{tag}' 不匹配，已丢弃该策略。"));
            policy = null;
        }

        var rejectedClassTokens = new List<string>();
        var className = UserBlockStructureRules.SanitizeClass(
            item.Attribute("class")?.Value,
            rejectedClassTokens);
        if (rejectedClassTokens.Count > 0)
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Warning,
                "USER_ELEMENTS_STRUCTURE_CLASS_INVALID",
                $"用户元素 '{definitionId}' 的结构类名 '{string.Join(' ', rejectedClassTokens)}' 含非法字符，已丢弃该 token。"));
        }

        var rejectedStyleDeclarations = new List<string>();
        var style = UserBlockStyleRules.Normalize(
            item.Attribute("style")?.Value,
            rejectedStyleDeclarations);
        if (rejectedStyleDeclarations.Count > 0)
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Warning,
                "USER_ELEMENTS_STRUCTURE_STYLE_INVALID",
                $"用户元素 '{definitionId}' 的布局声明 '{string.Join('；', rejectedStyleDeclarations)}' 不在白名单内，已丢弃。"));
        }

        var whenText = item.Attribute("when")?.Value;
        string? when = null;
        if (!string.IsNullOrWhiteSpace(whenText))
        {
            when = UserBlockStructureRules.NormalizeWhen(whenText);
            if (when is null)
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Warning,
                    "USER_ELEMENTS_STRUCTURE_WHEN_TEMPLATE_INVALID",
                    $"用户元素 '{definitionId}' 的结构部件条件 '{whenText}' 非法，已丢弃该条件。"));
            }
        }

        // 源数据条件只接受固定枚举；非法值丢弃该条件（部件仍按无条件发射）。
        // 与 when 并存属于一个部件两个条件槽位：读取期丢弃源数据条件，
        // 保存期由 ValidateWhenSourceFields 整条阻断。
        var whenSourceText = item.Attribute("when-source")?.Value;
        string? whenSource = null;
        if (!string.IsNullOrWhiteSpace(whenSourceText))
        {
            if (UserBlockStructureRules.TryNormalizeWhenSource(whenSourceText, out var normalizedWhenSource))
            {
                whenSource = normalizedWhenSource;
            }
            else
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Warning,
                    "USER_ELEMENTS_STRUCTURE_WHEN_SOURCE_UNKNOWN",
                    $"用户元素 '{definitionId}' 的结构部件源数据条件 '{whenSourceText}' 不在允许的枚举内，已丢弃该条件。"));
            }
        }

        // 重复只接受固定派生集合名；集合非法即丢弃重复，上限非法即回落缺省值（仍有界）。
        // 结构级约束（嵌套重复、重复根、1..64 越界）由 ValidateRepeatFields 在整条结构上阻断。
        var repeatText = item.Attribute("repeat")?.Value;
        string? repeat = null;
        var repeatMax = UserBlockStructureRules.DefaultRepeatMax;
        if (!string.IsNullOrWhiteSpace(repeatText))
        {
            if (!UserBlockStructureRules.TryNormalizeRepeat(repeatText, out var normalizedRepeat))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Warning,
                    "USER_ELEMENTS_STRUCTURE_REPEAT_COLLECTION_UNKNOWN",
                    $"用户元素 '{definitionId}' 的结构部件重复集合 '{repeatText}' 不在允许的派生集合内，已丢弃该重复声明。"));
            }
            else
            {
                repeat = normalizedRepeat;
                var maxText = item.Attribute("repeat-max")?.Value;
                if (!string.IsNullOrWhiteSpace(maxText))
                {
                    if (int.TryParse(maxText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max)
                        && UserBlockStructureRules.IsValidRepeatMax(max))
                    {
                        repeatMax = max;
                    }
                    else
                    {
                        diagnostics.Add(Diagnostic(
                            DiagnosticSeverity.Warning,
                            "USER_ELEMENTS_STRUCTURE_REPEAT_MAX_INVALID",
                            $"用户元素 '{definitionId}' 的结构部件重复上限 '{maxText}' 越界，已回落到 "
                            + $"{UserBlockStructureRules.DefaultRepeatMax}。"));
                    }
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(item.Attribute("repeat-max")?.Value))
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Warning,
                "USER_ELEMENTS_STRUCTURE_REPEAT_MAX_INVALID",
                $"用户元素 '{definitionId}' 的结构部件写了 repeat-max 但缺少 repeat，已忽略该上限。"));
        }

        // 生成器名与参数：名字非法即丢弃生成器与参数；参数非法只丢参数。
        var generatorText = item.Attribute("generator")?.Value;
        var generatorParamsText = item.Attribute("generator-params")?.Value;
        string? generator = null;
        string? generatorParams = null;
        if (!string.IsNullOrWhiteSpace(generatorText))
        {
            if (!UserBlockStructureRules.TryNormalizeGenerator(generatorText, out var normalizedGenerator))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Warning,
                    "USER_ELEMENTS_STRUCTURE_GENERATOR_UNKNOWN",
                    $"用户元素 '{definitionId}' 的结构部件生成器 '{generatorText}' 不在注册表内，已丢弃该生成器声明。"));
            }
            else
            {
                generator = normalizedGenerator;
                if (!UserBlockStructureRules.TryNormalizeGeneratorParams(
                        generator,
                        generatorParamsText,
                        out var normalizedParams,
                        out var paramError))
                {
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Warning,
                        paramError!,
                        $"用户元素 '{definitionId}' 的结构部件生成器参数 '{generatorParamsText}' 非法，已丢弃参数。"));
                }
                else
                {
                    generatorParams = normalizedParams;
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(generatorParamsText))
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Warning,
                "USER_ELEMENTS_STRUCTURE_GENERATOR_PARAMS_WITHOUT_GENERATOR",
                $"用户元素 '{definitionId}' 的结构部件写了生成器参数但缺少 generator，已忽略。"));
        }

        // 引用索引：只接受固定三个派生值；合法性（叶子约束）由 ValidateRefFields 整条校验。
        var refText = item.Attribute("ref")?.Value;
        string? reference = null;
        if (!string.IsNullOrWhiteSpace(refText))
        {
            if (UserBlockStructureRules.TryNormalizeRef(refText, out var normalizedRef))
            {
                reference = normalizedRef;
            }
            else
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Warning,
                    "USER_ELEMENTS_STRUCTURE_REF_UNKNOWN",
                    $"用户元素 '{definitionId}' 的结构部件引用值 '{refText}' 不在允许的枚举内，已丢弃该引用。"));
            }
        }

        var raw = string.Equals(
            item.Attribute("raw")?.Value,
            "true",
            StringComparison.OrdinalIgnoreCase);
        if (!raw && item.Attribute("raw") is { } rawAttribute && !string.IsNullOrWhiteSpace(rawAttribute.Value)
            && !string.Equals(rawAttribute.Value, "false", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Warning,
                "USER_ELEMENTS_STRUCTURE_RAW_INVALID",
                $"用户元素 '{definitionId}' 的结构部件原文槽取值 '{rawAttribute.Value}' 不是 true/false，已忽略。"));
        }

        var textText = item.Attribute("text")?.Value;
        string? text = null;
        if (!string.IsNullOrWhiteSpace(textText))
        {
            if (UserBlockStructureRules.TryParseFieldTemplate(textText, out _))
            {
                text = textText.Trim();
            }
            else
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Warning,
                    "USER_ELEMENTS_STRUCTURE_TEXT_TEMPLATE_INVALID",
                    $"用户元素 '{definitionId}' 的结构部件槽位文本 '{textText}' 不是 '{{field}}' 形式，已丢弃。"));
            }
        }

        var href = tag is null
            ? null
            : ReadBoundAttribute(
                UserBlockStructureRules.HrefAttribute,
                item.Attribute(UserBlockStructureRules.HrefAttribute)?.Value,
                tag,
                definitionId,
                diagnostics);
        var src = tag is null
            ? null
            : ReadBoundAttribute(
                UserBlockStructureRules.SrcAttribute,
                item.Attribute(UserBlockStructureRules.SrcAttribute)?.Value,
                tag,
                definitionId,
                diagnostics);

        var children = new List<UserBlockPart>();
        foreach (var child in item.Elements())
        {
            var part = ReadPart(
                child,
                definitionId,
                diagnostics,
                declaredSlots,
                allowTransparent,
                allowUndeclaredSlots);
            if (part is not null)
            {
                children.Add(part);
            }
        }

        // schema v3 原语：命名内容槽/配方/部件标志/显式属性绑定。
        var content = ReadRecipeAttribute(
            item, "content", "USER_ELEMENTS_STRUCTURE_CONTENT_UNKNOWN",
            UserBlockStructureRules.TryNormalizeContentWriter, definitionId, diagnostics);
        var css = ReadRecipeAttribute(
            item, "css", "USER_ELEMENTS_STRUCTURE_CSS_UNKNOWN",
            UserBlockStructureRules.TryNormalizeCssRecipe, definitionId, diagnostics);
        var classRecipe = ReadRecipeAttribute(
            item, "class-recipe", "USER_ELEMENTS_STRUCTURE_CLASS_RECIPE_UNKNOWN",
            UserBlockStructureRules.TryNormalizeClassRecipe, definitionId, diagnostics);
        var tagCondition = ReadRecipeAttribute(
            item, "tag-if", "USER_ELEMENTS_STRUCTURE_TAG_CONDITION_UNKNOWN",
            UserBlockStructureRules.TryNormalizeTagCondition, definitionId, diagnostics);
        var boundAttributes = ReadAttributeBindings(item, definitionId, diagnostics);

        var role = ReadRecipeAttribute(
            item, "role", "USER_ELEMENTS_STRUCTURE_ROLE_UNKNOWN",
            UserBlockStructureRules.TryNormalizePartRole, definitionId, diagnostics);
        var sourceCondition = ReadRecipeAttribute(
            item, "source-condition", "USER_ELEMENTS_STRUCTURE_SOURCE_CONDITION_UNKNOWN",
            UserBlockStructureRules.TryNormalizeSourceCondition, definitionId, diagnostics);
        var childCollection = ReadRecipeAttribute(
            item, "child-collection", "USER_ELEMENTS_STRUCTURE_CHILD_COLLECTION_UNKNOWN",
            UserBlockStructureRules.TryNormalizeCollection, definitionId, diagnostics);
        var emitWhenCollection = ReadRecipeAttribute(
            item, "emit-when-collection", "USER_ELEMENTS_STRUCTURE_EMIT_WHEN_COLLECTION_UNKNOWN",
            UserBlockStructureRules.TryNormalizeCollection, definitionId, diagnostics);
        var emptyCollection = ReadRecipeAttribute(
            item, "empty-collection", "USER_ELEMENTS_STRUCTURE_EMPTY_COLLECTION_UNKNOWN",
            UserBlockStructureRules.TryNormalizeEmptyCollection, definitionId, diagnostics);

        if (src is not null && children.Count > 0)
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Warning,
                "USER_ELEMENTS_STRUCTURE_BIND_REQUIRES_LEAF",
                $"用户元素 '{definitionId}' 的结构部件声明了图片地址绑定但仍含子部件，已丢弃该绑定（img 必须是叶子）。"));
            src = null;
        }

        return new UserBlockPart
        {
            Part = slot,
            Tag = tag,
            Addressable = !string.Equals(
                item.Attribute("addressable")?.Value,
                "false",
                StringComparison.OrdinalIgnoreCase),
            When = when,
            WhenSource = whenSource,
            Repeat = repeat,
            RepeatMax = repeatMax,
            Generator = generator,
            GeneratorParams = generatorParams,
            Ref = reference,
            Policy = policy,
            Raw = raw,
            Text = text,
            Href = href,
            Src = src,
            Class = className,
            Style = style,
            Content = content,
            Css = css,
            ClassRecipe = classRecipe,
            TagCondition = tagCondition,
            Role = role,
            AttributesAfterIdentity = string.Equals(
                item.Attribute("attrs-after-identity")?.Value,
                "true",
                StringComparison.OrdinalIgnoreCase),
            SourceCondition = sourceCondition,
            ChildCollection = childCollection,
            EmitWhenCollection = emitWhenCollection,
            EmptyCollection = emptyCollection,
            EmitNodeIdentity = !ReadBooleanAttribute(item, "no-identity", false, definitionId, diagnostics),
            SelfClosing = ReadBooleanAttribute(item, "self-closing", false, definitionId, diagnostics),
            SuppressNodeStyle = ReadBooleanAttribute(item, "suppress-style", false, definitionId, diagnostics),
            EmitNodeStyle = ReadBooleanAttribute(item, "emit-node-style", false, definitionId, diagnostics),
            EmitNone = ReadEmitNone(item, definitionId, diagnostics),
            Attributes = boundAttributes,
            Children = children,
            // 声明面随部件一起进模型：继承合并按它判断“子是否显式写了这个字段”。
            DeclaredFields = ReadPartDeclaredFields(item),
        };
    }

    private static string? ReadRecipeAttribute(
        XElement item,
        string attributeName,
        string errorCode,
        UserBlockStructureRules.RecipeNormalizer normalize,
        string definitionId,
        List<DiagnosticMessage> diagnostics)
    {
        var text = item.Attribute(attributeName)?.Value;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (normalize(text, out var normalized))
        {
            return normalized;
        }

        diagnostics.Add(Diagnostic(
            DiagnosticSeverity.Warning,
            errorCode,
            $"用户元素 '{definitionId}' 的结构部件命名取值 {attributeName}='{text}' 不在允许的注册表内，已丢弃该声明。"));
        return null;
    }

    private static bool ReadBooleanAttribute(
        XElement item,
        string attributeName,
        bool defaultValue,
        string definitionId,
        List<DiagnosticMessage> diagnostics)
    {
        var text = item.Attribute(attributeName)?.Value;
        if (string.IsNullOrWhiteSpace(text))
        {
            return defaultValue;
        }

        if (bool.TryParse(text.Trim(), out var value))
        {
            return value;
        }

        diagnostics.Add(Diagnostic(
            DiagnosticSeverity.Warning,
            "USER_ELEMENTS_STRUCTURE_FLAG_INVALID",
            $"用户元素 '{definitionId}' 的结构部件标志 {attributeName}='{text}' 不是 true/false，已按缺省处理。"));
        return defaultValue;
    }

    private static bool ReadEmitNone(
        XElement item,
        string definitionId,
        List<DiagnosticMessage> diagnostics)
    {
        var text = item.Attribute("emit")?.Value;
        if (string.IsNullOrWhiteSpace(text)
            || string.Equals(text.Trim(), "emit", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(text.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        diagnostics.Add(Diagnostic(
            DiagnosticSeverity.Warning,
            "USER_ELEMENTS_STRUCTURE_EMIT_INVALID",
            $"用户元素 '{definitionId}' 的结构部件 emit='{text}' 不是 emit/none，已按 emit 处理。"));
        return false;
    }

    private static IReadOnlyList<UserBlockPartAttribute> ReadAttributeBindings(
        XElement item,
        string definitionId,
        List<DiagnosticMessage> diagnostics)
    {
        var text = item.Attribute("attr")?.Value;
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var bindings = new List<UserBlockPartAttribute>();
        foreach (var token in text.Split(
                     [' ', '\t', '\r', '\n', ',', ';'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (UserBlockStructureRules.TryNormalizeAttributeBinding(token, out var binding))
            {
                bindings.Add(binding);
            }
            else
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Warning,
                    "USER_ELEMENTS_STRUCTURE_ATTRIBUTE_BINDING_INVALID",
                    $"用户元素 '{definitionId}' 的结构部件属性绑定 '{token}' 不在允许的取值器/属性名内，已丢弃。"));
            }
        }

        return bindings;
    }

    /// <summary>
    /// 读取受控 URL 绑定：只接受 a→href、img→src 与纯洞 <c>{field}</c>；
    /// 声明非法时丢弃该绑定并给固定码，部件本身仍可渲染。
    /// </summary>
    private static string? ReadBoundAttribute(
        string attributeName,
        string? template,
        string tag,
        string definitionId,
        List<DiagnosticMessage> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        if (UserBlockStructureRules.TryNormalizeBoundAttribute(
                tag,
                attributeName,
                template,
                out var normalized,
                out var errorCode))
        {
            return normalized;
        }

        diagnostics.Add(Diagnostic(
            DiagnosticSeverity.Warning,
            errorCode!,
            $"用户元素 '{definitionId}' 的结构部件 {attributeName} 绑定 '{template}' 非法，已丢弃该绑定。"));
        return null;
    }

    private static IReadOnlyList<UserBlockAttributeDeclaration> ReadAttributes(
        XElement element,
        string definitionId,
        List<DiagnosticMessage> diagnostics)
    {
        var container = element.Elements()
            .FirstOrDefault(child => string.Equals(child.Name.LocalName, "attributes", StringComparison.Ordinal));
        if (container is null)
        {
            return [];
        }

        var result = new List<UserBlockAttributeDeclaration>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var attribute in container.Elements())
        {
            if (!string.Equals(attribute.Name.LocalName, "attribute", StringComparison.Ordinal))
            {
                continue;
            }

            var key = (attribute.Attribute("key")?.Value ?? string.Empty).Trim();
            if (!UserBlockRegistry.IsValidIdentifier(key))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Warning,
                    "USER_ELEMENTS_ATTRIBUTE_INVALID",
                    $"用户元素 '{definitionId}' 的属性键 '{key}' 非法，已忽略该属性声明。"));
                continue;
            }

            if (!keys.Add(key))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Warning,
                    "USER_ELEMENTS_ATTRIBUTE_DUPLICATE",
                    $"用户元素 '{definitionId}' 的属性键 '{key}' 重复，已保留首次声明。"));
                continue;
            }

            var kind = UserBlockAttributeKind.Text;
            var kindText = attribute.Attribute("kind")?.Value;
            if (!string.IsNullOrWhiteSpace(kindText)
                && !Enum.TryParse(kindText, ignoreCase: true, out kind))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Warning,
                    "USER_ELEMENTS_ATTRIBUTE_KIND_INVALID",
                    $"用户元素 '{definitionId}' 的属性 '{key}' 取值类型 '{kindText}' 未知，按 Text 处理。"));
                kind = UserBlockAttributeKind.Text;
            }

            var allowedValues = ReadAllowedValues(attribute);
            var defaultValue = attribute.Attribute("default")?.Value;

            if (kind == UserBlockAttributeKind.Enum && allowedValues.Count == 0)
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Warning,
                    "USER_ELEMENTS_ENUM_WITHOUT_VALUES",
                    $"用户元素 '{definitionId}' 的属性 '{key}' 声明为 Enum 但未给出允许值，按任意文本处理。"));
            }

            if (defaultValue is not null
                && !UserBlockAttributeRules.IsValidValue(kind, defaultValue, allowedValues))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Warning,
                    "USER_ELEMENTS_ATTRIBUTE_DEFAULT_INVALID",
                    $"用户元素 '{definitionId}' 的属性 '{key}' 默认值 '{defaultValue}' 不满足声明约束"
                    + $"（{UserBlockAttributeRules.DescribeConstraint(kind, allowedValues)}），已忽略该默认值。"));
                defaultValue = null;
            }

            result.Add(new UserBlockAttributeDeclaration
            {
                Key = key,
                DefaultValue = defaultValue,
                Kind = kind,
                Required = string.Equals(attribute.Attribute("required")?.Value, "true", StringComparison.OrdinalIgnoreCase),
                AllowedValues = allowedValues
            });
        }

        return result;
    }

    /// <summary>
    /// 读取 <c>&lt;allowed value="..." /&gt;</c> 子元素：保持声明顺序，忽略空值，
    /// 重复项只保留首次出现。
    /// </summary>
    private static IReadOnlyList<string> ReadAllowedValues(XElement attribute)
    {
        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in attribute.Elements())
        {
            if (!string.Equals(item.Name.LocalName, "allowed", StringComparison.Ordinal))
            {
                continue;
            }

            var value = item.Attribute("value")?.Value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (seen.Add(value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static UserBlockContentModel ReadContent(
        XElement element,
        string definitionId,
        List<DiagnosticMessage> diagnostics)
    {
        var container = element.Elements()
            .FirstOrDefault(child => string.Equals(child.Name.LocalName, "content", StringComparison.Ordinal));
        if (container is null)
        {
            return UserBlockContentModel.Empty;
        }

        var blocks = new List<NodeType>();
        var inlines = new List<NodeType>();

        foreach (var item in container.Elements())
        {
            var isBlock = string.Equals(item.Name.LocalName, "block", StringComparison.Ordinal);
            var isInline = string.Equals(item.Name.LocalName, "inline", StringComparison.Ordinal);
            if (!isBlock && !isInline)
            {
                continue;
            }

            var typeText = item.Attribute("type")?.Value;
            if (!Enum.TryParse(typeText, ignoreCase: true, out NodeType nodeType)
                || !Enum.IsDefined(nodeType))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Warning,
                    "USER_ELEMENTS_CONTENT_TYPE_INVALID",
                    $"用户元素 '{definitionId}' 的内容模型包含未知类型 '{typeText}'，已忽略。"));
                continue;
            }

            var target = isBlock ? blocks : inlines;
            if (!target.Contains(nodeType))
            {
                target.Add(nodeType);
            }
        }

        return new UserBlockContentModel
        {
            AllowedBlocks = blocks,
            AllowedInlines = inlines
        };
    }

    private static int ReadInt(XElement element, string attributeName, int fallback)
    {
        var text = element.Attribute(attributeName)?.Value;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    private static DiagnosticMessage Diagnostic(DiagnosticSeverity severity, string code, string message) => new()
    {
        Severity = severity,
        Code = code,
        Message = message,
        Range = SourceRange.Empty()
    };

    // ── 受控 HTML 白名单（<inline-html> / <block-html>）──────────────────

    internal const string InlineHtmlSectionName = "inline-html";
    internal const string BlockHtmlSectionName = "block-html";

    /// <summary>
    /// 读取 <c>&lt;inline-html&gt;</c> 或 <c>&lt;block-html&gt;</c> 声明。整节非法时回落空声明
    /// （等价于翻案前的“全部不保留”），单个标签/属性非法时只丢该条并留诊断，
    /// 不在代码里放一份白名单。<paramref name="sectionName"/> 只决定读哪一节与诊断文案。
    /// </summary>
    private static InlineHtmlDefinition? ReadHtmlAllowlist(
        XElement element,
        string definitionId,
        List<DiagnosticMessage> diagnostics,
        string sectionName)
    {
        var section = element.Elements().FirstOrDefault(child =>
            string.Equals(child.Name.LocalName, sectionName, StringComparison.Ordinal));
        if (section is null)
        {
            return null;
        }

        InlineHtmlDefinition Invalid(string detail)
        {
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Error,
                "INLINE_HTML_DEFINITION_INVALID",
                $"定义 '{definitionId}' 的 {sectionName} 声明无效：{detail}。该节已丢弃，行为回落到“全部不保留”。"));
            return InlineHtmlDefinition.Empty;
        }

        var unknownAttribute = section.Attribute("unknown-attribute")?.Value?.Trim();
        if (unknownAttribute is not null
            && !string.Equals(unknownAttribute, "drop", StringComparison.OrdinalIgnoreCase))
        {
            return Invalid($"unknown-attribute='{unknownAttribute}' 只能取 drop");
        }

        var defaultDisposition = InlineHtmlDisposition.Unwrap;
        var dispositionText = section.Attribute("default-disposition")?.Value?.Trim();
        if (dispositionText is not null && !TryParseDisposition(dispositionText, out defaultDisposition))
        {
            return Invalid($"default-disposition='{dispositionText}' 不是 allow/unwrap/strip");
        }

        var maxDepth = ReadInt(section, "max-depth", InlineHtmlDefinition.DefaultMaxDepth);
        var maxAttributes = ReadInt(section, "max-attributes", InlineHtmlDefinition.DefaultMaxAttributes);
        var maxAttributeLength = ReadInt(section, "max-attribute-length", InlineHtmlDefinition.DefaultMaxAttributeLength);
        var maxTagNameLength = ReadInt(section, "max-tag-name-length", InlineHtmlDefinition.DefaultMaxTagNameLength);

        if (maxDepth is < 1 or > 32)
        {
            return Invalid($"max-depth={maxDepth} 越界（1~32）");
        }

        if (maxAttributes is < 1 or > 32)
        {
            return Invalid($"max-attributes={maxAttributes} 越界（1~32）");
        }

        if (maxAttributeLength is < 1 or > 4096)
        {
            return Invalid($"max-attribute-length={maxAttributeLength} 越界（1~4096）");
        }

        if (maxTagNameLength is < 1 or > 32)
        {
            return Invalid($"max-tag-name-length={maxTagNameLength} 越界（1~32）");
        }

        var tags = new List<InlineHtmlTagDefinition>();
        foreach (var tagElement in section.Elements())
        {
            if (!string.Equals(tagElement.Name.LocalName, "tag", StringComparison.Ordinal))
            {
                continue;
            }

            var tagName = tagElement.Attribute("name")?.Value?.Trim();
            if (!InlineHtmlRules.IsValidTagName(tagName, maxTagNameLength))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Error,
                    "INLINE_HTML_DEFINITION_INVALID",
                    $"定义 '{definitionId}' 的 {sectionName} 标签名 '{tagName}' 非法（字母开头，仅字母/数字/连字符，最长 {maxTagNameLength}），该条已忽略。"));
                continue;
            }

            // 标签自身缺省是“放行”；default-disposition 管的是“未声明的标签”。
            var tagDisposition = InlineHtmlDisposition.Allow;
            var tagDispositionText = tagElement.Attribute("disposition")?.Value?.Trim();
            if (tagDispositionText is not null && !TryParseDisposition(tagDispositionText, out tagDisposition))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Error,
                    "INLINE_HTML_DEFINITION_INVALID",
                    $"定义 '{definitionId}' 的 {sectionName} 标签 '{tagName}' 的 disposition='{tagDispositionText}' 非法，该条已忽略。"));
                continue;
            }

            // 机制地板：script/iframe/svg 这类标签声明成 allow 也只是“看起来放行”，
            // 解析期仍会被强制 strip。这里在装载期就把话说清楚：报错且该条不装载。
            // 声明成 strip/unwrap 是合法用法（显式收窄），不受此限。
            if (tagDisposition == InlineHtmlDisposition.Allow
                && InlineHtmlRules.IsForbiddenTag(tagName!))
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticSeverity.Error,
                    "INLINE_HTML_DEFINITION_INVALID",
                    $"定义 '{definitionId}' 的 {sectionName} 标签 '{tagName}' 被机制地板永久禁止放行，该条不装载。"));
                continue;
            }

            var isVoid = string.Equals(
                tagElement.Attribute("void")?.Value?.Trim(),
                "true",
                StringComparison.OrdinalIgnoreCase);

            // style 不走属性声明，只能由标签开关放行；开关值只认 true/false。
            var styleLayout = false;
            var styleLayoutText = tagElement.Attribute("style-layout")?.Value?.Trim();
            if (styleLayoutText is not null)
            {
                if (string.Equals(styleLayoutText, "true", StringComparison.OrdinalIgnoreCase))
                {
                    styleLayout = true;
                }
                else if (!string.Equals(styleLayoutText, "false", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Error,
                        "INLINE_HTML_DEFINITION_INVALID",
                        $"定义 '{definitionId}' 的 {sectionName} 标签 '{tagName}' 的 style-layout='{styleLayoutText}' 非法（只认 true/false），该条已忽略。"));
                    continue;
                }
            }

            var attributes = new List<InlineHtmlAttributeDefinition>();
            foreach (var attributeElement in tagElement.Elements())
            {
                if (!string.Equals(attributeElement.Name.LocalName, "attribute", StringComparison.Ordinal))
                {
                    continue;
                }

                var attributeName = attributeElement.Attribute("name")?.Value?.Trim()?.ToLowerInvariant();
                if (!InlineHtmlRules.IsValidAttributeName(attributeName))
                {
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Error,
                        "INLINE_HTML_DEFINITION_INVALID",
                        $"定义 '{definitionId}' 的 {sectionName} 标签 '{tagName}' 的属性名 '{attributeName}' 非法，该条已忽略。"));
                    continue;
                }

                // style 不走属性声明：只能由标签上的 style-layout 开关放行，
                // 且值必须过布局子集校验；声明成实例属性一律不装载。
                if (InlineHtmlRules.IsStyleAttribute(attributeName))
                {
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Error,
                        "INLINE_HTML_DEFINITION_INVALID",
                        $"定义 '{definitionId}' 的 {sectionName} 标签 '{tagName}' 把 style 声明成实例属性；style 只能通过标签的 style-layout 开关放行，该条已忽略。"));
                    continue;
                }

                // 机制地板：危险属性（on*/class/id/data-* 等）即使声明了也不收。
                if (InlineHtmlRules.IsForbiddenAttribute(attributeName!))
                {
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Error,
                        "INLINE_HTML_DEFINITION_INVALID",
                        $"定义 '{definitionId}' 的 {sectionName} 标签 '{tagName}' 声明了机制地板禁止的属性 '{attributeName}'，该条已忽略。"));
                    continue;
                }

                // 机制层常量通道：值由定义给出，不接受实例值。
                var valueSource = attributeElement.Attribute("value-source")?.Value?.Trim()?.ToLowerInvariant()
                    ?? InlineHtmlAttributeDefinition.InstanceValueSource;
                if (!string.Equals(valueSource, InlineHtmlAttributeDefinition.InstanceValueSource, StringComparison.Ordinal)
                    && !string.Equals(valueSource, InlineHtmlAttributeDefinition.ConstantValueSource, StringComparison.Ordinal))
                {
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Error,
                        "INLINE_HTML_DEFINITION_INVALID",
                        $"定义 '{definitionId}' 的 {sectionName} 属性 '{attributeName}' 的 value-source='{valueSource}' 非法（instance/constant），该条已忽略。"));
                    continue;
                }

                if (string.Equals(valueSource, InlineHtmlAttributeDefinition.ConstantValueSource, StringComparison.Ordinal))
                {
                    var constant = attributeElement.Attribute("constant")?.Value?.Trim();
                    if (!InlineHtmlRules.IsSafeConstantValue(constant)
                        || constant!.Length > maxAttributeLength)
                    {
                        diagnostics.Add(Diagnostic(
                            DiagnosticSeverity.Error,
                            "INLINE_HTML_DEFINITION_INVALID",
                            $"定义 '{definitionId}' 的 {sectionName} 属性 '{attributeName}' 的 constant 值缺失、过长或含不安全字符，该条已忽略。"));
                        continue;
                    }

                    var when = attributeElement.Attribute("when")?.Value?.Trim()?.ToLowerInvariant();
                    if (when is not null
                        && !string.Equals(when, InlineHtmlAttributeDefinition.AbsoluteHttpCondition, StringComparison.Ordinal))
                    {
                        diagnostics.Add(Diagnostic(
                            DiagnosticSeverity.Error,
                            "INLINE_HTML_DEFINITION_INVALID",
                            $"定义 '{definitionId}' 的 {sectionName} 属性 '{attributeName}' 的 when='{when}' 非法（只认 absolute-http），该条已忽略。"));
                        continue;
                    }

                    attributes.Add(new InlineHtmlAttributeDefinition
                    {
                        Name = attributeName!,
                        Constant = constant,
                        ConstantWhen = when,
                    });
                    continue;
                }

                // target / rel 只能走常量通道：作者写实例值一律拒绝。
                if (InlineHtmlRules.IsMechanismConstantAttribute(attributeName))
                {
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Error,
                        "INLINE_HTML_DEFINITION_INVALID",
                        $"定义 '{definitionId}' 的 {sectionName} 属性 '{attributeName}' 只能以 value-source=\"constant\" 声明，作者不可写实例值，该条已忽略。"));
                    continue;
                }

                var kind = attributeElement.Attribute("kind")?.Value?.Trim()?.ToLowerInvariant()
                    ?? InlineHtmlAttributeDefinition.TextKind;
                if (!string.Equals(kind, InlineHtmlAttributeDefinition.TextKind, StringComparison.Ordinal)
                    && !string.Equals(kind, InlineHtmlAttributeDefinition.UrlKind, StringComparison.Ordinal)
                    && !string.Equals(kind, InlineHtmlAttributeDefinition.IntegerKind, StringComparison.Ordinal))
                {
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Error,
                        "INLINE_HTML_DEFINITION_INVALID",
                        $"定义 '{definitionId}' 的 {sectionName} 属性 '{attributeName}' 的 kind='{kind}' 非法（text/url/integer），该条已忽略。"));
                    continue;
                }

                var min = ReadInt(attributeElement, "min", InlineHtmlAttributeDefinition.DefaultIntegerMin);
                var max = ReadInt(attributeElement, "max", InlineHtmlAttributeDefinition.DefaultIntegerMax);
                var isInteger = string.Equals(kind, InlineHtmlAttributeDefinition.IntegerKind, StringComparison.Ordinal);
                if (isInteger && (min < 0 || max <= min || max > 1_000_000))
                {
                    diagnostics.Add(Diagnostic(
                        DiagnosticSeverity.Error,
                        "INLINE_HTML_DEFINITION_INVALID",
                        $"定义 '{definitionId}' 的 {sectionName} 属性 '{attributeName}' 的 min/max 越界（0≤min<max≤1000000），该条已忽略。"));
                    continue;
                }

                var protocols = new List<string>();
                var protocolsText = attributeElement.Attribute("protocols")?.Value;
                if (!string.IsNullOrWhiteSpace(protocolsText))
                {
                    foreach (var part in protocolsText.Split(','))
                    {
                        var protocol = part.Trim().ToLowerInvariant();
                        if (protocol.Length == 0)
                        {
                            continue;
                        }

                        if (!InlineHtmlRules.IsValidProtocolName(protocol))
                        {
                            diagnostics.Add(Diagnostic(
                                DiagnosticSeverity.Error,
                                "INLINE_HTML_DEFINITION_INVALID",
                                $"定义 '{definitionId}' 的 {sectionName} 属性 '{attributeName}' 的协议 '{protocol}' 非法，该条已忽略。"));
                            protocol = string.Empty;
                            continue;
                        }

                        // 机制地板：javascript/data/vbscript 等永远不可放行。
                        if (InlineHtmlRules.IsForbiddenProtocol(protocol))
                        {
                            diagnostics.Add(Diagnostic(
                                DiagnosticSeverity.Error,
                                "INLINE_HTML_DEFINITION_INVALID",
                                $"定义 '{definitionId}' 的 {sectionName} 属性 '{attributeName}' 声明了机制地板禁止的协议 '{protocol}'，该条已剔除。"));
                            protocol = string.Empty;
                            continue;
                        }

                        if (!protocols.Contains(protocol, StringComparer.Ordinal))
                        {
                            protocols.Add(protocol);
                        }
                    }
                }

                attributes.Add(new InlineHtmlAttributeDefinition
                {
                    Name = attributeName!,
                    Kind = kind,
                    Protocols = protocols,
                    MaxLength = ReadInt(attributeElement, "max-length", 0),
                    Min = min,
                    Max = max,
                });
            }

            tags.Add(new InlineHtmlTagDefinition
            {
                Name = tagName!.ToLowerInvariant(),
                Disposition = tagDisposition,
                IsVoid = isVoid,
                StyleLayout = styleLayout,
                Attributes = attributes,
            });
        }

        return new InlineHtmlDefinition
        {
            DefaultDisposition = defaultDisposition,
            MaxDepth = maxDepth,
            MaxAttributes = maxAttributes,
            MaxAttributeLength = maxAttributeLength,
            MaxTagNameLength = maxTagNameLength,
            Tags = tags,
        };
    }

    private static bool TryParseDisposition(string text, out InlineHtmlDisposition disposition)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "allow":
                disposition = InlineHtmlDisposition.Allow;
                return true;
            case "unwrap":
                disposition = InlineHtmlDisposition.Unwrap;
                return true;
            case "strip":
                disposition = InlineHtmlDisposition.Strip;
                return true;
            default:
                disposition = InlineHtmlDisposition.Unwrap;
                return false;
        }
    }

    /// <summary>
    /// 写回 <c>&lt;inline-html&gt;</c> / <c>&lt;block-html&gt;</c>：缺省值不落字节，
    /// 保证 save→load→save 稳定。
    /// </summary>
    private static void WriteHtmlAllowlist(
        XmlWriter writer,
        InlineHtmlDefinition? definition,
        string sectionName)
    {
        if (definition is null)
        {
            return;
        }

        writer.WriteStartElement(sectionName);

        if (definition.DefaultDisposition != InlineHtmlDisposition.Unwrap)
        {
            writer.WriteAttributeString(
                "default-disposition",
                definition.DefaultDisposition == InlineHtmlDisposition.Allow ? "allow" : "strip");
        }

        WriteIntIfNotDefault(writer, "max-depth", definition.MaxDepth, InlineHtmlDefinition.DefaultMaxDepth);
        WriteIntIfNotDefault(writer, "max-attributes", definition.MaxAttributes, InlineHtmlDefinition.DefaultMaxAttributes);
        WriteIntIfNotDefault(
            writer,
            "max-attribute-length",
            definition.MaxAttributeLength,
            InlineHtmlDefinition.DefaultMaxAttributeLength);
        WriteIntIfNotDefault(
            writer,
            "max-tag-name-length",
            definition.MaxTagNameLength,
            InlineHtmlDefinition.DefaultMaxTagNameLength);

        foreach (var tag in definition.Tags)
        {
            writer.WriteStartElement("tag");
            writer.WriteAttributeString("name", tag.Name);

            if (tag.Disposition != InlineHtmlDisposition.Allow)
            {
                writer.WriteAttributeString(
                    "disposition",
                    tag.Disposition == InlineHtmlDisposition.Unwrap ? "unwrap" : "strip");
            }

            if (tag.IsVoid)
            {
                writer.WriteAttributeString("void", "true");
            }

            if (tag.StyleLayout)
            {
                writer.WriteAttributeString("style-layout", "true");
            }

            foreach (var attribute in tag.Attributes)
            {
                writer.WriteStartElement("attribute");
                writer.WriteAttributeString("name", attribute.Name);

                if (attribute.IsConstant)
                {
                    writer.WriteAttributeString("value-source", InlineHtmlAttributeDefinition.ConstantValueSource);
                    writer.WriteAttributeString("constant", attribute.Constant!);

                    if (attribute.ConstantWhen is not null)
                    {
                        writer.WriteAttributeString("when", attribute.ConstantWhen);
                    }

                    writer.WriteEndElement();
                    continue;
                }

                if (!string.Equals(attribute.Kind, InlineHtmlAttributeDefinition.TextKind, StringComparison.Ordinal))
                {
                    writer.WriteAttributeString("kind", attribute.Kind);
                }

                if (attribute.Protocols.Count > 0)
                {
                    writer.WriteAttributeString("protocols", string.Join(",", attribute.Protocols));
                }

                if (attribute.MaxLength > 0)
                {
                    writer.WriteAttributeString(
                        "max-length",
                        attribute.MaxLength.ToString(CultureInfo.InvariantCulture));
                }

                if (attribute.IsInteger)
                {
                    WriteIntIfNotDefault(
                        writer,
                        "min",
                        attribute.Min,
                        InlineHtmlAttributeDefinition.DefaultIntegerMin);
                    WriteIntIfNotDefault(
                        writer,
                        "max",
                        attribute.Max,
                        InlineHtmlAttributeDefinition.DefaultIntegerMax);
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteIntIfNotDefault(XmlWriter writer, string name, int value, int defaultValue)
    {
        if (value != defaultValue)
        {
            writer.WriteAttributeString(name, value.ToString(CultureInfo.InvariantCulture));
        }
    }
}
