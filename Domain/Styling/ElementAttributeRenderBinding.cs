using System;
using System.Collections.Generic;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

/// <summary>属性值在部件上的注入通道。</summary>
public enum ElementAttributeBindingTarget
{
    /// <summary>写到部件内联样式的自定义属性：<c>--name: value;</c></summary>
    CssVar,

    /// <summary>写到部件上的 data 属性：<c>data-name="value"</c></summary>
    DataAttribute,

    /// <summary>作为 class 追加到部件上。</summary>
    ClassToken
}

/// <summary>
/// 类型级绑定：把某个声明属性的实例值注入到某个部件的样式/属性/类名上。
/// 只影响呈现，不决定部件是否存在。
/// </summary>
public sealed class ElementAttributeRenderBinding
{
    public ElementAttributeBindingTarget Target { get; set; } = ElementAttributeBindingTarget.CssVar;

    /// <summary>目标名。必须通过 <see cref="ElementRenderBindingRules"/> 的白名单校验。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>目标部件；空表示块根。</summary>
    public string? Part { get; set; }

    /// <summary>可选值映射，形如 <c>true=is-active;false=is-idle</c>。</summary>
    public string? ValueMap { get; set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Name);

    public ElementAttributeRenderBinding Clone() => new()
    {
        Target = Target,
        Name = Name,
        Part = Part,
        ValueMap = ValueMap,
    };
}

public readonly record struct RenderBindingValidation(bool IsValid, string? Message)
{
    public static RenderBindingValidation Ok => new(true, null);

    public static RenderBindingValidation Fail(string message) => new(false, message);
}

/// <summary>
/// 绑定白名单：目标名、部部件与取值都按固定形态校验，任何一项不合法就整体拒绝注入。
/// 校验只做“允许什么”，不做隐式修正，避免渲染端出现半生效的绑定。
/// </summary>
public static class ElementRenderBindingRules
{
    public const int MaxNameLength = 64;
    public const int MaxValueLength = 200;
    public const int MaxValueMapEntries = 32;

    public static RenderBindingValidation Validate(
        NodeType nodeType,
        ElementAttributeDeclaration declaration,
        ElementAttributeRenderBinding binding)
    {
        if (string.IsNullOrWhiteSpace(declaration.Key))
        {
            return RenderBindingValidation.Fail("绑定所属的属性键为空。");
        }

        if (binding.IsEmpty)
        {
            return RenderBindingValidation.Fail("绑定名为空。");
        }

        if (binding.Name.Length > MaxNameLength)
        {
            return RenderBindingValidation.Fail($"绑定名超过 {MaxNameLength} 个字符。");
        }

        switch (binding.Target)
        {
            case ElementAttributeBindingTarget.CssVar:
                if (!IsCssVarName(binding.Name))
                {
                    return RenderBindingValidation.Fail(
                        $"CSS 变量名「{binding.Name}」不合法，必须形如 --accent-color。");
                }

                break;
            case ElementAttributeBindingTarget.DataAttribute:
                if (!IsDataAttributeName(binding.Name))
                {
                    return RenderBindingValidation.Fail(
                        $"data 属性名「{binding.Name}」不合法，必须形如 data-tone。");
                }

                break;
            case ElementAttributeBindingTarget.ClassToken:
                if (!IsClassToken(binding.Name))
                {
                    return RenderBindingValidation.Fail(
                        $"类名「{binding.Name}」不合法，只能使用小写字母、数字与连字符。");
                }

                break;
            default:
                return RenderBindingValidation.Fail("未知的绑定目标。");
        }

        var part = string.IsNullOrWhiteSpace(binding.Part) ? PartTokens.Root : binding.Part.Trim();
        if (PartCatalog.Find(nodeType, part) is null)
        {
            return RenderBindingValidation.Fail(
                $"部件「{part}」不在 {nodeType} 的部件目录中。");
        }

        if (!string.IsNullOrWhiteSpace(binding.ValueMap)
            && !TryParseValueMap(binding.ValueMap, out var entries, out var mapError))
        {
            return RenderBindingValidation.Fail(mapError!);
        }

        return RenderBindingValidation.Ok;
    }

    /// <summary>
    /// 解析值映射并适配绑定目标：键是原始属性值，值是最终注入内容。
    /// 未配置映射时直接把原始值按目标规则清洗。
    /// </summary>
    public static string? ResolveValue(
        ElementAttributeBindingTarget target,
        string? valueMap,
        string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var value = rawValue.Trim();

        if (!string.IsNullOrWhiteSpace(valueMap) && TryParseValueMap(valueMap, out var entries, out _))
        {
            if (!entries.TryGetValue(value, out var mapped))
            {
                // 有映射但当前值没有对应项：视为本次不注入，避免把原始值误写进目标。
                return null;
            }

            value = mapped;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return IsValueAllowed(target, value) ? value : null;
    }

    public static bool IsValueAllowed(ElementAttributeBindingTarget target, string value)
    {
        if (value.Length > MaxValueLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                return false;
            }

            switch (target)
            {
                case ElementAttributeBindingTarget.ClassToken:
                    if (!IsLowerTokenChar(c)) return false;
                    break;
                case ElementAttributeBindingTarget.CssVar:
                    if (c is ';' or '{' or '}' or '<' or '>' or '"' or '\'' or '\\') return false;
                    break;
                case ElementAttributeBindingTarget.DataAttribute:
                    if (c is '<' or '>' or '"' or '\'') return false;
                    break;
            }
        }

        return target != ElementAttributeBindingTarget.ClassToken
               || IsClassToken(value);
    }

    public static bool TryParseValueMap(
        string valueMap,
        out Dictionary<string, string> entries,
        out string? error)
    {
        entries = new Dictionary<string, string>(StringComparer.Ordinal);
        error = null;

        var pairs = valueMap.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pairs.Length == 0)
        {
            error = "值映射为空。";
            return false;
        }

        if (pairs.Length > MaxValueMapEntries)
        {
            error = $"值映射最多 {MaxValueMapEntries} 项。";
            return false;
        }

        foreach (var pair in pairs)
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                error = $"值映射项「{pair}」缺少 '='。";
                return false;
            }

            var key = pair[..separator].Trim();
            var value = pair[(separator + 1)..].Trim();
            if (key.Length == 0)
            {
                error = $"值映射项「{pair}」的源值为空。";
                return false;
            }

            if (!entries.TryAdd(key, value))
            {
                error = $"值映射源值「{key}」重复。";
                return false;
            }
        }

        return true;
    }

    private static bool IsCssVarName(string name) =>
        name.StartsWith("--", StringComparison.Ordinal)
        && name.Length > 2
        && IsLowerToken(name.AsSpan(2));

    private static bool IsDataAttributeName(string name) =>
        name.StartsWith("data-", StringComparison.Ordinal)
        && name.Length > 5
        && IsLowerToken(name.AsSpan(5));

    private static bool IsClassToken(string name) =>
        name.Length > 0
        && !char.IsDigit(name[0])
        && IsLowerToken(name.AsSpan());

    private static bool IsLowerToken(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (!IsLowerTokenChar(c))
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    private static bool IsLowerTokenChar(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-';
}
