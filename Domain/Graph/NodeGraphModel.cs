using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;

/// <summary>端口与常量的值类型；封闭集合，禁止任意对象在图上游走。</summary>
public enum GraphValueKind
{
    None = 0,
    Any = 1,
    NodeRef = 2,
    NodeSeq = 3,
    Fields = 4,
    PartTree = 5,
    StyledPartTree = 6,
    Html = 7,
    Text = 8,
    Number = 9,
    Bool = 10,
    Enum = 11,
    Color = 12,
    StyleMap = 13,
}

/// <summary>部件内填充内容的来源。透明部件仍是结构的一部分，只是自身不产出标签。</summary>
public enum GraphPartSlot
{
    None = 0,
    Children = 1,
    Icon = 2,
    TitleText = 3,
}

/// <summary>有序属性：顺序参与输出字节，因此不能用字典。</summary>
public sealed record GraphAttribute(string Name, string? Value);

/// <summary>
/// 一个部件帧：结构模板的输出单位，尚未落样式。
/// <para>
/// <see cref="Tag"/> 为 null 表示透明部件（只输出槽内容，不包裹元素）；
/// <see cref="Role"/> 为 Root 时由 emit 写入节点身份属性与节点样式。
/// </para>
/// </summary>
public sealed record GraphPart(
    string Name,
    string? Tag,
    string CssClass,
    IReadOnlyList<GraphPart> Children,
    IReadOnlyList<GraphAttribute>? Attributes = null,
    ElementPartRole Role = ElementPartRole.Content,
    GraphPartSlot Slot = GraphPartSlot.None,
    string? SlotText = null,
    string? SlotHtml = null,
    string? BindingCss = null,
    string? StyleCss = null,
    bool IsCustom = false,
    // schema v2 定义字段：图 → 结构声明的无损投影。
    // Tag 只放静态标签，插值原文放 TagTemplate；CssClass 只放无条件 token，原始 class 串放 RawClass。
    string? TagTemplate = null,
    string? RawClass = null,
    bool Addressable = true,
    string? When = null,
    // 源数据条件（固定枚举）：按解析结果决定是否发射本部件及其子树，不命中零发射。
    string? WhenSource = null,
    string? Repeat = null,
    int RepeatMax = 64,
    string? Generator = null,
    string? GeneratorParams = null,
    string? Ref = null,
    string? Policy = null,
    // 原文槽：内容由渲染器按块源文本转义写入，永不解码 HTML。
    bool Raw = false,
    // 槽位文本：`{field}` 引用已声明字段，渲染时转义写入内容。
    string? Text = null,
    // 受控 URL 绑定：只允许 a→href、img→src，值必须是 `{field}` 纯洞。
    string? Href = null,
    string? Src = null);

