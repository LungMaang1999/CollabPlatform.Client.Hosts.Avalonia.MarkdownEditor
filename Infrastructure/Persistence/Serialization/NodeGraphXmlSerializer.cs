using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Rendering.Graph;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

public sealed record NodeGraphReadResult(
    IReadOnlyList<NodeGraph> Graphs,
    IReadOnlyList<GraphDiagnostic> Diagnostics)
{
    /// <summary>文件声明的复合阶段；装载期已按校验结果丢弃坏声明。</summary>
    public IReadOnlyList<CompositeStageDefinition> Stages { get; init; } = [];
}

/// <summary>
/// 图伴随文件的读写。只存结构与端口常量；值仍归 .md.styles.xml 与节点属性。
/// </summary>
public static class NodeGraphXmlSerializer
{
    public const string RootElementName = "NodeGraphs";
    public const string NamespaceName = "urn:markdown-enhanced-editor";

    private static readonly XNamespace Ns = NamespaceName;

    public static string WriteToString(IEnumerable<NodeGraph> graphs) =>
        Write(graphs).ToString(SaveOptions.DisableFormatting) + Environment.NewLine;

    public static string WriteToString(
        IEnumerable<NodeGraph> graphs,
        IEnumerable<CompositeStageDefinition> stages) =>
        Write(graphs, stages).ToString(SaveOptions.DisableFormatting) + Environment.NewLine;

