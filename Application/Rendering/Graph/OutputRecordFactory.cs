using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Html;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Rendering.Graph;

/// <summary>
/// 在求值出口按产物纯派生输出记录。工厂只读：不写回图、不写缓存、不改变 HTML 与块签名。
/// <para>
/// 取法约定：<c>blockSig</c> 只从 emit 产物里读，绝不重算；CSS 值分别取
/// template（declared）/ bindings（bound）/ style（effective）三个阶段的产物；
/// 逐属性 CSS 来源走 <see cref="StyleResolver.ResolveProvenance"/> 的同层序影子重放。
/// </para>
/// </summary>
public static class OutputRecordFactory
{
    private const string AttributePositionUnavailable = "parser.attribute-position-unavailable";

    private static readonly Regex GlyphHexRegex = new(
        "&#x([0-9a-fA-F]+);",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DecimalGlyphRegex = new(
        "&#([0-9]+);",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CssVariableRegex = new(
        @"--([A-Za-z0-9-]+)\s*:\s*([^;]+);",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static OutputRecord Build(
        GraphEvaluationResult result,
        NodeGraph graph,
        MarkdownNode node,
        MarkdownDocument document,
        ElementDefinitionFingerprintSet? definitions = null,
        string? sourceFile = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(document);

        definitions ??= ElementDefinitionFingerprint.ComputeSet(
            document.StyleSheet,
            document.EffectiveUserBlockDefinitions,
            document.GraphStages);

        var kind = ElementKind.Resolve(node);
        var definition = HtmlRenderer.ResolveTypeDefinition(document.StyleSheet, node.Type, kind);
        var diagnostics = new List<OutputRecordDiagnostic>();
        diagnostics.AddRange(MapDiagnostics(result));

        // 汇点口径与求值器共用：顶层 emit 优先；被复合节点顶替时读包装节点的 html 输出；
        // 歧义时不选任何一个，产物留空并沿用求值阶段已报的 graph.composite_sink_ambiguous。
        var sink = GraphSinkResolver.Resolve(graph, document.GraphStages);
        var emitNode = sink.Node;
        var html = emitNode is null ? null : ReadHtml(result, emitNode.Id);
        var rootAttributes = ParseFirstTagAttributes(html);

        // 签名只读产物：product 里没有就留空并报错，不重算。
        var blockSig = rootAttributes.TryGetValue("data-block-sig", out var sig) ? sig ?? string.Empty : string.Empty;
        if (string.IsNullOrEmpty(blockSig))
        {
            diagnostics.Add(new OutputRecordDiagnostic(
                "error",
                "output_record.block_sig_unavailable",
                "发射产物中没有可读取的 data-block-sig；记录不使用重算值。",
                new OutputRecordDiagnosticAnchor(GraphNode: emitNode?.Id, Port: "html")));
        }

        definitions.TryGetTypeFingerprint(node.Type, kind, out var fingerprint);
        if (string.IsNullOrEmpty(fingerprint))
        {
            diagnostics.Add(new OutputRecordDiagnostic(
                "info",
                "output_record.definition_fingerprint_missing",
                $"类型 {node.Type} 未登记定义指纹，记录无法判断定义是否变化。"));
        }

        var templateParts = ReadParts(result, graph, NodeGraphStageLibrary.TemplateStage, "parts");
        var bindingParts = ReadParts(result, graph, NodeGraphStageLibrary.BindingsStage, "parts");
        var styledParts = ReadParts(result, graph, NodeGraphStageLibrary.StyleStage, "styled");
        var effectiveParts = styledParts.Count > 0
            ? styledParts
            : bindingParts.Count > 0
                ? bindingParts
                : templateParts;

        var presentation = new List<OutputRecordPresentation>();
        CollectKindRootClass(node, kind, effectiveParts, templateParts, graph, presentation, diagnostics);
        CollectDefaultIcon(node, templateParts, kind, graph, presentation);
        CollectBindingProvenance(node, definition, templateParts, bindingParts, graph, presentation, diagnostics);

        var attributes = BuildAttributes(node, definition, kind, presentation);
        var cssProperties = BuildCssProvenance(node, document);
        var parts = BuildPartTree(
            effectiveParts,
            IndexByPath(templateParts),
            IndexByPath(bindingParts),
            rootAttributes,
            cssProperties);

        return new OutputRecord(
            OutputRecord.SchemaVersion,
            BuildIdentity(result, graph, node, kind, blockSig, definitions, fingerprint),
            parts,
            attributes,
            presentation,
            diagnostics,
            BuildAnchors(graph, node, kind, sourceFile));
    }

    private static OutputRecordIdentity BuildIdentity(
        GraphEvaluationResult result,
        NodeGraph graph,
        MarkdownNode node,
        string? kind,
        string blockSig,
        ElementDefinitionFingerprintSet definitions,
        string? fingerprint) =>
        new(
            node.Id,
            node.Type.ToString(),
            kind,
            new OutputRecordSourceRange(node.Range.StartLine, node.Range.EndLine),
            blockSig,
            new OutputRecordDefinitions(
                string.IsNullOrEmpty(fingerprint) ? null : fingerprint,
                ElementDefinitionFingerprint.Version,
                UserBlockSyntaxFingerprint.Version,
                definitions.Aggregate,
                definitions.DocumentLevel),
            new OutputRecordEvaluation(
                DescribeGraph(graph, node.Type, kind),
                result.Status.ToString(),
                result.ExecutionOrder.ToArray()));

    private static string DescribeGraph(NodeGraph graph, NodeType type, string? kind)
    {
        if (!string.IsNullOrWhiteSpace(graph.Id) && !IsGeneratedId(graph.Id))
        {
            return graph.Id;
        }

        var typeToken = type.ToString().ToLowerInvariant();
        var kindToken = ElementKind.NormalizeToken(kind);
        return kindToken is null ? $"builtin:{typeToken}" : $"builtin:{typeToken}/{kindToken}";
    }

    private static bool IsGeneratedId(string id) =>
        id.Length == 32 && id.All(char.IsAsciiHexDigit);

    private static IReadOnlyList<OutputRecordDiagnostic> MapDiagnostics(GraphEvaluationResult result)
    {
        var mapped = new List<OutputRecordDiagnostic>(result.Diagnostics.Count);
        foreach (var diagnostic in result.Diagnostics)
        {
            mapped.Add(new OutputRecordDiagnostic(
                IsErrorCode(diagnostic.Code) ? "error" : "warning",
                diagnostic.Code,
                diagnostic.Message,
                new OutputRecordDiagnosticAnchor(
                    GraphNode: diagnostic.NodeId,
                    Port: diagnostic.PortName)));
        }

        return mapped;
    }

    private static bool IsErrorCode(string code) =>
        code is "graph.duplicate_node" or "graph.unknown_node" or "graph.unknown_port"
            or "graph.edge_kind_mismatch" or "graph.input_occupied" or "graph.cycle"
            or "graph.unknown_definition" or "graph.node_failed"
            or GraphSinkResolver.AmbiguousCode;

    private static string? ReadHtml(GraphEvaluationResult result, string nodeId)
    {
        var value = result.GetOutput(nodeId, "html");
        return value.Kind == GraphValueKind.Html ? value.AsHtml() : null;
    }

    private static IReadOnlyList<GraphPart> ReadParts(
        GraphEvaluationResult result,
        NodeGraph graph,
        string stageDefinitionId,
        string portName)
    {
        var stage = graph.Nodes.FirstOrDefault(item =>
            string.Equals(item.DefinitionId, stageDefinitionId, StringComparison.Ordinal));
        if (stage is null)
        {
            return [];
        }

        var value = result.GetOutput(stage.Id, portName);
        return value.Kind is GraphValueKind.PartTree or GraphValueKind.StyledPartTree
            ? value.AsParts()
            : [];
    }

    // ── 部件树 ────────────────────────────────────────────────────────────

    private static IReadOnlyList<OutputRecordCssProperty> BuildCssProvenance(
        MarkdownNode node,
        MarkdownDocument document)
    {
        var resolver = new StyleResolver(document);
        return resolver.ResolveProvenance(node)
            .Select(item => new OutputRecordCssProperty(item.Property, item.Value, item.Source, item.Chain))
            .ToArray();
    }

    private static IReadOnlyList<OutputRecordPart> BuildPartTree(
        IReadOnlyList<GraphPart> parts,
        IReadOnlyDictionary<string, GraphPart> declared,
        IReadOnlyDictionary<string, GraphPart> bound,
        IReadOnlyDictionary<string, string?> rootAttributes,
        IReadOnlyList<OutputRecordCssProperty> cssProperties)
    {
        var result = new List<OutputRecordPart>(parts.Count);
        foreach (var part in parts)
        {
            result.Add(BuildPart(part, parentPath: null, declared, bound, rootAttributes, cssProperties));
        }

        return result;
    }

    private static OutputRecordPart BuildPart(
        GraphPart part,
        string? parentPath,
        IReadOnlyDictionary<string, GraphPart> declared,
        IReadOnlyDictionary<string, GraphPart> bound,
        IReadOnlyDictionary<string, string?> rootAttributes,
        IReadOnlyList<OutputRecordCssProperty> cssProperties)
    {
        var path = parentPath is null ? part.Name : parentPath + "/" + part.Name;
        var isRoot = string.Equals(path, PartTokens.Root, StringComparison.Ordinal);

        declared.TryGetValue(path, out var declaredPart);
        bound.TryGetValue(path, out var boundPart);

        var classes = isRoot && rootAttributes.TryGetValue("class", out var rootClass)
            ? SplitClasses(rootClass)
            : SplitClasses(part.CssClass);

        var attrs = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (isRoot && rootAttributes.Count > 0)
        {
            foreach (var (name, value) in rootAttributes)
            {
                if (name is "class" or "style")
                {
                    continue;
                }

                attrs[name] = value;
            }
        }
        else
        {
            attrs["data-part"] = part.Name;
            if (part.Attributes is not null)
            {
                foreach (var attribute in part.Attributes)
                {
                    attrs[attribute.Name] = attribute.Value;
                }
            }
        }

        var children = new List<OutputRecordPart>(part.Children.Count);
        foreach (var child in part.Children)
        {
            children.Add(BuildPart(child, path, declared, bound, rootAttributes, []));
        }

        return new OutputRecordPart(
            path,
            part.Tag,
            classes,
            attrs,
            new OutputRecordCss(
                declaredPart?.StyleCss,
                boundPart?.BindingCss,
                part.StyleCss,
                isRoot ? cssProperties : []),
            ReadContent(part),
            children);
    }

    private static OutputRecordContent? ReadContent(GraphPart part)
    {
        if (part.SlotText is not null)
        {
            return new OutputRecordContent("text", part.SlotText);
        }

        return part.SlotHtml is not null ? new OutputRecordContent("html", part.SlotHtml) : null;
    }

    private static Dictionary<string, GraphPart> IndexByPath(IReadOnlyList<GraphPart> parts)
    {
        var map = new Dictionary<string, GraphPart>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            IndexPart(part, parentPath: null, map);
        }

        return map;
    }

    private static void IndexPart(GraphPart part, string? parentPath, IDictionary<string, GraphPart> map)
    {
        var path = parentPath is null ? part.Name : parentPath + "/" + part.Name;
        map[path] = part;
        foreach (var child in part.Children)
        {
            IndexPart(child, path, map);
        }
    }

    // ── presentation：驱动层溯源 ──────────────────────────────────────────

    private static void CollectKindRootClass(
        MarkdownNode node,
        string? kind,
        IReadOnlyList<GraphPart> effectiveParts,
        IReadOnlyList<GraphPart> declaredParts,
        NodeGraph graph,
        ICollection<OutputRecordPresentation> presentation,
        ICollection<OutputRecordDiagnostic> diagnostics)
    {
        if (kind is null || effectiveParts.Count == 0)
        {
            return;
        }

        var normalized = ElementKind.NormalizeToken(kind);
        if (normalized is null)
        {
            return;
        }

        // 只认渲染器用过的那个 token：找不到就报出来，不臆造一条“应该有的”类名。
        var token = "custom-" + normalized;
        var root = effectiveParts[0];
        var classes = SplitClasses(root.CssClass);
        if (!classes.Contains(token, StringComparer.Ordinal))
        {
            if (node.Type == NodeType.Custom)
            {
                diagnostics.Add(new OutputRecordDiagnostic(
                    "info",
                    "output_record.kind_root_class_unattributed",
                    $"根部件类名中没有找到 kind 根类「{token}」，记录不臆造驱动来源。",
                    new OutputRecordDiagnosticAnchor(Part: PartTokens.Root)));
            }

            return;
        }

        var declaredClasses = declaredParts.Count > 0
            ? SplitClasses(declaredParts[0].CssClass)
            : classes;
        var baseClasses = declaredClasses
            .Where(item => !string.Equals(item, token, StringComparison.Ordinal))
            .ToArray();

        presentation.Add(new OutputRecordPresentation(
            PartTokens.Root,
            "class",
            baseClasses.Length > 0 ? string.Join(' ', baseClasses) : null,
            string.Join(' ', classes),
            "driver",
            new OutputRecordPresentationRef(
                "kind-root-class",
                DefinitionId: kind,
                Name: token,
                Graph: StageGraphRef(graph, NodeGraphStageLibrary.TemplateStage, "parts"))));
    }

    private static void CollectDefaultIcon(
        MarkdownNode node,
        IReadOnlyList<GraphPart> templateParts,
        string? kind,
        NodeGraph graph,
        ICollection<OutputRecordPresentation> presentation)
    {
        // 用户指定了 icon 时，图标来自属性而不是渲染器的默认值，这里不冒充驱动。
        if (node.Attributes.ContainsKey(MarkdownAttributes.Icon))
        {
            return;
        }

        var icon = FindFirstPart(templateParts, parentPath: null, part => part.Slot == GraphPartSlot.Icon);
        if (icon is not { } iconPart)
        {
            return;
        }

        var glyph = ExtractGlyph(iconPart.Part.SlotHtml);
        if (glyph is null)
        {
            return;
        }

        presentation.Add(new OutputRecordPresentation(
            iconPart.Path,
            "glyph",
            null,
            glyph,
            "driver",
            new OutputRecordPresentationRef(
                "element-default-icon",
                DefinitionId: kind,
                Graph: StageGraphRef(graph, NodeGraphStageLibrary.TemplateStage, "parts"))));
    }

    private static void CollectBindingProvenance(
        MarkdownNode node,
        StyleDefinition? definition,
        IReadOnlyList<GraphPart> templateParts,
        IReadOnlyList<GraphPart> bindingParts,
        NodeGraph graph,
        ICollection<OutputRecordPresentation> presentation,
        ICollection<OutputRecordDiagnostic> diagnostics)
    {
        if (definition is null || bindingParts.Count == 0)
        {
            return;
        }

        var declaredMap = IndexByPath(templateParts);
        var boundMap = IndexByPath(bindingParts);

        foreach (var (path, boundPart) in boundMap)
        {
            declaredMap.TryGetValue(path, out var declaredPart);
            var graphRef = StageGraphRef(graph, NodeGraphStageLibrary.BindingsStage, "parts");

            CollectClassBindings(node, definition, path, declaredPart, boundPart, graphRef, presentation, diagnostics);
            CollectCssVariableBindings(node, definition, path, declaredPart, boundPart, graphRef, presentation, diagnostics);
            CollectDataAttributeBindings(node, definition, path, declaredPart, boundPart, graphRef, presentation, diagnostics);
        }
    }

    private static void CollectClassBindings(
        MarkdownNode node,
        StyleDefinition definition,
        string path,
        GraphPart? declaredPart,
        GraphPart boundPart,
        OutputRecordGraphRef? graphRef,
        ICollection<OutputRecordPresentation> presentation,
        ICollection<OutputRecordDiagnostic> diagnostics)
    {
        if (string.Equals(declaredPart?.CssClass, boundPart.CssClass, StringComparison.Ordinal))
        {
            return;
        }

        var baseTokens = SplitClasses(declaredPart?.CssClass);
        var boundTokens = SplitClasses(boundPart.CssClass);
        var added = boundTokens
            .Where(token => !baseTokens.Contains(token, StringComparer.Ordinal))
            .ToArray();

        foreach (var token in added)
        {
            var key = FindDeclarationKey(
                node,
                definition,
                path,
                ElementAttributeBindingTarget.ClassToken,
                preferredName: token);

            presentation.Add(new OutputRecordPresentation(
                path,
                "class",
                declaredPart?.CssClass,
                boundPart.CssClass,
                "driver",
                new OutputRecordPresentationRef(
                    "element-binding",
                    DefinitionId: definition.Id,
                    DeclarationKey: key,
                    Name: token,
                    Target: "class",
                    Graph: graphRef)));

            if (key is null)
            {
                diagnostics.Add(new OutputRecordDiagnostic(
                    "warning",
                    "output_record.binding_unattributed",
                    $"部件「{path}」新增类名「{token}」找不到对应的绑定声明。",
                    new OutputRecordDiagnosticAnchor(Part: path, GraphNode: graphRef?.Node, Port: graphRef?.Port)));
            }
        }
    }

    private static void CollectCssVariableBindings(
        MarkdownNode node,
        StyleDefinition definition,
        string path,
        GraphPart? declaredPart,
        GraphPart boundPart,
        OutputRecordGraphRef? graphRef,
        ICollection<OutputRecordPresentation> presentation,
        ICollection<OutputRecordDiagnostic> diagnostics)
    {
        var declaredVariables = ParseCssVariables(declaredPart?.BindingCss);
        var boundVariables = ParseCssVariables(boundPart.BindingCss);

        foreach (var (name, value) in boundVariables)
        {
            if (declaredVariables.TryGetValue(name, out var previous) &&
                string.Equals(previous, value, StringComparison.Ordinal))
            {
                continue;
            }

            var key = FindDeclarationKey(
                node,
                definition,
                path,
                ElementAttributeBindingTarget.CssVar,
                preferredName: name);

            presentation.Add(new OutputRecordPresentation(
                path,
                "css-var",
                previous is null ? null : $"{name}: {previous};",
                $"{name}: {value};",
                "driver",
                new OutputRecordPresentationRef(
                    "element-binding",
                    DefinitionId: definition.Id,
                    DeclarationKey: key,
                    Name: name,
                    Target: "css-var",
                    Graph: graphRef)));

            if (key is null)
            {
                diagnostics.Add(new OutputRecordDiagnostic(
                    "warning",
                    "output_record.binding_unattributed",
                    $"部件「{path}」写入的 CSS 变量「{name}」找不到对应的绑定声明。",
                    new OutputRecordDiagnosticAnchor(Part: path, GraphNode: graphRef?.Node, Port: graphRef?.Port)));
            }
        }
    }

    private static void CollectDataAttributeBindings(
        MarkdownNode node,
        StyleDefinition definition,
        string path,
        GraphPart? declaredPart,
        GraphPart boundPart,
        OutputRecordGraphRef? graphRef,
        ICollection<OutputRecordPresentation> presentation,
        ICollection<OutputRecordDiagnostic> diagnostics)
    {
        var declaredAttributes = ToAttributeMap(declaredPart?.Attributes);
        var boundAttributes = ToAttributeMap(boundPart.Attributes);

        foreach (var (name, value) in boundAttributes)
        {
            if (declaredAttributes.TryGetValue(name, out var previous) &&
                string.Equals(previous, value, StringComparison.Ordinal))
            {
                continue;
            }

            var key = FindDeclarationKey(
                node,
                definition,
                path,
                ElementAttributeBindingTarget.DataAttribute,
                preferredName: name);

            presentation.Add(new OutputRecordPresentation(
                path,
                "attribute",
                previous,
                value ?? string.Empty,
                "driver",
                new OutputRecordPresentationRef(
                    "element-binding",
                    DefinitionId: definition.Id,
                    DeclarationKey: key,
                    Name: name,
                    Target: "data-attribute",
                    Graph: graphRef)));

            if (key is null)
            {
                diagnostics.Add(new OutputRecordDiagnostic(
                    "warning",
                    "output_record.binding_unattributed",
                    $"部件「{path}」写入的 data 属性「{name}」找不到对应的绑定声明。",
                    new OutputRecordDiagnosticAnchor(Part: path, GraphNode: graphRef?.Node, Port: graphRef?.Port)));
            }
        }
    }

    private static string? FindDeclarationKey(
        MarkdownNode node,
        StyleDefinition definition,
        string partPath,
        ElementAttributeBindingTarget target,
        string? preferredName)
    {
        string? fallback = null;
        foreach (var declaration in definition.Attributes)
        {
            var binding = declaration.Binding;
            if (binding is null || binding.Target != target)
            {
                continue;
            }

            if (!ElementRenderBindingRules.Validate(node.Type, declaration, binding).IsValid)
            {
                continue;
            }

            var bindingPart = string.IsNullOrWhiteSpace(binding.Part) ? PartTokens.Root : binding.Part.Trim();
            if (!string.Equals(bindingPart, partPath, StringComparison.Ordinal))
            {
                continue;
            }

            if (preferredName is not null && string.Equals(binding.Name, preferredName, StringComparison.Ordinal))
            {
                return declaration.Key;
            }

            fallback ??= declaration.Key;
        }

        return fallback;
    }

    private static Dictionary<string, string?> ToAttributeMap(IReadOnlyList<GraphAttribute>? attributes)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (attributes is null)
        {
            return map;
        }

        foreach (var attribute in attributes)
        {
            map[attribute.Name] = attribute.Value;
        }

        return map;
    }

    // ── attributes：四层覆盖链 ────────────────────────────────────────────

    private static IReadOnlyList<OutputRecordAttribute> BuildAttributes(
        MarkdownNode node,
        StyleDefinition? definition,
        string? kind,
        IReadOnlyList<OutputRecordPresentation> presentation)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (definition is not null)
        {
            foreach (var declaration in definition.Attributes)
            {
                if (seen.Add(declaration.Key))
                {
                    keys.Add(declaration.Key);
                }
            }
        }

        foreach (var key in node.Attributes.Keys)
        {
            if (seen.Add(key))
            {
                keys.Add(key);
            }
        }

        foreach (var key in node.InstanceAttributes.Keys)
        {
            if (seen.Add(key))
            {
                keys.Add(key);
            }
        }

        var result = new List<OutputRecordAttribute>(keys.Count);
        foreach (var key in keys)
        {
            result.Add(BuildAttribute(node, definition, kind, key, presentation));
        }

        return result;
    }

