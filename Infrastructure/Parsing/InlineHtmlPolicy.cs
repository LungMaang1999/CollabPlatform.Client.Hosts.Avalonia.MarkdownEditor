using System.Globalization;
using System.Text;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>单个属性的判定结果。</summary>
internal enum InlineHtmlAttributeVerdict
{
    Accepted,
    Dropped,
    ProtocolRejected,

    /// <summary><c>style</c> 通过了标签开关，但没有任何声明落在布局子集内。</summary>
    StyleRejected
}

/// <summary>行内标签 token（解析期中间形态，不是节点）。</summary>
internal readonly record struct InlineHtmlTagToken(
    bool IsClosing,
    bool IsSelfClosing,
    string Name,
    IReadOnlyList<InlineHtmlAttributeToken> Attributes);

/// <summary>行内标签属性 token（名字与原始值，未转义）。</summary>
internal readonly record struct InlineHtmlAttributeToken(string Name, string Value);

/// <summary>
/// 行内 HTML 的解析期策略：把数据面声明（<see cref="InlineHtmlDefinition"/>）
/// 与机制地板（<see cref="InlineHtmlRules"/>）合起来回答三件事——
/// 这个标签留不留、这个属性留不留、这个 URL 协议能不能过。<b>不发射任何 HTML。</b>
/// </summary>
internal sealed class InlineHtmlPolicy
{
    private readonly Dictionary<string, InlineHtmlTagDefinition> _tags;