    public static XDocument Write(
        IEnumerable<NodeGraph> graphs,
        IEnumerable<CompositeStageDefinition>? stages = null)
    {
        ArgumentNullException.ThrowIfNull(graphs);
        var root = new XElement(
            Ns + RootElementName,
            new XAttribute("SchemaVersion", NodeGraph.CurrentSchemaVersion));

        foreach (var graph in graphs.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var element = new XElement(
                Ns + "NodeGraph",
                new XAttribute("Id", graph.Id),
                new XAttribute("AppliesTo", graph.AppliesTo.ToString()),
                new XAttribute("SchemaVersion", graph.SchemaVersion));

            var nodes = new XElement(Ns + "Nodes");
            foreach (var node in graph.Nodes.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                nodes.Add(WriteNode(node));
            }

            element.Add(nodes);
            element.Add(WriteEdges(graph.Edges));
            root.Add(element);
        }

        if (stages is not null)
        {
            var declared = stages.OrderBy(item => item.Id, StringComparer.Ordinal).ToList();
            if (declared.Count > 0)
            {
                var stagesElement = new XElement(Ns + "NodeStages");
                foreach (var stage in declared)
                {
                    stagesElement.Add(WriteStage(stage));
                }

                root.Add(stagesElement);
            }
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    private static XElement WriteEdges(IEnumerable<GraphEdge> edges)
    {
        var element = new XElement(Ns + "Edges");
        foreach (var edge in edges
                     .OrderBy(item => item.FromNodeId, StringComparer.Ordinal)
                     .ThenBy(item => item.FromPort, StringComparer.Ordinal)
                     .ThenBy(item => item.ToNodeId, StringComparer.Ordinal)
                     .ThenBy(item => item.ToPort, StringComparer.Ordinal))
        {
            element.Add(new XElement(
                Ns + "Edge",
                new XAttribute("From", edge.FromNodeId),
                new XAttribute("FromPort", edge.FromPort),
                new XAttribute("To", edge.ToNodeId),
                new XAttribute("ToPort", edge.ToPort)));
        }

        return element;
    }

    private static XElement WriteStage(CompositeStageDefinition stage)
    {
        var element = new XElement(
            Ns + "NodeStage",
            new XAttribute("Id", stage.Id),
            new XAttribute("Name", string.IsNullOrWhiteSpace(stage.DisplayName) ? stage.Id : stage.DisplayName),
            new XAttribute("Category", stage.Category),
            new XAttribute("AppliesTo", stage.AppliesTo.ToString()));

        if (!string.IsNullOrEmpty(stage.Description))
        {
            element.SetAttributeValue("Description", stage.Description);
        }

        var inputs = new XElement(Ns + "Inputs");
        foreach (var input in stage.Inputs)
        {
            var port = new XElement(
                Ns + "Port",
                new XAttribute("Name", input.Name),
                new XAttribute("Kind", input.Kind.ToString()),
                new XAttribute("To", input.InnerTarget),
                new XAttribute("Required", input.IsRequired ? "true" : "false"),
                new XAttribute("Multi", input.IsMulti ? "true" : "false"));
            inputs.Add(port);
        }

        var outputs = new XElement(Ns + "Outputs");
        foreach (var output in stage.Outputs)
        {
            outputs.Add(new XElement(
                Ns + "Port",
                new XAttribute("Name", output.Name),
                new XAttribute("Kind", output.Kind.ToString()),
                new XAttribute("From", output.InnerSource)));
        }

        element.Add(inputs);
        element.Add(outputs);

        var nodes = new XElement(Ns + "Nodes");
        foreach (var node in stage.Graph.Nodes.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            nodes.Add(WriteNode(node));
        }

        element.Add(nodes);
        element.Add(WriteEdges(stage.Graph.Edges));
        return element;
    }

    public static NodeGraphReadResult Read(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        return Read(XDocument.Parse(xml));
    }

    public static NodeGraphReadResult Read(string xml, GraphNodeRegistry? registry)
    {
        ArgumentNullException.ThrowIfNull(xml);
        return Read(XDocument.Parse(xml), registry);
    }

    public static NodeGraphReadResult Read(XDocument document, GraphNodeRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        registry ??= NodeGraphStageLibrary.CreateRegistry();

        var diagnostics = new List<GraphDiagnostic>();
        var graphs = new List<NodeGraph>();
        var root = document.Root;
        if (root is null || root.Name.LocalName != RootElementName)
        {
            diagnostics.Add(new GraphDiagnostic(
                "graph.invalid_root",
                $"图文件根节点应为 {RootElementName}。"));
            return new NodeGraphReadResult(graphs, diagnostics);
        }

        if (!IsSupportedVersion(root.Attribute("SchemaVersion")?.Value))
        {
            diagnostics.Add(new GraphDiagnostic(
                "graph.unsupported_version",
                "图文件版本不受支持，已回退默认投影。"));
            return new NodeGraphReadResult(graphs, diagnostics);
        }

        foreach (var element in root.Elements().Where(item => item.Name.LocalName == "NodeGraph"))
        {
            var graph = ReadGraph(element, registry, diagnostics);
            if (graph is not null)
            {
                graphs.Add(graph);
            }
        }

        var stages = ReadStages(root, registry, diagnostics);
        return new NodeGraphReadResult(graphs, diagnostics) { Stages = stages };
    }

    /// <summary>
    /// 复合阶段声明按两段式解析：先按声明建空图并把端口登记到临时注册表，
    /// 再回填节点与连线——这样前面声明的阶段可以引用后面声明的阶段。
    /// 解析完成后交给 <see cref="CompositeStageValidator"/> 统一校验，坏声明在此丢弃。
    /// </summary>
    private static IReadOnlyList<CompositeStageDefinition> ReadStages(
        XElement root,
        GraphNodeRegistry registry,
        List<GraphDiagnostic> diagnostics)
    {
        var stageElements = root.Elements()
            .Where(item => item.Name.LocalName == "NodeStages")
            .Elements()
            .Where(item => item.Name.LocalName == "NodeStage")
            .ToList();
        if (stageElements.Count == 0)
        {
            return [];
        }

        var staging = new GraphNodeRegistry();
        foreach (var definition in registry.Definitions)
        {
            staging.Register(definition);
        }

        var stages = new List<CompositeStageDefinition>();
        var sources = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var element in stageElements)
        {
            var id = element.Attribute("Id")?.Value;
            if (string.IsNullOrWhiteSpace(id))
            {
                diagnostics.Add(new GraphDiagnostic(
                    CompositeStageValidator.InvalidDeclarationCode,
                    "复合阶段缺少 Id，已跳过该条声明。"));
                continue;
            }

            if (staging.Contains(id))
            {
                diagnostics.Add(new GraphDiagnostic(
                    CompositeStageValidator.InvalidDeclarationCode,
                    $"复合阶段 Id「{id}」重复或与既有定义冲突，已跳过该条声明。",
                    id));
                continue;
            }

            if (!TryReadPortBindings(element, id, diagnostics, out var inputs, out var outputs))
            {
                continue;
            }

            var appliesTo = Enum.TryParse<NodeType>(element.Attribute("AppliesTo")?.Value, out var parsed)
                ? parsed
                : NodeType.Custom;
            var stage = new CompositeStageDefinition
            {
                Id = id,
                DisplayName = element.Attribute("Name")?.Value ?? id,
                Category = element.Attribute("Category")?.Value ?? "复合阶段",
                Description = element.Attribute("Description")?.Value ?? string.Empty,
                AppliesTo = appliesTo,
                Inputs = inputs,
                Outputs = outputs,
                Graph = new NodeGraph { Id = id, AppliesTo = appliesTo },
            };

            staging.Register(stage.ToNodeDefinition());
            stages.Add(stage);
            sources[id] = element;
        }

        foreach (var stage in stages)
        {
            var element = sources[stage.Id];
            foreach (var nodeElement in element.Elements()
                         .Where(item => item.Name.LocalName == "Nodes")
                         .Elements()
                         .Where(item => item.Name.LocalName == "Node"))
            {
                var node = ReadNode(nodeElement, staging, diagnostics);
                if (node is not null)
                {
                    stage.Graph.Nodes.Add(node);
                }
            }

            foreach (var edgeElement in element.Elements()
                         .Where(item => item.Name.LocalName == "Edges")
                         .Elements()
                         .Where(item => item.Name.LocalName == "Edge"))
            {
                var from = edgeElement.Attribute("From")?.Value;
                var fromPort = edgeElement.Attribute("FromPort")?.Value;
                var to = edgeElement.Attribute("To")?.Value;
                var toPort = edgeElement.Attribute("ToPort")?.Value;
                if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(fromPort) ||
                    string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(toPort))
                {
                    diagnostics.Add(new GraphDiagnostic(
                        "graph.invalid_edge",
                        $"复合阶段 {stage.Id} 存在残缺连线，已跳过。",
                        stage.Id));
                    continue;
                }

                stage.Graph.Edges.Add(new GraphEdge(from, fromPort, to, toPort));
            }
        }

        var validated = CompositeStageValidator.Validate(stages, registry);
        diagnostics.AddRange(validated.Diagnostics);
        return validated.Stages;
    }

