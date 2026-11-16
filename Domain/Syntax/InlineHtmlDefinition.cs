namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

/// <summary>行内 HTML 标签的处置方式，取值来自数据面声明。</summary>
public enum InlineHtmlDisposition
{
    /// <summary>保留为真元素（标签名与已放行属性重新拼装后输出）。</summary>
    Allow,

    /// <summary>丢弃标签、保留内部内容（与翻案前的行为一致）。</summary>
    Unwrap,

    /// <summary>丢弃标签连同内部内容（脚本类等危险标签）。</summary>
    Strip
}

/// <summary>
/// 受控 HTML 白名单声明（<c>&lt;inline-html&gt;</c> 与 <c>&lt;block-html&gt;</c> 两节共用此形状）。
/// 只描述“哪些可以放行”，永远不能放宽机制层的地板（<c>on*</c>、<c>class</c>、<c>data-*</c>、
/// <c>id</c> 与危险协议）；<c>style</c> 不走属性声明，只能由标签上的
/// <see cref="InlineHtmlTagDefinition.StyleLayout"/> 开关放行、且必须过布局子集校验。
/// </summary>
public sealed record InlineHtmlDefinition
{
    /// <summary>空声明：等价于“全部不保留”，解析产物与翻案前逐字节一致。</summary>
    public static InlineHtmlDefinition Empty { get; } = new();

    /// <summary>未在 <see cref="Tags"/> 里出现的标签怎么处置。</summary>
    public InlineHtmlDisposition DefaultDisposition { get; init; } = InlineHtmlDisposition.Unwrap;

    /// <summary>行内标签嵌套深度上限；超限的那一层按未声明处置。</summary>
    public int MaxDepth { get; init; } = DefaultMaxDepth;

    /// <summary>单个标签保留的属性数上限。</summary>
    public int MaxAttributes { get; init; } = DefaultMaxAttributes;

    /// <summary>单个属性值长度上限；超出则整条属性丢弃。</summary>
    public int MaxAttributeLength { get; init; } = DefaultMaxAttributeLength;

    /// <summary>标签名长度上限；超出按未知标签处理。</summary>
    public int MaxTagNameLength { get; init; } = DefaultMaxTagNameLength;

    /// <summary>标签声明集合，顺序即文件书写顺序。</summary>
    public IReadOnlyList<InlineHtmlTagDefinition> Tags { get; init; } = [];

    public const int DefaultMaxDepth = 8;
    public const int DefaultMaxAttributes = 8;
    public const int DefaultMaxAttributeLength = 256;
    public const int DefaultMaxTagNameLength = 16;

    /// <summary>未声明任何标签：解析器把每个标签都当未声明处置，不产生 HtmlInline 节点。</summary>
    public bool IsEmpty => Tags.Count == 0;

    /// <summary>按标签名（忽略大小写）取声明；命中多条时取首条。</summary>
    public InlineHtmlTagDefinition? FindTag(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;

        foreach (var tag in Tags)
        {
            if (string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return tag;
            }
        }

        return null;
    }
}

/// <summary>单个标签的声明。</summary>
public sealed record InlineHtmlTagDefinition
{
    public required string Name { get; init; }

    public InlineHtmlDisposition Disposition { get; init; } = InlineHtmlDisposition.Allow;

    /// <summary>空元素（如 <c>br</c>）：不压栈、不需要闭合标签。</summary>
    public bool IsVoid { get; init; }

    /// <summary>
    /// 放行 <c>style</c> 属性：值不走原样透传，必须先过
    /// <see cref="UserBlockStyleRules.Normalize"/> 的布局子集（只减不增）。
    /// 未开启的标签上写 <c>style</c> 与翻案前一致：整条属性丢弃。
    /// </summary>
    public bool StyleLayout { get; init; }

    /// <summary>该标签允许透传的属性声明。</summary>
    public IReadOnlyList<InlineHtmlAttributeDefinition> Attributes { get; init; } = [];

    public InlineHtmlAttributeDefinition? FindAttribute(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;

        foreach (var attribute in Attributes)
        {
            if (string.Equals(attribute.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return attribute;
            }
        }

        return null;
    }
}

/// <summary>单个属性的声明。</summary>
public sealed record InlineHtmlAttributeDefinition
{
    public required string Name { get; init; }

    /// <summary><c>text</c>（缺省）、<c>url</c> 或 <c>integer</c>；只有 <c>url</c> 做协议校验，<c>integer</c> 做整数收窄。</summary>
    public string Kind { get; init; } = TextKind;

    /// <summary><c>kind="url"</c> 时可放行的协议集合（上限，机制地板仍会再收一次）。</summary>
    public IReadOnlyList<string> Protocols { get; init; } = [];

    /// <summary>该属性值的长度上限；0 表示沿用 <see cref="InlineHtmlDefinition.MaxAttributeLength"/>。</summary>
    public int MaxLength { get; init; }

    /// <summary><c>kind="integer"</c> 时的下界。</summary>
    public int Min { get; init; } = DefaultIntegerMin;

    /// <summary><c>kind="integer"</c> 时的上界。</summary>
    public int Max { get; init; } = DefaultIntegerMax;

    /// <summary>
    /// 机制层常量：值由定义给出、不接受实例值（<c>value-source="constant"</c>）。
    /// 作者写不出这个属性的值，因此它不属于“可实例化属性”。
    /// </summary>
    public string? Constant { get; init; }

    /// <summary>
    /// 常量的发射条件；<c>null</c> 表示无条件，
    /// <see cref="AbsoluteHttpCondition"/> 表示同标签存在绝对 <c>http</c>/<c>https</c> 链接时才发射。
    /// </summary>
    public string? ConstantWhen { get; init; }

    public const string TextKind = "text";
    public const string UrlKind = "url";
    public const string IntegerKind = "integer";

    public const string InstanceValueSource = "instance";
    public const string ConstantValueSource = "constant";
    public const string AbsoluteHttpCondition = "absolute-http";

    public const int DefaultIntegerMin = 0;
    public const int DefaultIntegerMax = 4096;

    public bool IsUrl => string.Equals(Kind, UrlKind, StringComparison.OrdinalIgnoreCase);

    public bool IsInteger => string.Equals(Kind, IntegerKind, StringComparison.OrdinalIgnoreCase);

    public bool IsConstant => Constant is not null;
}