/// <summary>端口间流动的值。载荷只允许 <see cref="GraphValueKind"/> 对应的类型。</summary>
public readonly struct GraphValue
{
    private readonly object? _payload;

    private GraphValue(GraphValueKind kind, object? payload)
    {
        Kind = kind;
        _payload = payload;
    }

    public GraphValueKind Kind { get; }

    public object? Payload => _payload;

    public bool IsNone => Kind == GraphValueKind.None;

    public static GraphValue None => new(GraphValueKind.None, null);

    public static GraphValue FromNode(MarkdownNode node) => new(GraphValueKind.NodeRef, node);

    public static GraphValue FromNodes(IReadOnlyList<MarkdownNode> nodes) =>
        new(GraphValueKind.NodeSeq, nodes);

    public static GraphValue FromFields(IReadOnlyList<IReadOnlyDictionary<string, string>> fields) =>
        new(GraphValueKind.Fields, fields);

    public static GraphValue FromParts(IReadOnlyList<GraphPart> parts, bool styled = false) =>
        new(styled ? GraphValueKind.StyledPartTree : GraphValueKind.PartTree, parts);

    public static GraphValue FromHtml(string html) => new(GraphValueKind.Html, html);

    public static GraphValue FromText(string text) => new(GraphValueKind.Text, text);

    public static GraphValue FromEnum(string value) => new(GraphValueKind.Enum, value);

    public static GraphValue FromColor(string value) => new(GraphValueKind.Color, value);

    public static GraphValue FromStyleMap(IReadOnlyDictionary<string, string> values) =>
        new(GraphValueKind.StyleMap, values);

    public static GraphValue FromNumber(double value) => new(GraphValueKind.Number, value);

    public static GraphValue FromBool(bool value) => new(GraphValueKind.Bool, value);

    public MarkdownNode AsNode() =>
        _payload as MarkdownNode
        ?? throw new InvalidOperationException($"值类型 {Kind} 不是 NodeRef。");

    public IReadOnlyList<MarkdownNode> AsNodes() =>
        _payload as IReadOnlyList<MarkdownNode>
        ?? throw new InvalidOperationException($"值类型 {Kind} 不是 NodeSeq。");

    public IReadOnlyList<IReadOnlyDictionary<string, string>> AsFields() =>
        _payload as IReadOnlyList<IReadOnlyDictionary<string, string>>
        ?? throw new InvalidOperationException($"值类型 {Kind} 不是 Fields。");

    public IReadOnlyList<GraphPart> AsParts() =>
        _payload as IReadOnlyList<GraphPart>
        ?? throw new InvalidOperationException($"值类型 {Kind} 不是 PartTree。");

    public string AsHtml() =>
        _payload as string ?? throw new InvalidOperationException($"值类型 {Kind} 不是 Html。");

    public string AsText() =>
        _payload as string ?? throw new InvalidOperationException($"值类型 {Kind} 不是文本。");

    public string AsEnum() =>
        _payload as string ?? throw new InvalidOperationException($"值类型 {Kind} 不是枚举值。");

    public double AsNumber() =>
        _payload is double value
            ? value
            : throw new InvalidOperationException($"值类型 {Kind} 不是 Number。");

    public bool AsBool() =>
        _payload is bool value
            ? value
            : throw new InvalidOperationException($"值类型 {Kind} 不是 Bool。");

    public IReadOnlyDictionary<string, string> AsStyleMap() =>
        _payload as IReadOnlyDictionary<string, string>
        ?? throw new InvalidOperationException($"值类型 {Kind} 不是 StyleMap。");

    public override string ToString() => Kind + ":" + (_payload?.ToString() ?? "null");
}

/// <summary>输入端口的常量。结构固定，值可改；实例值不写回这里。</summary>
public sealed class GraphConstant
{
    public GraphValueKind Kind { get; set; } = GraphValueKind.Text;

    public string? Raw { get; set; }

    public double? Number { get; set; }

    public bool? Flag { get; set; }

    public GraphValue ToValue() => Kind switch
    {
        GraphValueKind.Text => GraphValue.FromText(Raw ?? string.Empty),
        GraphValueKind.Enum => GraphValue.FromEnum(Raw ?? string.Empty),
        GraphValueKind.Color => GraphValue.FromColor(Raw ?? string.Empty),
        GraphValueKind.Number => GraphValue.FromNumber(Number ?? 0),
        GraphValueKind.Bool => GraphValue.FromBool(Flag ?? false),
        GraphValueKind.PartTree => UserBlockStructureGraph.ToValue(Raw),
        _ => GraphValue.None,
    };

    public bool HasValue => Kind switch
    {
        GraphValueKind.Text or GraphValueKind.Enum or GraphValueKind.Color or GraphValueKind.PartTree =>
            Raw is not null,
        GraphValueKind.Number => Number.HasValue,
        GraphValueKind.Bool => Flag.HasValue,
        _ => false,
    };

    public GraphConstant Clone() => new()
    {
        Kind = Kind,
        Raw = Raw,
        Number = Number,
        Flag = Flag,
    };

    internal string ToCanonical()
    {
        var raw = Kind switch
        {
            GraphValueKind.Number => Number?.ToString("R", CultureInfo.InvariantCulture),
            GraphValueKind.Bool => Flag?.ToString(),
            _ => Raw,
        };

        return Kind.ToString() + "=" + (raw ?? string.Empty);
    }
}

/// <summary>图的端口。输入端口可带常量，输出端口不带。</summary>
public sealed class GraphPort
{
    public GraphPort(string name, GraphValueKind kind, bool isRequired = true, bool isMulti = false)
    {
        Name = name;
        Kind = kind;
        IsRequired = isRequired;
        IsMulti = isMulti;
    }

    public string Name { get; }

    public GraphValueKind Kind { get; }

    public bool IsRequired { get; }

    public bool IsMulti { get; }

    public GraphConstant? Constant { get; set; }

    public GraphPort Clone() => new(Name, Kind, IsRequired, IsMulti) { Constant = Constant?.Clone() };
}