    private static bool TryReadPortBindings(
        XElement element,
        string stageId,
        ICollection<GraphDiagnostic> diagnostics,
        out IReadOnlyList<CompositePortBinding> inputs,
        out IReadOnlyList<CompositeOutputBinding> outputs)
    {
        var parsedInputs = new List<CompositePortBinding>();
        var parsedOutputs = new List<CompositeOutputBinding>();
        var valid = true;

        foreach (var port in element.Elements()
                     .Where(item => item.Name.LocalName == "Inputs")
                     .Elements()
                     .Where(item => item.Name.LocalName == "Port"))
        {
            if (!Enum.TryParse<GraphValueKind>(port.Attribute("Kind")?.Value, out var kind))
            {
                diagnostics.Add(new GraphDiagnostic(
                    CompositeStageValidator.PortInvalidCode,
                    $"复合阶段「{stageId}」的输入「{port.Attribute("Name")?.Value}」类型无法识别，已丢弃该条声明。",
                    stageId));
                valid = false;
                continue;
            }

            parsedInputs.Add(new CompositePortBinding(
                port.Attribute("Name")?.Value ?? string.Empty,
                kind,
                !string.Equals(port.Attribute("Required")?.Value, "false", StringComparison.OrdinalIgnoreCase),
                string.Equals(port.Attribute("Multi")?.Value, "true", StringComparison.OrdinalIgnoreCase),
                port.Attribute("To")?.Value ?? string.Empty));
        }

        foreach (var port in element.Elements()
                     .Where(item => item.Name.LocalName == "Outputs")
                     .Elements()
                     .Where(item => item.Name.LocalName == "Port"))
        {
            if (!Enum.TryParse<GraphValueKind>(port.Attribute("Kind")?.Value, out var kind))
            {
                diagnostics.Add(new GraphDiagnostic(
                    CompositeStageValidator.PortInvalidCode,
                    $"复合阶段「{stageId}」的输出「{port.Attribute("Name")?.Value}」类型无法识别，已丢弃该条声明。",
                    stageId));
                valid = false;
                continue;
            }

            parsedOutputs.Add(new CompositeOutputBinding(
                port.Attribute("Name")?.Value ?? string.Empty,
                kind,
                port.Attribute("From")?.Value ?? string.Empty));
        }

        inputs = parsedInputs;
        outputs = parsedOutputs;
        return valid;
    }

    private static NodeGraph? ReadGraph(
        XElement element,
        GraphNodeRegistry registry,
        ICollection<GraphDiagnostic> diagnostics)
    {
        var id = element.Attribute("Id")?.Value;
        if (string.IsNullOrWhiteSpace(id))
        {
            diagnostics.Add(new GraphDiagnostic("graph.missing_id", "图缺少 Id，已跳过。"));
            return null;
        }

        if (!Enum.TryParse<NodeType>(element.Attribute("AppliesTo")?.Value, out var appliesTo))
        {
            diagnostics.Add(new GraphDiagnostic("graph.unknown_type", $"图 {id} 的类型无法识别，已跳过。"));
            return null;
        }

        if (!IsSupportedVersion(element.Attribute("SchemaVersion")?.Value))
        {
            diagnostics.Add(new GraphDiagnostic(
                "graph.unsupported_version",
                $"图 {id} 的版本不受支持，已回退默认投影。"));
            return null;
        }

        var graph = new NodeGraph
        {
            Id = id,
            AppliesTo = appliesTo,
            SchemaVersion = element.Attribute("SchemaVersion")?.Value ?? NodeGraph.CurrentSchemaVersion,
        };

        foreach (var nodeElement in element.Elements()
                     .Where(item => item.Name.LocalName == "Nodes")
                     .Elements()
                     .Where(item => item.Name.LocalName == "Node"))
        {
            var node = ReadNode(nodeElement, registry, diagnostics);
            if (node is not null)
            {
                graph.Nodes.Add(node);
            }
        }

        foreach (var edgeElement in element.Elements()
                     .Where(item => item.Name.LocalName == "Edges")
                     .Elements()
                     .Where(item => item.Name.LocalName == "Edge"))
        {
            var from = edgeElement.Attribute("From")?.Value;
            var fromPort = edgeElement.Attribute("FromPort")?.Value;
            var to = edgeElement.Attribute("To")?.Value;
            var toPort = edgeElement.Attribute("ToPort")?.Value;
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(fromPort) ||
                string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(toPort))
            {
                diagnostics.Add(new GraphDiagnostic("graph.invalid_edge", $"图 {id} 存在残缺连线，已跳过。"));
                continue;
            }

            graph.Edges.Add(new GraphEdge(from, fromPort, to, toPort));
        }