    private static OutputRecordAttribute BuildAttribute(
        MarkdownNode node,
        StyleDefinition? definition,
        string? kind,
        string key,
        IReadOnlyList<OutputRecordPresentation> presentation)
    {
        var declaration = definition?.FindAttribute(key);
        var attributeSource = node.GetAttributeSource(key);
        var effective = node.Attributes.TryGetValue(key, out var value) ? value : null;

        var hasSource = attributeSource == AttributeValueSource.Source && effective is not null;
        var hasInstance = node.InstanceAttributes.TryGetValue(key, out var instanceValue);
        var defaultValue = attributeSource == AttributeValueSource.DefinitionDefault
            ? effective
            : declaration?.DefaultValue;
        var hasDefault = defaultValue is not null;

        var chain = new List<OutputRecordAttributeLayer>(4)
        {
            new("source", hasSource, hasSource ? effective : null, null, BuildSourceRef(node, key, hasSource)),
            new("instance", hasInstance, hasInstance ? instanceValue : null, null, hasInstance
                ? new OutputRecordAnchorRef(InstanceKey: node.Id, Field: "attributes." + key)
                : null),
            new("driver", false, null, null, null),
            new("definition-default", hasDefault, defaultValue, null, hasDefault && declaration is not null
                ? new OutputRecordAnchorRef(
                    DefinitionId: kind ?? definition?.Id,
                    Field: "attributes." + key + ".default")
                : null),
        };

        // 生效层从链里推，而不是相信单一来源枚举，保证 chain 与 effective 不会各说一套。
        var winnerIndex = chain.FindIndex(layer => layer.Present);
        if (winnerIndex < 0)
        {
            winnerIndex = chain.Count - 1;
        }

        for (var index = 0; index < chain.Count; index++)
        {
            if (!chain[index].Present)
            {
                continue;
            }

            var overriddenBy = index == winnerIndex ? null : chain[winnerIndex].Layer;
            chain[index] = chain[index] with { OverriddenBy = overriddenBy };
        }

        return new OutputRecordAttribute(
            key,
            declaration is not null,
            ResolveValueKind(declaration, effective),
            effective,
            chain[winnerIndex].Layer,
            ResolveOrigin(key, attributeSource),
            chain,
            BuildDrives(key, presentation));
    }

