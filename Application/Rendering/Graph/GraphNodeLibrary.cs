using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Rendering.Graph;

/// <summary>
/// 通用节点库：输入、文本、计算、控制、集合、布局、结构七类。
/// 只依赖端口值与 <see cref="MarkdownNode"/>，不认识任何具体元素，
/// 图文件按定义 Id 引用，新增节点只往这里加一条。
/// </summary>
public static class GraphNodeLibrary
{
    public const string CategoryInput = "输入";
    public const string CategoryText = "文本";
    public const string CategoryCalc = "计算";
    public const string CategoryControl = "控制";
    public const string CategoryCollection = "集合";
    public const string CategoryLayout = "布局";
    public const string CategoryStructure = "结构";

    /// <summary>可作为常量编辑的输入端口：其余类型由上游连线提供，不给输入框。</summary>
    public static GraphControlKind? EditorFor(GraphValueKind kind) => kind switch
    {
        GraphValueKind.Text => GraphControlKind.Text,
        GraphValueKind.Enum => GraphControlKind.Select,
        GraphValueKind.Number => GraphControlKind.Number,
        GraphValueKind.Bool => GraphControlKind.Toggle,
        GraphValueKind.Color => GraphControlKind.Color,
        _ => null,
    };

    public static GraphValueKind ValueKindFor(GraphControlKind kind) => kind switch
    {
        GraphControlKind.Number or GraphControlKind.Slider => GraphValueKind.Number,
        GraphControlKind.Toggle => GraphValueKind.Bool,
        GraphControlKind.Select => GraphValueKind.Enum,
        GraphControlKind.Color => GraphValueKind.Color,
        _ => GraphValueKind.Text,
    };

    /// <summary>常量在检查器里的文本形态；空串表示没有值。</summary>
    public static string ToEditorText(GraphConstant? constant) => constant?.Kind switch
    {
        GraphValueKind.Number => constant.Number?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty,
        GraphValueKind.Bool => constant.Flag == true ? "true" : "false",
        GraphValueKind.Text or GraphValueKind.Enum or GraphValueKind.Color => constant.Raw ?? string.Empty,
        _ => string.Empty,
    };

    /// <summary>由检查器文本生成端口常量；空文本表示清空常量，返回 null。</summary>
    public static GraphConstant? FromEditorText(GraphControlKind kind, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var constant = new GraphConstant { Kind = ValueKindFor(kind) };
        switch (kind)
        {
            case GraphControlKind.Number:
            case GraphControlKind.Slider:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    return null;
                }

                constant.Number = number;
                break;
            case GraphControlKind.Toggle:
                constant.Flag = string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
                break;
            default:
                constant.Raw = text;
                break;
        }