    public InlineHtmlPolicy(InlineHtmlDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _tags = new Dictionary<string, InlineHtmlTagDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in definition.Tags)
        {
            _tags.TryAdd(tag.Name, tag);
        }
    }

    public InlineHtmlDefinition Definition { get; }

    public int MaxDepth => Definition.MaxDepth;

    public int MaxAttributes => Definition.MaxAttributes;

    public int MaxAttributeLength => Definition.MaxAttributeLength;

    public int MaxTagNameLength => Definition.MaxTagNameLength;

    public InlineHtmlTagDefinition? FindTag(string name) =>
        _tags.TryGetValue(name, out var tag) ? tag : null;

    /// <summary>
    /// 标签的最终处置：数据声明与机制地板取交集。
    /// 未声明 → <see cref="InlineHtmlDefinition.DefaultDisposition"/>；
    /// 声明成 <c>allow</c> 但名字在地板里 → 强制 <c>strip</c>。
    /// </summary>
    public InlineHtmlDisposition ResolveDisposition(InlineHtmlTagDefinition? tag, string name)
    {
        if (tag is null)
        {
            return InlineHtmlRules.IsForbiddenTag(name)
                ? InlineHtmlDisposition.Strip
                : Definition.DefaultDisposition;
        }

        if (tag.Disposition == InlineHtmlDisposition.Allow && InlineHtmlRules.IsForbiddenTag(tag.Name))
        {
            return InlineHtmlDisposition.Strip;
        }

        return tag.Disposition;
    }

    /// <summary>
    /// 单个属性的判定。按“数据声明 → 机制地板 → 长度 → 协议”的顺序收敛，
    /// 任何一步不通过都不发射该属性（标签与文字照常保留）。
    /// </summary>
    public InlineHtmlAttributeVerdict ResolveAttribute(
        InlineHtmlTagDefinition tag,
        InlineHtmlAttributeToken attribute,
        out string value)
    {
        value = attribute.Value;

        var name = attribute.Name.ToLowerInvariant();
        if (!InlineHtmlRules.IsValidAttributeName(name))
        {
            return InlineHtmlAttributeVerdict.Dropped;
        }

        // style 不走属性声明：只能由标签的 style-layout 开关放行，且必须过布局子集。
        if (InlineHtmlRules.IsStyleAttribute(name))
        {
            return ResolveStyle(tag, attribute.Value, out value);
        }

        if (InlineHtmlRules.IsForbiddenAttribute(name)
            || InlineHtmlRules.IsMechanismConstantAttribute(name))
        {
            return InlineHtmlAttributeVerdict.Dropped;
        }

        var declaration = tag.FindAttribute(name);
        if (declaration is null || declaration.IsConstant)
        {
            return InlineHtmlAttributeVerdict.Dropped;
        }

        var limit = declaration.MaxLength > 0 ? declaration.MaxLength : MaxAttributeLength;
        if (attribute.Value.Length > limit)
        {
            return InlineHtmlAttributeVerdict.Dropped;
        }

        if (declaration.IsInteger)
        {
            return ResolveInteger(declaration, attribute.Value, out value);
        }

        if (!declaration.IsUrl)
        {
            return InlineHtmlAttributeVerdict.Accepted;
        }

        var scheme = ExtractScheme(attribute.Value);
        if (scheme is null)
        {
            return InlineHtmlAttributeVerdict.Accepted; // 相对地址：协议继承页面
        }

        if (InlineHtmlRules.IsForbiddenProtocol(scheme))
        {
            return InlineHtmlAttributeVerdict.ProtocolRejected;
        }

        foreach (var allowed in declaration.Protocols)
        {
            if (string.Equals(allowed, scheme, StringComparison.OrdinalIgnoreCase))
            {
                return InlineHtmlAttributeVerdict.Accepted;
            }
        }

        return InlineHtmlAttributeVerdict.ProtocolRejected;
    }

    /// <summary>
    /// <c>kind="integer"</c>：只接受纯十进制数字，收窄到声明的 <c>min</c>/<c>max</c>，
    /// 输出规范化为无前导零的十进制串（<c>width="0100"</c> → <c>100</c>）。
    /// </summary>
    private static InlineHtmlAttributeVerdict ResolveInteger(
        InlineHtmlAttributeDefinition declaration,
        string raw,
        out string value)
    {
        value = raw;
        var text = raw.Trim();
        if (text.Length is 0 or > 12)
        {
            return InlineHtmlAttributeVerdict.Dropped;
        }

        foreach (var ch in text)
        {
            if (!char.IsAsciiDigit(ch))
            {
                return InlineHtmlAttributeVerdict.Dropped;
            }
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number < declaration.Min
            || number > declaration.Max)
        {
            return InlineHtmlAttributeVerdict.Dropped;
        }

        value = number.ToString(CultureInfo.InvariantCulture);
        return InlineHtmlAttributeVerdict.Accepted;
    }

    /// <summary>
    /// <c>style</c> 通道：标签必须开 <see cref="InlineHtmlTagDefinition.StyleLayout"/>；
    /// 值交给 <see cref="UserBlockStyleRules.Normalize"/>（布局档）做白名单与语法校验，
    /// 输出的是规范化串，绝不回写原文。
    /// </summary>
    private static InlineHtmlAttributeVerdict ResolveStyle(
        InlineHtmlTagDefinition tag,
        string raw,
        out string value)
    {
        value = string.Empty;
        if (!tag.StyleLayout)
        {
            return InlineHtmlAttributeVerdict.Dropped;
        }

        var normalized = UserBlockStyleRules.Normalize(raw, rejected: null, layoutOnly: true);
        if (string.IsNullOrEmpty(normalized))
        {
            return InlineHtmlAttributeVerdict.StyleRejected;
        }

        value = normalized;
        return InlineHtmlAttributeVerdict.Accepted;
    }

    /// <summary>该值是否是绝对 <c>http</c>/<c>https</c> 地址（相对地址与其它协议返回 <c>false</c>）。</summary>
    internal static bool IsAbsoluteHttpUrl(string raw)
    {
        var scheme = ExtractScheme(raw);
        return scheme is "http" or "https";
    }

    /// <summary>
    /// 取 URL 的协议：先做一次 HTML 字符引用解码（<c>&amp;#106;avascript:</c> 这类混淆），
    /// 再剥掉全部控制字符与空白（<c>java\tscript:</c> 这类混淆），最后取首个分隔符之前的 scheme。
    /// 没有 scheme（相对地址、片段、协议相对地址）返回 <c>null</c>。
    /// </summary>
    internal static string? ExtractScheme(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;

        var decoded = DecodeCharacterReferences(raw);
        var builder = new StringBuilder(decoded.Length);
        foreach (var ch in decoded)
        {
            if (ch <= ' ' || ch == '\u007f') continue;
            builder.Append(ch);
        }

        var value = builder.ToString();
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (ch == ':')
            {
                return index == 0 ? null : value[..index].ToLowerInvariant();
            }

            if (ch is '/' or '?' or '#')
            {
                return null; // 分隔符先出现：这是相对地址
            }
        }

        return null;
    }

    /// <summary>只解码用于判定的实体；输出仍用原值并统一转义。</summary>
    private static string DecodeCharacterReferences(string raw)
    {
        if (raw.IndexOf('&') < 0) return raw;

        var builder = new StringBuilder(raw.Length);
        for (var index = 0; index < raw.Length; index++)
        {
            var ch = raw[index];
            if (ch != '&')
            {
                builder.Append(ch);
                continue;
            }

            var semicolon = raw.IndexOf(';', index + 1);
            if (semicolon < 0 || semicolon - index > 10)
            {
                builder.Append(ch);
                continue;
            }

            var entity = raw[(index + 1)..semicolon];
            if (!TryDecodeEntity(entity, out var decoded))
            {
                builder.Append(ch);
                continue;
            }

            builder.Append(decoded);
            index = semicolon;
        }

        return builder.ToString();
    }

    private static bool TryDecodeEntity(string entity, out char decoded)
    {
        decoded = '\0';
        if (entity.Length == 0) return false;

        if (entity[0] == '#')
        {
            var digits = entity[1..];
            var hex = digits.StartsWith("x", StringComparison.OrdinalIgnoreCase);
            if (hex) digits = digits[1..];
            if (digits.Length == 0) return false;

            if (!int.TryParse(
                    digits,
                    hex ? NumberStyles.HexNumber : NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var code)
                || code is < 1 or > 0xFFFF)
            {
                return false;
            }

            decoded = (char)code;
            return true;
        }

        switch (entity.ToLowerInvariant())
        {
            case "amp": decoded = '&'; return true;
            case "lt": decoded = '<'; return true;
            case "gt": decoded = '>'; return true;
            case "quot": decoded = '"'; return true;
            case "apos": decoded = '\''; return true;
            case "colon": decoded = ':'; return true;
            case "tab": decoded = '\t'; return true;
            case "newline": decoded = '\n'; return true;
            default: return false;
        }
    }
}