    private static OutputRecordAnchorRef? BuildSourceRef(MarkdownNode node, string key, bool present)
    {
        if (!present)
        {
            return null;
        }

        // 解析器目前只给块级 Range。属性名/值列号拿不到就留空并写明原因，
        // 不拿块级行号冒充属性级位置。
        return new OutputRecordAnchorRef(
            SourceLine: null,
            SourceColumn: null,
            UnavailableReason: AttributePositionUnavailable,
            Syntax: MarkdownAttributes.SourceDerivedKeys.Contains(key) ? "derived" : "fence.attribute",
            NodeId: node.Id);
    }

    private static string ResolveOrigin(string key, AttributeValueSource source) =>
        source switch
        {
            AttributeValueSource.Source => MarkdownAttributes.SourceDerivedKeys.Contains(key) ? "derived" : "inline",
            AttributeValueSource.Instance => "panel",
            _ => "default",
        };

    private static string ResolveValueKind(ElementAttributeDeclaration? declaration, string? effective)
    {
        if (declaration is not null)
        {
            return declaration.ValueKind.ToString();
        }

        if (bool.TryParse(effective, out _))
        {
            return "Boolean";
        }

        return double.TryParse(effective, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            ? "Number"
            : "Text";
    }

    private static IReadOnlyList<OutputRecordDrive>? BuildDrives(
        string key,
        IReadOnlyList<OutputRecordPresentation> presentation)
    {
        if (!string.Equals(key, ElementKind.AttributeKey, StringComparison.Ordinal))
        {
            return null;
        }

        var drives = presentation
            .Where(item => item.Ref.Rule == "kind-root-class" && item.Ref.Name is not null)
            .Select(item => new OutputRecordDrive(
                "part:" + item.Part + "/class",
                item.Ref.Name!,
                item.Ref.Rule))
            .ToArray();

        return drives.Length > 0 ? drives : null;
    }

    // ── 锚点 ──────────────────────────────────────────────────────────────

    private static OutputRecordAnchors BuildAnchors(
        NodeGraph graph,
        MarkdownNode node,
        string? kind,
        string? sourceFile)
    {
        var nodes = new Dictionary<string, string>(StringComparer.Ordinal);
        var ports = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in graph.Nodes)
        {
            var stageName = item.DefinitionId.StartsWith("stage.", StringComparison.Ordinal)
                ? item.DefinitionId["stage.".Length..]
                : item.DefinitionId;
            nodes[stageName] = item.Id;
            foreach (var port in item.Outputs)
            {
                ports[stageName + "/" + port.Name] = port.Name;
            }
        }

        return new OutputRecordAnchors(
            new OutputRecordSourceAnchor(sourceFile, node.Range.StartLine, node.Range.EndLine),
            kind is null
                ? null
                : new OutputRecordDefinitionAnchor(
                    kind,
                    Field: null,
                    FingerprintVersion: ElementDefinitionFingerprint.Version),
            new OutputRecordInstanceAnchor(node.Id),
            new OutputRecordGraphAnchors(nodes, ports));
    }