/// <summary>图节点。Id 在拖动、连线、保存之间保持不变。</summary>
public sealed class GraphNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string DefinitionId { get; set; } = string.Empty;

    public double X { get; set; }

    public double Y { get; set; }

    public bool IsBypassed { get; set; }

    public List<GraphPort> Inputs { get; } = [];

    public List<GraphPort> Outputs { get; } = [];

    public GraphPort? FindInput(string name) =>
        Inputs.FirstOrDefault(port => string.Equals(port.Name, name, StringComparison.Ordinal));

    public GraphPort? FindOutput(string name) =>
        Outputs.FirstOrDefault(port => string.Equals(port.Name, name, StringComparison.Ordinal));

    public GraphNode Clone()
    {
        var clone = new GraphNode
        {
            Id = Id,
            DefinitionId = DefinitionId,
            X = X,
            Y = Y,
            IsBypassed = IsBypassed,
        };
        clone.Inputs.AddRange(Inputs.Select(port => port.Clone()));
        clone.Outputs.AddRange(Outputs.Select(port => port.Clone()));
        return clone;
    }
}

/// <summary>一条连线。端口名是图内唯一寻址，不支持按索引。</summary>
public sealed record GraphEdge(
    string FromNodeId,
    string FromPort,
    string ToNodeId,
    string ToPort);

/// <summary>
/// 一个类型的结构图。只存结构与端口常量；实例值在求值时由外部注入，绝不写回这里。
/// </summary>
public sealed class NodeGraph
{
    public const string CurrentSchemaVersion = "1.0";

    public string Id { get; set; } = string.Empty;

    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    public NodeType AppliesTo { get; set; }

    public List<GraphNode> Nodes { get; } = [];

    public List<GraphEdge> Edges { get; } = [];

    public GraphNode? FindNode(string id) =>
        Nodes.FirstOrDefault(node => string.Equals(node.Id, id, StringComparison.Ordinal));

    public GraphNode? FindSink() =>
        Nodes.FirstOrDefault(node => string.Equals(node.DefinitionId, "stage.emit", StringComparison.Ordinal));

    public NodeGraph Clone()
    {
        var clone = new NodeGraph
        {
            Id = Id,
            SchemaVersion = SchemaVersion,
            AppliesTo = AppliesTo,
        };
        clone.Nodes.AddRange(Nodes.Select(node => node.Clone()));
        clone.Edges.AddRange(Edges);
        return clone;
    }

    /// <summary>
    /// 渲染哈希：只覆盖结构（定义、端口、常量、连线），不覆盖画布坐标。
    /// 拖动节点不会让块缓存失效；改线/改常量会。
    /// </summary>
    public string ComputeRenderHash()
    {
        var builder = new StringBuilder();
        builder.Append(Id).Append('\n').Append(AppliesTo).Append('\n').Append(SchemaVersion).Append('\n');

        foreach (var node in Nodes.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            builder.Append("N|").Append(node.Id).Append('|').Append(node.DefinitionId)
                .Append('|').Append(node.IsBypassed ? '1' : '0').Append('\n');

            foreach (var port in node.Inputs.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                builder.Append("I|").Append(port.Name).Append('|').Append(port.Kind)
                    .Append('|').Append(port.IsRequired ? '1' : '0')
                    .Append('|').Append(port.IsMulti ? '1' : '0')
                    .Append('|').Append(port.Constant?.ToCanonical() ?? "-")
                    .Append('\n');
            }

            foreach (var port in node.Outputs.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                builder.Append("O|").Append(port.Name).Append('|').Append(port.Kind).Append('\n');
            }
        }

        foreach (var edge in Edges
                     .OrderBy(item => item.FromNodeId, StringComparer.Ordinal)
                     .ThenBy(item => item.FromPort, StringComparer.Ordinal)
                     .ThenBy(item => item.ToNodeId, StringComparer.Ordinal)
                     .ThenBy(item => item.ToPort, StringComparer.Ordinal))
        {
            builder.Append("E|").Append(edge.FromNodeId).Append('|').Append(edge.FromPort)
                .Append('|').Append(edge.ToNodeId).Append('|').Append(edge.ToPort).Append('\n');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

internal static class GraphValueCompatibility
{
    /// <summary>连线类型校验：同类型、Any、或 NodeRef 提升为 NodeSeq。</summary>
    public static bool IsAssignable(GraphValueKind from, GraphValueKind to) =>
        from == GraphValueKind.Any
        || to == GraphValueKind.Any
        || from == to
        || (from == GraphValueKind.NodeRef && to == GraphValueKind.NodeSeq);
}