        return constant;
    }

    public static IReadOnlyList<GraphNodeDefinition> Definitions { get; } =
    [
        new()
        {
            Id = "value.text",
            DisplayName = "文本",
            Category = CategoryInput,
            Inputs = [new GraphPortDescriptor("value", GraphValueKind.Text, IsRequired: false)],
            Outputs = [new GraphPortDescriptor("text", GraphValueKind.Text)],
            Controls = [new GraphControlDescriptor("value", "内容", GraphControlKind.Text)],
            Executor = context => Emit(
                ("text", GraphValue.FromText(Text(context, "value") ?? string.Empty))),
        },
        new()
        {
            Id = "value.number",
            DisplayName = "数值",
            Category = CategoryInput,
            Inputs = [new GraphPortDescriptor("value", GraphValueKind.Number, IsRequired: false)],
            Outputs = [new GraphPortDescriptor("number", GraphValueKind.Number)],
            Controls = [new GraphControlDescriptor("value", "数值", GraphControlKind.Number)],
            Executor = context => Emit(
                ("number", GraphValue.FromNumber(Number(context, "value", 0)))),
        },
        new()
        {
            Id = "value.bool",
            DisplayName = "开关",
            Category = CategoryInput,
            Inputs = [new GraphPortDescriptor("value", GraphValueKind.Bool, IsRequired: false)],
            Outputs = [new GraphPortDescriptor("flag", GraphValueKind.Bool)],
            Controls = [new GraphControlDescriptor("value", "开关", GraphControlKind.Toggle)],
            Executor = context => Emit(
                ("flag", GraphValue.FromBool(Flag(context, "value", false)))),
        },
        new()
        {
            Id = "text.concat",
            DisplayName = "文本拼接",
            Category = CategoryText,
            Inputs =
            [
                new GraphPortDescriptor("a", GraphValueKind.Text, IsRequired: false),
                new GraphPortDescriptor("b", GraphValueKind.Text, IsRequired: false),
                new GraphPortDescriptor("separator", GraphValueKind.Text, IsRequired: false),
            ],
            Outputs = [new GraphPortDescriptor("text", GraphValueKind.Text)],
            Controls =
            [
                new GraphControlDescriptor("a", "前半段", GraphControlKind.Text),
                new GraphControlDescriptor("b", "后半段", GraphControlKind.Text),
                new GraphControlDescriptor("separator", "连接符", GraphControlKind.Text),
            ],
            Executor = context =>
            {
                var parts = new[] { Text(context, "a"), Text(context, "b") }
                    .Where(item => !string.IsNullOrEmpty(item))
                    .ToArray();
                return Emit(("text", GraphValue.FromText(string.Join(Text(context, "separator") ?? string.Empty, parts))));
            },
        },
        new()
        {
            Id = "text.replace",
            DisplayName = "文本替换",
            Category = CategoryText,
            Inputs =
            [
                new GraphPortDescriptor("source", GraphValueKind.Text, IsRequired: false),
                new GraphPortDescriptor("find", GraphValueKind.Text, IsRequired: false),
                new GraphPortDescriptor("replace", GraphValueKind.Text, IsRequired: false),
            ],
            Outputs = [new GraphPortDescriptor("text", GraphValueKind.Text)],
            Controls =
            [
                new GraphControlDescriptor("source", "原文", GraphControlKind.Text),
                new GraphControlDescriptor("find", "查找", GraphControlKind.Text),
                new GraphControlDescriptor("replace", "替换为", GraphControlKind.Text),
            ],
            Executor = context =>
            {
                var source = Text(context, "source") ?? string.Empty;
                var find = Text(context, "find");
                return Emit(("text", GraphValue.FromText(
                    string.IsNullOrEmpty(find)
                        ? source
                        : source.Replace(find, Text(context, "replace") ?? string.Empty, StringComparison.Ordinal))));
            },
        },
        new()
        {
            Id = "text.regex",
            DisplayName = "正则提取",
            Category = CategoryText,
            Inputs =
            [
                new GraphPortDescriptor("source", GraphValueKind.Text, IsRequired: false),
                new GraphPortDescriptor("pattern", GraphValueKind.Text, IsRequired: false),
            ],
            Outputs =
            [
                new GraphPortDescriptor("text", GraphValueKind.Text),
                new GraphPortDescriptor("matched", GraphValueKind.Bool),
            ],
            Controls =
            [
                new GraphControlDescriptor("source", "原文", GraphControlKind.Text),
                new GraphControlDescriptor("pattern", "正则（取第 1 组）", GraphControlKind.Text),
            ],
            Executor = context =>
            {
                var source = Text(context, "source") ?? string.Empty;
                var pattern = Text(context, "pattern");
                if (string.IsNullOrEmpty(pattern))
                {
                    return Emit(("text", GraphValue.FromText(source)), ("matched", GraphValue.FromBool(false)));
                }

                try
                {
                    var match = Regex.Match(source, pattern);
                    var captured = match.Success
                        ? (match.Groups.Count > 1 ? match.Groups[1].Value : match.Value)
                        : string.Empty;
                    return Emit(("text", GraphValue.FromText(captured)), ("matched", GraphValue.FromBool(match.Success)));
                }
                catch (ArgumentException)
                {
                    context.Report("graph.bad_regex", "正则表达式无效，输出空文本。", "pattern");
                    return Emit(("text", GraphValue.FromText(string.Empty)), ("matched", GraphValue.FromBool(false)));
                }
            },
        },
        new()
        {
            Id = "calc.math",
            DisplayName = "四则运算",
            Category = CategoryCalc,
            Inputs =
            [
                new GraphPortDescriptor("a", GraphValueKind.Number, IsRequired: false),
                new GraphPortDescriptor("b", GraphValueKind.Number, IsRequired: false),
                new GraphPortDescriptor("op", GraphValueKind.Enum, IsRequired: false),
            ],
            Outputs = [new GraphPortDescriptor("value", GraphValueKind.Number)],
            Controls =
            [
                new GraphControlDescriptor("a", "左值", GraphControlKind.Number),
                new GraphControlDescriptor("b", "右值", GraphControlKind.Number),
                new GraphControlDescriptor("op", "运算符", GraphControlKind.Select, ["+", "-", "*", "/"]),
            ],
            Executor = context =>
            {
                var left = Number(context, "a", 0);
                var right = Number(context, "b", 0);
                var op = Text(context, "op") ?? "+";
                return op switch
                {
                    "-" => Emit(("value", GraphValue.FromNumber(left - right))),
                    "*" => Emit(("value", GraphValue.FromNumber(left * right))),
                    "/" => Divide(context, left, right),
                    _ => Emit(("value", GraphValue.FromNumber(left + right))),
                };
            },
        },
        new()
        {
            Id = "calc.compare",
            DisplayName = "数值比较",
            Category = CategoryCalc,
            Inputs =
            [
                new GraphPortDescriptor("a", GraphValueKind.Number, IsRequired: false),
                new GraphPortDescriptor("b", GraphValueKind.Number, IsRequired: false),
                new GraphPortDescriptor("op", GraphValueKind.Enum, IsRequired: false),
            ],
            Outputs = [new GraphPortDescriptor("result", GraphValueKind.Bool)],
            Controls =
            [
                new GraphControlDescriptor("a", "左值", GraphControlKind.Number),
                new GraphControlDescriptor("b", "右值", GraphControlKind.Number),
                new GraphControlDescriptor("op", "比较", GraphControlKind.Select, [">", ">=", "<", "<=", "==", "!="]),
            ],
            Executor = context =>
            {
                var left = Number(context, "a", 0);
                var right = Number(context, "b", 0);
                var result = (Text(context, "op") ?? ">") switch
                {
                    ">=" => left >= right,
                    "<" => left < right,
                    "<=" => left <= right,
                    "==" => Math.Abs(left - right) < 1e-9,
                    "!=" => Math.Abs(left - right) >= 1e-9,
                    _ => left > right,
                };
                return Emit(("result", GraphValue.FromBool(result)));
            },
        },
        new()
        {
            Id = "logic.if",
            DisplayName = "条件选择",
            Category = CategoryControl,
            Inputs =
            [
                new GraphPortDescriptor("condition", GraphValueKind.Bool, IsRequired: false),
                new GraphPortDescriptor("then", GraphValueKind.Any, IsRequired: false),
                new GraphPortDescriptor("else", GraphValueKind.Any, IsRequired: false),
            ],
            Outputs = [new GraphPortDescriptor("value", GraphValueKind.Any)],
            Controls = [new GraphControlDescriptor("condition", "条件", GraphControlKind.Toggle)],
            Executor = context => Emit(
                ("value", Flag(context, "condition", false)
                    ? context.InputValue("then")
                    : context.InputValue("else"))),
        },
        new()
        {
            Id = "list.take",
            DisplayName = "取前 N 条",
            Category = CategoryCollection,
            Inputs =
            [
                new GraphPortDescriptor("nodes", GraphValueKind.NodeSeq),
                new GraphPortDescriptor("count", GraphValueKind.Number, IsRequired: false),
            ],
            Outputs = [new GraphPortDescriptor("nodes", GraphValueKind.NodeSeq)],
            Controls = [new GraphControlDescriptor("count", "条数", GraphControlKind.Number)],
            Executor = context =>
            {
                var nodes = context.InputValue("nodes");
                var source = nodes.IsNone ? [] : nodes.AsNodes();
                var count = Math.Max(0, (int)Number(context, "count", 0));
                return Emit(("nodes", GraphValue.FromNodes(source.Take(count).ToList())));
            },
        },
        new()
        {
            Id = "list.filter",
            DisplayName = "按字段过滤",
            Category = CategoryCollection,
            Inputs =
            [
                new GraphPortDescriptor("nodes", GraphValueKind.NodeSeq),
                new GraphPortDescriptor("field", GraphValueKind.Text, IsRequired: false),
                new GraphPortDescriptor("equals", GraphValueKind.Text, IsRequired: false),
            ],
            Outputs = [new GraphPortDescriptor("nodes", GraphValueKind.NodeSeq)],
            Controls =
            [
                new GraphControlDescriptor("field", "字段", GraphControlKind.Text),
                new GraphControlDescriptor("equals", "等于", GraphControlKind.Text),
            ],
            Executor = context =>
            {
                var nodes = context.InputValue("nodes");
                var source = nodes.IsNone ? [] : nodes.AsNodes();
                var field = Text(context, "field");
                var expected = Text(context, "equals") ?? string.Empty;
                if (string.IsNullOrEmpty(field))
                {
                    return Emit(("nodes", GraphValue.FromNodes(source)));
                }

                var kept = source
                    .Where(node => string.Equals(FieldValue(node, field), expected, StringComparison.Ordinal))
                    .ToList();
                return Emit(("nodes", GraphValue.FromNodes(kept)));
            },
        },
        new()
        {
            Id = "layout.wrap",
            DisplayName = "包裹元素",
            Category = CategoryLayout,
            Inputs =
            [
                new GraphPortDescriptor("tag", GraphValueKind.Text, IsRequired: false),
                new GraphPortDescriptor("class", GraphValueKind.Text, IsRequired: false),
                new GraphPortDescriptor("content", GraphValueKind.Html, IsRequired: false),
            ],
            Outputs = [new GraphPortDescriptor("html", GraphValueKind.Html)],
            Controls =
            [
                new GraphControlDescriptor("tag", "标签", GraphControlKind.Text),
                new GraphControlDescriptor("class", "类名", GraphControlKind.Text),
            ],
            Executor = context =>
            {
                var tag = NormalizeTag(Text(context, "tag"));
                var cssClass = Text(context, "class");
                var content = context.InputValue("content");
                var inner = content.IsNone ? string.Empty : content.AsHtml();
                var builder = new StringBuilder("<").Append(tag);
                if (!string.IsNullOrWhiteSpace(cssClass))
                {
                    builder.Append(" class=\"").Append(cssClass.Trim()).Append('"');
                }

                builder.Append('>').Append(inner).Append("</").Append(tag).Append('>');
                return Emit(("html", GraphValue.FromHtml(builder.ToString())));
            },
        },
        new()
        {
            Id = "structure.css",
            DisplayName = "样式键值",
            Category = CategoryStructure,
            Inputs =
            [
                new GraphPortDescriptor("key", GraphValueKind.Text, IsRequired: false),
                new GraphPortDescriptor("value", GraphValueKind.Text, IsRequired: false),
            ],
            Outputs = [new GraphPortDescriptor("style", GraphValueKind.StyleMap)],
            Controls =
            [
                new GraphControlDescriptor("key", "属性", GraphControlKind.Text),
                new GraphControlDescriptor("value", "取值", GraphControlKind.Text),
            ],
            Executor = context =>
            {
                var key = Text(context, "key");
                var value = Text(context, "value");
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    map[key.Trim()] = value ?? string.Empty;
                }

                return Emit(("style", GraphValue.FromStyleMap(map)));
            },
        },
        new()
        {
            Id = "structure.element",
            DisplayName = "部件",
            Category = CategoryStructure,
            Inputs =
            [
                new GraphPortDescriptor("name", GraphValueKind.Text, IsRequired: false),
                new GraphPortDescriptor("tag", GraphValueKind.Text, IsRequired: false),
                new GraphPortDescriptor("class", GraphValueKind.Text, IsRequired: false),
                new GraphPortDescriptor("role", GraphValueKind.Enum, IsRequired: false),
                new GraphPortDescriptor("raw", GraphValueKind.Bool, IsRequired: false),
                new GraphPortDescriptor("text", GraphValueKind.Text, IsRequired: false),
                new GraphPortDescriptor("children", GraphValueKind.PartTree, IsRequired: false, IsMulti: true),
            ],
            Outputs = [new GraphPortDescriptor("parts", GraphValueKind.PartTree)],
            Controls =
            [
                new GraphControlDescriptor("name", "分区名", GraphControlKind.Text),
                new GraphControlDescriptor("tag", "标签", GraphControlKind.Text),
                new GraphControlDescriptor("class", "类名", GraphControlKind.Text),
                new GraphControlDescriptor("role", "角色", GraphControlKind.Select, ["根部件", "内容分区"]),
                new GraphControlDescriptor("raw", "原文槽", GraphControlKind.Toggle),
                new GraphControlDescriptor("text", "槽位文本", GraphControlKind.Text),
            ],
            Executor = context =>
            {
                var children = context.InputValue("children");
                var inner = children.IsNone ? [] : children.AsParts();
                var role = string.Equals(Text(context, "role"), "内容分区", StringComparison.Ordinal)
                    ? ElementPartRole.Content
                    : ElementPartRole.Root;
                var textField = Text(context, "text");
                var part = new GraphPart(
                    Name: NormalizePartName(Text(context, "name")),
                    Tag: NormalizePartTag(Text(context, "tag")),
                    CssClass: SanitizeClass(Text(context, "class")),
                    Children: inner,
                    Role: role,
                    IsCustom: true,
                    Raw: Flag(context, "raw", false),
                    Text: string.IsNullOrWhiteSpace(textField) ? null : textField.Trim());
                return Emit(("parts", GraphValue.FromParts([part])));
            },
        },
        new()
        {
            Id = "structure.merge",
            DisplayName = "合并部件",
            Category = CategoryStructure,
            Inputs =
            [
                new GraphPortDescriptor("a", GraphValueKind.PartTree, IsRequired: false),
                new GraphPortDescriptor("b", GraphValueKind.PartTree, IsRequired: false),
            ],
            Outputs = [new GraphPortDescriptor("parts", GraphValueKind.PartTree)],
            Executor = context =>
            {
                var first = context.InputValue("a");
                var second = context.InputValue("b");
                var merged = new List<GraphPart>();
                if (!first.IsNone)
                {
                    merged.AddRange(first.AsParts());
                }

                if (!second.IsNone)
                {
                    merged.AddRange(second.AsParts());
                }

                // 合并本身就是一次结构编排，标成图产出，下游才按指定结构换根。
                return Emit(("parts", GraphValue.FromParts(
                    merged.Select(part => part with { IsCustom = true }).ToList())));
            },
        },
        new()
        {
            Id = "structure.slot",
            DisplayName = "固定槽内容",
            Category = CategoryStructure,
            Inputs =
            [
                new GraphPortDescriptor("parts", GraphValueKind.PartTree),
                new GraphPortDescriptor("html", GraphValueKind.Html, IsRequired: false),
                new GraphPortDescriptor("text", GraphValueKind.Text, IsRequired: false),
            ],
            Outputs = [new GraphPortDescriptor("parts", GraphValueKind.PartTree)],
            Controls = [new GraphControlDescriptor("text", "文本", GraphControlKind.Text)],
            Executor = context =>
            {
                var parts = context.InputValue("parts");
                var source = parts.IsNone ? [] : parts.AsParts();
                if (source.Count == 0)
                {
                    return Emit(("parts", GraphValue.FromParts(source)));
                }

                var html = context.InputValue("html");
                var slotHtml = html.IsNone
                    ? System.Net.WebUtility.HtmlEncode(Text(context, "text") ?? string.Empty)
                    : html.AsHtml();

                // 固定内容写在根部件上，按分区名覆盖该槽位原本的填充结果。
                var replaced = source.ToList();
                replaced[0] = replaced[0] with { SlotHtml = slotHtml, IsCustom = true };
                return Emit(("parts", GraphValue.FromParts(replaced)));
            },
        },
        new()
        {
            Id = "structure.if",
            DisplayName = "条件结构",
            Category = CategoryStructure,
            Inputs =
            [
                new GraphPortDescriptor("condition", GraphValueKind.Bool, IsRequired: false),
                new GraphPortDescriptor("then", GraphValueKind.PartTree, IsRequired: false),
                new GraphPortDescriptor("else", GraphValueKind.PartTree, IsRequired: false),
            ],
            Outputs = [new GraphPortDescriptor("parts", GraphValueKind.PartTree)],
            Controls = [new GraphControlDescriptor("condition", "条件", GraphControlKind.Toggle)],
            Executor = context =>
            {
                var picked = Flag(context, "condition", false)
                    ? context.InputValue("then")
                    : context.InputValue("else");
                var parts = picked.IsNone ? [] : picked.AsParts();

                // 分支结果属于图编排出的结构，下游据此换根；否则会退回渲染器快照。
                return Emit(("parts", GraphValue.FromParts(
                    parts.Select(part => part with { IsCustom = true }).ToList())));
            },
        },
        new()
        {
            Id = "structure.foreach",
            DisplayName = "遍历产出",
            Category = CategoryStructure,
            Inputs =
            [
                new GraphPortDescriptor("nodes", GraphValueKind.NodeSeq),
                new GraphPortDescriptor("template", GraphValueKind.PartTree, IsRequired: false),
                new GraphPortDescriptor("name", GraphValueKind.Text, IsRequired: false),
                new GraphPortDescriptor("tag", GraphValueKind.Text, IsRequired: false),
                new GraphPortDescriptor("class", GraphValueKind.Text, IsRequired: false),
            ],
            Outputs = [new GraphPortDescriptor("parts", GraphValueKind.PartTree)],
            Controls =
            [
                new GraphControlDescriptor("name", "分区名", GraphControlKind.Text),
                new GraphControlDescriptor("tag", "标签（无模板时）", GraphControlKind.Text),
                new GraphControlDescriptor("class", "类名（无模板时）", GraphControlKind.Text),
            ],
            Executor = context =>
            {
                var source = context.InputValue("nodes");
                var nodes = source.IsNone ? [] : source.AsNodes();
                var template = context.InputValue("template");
                var parts = new List<GraphPart>(nodes.Count);

                if (!template.IsNone && template.AsParts() is { Count: > 0 } templateParts)
                {
                    // 模板只描述形状，逐条深拷贝并替换字面量占位符。
                    for (var index = 0; index < nodes.Count; index++)
                    {
                        foreach (var part in templateParts)
                        {
                            parts.Add(InterpolatePart(part, nodes[index], index));
                        }
                    }
                }
                else
                {
                    // 没有模板时按条目生成最小可用部件，避免调了半天什么都不出。
                    var name = NormalizePartName(Text(context, "name"));
                    var tag = NormalizePartTag(Text(context, "tag") ?? "li");
                    var cssClass = SanitizeClass(Text(context, "class"));
                    foreach (var node in nodes)
                    {
                        parts.Add(new GraphPart(
                            Name: name,
                            Tag: tag,
                            CssClass: cssClass,
                            Children: [],
                            SlotText: node.Text,
                            IsCustom: true));
                    }
                }

                return Emit(("parts", GraphValue.FromParts(parts)));
            },
        },
    ];

    /// <summary>把通用节点注册进图注册表；已存在同名定义时保留先注册的一方。</summary>
    public static void RegisterInto(GraphNodeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        foreach (var definition in Definitions)
        {
            if (!registry.Contains(definition.Id))
            {
                registry.Register(definition);
            }
        }
    }

    private static IReadOnlyDictionary<string, GraphValue> Emit(params (string Port, GraphValue Value)[] values) =>
        values.ToDictionary(item => item.Port, item => item.Value, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, GraphValue> Divide(
        GraphEvaluationContext context,
        double left,
        double right)
    {
        if (Math.Abs(right) < 1e-12)
        {
            context.Report("graph.divide_by_zero", "除数为 0，输出 0。", "b");
            return Emit(("value", GraphValue.FromNumber(0)));
        }

        return Emit(("value", GraphValue.FromNumber(left / right)));
    }

    private static string? Text(GraphEvaluationContext context, string portName)
    {
        var value = context.InputValue(portName);
        return value.IsNone ? null : value.Payload as string;
    }

    private static double Number(GraphEvaluationContext context, string portName, double fallback)
    {
        var value = context.InputValue(portName);
        return !value.IsNone && value.Kind == GraphValueKind.Number ? value.AsNumber() : fallback;
    }

    private static bool Flag(GraphEvaluationContext context, string portName, bool fallback)
    {
        var value = context.InputValue(portName);
        return !value.IsNone && value.Kind == GraphValueKind.Bool ? value.AsBool() : fallback;
    }

    private static string? FieldValue(MarkdownNode node, string field) => field switch
    {
        "text" => node.Text,
        "raw" => node.RawMarkdown,
        "level" => node.Level?.ToString(),
        _ when field.StartsWith("attr:", StringComparison.Ordinal) =>
            node.Attributes.GetValueOrDefault(field["attr:".Length..]),
        _ => null,
    };

    /// <summary>逐条展开模板：递归复制部件，只替换槽内容与类名里的占位符，分区名保持稳定以维持槽位映射。</summary>
    private static GraphPart InterpolatePart(GraphPart part, MarkdownNode node, int index) =>
        part with
        {
            CssClass = Interpolate(part.CssClass, node, index, encode: false),
            SlotText = part.SlotText is null ? null : Interpolate(part.SlotText, node, index, encode: false),
            SlotHtml = part.SlotHtml is null ? null : Interpolate(part.SlotHtml, node, index, encode: true),
            StyleCss = part.StyleCss is null ? null : Interpolate(part.StyleCss, node, index, encode: false),
            Attributes = part.Attributes?
                .Select(attribute => new GraphAttribute(
                    attribute.Name,
                    attribute.Value is null ? null : Interpolate(attribute.Value, node, index, encode: true)))
                .ToList(),
            Children = part.Children.Select(child => InterpolatePart(child, node, index)).ToList(),
            IsCustom = true,
        };

    /// <summary>替换 <c>{text}</c>/<c>{raw}</c>/<c>{level}</c>/<c>{index}</c>/<c>{attr:key}</c>；未知占位符原样保留。</summary>
    private static string Interpolate(string template, MarkdownNode node, int index, bool encode)
    {
        if (template.IndexOf('{') < 0)
        {
            return template;
        }

        var builder = new StringBuilder(template.Length + 16);
        for (var position = 0; position < template.Length; position++)
        {
            if (template[position] != '{')
            {
                builder.Append(template[position]);
                continue;
            }

            var end = template.IndexOf('}', position + 1);
            if (end < 0)
            {
                builder.Append(template.AsSpan(position));
                break;
            }

            var token = template[(position + 1)..end];
            var value = ResolveToken(token, node, index);
            builder.Append(encode ? System.Net.WebUtility.HtmlEncode(value) : value);
            position = end;
        }

        return builder.ToString();
    }

    private static string ResolveToken(string token, MarkdownNode node, int index) => token switch
    {
        "text" => node.Text ?? string.Empty,
        "raw" => node.RawMarkdown ?? string.Empty,
        "level" => node.Level?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        "index" => (index + 1).ToString(CultureInfo.InvariantCulture),
        _ when token.StartsWith("attr:", StringComparison.Ordinal) =>
            node.Attributes.GetValueOrDefault(token["attr:".Length..]) ?? string.Empty,
        _ => "{" + token + "}",
    };

    /// <summary>标签只保留字母数字，非法或空值回落 div，避免把任意文本拼进标签名。</summary>
    private static string NormalizeTag(string? tag)
    {
        var trimmed = (tag ?? string.Empty).Trim();
        if (trimmed.Length == 0 || !trimmed.All(char.IsLetterOrDigit))
        {
            return "div";
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>分区名为空时回落 content，只留字母数字与连字符，保证能当槽位键。</summary>
    private static string NormalizePartName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return "content";
        }

        var buffer = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                buffer.Append(ch);
            }
        }

        return buffer.Length == 0 ? "content" : buffer.ToString();
    }

    private static string? NormalizePartTag(string? tag)
    {
        var trimmed = (tag ?? string.Empty).Trim();
        return trimmed.Length == 0 ? "div" : NormalizeTag(trimmed);
    }

    /// <summary>类名去掉会提前闭合属性的字符，多类名之间的空格保留。</summary>
    private static string SanitizeClass(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (ch is '"' or '\'' or '<' or '>' or '&')
            {
                continue;
            }

            buffer.Append(ch);
        }

        return buffer.ToString().Trim();
    }
}