    private static OutputRecordGraphRef? StageGraphRef(NodeGraph graph, string stageDefinitionId, string portName)
    {
        var stage = graph.Nodes.FirstOrDefault(item =>
            string.Equals(item.DefinitionId, stageDefinitionId, StringComparison.Ordinal));
        return stage is null ? null : new OutputRecordGraphRef(stage.Id, portName);
    }

    // ── 小工具 ────────────────────────────────────────────────────────────

    private static string[] SplitClasses(string? cssClass) =>
        string.IsNullOrWhiteSpace(cssClass)
            ? []
            : cssClass.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static (string Path, GraphPart Part)? FindFirstPart(
        IReadOnlyList<GraphPart> parts,
        string? parentPath,
        Func<GraphPart, bool> predicate)
    {
        foreach (var part in parts)
        {
            var path = parentPath is null ? part.Name : parentPath + "/" + part.Name;
            if (predicate(part))
            {
                return (path, part);
            }

            var child = FindFirstPart(part.Children, path, predicate);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private static string? ExtractGlyph(string? slotHtml)
    {
        if (string.IsNullOrEmpty(slotHtml))
        {
            return null;
        }

        var hex = GlyphHexRegex.Match(slotHtml);
        if (hex.Success)
        {
            return hex.Groups[1].Value.ToLowerInvariant();
        }

        var dec = DecimalGlyphRegex.Match(slotHtml);
        if (dec.Success && int.TryParse(dec.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
        {
            return code.ToString("x", CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static Dictionary<string, string> ParseCssVariables(string? css)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(css))
        {
            return result;
        }

        foreach (Match match in CssVariableRegex.Matches(css))
        {
            result["--" + match.Groups[1].Value] = match.Groups[2].Value.Trim();
        }

        return result;
    }

    private static Dictionary<string, string?> ParseFirstTagAttributes(string? html)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(html) || html[0] != '<')
        {
            return result;
        }

        var end = FindTagEnd(html);
        if (end <= 1)
        {
            return result;
        }

        var index = 1;
        while (index < end && !char.IsWhiteSpace(html[index]) && html[index] is not ('/' or '>'))
        {
            index++;
        }

        while (index < end)
        {
            while (index < end && char.IsWhiteSpace(html[index]))
            {
                index++;
            }

            if (index >= end || html[index] == '/')
            {
                break;
            }

            var nameStart = index;
            while (index < end && !char.IsWhiteSpace(html[index]) && html[index] != '=')
            {
                index++;
            }

            var name = html[nameStart..index];
            while (index < end && char.IsWhiteSpace(html[index]))
            {
                index++;
            }

            string? value = null;
            if (index < end && html[index] == '=')
            {
                index++;
                while (index < end && char.IsWhiteSpace(html[index]))
                {
                    index++;
                }

                if (index < end && html[index] is '"' or '\'')
                {
                    var quote = html[index++];
                    var valueStart = index;
                    while (index < end && html[index] != quote)
                    {
                        index++;
                    }

                    value = html[valueStart..index];
                    if (index < end)
                    {
                        index++;
                    }
                }
                else
                {
                    var valueStart = index;
                    while (index < end && !char.IsWhiteSpace(html[index]))
                    {
                        index++;
                    }

                    value = html[valueStart..index];
                }
            }

            if (name.Length > 0)
            {
                result[name] = value;
            }
        }

        return result;
    }

    private static int FindTagEnd(string html)
    {
        var quote = '\0';
        for (var index = 1; index < html.Length; index++)
        {
            var c = html[index];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return index;
            }
        }

        return -1;
    }
}
