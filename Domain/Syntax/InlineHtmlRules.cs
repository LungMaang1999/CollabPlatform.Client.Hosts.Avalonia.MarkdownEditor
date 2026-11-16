namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

/// <summary>
/// 行内/块级 HTML 的<b>机制地板</b>：数据面只能在这些约束之内放行，永远不能放宽。
/// 这里不含任何“允许名单”——允许什么由 <see cref="InlineHtmlDefinition"/> 声明，
/// 本类只回答“无论谁声明都不许”。
/// </summary>
public static class InlineHtmlRules
{
    /// <summary>
    /// <c>style</c> 属性的名字。它不走“属性声明”通道（声明成实例属性一律装载期报错），
    /// 只能由标签上的 <c>style-layout</c> 开关放行，且值必须过
    /// <see cref="UserBlockStyleRules.Normalize"/> 的布局子集。
    /// </summary>
    public const string StyleAttributeName = "style";

    /// <summary>任何声明都不得透传的属性名（忽略大小写）。</summary>
    public static readonly IReadOnlySet<string> ForbiddenAttributeNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "class",
            "id",
            "srcset",
            "formaction",
            "xlink:href",
            "srcdoc",
            "http-equiv",
        };

    /// <summary>任何声明都不得以 <c>allow</c> 处置的属性名前缀（忽略大小写）。</summary>
    public static readonly IReadOnlyList<string> ForbiddenAttributePrefixes = ["on", "data-"];

    /// <summary>
    /// 只能由机制层常量通道发射的属性名：作者不可写实例值，
    /// 只能通过 <c>value-source="constant"</c> 由定义给出固定值。
    /// </summary>
    public static readonly IReadOnlySet<string> MechanismConstantAttributeNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "target",
            "rel",
        };

    /// <summary>任何声明都不得放行的标签：即使声明成 <c>allow</c> 也按 <c>strip</c> 处置。</summary>
    public static readonly IReadOnlySet<string> ForbiddenTagNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "script",
            "style",
            "iframe",
            "object",
            "embed",
            "svg",
            "math",
            "template",
            "form",
            "input",
            "button",
            "textarea",
            "select",
            "option",
            "link",
            "meta",
            "base",
            "applet",
            "frame",
            "frameset",
            "noscript",
        };

    /// <summary>任何声明都不得放行的 URL 协议（小写，不含冒号）。</summary>
    public static readonly IReadOnlySet<string> ForbiddenProtocols =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "javascript",
            "vbscript",
            "data",
            "file",
            "blob",
            "about",
            "filesystem",
        };

    /// <summary>标签名：字母开头，仅字母/数字/连字符，长度不超过上限。</summary>
    public static bool IsValidTagName(string? name, int maxLength)
    {
        if (string.IsNullOrEmpty(name) || name.Length > maxLength) return false;
        if (!IsAsciiLetter(name[0])) return false;

        foreach (var ch in name)
        {
            if (!IsAsciiLetter(ch) && !char.IsAsciiDigit(ch) && ch != '-') return false;
        }

        return true;
    }

    /// <summary>属性名：字母开头，仅字母/数字/连字符。</summary>
    public static bool IsValidAttributeName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!IsAsciiLetter(name[0])) return false;

        foreach (var ch in name)
        {
            if (!IsAsciiLetter(ch) && !char.IsAsciiDigit(ch) && ch != '-') return false;
        }

        return true;
    }

    /// <summary>该属性名是否被机制地板永久拒绝（含 <c>on*</c> 与 <c>data-*</c>）。</summary>
    public static bool IsForbiddenAttribute(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        if (ForbiddenAttributeNames.Contains(name)) return true;

        foreach (var prefix in ForbiddenAttributePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>该标签是否被机制地板永久拒绝放行。</summary>
    public static bool IsForbiddenTag(string name) =>
        !string.IsNullOrEmpty(name) && ForbiddenTagNames.Contains(name);

    /// <summary>该属性名是否只能由机制层常量通道发射（<c>target</c>/<c>rel</c>）。</summary>
    public static bool IsMechanismConstantAttribute(string? name) =>
        !string.IsNullOrEmpty(name) && MechanismConstantAttributeNames.Contains(name);

    /// <summary>该属性名是否是受管控的 <c>style</c> 通道。</summary>
    public static bool IsStyleAttribute(string? name) =>
        string.Equals(name, StyleAttributeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 机制层常量的取值是否可安全发射：不允许引号、尖括号、花括号、反斜杠、<c>&amp;</c> 与换行/制表，
    /// 避免常量通道成为第二个属性拼接点。
    /// </summary>
    public static bool IsSafeConstantValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;

        foreach (var ch in value)
        {
            if (ch is '"' or '\'' or '<' or '>' or '{' or '}' or '\\' or '&' or '\r' or '\n' or '\t')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>协议 token 是否合法（字母开头，仅字母/数字/加号/减号/点）。</summary>
    public static bool IsValidProtocolName(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (!IsAsciiLetter(token[0])) return false;

        foreach (var ch in token)
        {
            if (!IsAsciiLetter(ch) && !char.IsAsciiDigit(ch) && ch is not ('+' or '-' or '.')) return false;
        }

        return true;
    }

    /// <summary>该协议是否被机制地板永久拒绝。</summary>
    public static bool IsForbiddenProtocol(string? scheme) =>
        !string.IsNullOrEmpty(scheme) && ForbiddenProtocols.Contains(scheme.ToLowerInvariant());

    private static bool IsAsciiLetter(char ch) => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}
