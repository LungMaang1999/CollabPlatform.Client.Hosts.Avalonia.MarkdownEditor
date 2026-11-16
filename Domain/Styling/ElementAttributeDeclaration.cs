using System;
using System.Collections.Generic;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

/// <summary>类型声明属性的取值类型，决定实例级面板生成哪种控件。</summary>
public enum ElementAttributeValueKind
{
    Text,
    Number,
    Boolean,
    Color,
    Enum,
    Json,
    Url
}

/// <summary>类型声明属性的写入通道。</summary>
public enum ElementAttributeTarget
{
    /// <summary>块参数：实例值写在源码围栏 info / 标记行里，重解析后由解析器派生。</summary>
    FenceParameter,

    /// <summary>节点属性：实例值存在 <c>MarkdownNode.Attributes</c>，随 .md.styles.xml 持久化。</summary>
    DocumentAttribute
}

/// <summary>
/// 类型级属性声明：某一类语法元素允许拥有哪些属性、默认值与控件形态。
/// 实例面板只负责填值，不再各自维护白名单。
/// </summary>
public sealed class ElementAttributeDeclaration
{
    /// <summary>属性键，必须与解析器写入 Attributes 的键或围栏参数键一致。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>实例面板展示用中文名。</summary>
    public string Label { get; set; } = string.Empty;

    public ElementAttributeValueKind ValueKind { get; set; } = ElementAttributeValueKind.Text;

    /// <summary>实例未显式赋值时采用的默认值，null 表示由渲染器自身兜底。</summary>
    public string? DefaultValue { get; set; }

    /// <summary>枚举可选值；为空表示非枚举。</summary>
    public List<string> Options { get; } = new();

    /// <summary>控件下方的说明文案。</summary>
    public string Hint { get; set; } = string.Empty;

    /// <summary>是否启用；停用后实例面板不出现该属性。</summary>
    public bool IsEnabled { get; set; } = true;

    public ElementAttributeTarget Target { get; set; } = ElementAttributeTarget.DocumentAttribute;

    /// <summary>true 表示键由解析器按源码派生、只读，不随用户编辑独立存在。</summary>
    public bool IsDerived { get; set; }

    /// <summary>实例面板中的分组标题，空表示归入“常规”。</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>把实例值注入到部件呈现层的绑定；null 表示不绑定。</summary>
    public ElementAttributeRenderBinding? Binding { get; set; }

    /// <summary>编辑侧呈现设置；全默认等价于未设置。</summary>
    public ElementAttributePresentation Presentation { get; set; } = new();

    public ElementAttributeDeclaration Clone()
    {
        var clone = new ElementAttributeDeclaration
        {
            Key = Key,
            Label = Label,
            ValueKind = ValueKind,
            DefaultValue = DefaultValue,
            Hint = Hint,
            IsEnabled = IsEnabled,
            Target = Target,
            IsDerived = IsDerived,
            Group = Group,
            Binding = Binding?.Clone(),
            Presentation = Presentation.Clone()
        };

        clone.Options.AddRange(Options);
        return clone;
    }

    public override string ToString() =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{Key} ({ValueKind}, enabled={IsEnabled})");
}