        return graph;
    }

    private static GraphNode? ReadNode(
        XElement element,
        GraphNodeRegistry registry,
        ICollection<GraphDiagnostic> diagnostics)
    {
        var id = element.Attribute("Id")?.Value;
        var definitionId = element.Attribute("Definition")?.Value;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(definitionId))
        {
            diagnostics.Add(new GraphDiagnostic("graph.invalid_node", "节点缺少 Id 或 Definition，已跳过。"));
            return null;
        }

        var definition = registry.Find(definitionId);
        GraphNode node;
        if (definition is null)
        {
            diagnostics.Add(new GraphDiagnostic(
                "graph.unknown_definition",
                $"节点定义 {definitionId} 未注册，端口无法重建。",
                id));
            node = new GraphNode { Id = id, DefinitionId = definitionId };
        }
        else
        {
            node = definition.CreateNode(id);
        }

        node.X = ReadDouble(element.Attribute("X")?.Value);
        node.Y = ReadDouble(element.Attribute("Y")?.Value);
        node.IsBypassed = string.Equals(element.Attribute("Bypassed")?.Value, "true", StringComparison.OrdinalIgnoreCase);

        foreach (var inputElement in element.Elements().Where(item => item.Name.LocalName == "Input"))
        {
            var name = inputElement.Attribute("Name")?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var port = node.FindInput(name);
            if (port is null)
            {
                // 旧图文件残留的 template.variant 常量：变体已归类型行为，静默丢弃。
                if (NodeGraphMigration.IsRetiredInput(definitionId, name))
                {
                    continue;
                }

                diagnostics.Add(new GraphDiagnostic(
                    "graph.unknown_port",
                    $"节点 {id} 的输入端口 {name} 不存在，已忽略。",
                    id,
                    name));
                continue;
            }

            port.Constant = ReadConstant(inputElement);
        }

        return node;
    }

    private static GraphConstant ReadConstant(XElement element)
    {
        var kindText = element.Attribute("Kind")?.Value;
        var kind = Enum.TryParse<GraphValueKind>(kindText, out var parsed) ? parsed : GraphValueKind.Text;

        return new GraphConstant
        {
            Kind = kind,
            Raw = element.Attribute("Raw")?.Value,
            Number = double.TryParse(
                element.Attribute("Number")?.Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number)
                ? number
                : null,
            Flag = bool.TryParse(element.Attribute("Flag")?.Value, out var flag) ? flag : null,
        };
    }

    private static XElement WriteNode(GraphNode node)
    {
        var element = new XElement(
            Ns + "Node",
            new XAttribute("Id", node.Id),
            new XAttribute("Definition", node.DefinitionId),
            new XAttribute("X", node.X.ToString("R", CultureInfo.InvariantCulture)),
            new XAttribute("Y", node.Y.ToString("R", CultureInfo.InvariantCulture)),
            new XAttribute("Bypassed", node.IsBypassed ? "true" : "false"));

        foreach (var port in node.Inputs
                     .Where(item => item.Constant is not null)
                     .OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            var constant = port.Constant!;
            var inputElement = new XElement(
                Ns + "Input",
                new XAttribute("Name", port.Name),
                new XAttribute("Kind", constant.Kind.ToString()));

            if (constant.Raw is not null)
            {
                inputElement.SetAttributeValue("Raw", constant.Raw);
            }

            if (constant.Number.HasValue)
            {
                inputElement.SetAttributeValue("Number", constant.Number.Value.ToString("R", CultureInfo.InvariantCulture));
            }

            if (constant.Flag.HasValue)
            {
                inputElement.SetAttributeValue("Flag", constant.Flag.Value ? "true" : "false");
            }

            element.Add(inputElement);
        }

        return element;
    }

    private static bool IsSupportedVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return true;
        }

        var majorText = version.Split('.')[0];
        return int.TryParse(majorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) && major == 1;
    }

    private static double ReadDouble(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