/// <summary>
/// 标签 token 的容错解析：接受 <c>&lt;kbd&gt;</c>、<c>&lt;/kbd&gt;</c>、<c>&lt;br/&gt;</c>、
/// 带引号/不带引号的属性。注释、声明、处理指令一律返回 <c>false</c>（按“不是标签”丢弃）。
/// </summary>
internal static class InlineHtmlTagParser
{
    public static bool TryParse(string? raw, int maxTagNameLength, out InlineHtmlTagToken token)
    {
        token = default;
        if (string.IsNullOrEmpty(raw)) return false;

        var text = raw.Trim();
        if (text.Length < 3 || text[0] != '<') return false;
        if (text[1] is '!' or '?') return false; // 注释/声明/处理指令：不是元素标签

        var index = 1;
        var isClosing = text[index] == '/';
        if (isClosing) index++;
        if (index >= text.Length || !IsAsciiLetter(text[index])) return false;

        var nameStart = index;
        while (index < text.Length && IsNameChar(text[index])) index++;
        var name = text[nameStart..index].ToLowerInvariant();
        if (name.Length > maxTagNameLength) return false;

        var attributes = new List<InlineHtmlAttributeToken>();
        var selfClosing = false;

        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            if (index >= text.Length) return false;

            if (text[index] == '>')
            {
                index++;
                return index == text.Length && Build(isClosing, selfClosing, name, attributes, out token);
            }

            if (text[index] == '/')
            {
                index++;
                if (index < text.Length && text[index] == '>')
                {
                    index++;
                    selfClosing = true;
                    return index == text.Length && Build(isClosing, selfClosing, name, attributes, out token);
                }

                return false;
            }

            if (isClosing) return false; // 闭合标签不允许带属性

            var attributeNameStart = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index])
                   && text[index] is not ('=' or '>' or '/'))
            {
                index++;
            }

            if (index == attributeNameStart) return false;
            var attributeName = text[attributeNameStart..index];

            var value = string.Empty;
            var save = index;
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            if (index < text.Length && text[index] == '=')
            {
                index++;
                while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
                if (index >= text.Length) return false;

                if (text[index] is '"' or '\'')
                {
                    var quote = text[index];
                    index++;
                    var valueStart = index;
                    while (index < text.Length && text[index] != quote) index++;
                    if (index >= text.Length) return false; // 引号未闭合：整个标签不成立
                    value = text[valueStart..index];
                    index++;
                }
                else
                {
                    var valueStart = index;
                    while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] != '>') index++;
                    value = text[valueStart..index];
                }
            }
            else
            {
                index = save;
            }

            attributes.Add(new InlineHtmlAttributeToken(attributeName, value));
        }

        return false;
    }

    private static bool Build(
        bool isClosing,
        bool selfClosing,
        string name,
        List<InlineHtmlAttributeToken> attributes,
        out InlineHtmlTagToken token)
    {
        token = new InlineHtmlTagToken(isClosing, selfClosing, name, attributes);
        return true;
    }

    private static bool IsNameChar(char ch) =>
        IsAsciiLetter(ch) || char.IsAsciiDigit(ch) || ch == '-';

    private static bool IsAsciiLetter(char ch) => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}
