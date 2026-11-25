namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 颜色声明解析：行内 <c>`color: #16A34A 品牌绿`</c> 与集中声明围栏共用同一套色值判定。
/// 只接受十六进制色值（<c>#RGB</c>、<c>#RGBA</c>、<c>#RRGGBB</c>、<c>#RRGGBBAA</c>），
/// 其它写法一律判为非法并原样降级，绝不把任意字符串写进样式属性。
/// </summary>
public static class ColorValueParser
{
    private const string Prefix = "color:";

    /// <summary>行内声明前缀之后的第一段是否色值；名称是色值之后的其余文本。</summary>
    public static bool TryParseDeclaration(
        string? content,
        out string value,
        out string name)
    {
        value = string.Empty;
        name = string.Empty;

        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var text = content.Trim();
        if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TrySplitEntry(text[Prefix.Length..], out value, out name);
    }

    /// <summary>集中声明的一行：色值在前，可选的名称在后；空行不算条目。</summary>
    public static bool TryParseEntry(string? line, out string value, out string name) =>
        TrySplitEntry(line, out value, out name);

    /// <summary>单个色值判定：合法时返回大写原文（不展开缩写）。</summary>
    public static bool TryParseValue(string? raw, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim();
        if (text.Length < 4 || text[0] != '#')
        {
            return false;
        }

        var digits = text.AsSpan(1);
        if (digits.Length is not (3 or 4 or 6 or 8))
        {
            return false;
        }

        foreach (var digit in digits)
        {
            if (!Uri.IsHexDigit(digit))
            {
                return false;
            }
        }

        value = text.ToUpperInvariant();
        return true;
    }

    private static bool TrySplitEntry(string? raw, out string value, out string name)
    {
        value = string.Empty;
        name = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim();
        var separator = -1;
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                separator = index;
                break;
            }
        }

        var token = separator < 0 ? text : text[..separator];
        if (!TryParseValue(token, out value))
        {
            return false;
        }

        name = separator < 0
            ? string.Empty
            : text[(separator + 1)..].Trim();
        return true;
    }
}
