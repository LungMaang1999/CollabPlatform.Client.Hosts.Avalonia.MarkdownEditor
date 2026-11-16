using System;
using System.Collections.Generic;
using System.Text;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

/// <summary>声明属性在编辑侧的控件形态。Auto 时仍按值类型推断。</summary>
public enum ElementAttributeControlKind
{
    Auto = 0,
    Text = 1,
    MultilineText = 2,
    Number = 3,
    Slider = 4,
    Toggle = 5,
    Select = 6,
    Color = 7,
    Url = 8
}

/// <summary>编辑侧字段占宽。</summary>
public enum ElementAttributeFieldWidth
{
    Auto = 0,
    Full = 1,
    Half = 2,
    Compact = 3
}

/// <summary>条件显示支持的运算符，无表达式引擎。</summary>
public enum ElementAttributeConditionOperator
{
    Equals = 0,
    NotEquals = 1,
    In = 2,
    NotEmpty = 3
}

/// <summary>条件显示的一项：同类型另一个声明键 + 运算符 + 比较值。</summary>
public sealed class ElementAttributeVisibilityCondition
{
    public string Key { get; set; } = string.Empty;

    public ElementAttributeConditionOperator Operator { get; set; } =
        ElementAttributeConditionOperator.Equals;

    /// <summary>
    /// 无法识别的运算符原文。非空时条件求值恒不成立，但写回文件时保留原文，
    /// 避免读一次就把用户手写的运算符改成默认值。
    /// </summary>
    public string? UnknownOperator { get; set; }

    /// <summary>比较值；<see cref="ElementAttributeConditionOperator.In"/> 用逗号分隔多项。</summary>
    public string Value { get; set; } = string.Empty;

    public bool IsKnownOperator => string.IsNullOrEmpty(UnknownOperator);

    public ElementAttributeVisibilityCondition Clone() => new()
    {
        Key = Key,
        Operator = Operator,
        UnknownOperator = UnknownOperator,
        Value = Value
    };
}

/// <summary>
/// 类型级呈现设置：只影响编辑侧长什么样，不参与渲染。
/// 全默认时序列化不写节点，老文件缺节点等价于全默认。
/// </summary>
public sealed class ElementAttributePresentation
{
    public ElementAttributeControlKind Control { get; set; } = ElementAttributeControlKind.Auto;

    public string Placeholder { get; set; } = string.Empty;

    public string Unit { get; set; } = string.Empty;

    public ElementAttributeFieldWidth Width { get; set; } = ElementAttributeFieldWidth.Auto;

    /// <summary>排序权重，null 表示不参与显式排序。</summary>
    public int? Order { get; set; }

    public bool IsAdvanced { get; set; }

    public double? Min { get; set; }

    public double? Max { get; set; }

    public double? Step { get; set; }

    public int? Precision { get; set; }

    public int? Rows { get; set; }

    public int? MaxLength { get; set; }

    public string Pattern { get; set; } = string.Empty;

    public List<ElementAttributeVisibilityCondition> VisibleWhen { get; } = [];

    public bool IsDefault =>
        Control == ElementAttributeControlKind.Auto
        && Width == ElementAttributeFieldWidth.Auto
        && Order is null
        && !IsAdvanced
        && Min is null
        && Max is null
        && Step is null
        && Precision is null
        && Rows is null
        && MaxLength is null
        && string.IsNullOrEmpty(Placeholder)
        && string.IsNullOrEmpty(Unit)
        && string.IsNullOrEmpty(Pattern)
        && VisibleWhen.Count == 0;

    public ElementAttributePresentation Clone()
    {
        var clone = new ElementAttributePresentation
        {
            Control = Control,
            Placeholder = Placeholder,
            Unit = Unit,
            Width = Width,
            Order = Order,
            IsAdvanced = IsAdvanced,
            Min = Min,
            Max = Max,
            Step = Step,
            Precision = Precision,
            Rows = Rows,
            MaxLength = MaxLength,
            Pattern = Pattern
        };

        foreach (var condition in VisibleWhen)
        {
            clone.VisibleWhen.Add(condition.Clone());
        }

        return clone;
    }
}

/// <summary>
/// 条件显示求值：只做同类型声明之间的 AND 列表，不跨节点、不嵌套。
/// 引用不存在的键视为条件不成立；未知运算符同样不成立。
/// </summary>
public static class ElementAttributePresentationEvaluator
{
    public static bool IsVisible(
        ElementAttributePresentation presentation,
        IReadOnlyDictionary<string, string?> values)
    {
        if (presentation.VisibleWhen.Count == 0)
        {
            return true;
        }

        foreach (var condition in presentation.VisibleWhen)
        {
            if (!condition.IsKnownOperator)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(condition.Key)
                || !values.TryGetValue(condition.Key, out var current))
            {
                return false;
            }

            if (!Matches(condition, current ?? string.Empty))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Matches(
        ElementAttributeVisibilityCondition condition,
        string current)
    {
        switch (condition.Operator)
        {
            case ElementAttributeConditionOperator.Equals:
                return string.Equals(current, condition.Value, StringComparison.Ordinal);

            case ElementAttributeConditionOperator.NotEquals:
                return !string.Equals(current, condition.Value, StringComparison.Ordinal);

            case ElementAttributeConditionOperator.NotEmpty:
                return !string.IsNullOrWhiteSpace(current);

            case ElementAttributeConditionOperator.In:
                foreach (var candidate in SplitValues(condition.Value))
                {
                    if (string.Equals(current, candidate, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    private static IEnumerable<string> SplitValues(string value)
    {
        foreach (var item in value.Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return item;
        }
    }

    /// <summary>拼出用于样式指纹/相等判断的稳定文本，空表示全默认。</summary>
    public static string Describe(ElementAttributePresentation presentation)
    {
        if (presentation.IsDefault)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append(presentation.Control)
            .Append('|').Append(presentation.Width)
            .Append('|').Append(presentation.Order?.ToString() ?? "-")
            .Append('|').Append(presentation.IsAdvanced ? '1' : '0')
            .Append('|').Append(presentation.Min?.ToString() ?? "-")
            .Append('|').Append(presentation.Max?.ToString() ?? "-")
            .Append('|').Append(presentation.Step?.ToString() ?? "-")
            .Append('|').Append(presentation.Precision?.ToString() ?? "-")
            .Append('|').Append(presentation.Rows?.ToString() ?? "-")
            .Append('|').Append(presentation.MaxLength?.ToString() ?? "-")
            .Append('|').Append(presentation.Placeholder)
            .Append('|').Append(presentation.Unit)
            .Append('|').Append(presentation.Pattern);

        foreach (var condition in presentation.VisibleWhen)
        {
            builder.Append("|C:")
                .Append(condition.Key)
                .Append(':').Append(condition.UnknownOperator ?? condition.Operator.ToString())
                .Append(':').Append(condition.Value);
        }

        return builder.ToString();
    }
}
