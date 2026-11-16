using System;
using System.Collections.Generic;
using System.Text;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

/// <summary>
/// 结构部件的盒子布局声明：只接受白名单 CSS 子集，并按固定属性顺序规范化输出。
///
/// 安全边界：这里不做任何原文透传。属性名必须命中白名单，取值必须命中关键字表或
/// 「0 / 非负整数 px / var(--token)」，含引号、尖括号、大括号、分号与未知函数的值整条丢弃。
/// 因此定义里写 <c>expression()</c>、<c>url()</c>、<c>content:</c> 都无法出现在输出里。
///
/// 规范化文本会同时进入：序列化器写出的 <c>style</c> 属性、结构指纹、渲染时输出的
/// 内联 style，三处共用同一份结果，避免“定义里写什么”与“输出什么”各说各话。
/// </summary>
public static class UserBlockStyleRules
{
    /// <summary>规范化后的声明串上限，防止单部件塞入超长样式。</summary>
    public const int MaxStyleLength = 768;

    /// <summary>单个部件允许的声明条数上限（13 个盒子属性 + 11 个排版属性 + 7 个网格属性）。</summary>
    public const int MaxDeclarations = 32;

    /// <summary>规范化输出的属性顺序：与检查器字段顺序一致，保证字节稳定。</summary>
    private static readonly string[] PropertyOrder =
    [
        "display",
        "flex-direction",
        "flex-wrap",
        "justify-content",
        "align-items",
        "gap",
        "padding",
        "margin",
        "border-width",
        "border-style",
        "border-color",
        "border-radius",
        "background-color",
        // 排版属性追加在盒子属性之后：历史定义（只写盒子）的规范化串顺序不变。
        "font-family",
        "font-size",
        "font-weight",
        "font-style",
        "line-height",
        "letter-spacing",
        "text-align",
        "text-transform",
        "text-decoration",
        "white-space",
        "vertical-align",
        // 网格属性追加在末尾：重排会让历史定义的规范化串变字节，只能新增不能插队。
        "grid-template-columns",
        "grid-template-rows",
        "grid-auto-flow",
        "grid-column",
        "grid-row",
        "align-content",
        "justify-items",
    ];

    private static readonly Dictionary<string, string[]> KeywordValues = new(StringComparer.Ordinal)
    {
        ["display"] = ["block", "flex", "grid", "inline-block", "inline-flex", "none"],
        ["flex-direction"] = ["row", "row-reverse", "column", "column-reverse"],
        ["flex-wrap"] = ["nowrap", "wrap", "wrap-reverse"],
        ["justify-content"] =
        [
            "flex-start", "flex-end", "center", "space-between", "space-around", "space-evenly",
            "start", "end", "stretch",
        ],
        ["align-items"] =
        [
            "stretch", "flex-start", "flex-end", "center", "baseline", "start", "end",
        ],
        ["border-style"] = ["none", "solid", "dashed", "dotted"],
        ["font-family"] = ["sans-serif", "serif", "monospace", "system-ui", "inherit"],
        ["font-weight"] = ["normal", "bold"],
        ["font-style"] = ["normal", "italic"],
        ["line-height"] = ["normal"],
        ["text-align"] = ["left", "right", "center", "justify", "start", "end"],
        ["text-transform"] = ["none", "uppercase", "lowercase", "capitalize"],
        ["text-decoration"] = ["none", "underline", "line-through"],
        ["white-space"] = ["normal", "nowrap", "pre-wrap"],
        ["vertical-align"] = ["baseline", "top", "middle", "bottom", "sub", "super"],
        ["align-content"] =
        [
            "flex-start", "flex-end", "center", "space-between", "space-around", "space-evenly",
            "start", "end", "stretch",
        ],
        ["justify-items"] = ["stretch", "start", "end", "center"],
    };

    /// <summary>取值是「1–N 个长度」的属性及其允许的个数上限。</summary>
    private static readonly Dictionary<string, int> LengthValueCounts = new(StringComparer.Ordinal)
    {
        ["gap"] = 2,
        ["padding"] = 4,
        ["margin"] = 4,
        ["border-width"] = 4,
        ["border-radius"] = 4,
        ["font-size"] = 1,
        ["letter-spacing"] = 1,
    };

    /// <summary>取值是颜色的属性。</summary>
    private static readonly HashSet<string> ColorProperties = new(StringComparer.Ordinal)
    {
        "border-color",
        "background-color",
    };

    /// <summary>
    /// 布局档：<c>style</c> 走行内/块级 HTML 通道时放行的属性，是 <see cref="PropertyOrder"/> 的子集。
    /// 它与 <see cref="PropertyOrder"/> 同表相邻维护，<b>不是第二份规则表</b>：
    /// 判定、取值语法、规范化顺序全部复用 <see cref="Normalize"/>，只是多一道“属性必须在布局档内”。
    /// 排版（<c>font-*</c>/<c>line-height</c>/<c>text-*</c>）与颜色（<c>*-color</c>）属于呈现，不在此档。
    /// </summary>
    private static readonly HashSet<string> LayoutProperties = new(StringComparer.Ordinal)
    {
        "display",
        "flex-direction",
        "flex-wrap",
        "justify-content",
        "align-items",
        "align-content",
        "justify-items",
        "gap",
        "padding",
        "margin",
        "grid-template-columns",
        "grid-template-rows",
        "grid-auto-flow",
        "grid-column",
        "grid-row",
    };

    /// <summary>单个长度上限（px）：超过即视为越界。</summary>
    private const int MaxLengthPx = 512;

    private const string FontWeightProperty = "font-weight";
    private const string LineHeightProperty = "line-height";
    private const string GridTemplateColumnsProperty = "grid-template-columns";
    private const string GridTemplateRowsProperty = "grid-template-rows";
    private const string GridAutoFlowProperty = "grid-auto-flow";
    private const string GridColumnProperty = "grid-column";
    private const string GridRowProperty = "grid-row";

    /// <summary>单条 track 列表最多描述的轨道数（<c>repeat()</c> 按展开后的数量计）。</summary>
    private const int MaxTrackCount = 12;

    /// <summary>网格线的最大序号（<c>grid-column</c>/<c>grid-row</c>）。</summary>
    private const int MaxGridLine = 99;

    /// <summary>字重：关键字 normal/bold，或 100–900 的整百数值。</summary>
    private static bool TryNormalizeFontWeight(string value, out string normalized)
    {
        normalized = string.Empty;
        var lower = value.ToLowerInvariant();
        if (lower is "normal" or "bold")
        {
            normalized = lower;
            return true;
        }

        if (lower.Length != 3 || !int.TryParse(lower, out var weight) || weight % 100 != 0 || weight is < 100 or > 900)
        {
            return false;
        }

        normalized = weight.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>行高：关键字 normal、0.5–4 的无单位倍数（最多两位小数），或 px 长度。</summary>
    private static bool TryNormalizeLineHeight(string value, out string normalized)
    {
        normalized = string.Empty;
        var lower = value.ToLowerInvariant();
        if (lower == "normal")
        {
            normalized = lower;
            return true;
        }

        if (lower.EndsWith("px", StringComparison.Ordinal))
        {
            return TryNormalizeLength(lower, out normalized);
        }

        if (lower.Contains('.', StringComparison.Ordinal))
        {
            var parts = lower.Split('.');
            if (parts.Length != 2 || parts[0].Length > 2 || parts[1].Length is 0 or > 2)
            {
                return false;
            }
        }
        else if (lower.Length is 1 or 2)
        {
            // 无小数点的整数倍数也允许（如 1）。
        }
        else
        {
            return false;
        }

        if (!decimal.TryParse(
                lower,
                System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture,
                out var ratio)
            || ratio < 0.5m
            || ratio > 4m)
        {
            return false;
        }

        normalized = ratio.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>某个属性允许的关键字（按定义顺序）；非关键字属性返回空列表。</summary>
    public static IReadOnlyList<string> KeywordsOf(string property) =>
        KeywordValues.TryGetValue(property, out var values) ? values : [];

    /// <summary>属性是否在排版/盒子白名单内（检查器据此保留未知属性的既有声明）。</summary>
    public static bool IsKnownProperty(string property) =>
        Array.IndexOf(PropertyOrder, property) >= 0;

    /// <summary>属性是否在布局档内（行内/块级 HTML 的 <c>style</c> 通道只放行这一档）。</summary>
    public static bool IsLayoutProperty(string property) => LayoutProperties.Contains(property);

    /// <summary>
    /// 规范化一段 <c>style</c> 文本。返回 null 表示没有任何合法声明；
    /// <paramref name="rejected"/> 收集被丢弃的原始声明，供诊断使用。
    /// <paramref name="layoutOnly"/> 为 true 时只放行布局档（HTML 标签的 <c>style</c> 通道），
    /// 白名单、取值语法与输出顺序仍走同一张表。
    /// </summary>
    public static string? Normalize(string? raw, ICollection<string>? rejected = null, bool layoutOnly = false)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (raw.Length > MaxStyleLength * 4)
        {
            rejected?.Add(raw[..Math.Min(raw.Length, 64)] + "…");
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var declarations = 0;

        foreach (var segment in raw.Split(';'))
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            if (++declarations > MaxDeclarations)
            {
                rejected?.Add(segment.Trim());
                break;
            }

            var separator = segment.IndexOf(':');
            if (separator <= 0 || separator == segment.Length - 1)
            {
                rejected?.Add(segment.Trim());
                continue;
            }

            var property = segment[..separator].Trim().ToLowerInvariant();
            var value = segment[(separator + 1)..].Trim();

            if (Array.IndexOf(PropertyOrder, property) < 0)
            {
                rejected?.Add(segment.Trim());
                continue;
            }

            if (layoutOnly && !LayoutProperties.Contains(property))
            {
                rejected?.Add(segment.Trim());
                continue;
            }

            if (!TryNormalizeValue(property, value, out var normalized))
            {
                rejected?.Add(segment.Trim());
                continue;
            }

            // 同一属性重复声明时后者覆盖前者，与浏览器行为一致。
            values[property] = normalized;
        }

        if (values.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder(values.Count * 20);
        foreach (var property in PropertyOrder)
        {
            if (!values.TryGetValue(property, out var value))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(';');
            }

            builder.Append(property).Append(':').Append(value);
        }

        var result = builder.ToString();
        return result.Length > MaxStyleLength ? null : result;
    }

    /// <summary>规范化并返回失败原因，供检查器置灰提示使用。</summary>
    public static bool TryNormalize(string? raw, out string? normalized, out string? rejection)
    {
        var rejected = new List<string>();
        normalized = Normalize(raw, rejected);
        rejection = rejected.Count == 0 ? null : string.Join("；", rejected);
        return normalized is not null;
    }

    private static bool TryNormalizeValue(string property, string value, out string normalized)
    {
        normalized = string.Empty;
        if (value.Length == 0 || value.Length > 200 || ContainsUnsafeChar(value))
        {
            return false;
        }

        if (property == FontWeightProperty)
        {
            return TryNormalizeFontWeight(value, out normalized);
        }

        if (property == LineHeightProperty)
        {
            return TryNormalizeLineHeight(value, out normalized);
        }

        if (property is GridTemplateColumnsProperty or GridTemplateRowsProperty)
        {
            return TryNormalizeTrackList(value, out normalized);
        }

        if (property == GridAutoFlowProperty)
        {
            return TryNormalizeGridAutoFlow(value, out normalized);
        }

        if (property is GridColumnProperty or GridRowProperty)
        {
            return TryNormalizeGridLine(value, out normalized);
        }

        if (KeywordValues.TryGetValue(property, out var keywords))
        {
            var keyword = value.ToLowerInvariant();
            if (Array.IndexOf(keywords, keyword) < 0)
            {
                return false;
            }

            normalized = keyword;
            return true;
        }

        if (ColorProperties.Contains(property))
        {
            return TryNormalizeColor(value, out normalized);
        }

        if (LengthValueCounts.TryGetValue(property, out var maxCount))
        {
            return TryNormalizeLengths(value, maxCount, out normalized);
        }

        return false;
    }

    private static bool TryNormalizeColor(string value, out string normalized)
    {
        normalized = string.Empty;
        var lower = value.ToLowerInvariant();

        if (lower is "transparent" or "currentcolor")
        {
            normalized = lower;
            return true;
        }

        // 只接受主题变量：var(--token)，token 限定小写字母、数字与短横线。
        if (lower.StartsWith("var(--", StringComparison.Ordinal) && lower.EndsWith(')'))
        {
            var token = lower[6..^1];
            if (token.Length is 0 or > 64)
            {
                return false;
            }

            foreach (var c in token)
            {
                if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
                {
                    return false;
                }
            }

            normalized = "var(--" + token + ")";
            return true;
        }

        return false;
    }

    private static bool TryNormalizeLengths(string value, int maxCount, out string normalized)
    {
        normalized = string.Empty;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length > maxCount)
        {
            return false;
        }

        var tokens = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!TryNormalizeLength(parts[i], out var token))
            {
                return false;
            }

            tokens[i] = token;
        }

        normalized = string.Join(' ', tokens);
        return true;
    }

    private static bool TryNormalizeLength(string token, out string normalized)
    {
        normalized = string.Empty;
        var lower = token.ToLowerInvariant();

        if (lower == "0" || lower == "0px")
        {
            normalized = "0";
            return true;
        }

        if (!lower.EndsWith("px", StringComparison.Ordinal))
        {
            // 无单位纯数字按 px 补齐：避免“18 被整条丢弃”的静默失败。
            return TryNormalizeBareLength(lower, out normalized);
        }

        var digits = lower[..^2];
        if (digits.Length is 0 or > 4)
        {
            return false;
        }

        foreach (var c in digits)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        if (!int.TryParse(digits, out var px) || px <= 0 || px > MaxLengthPx)
        {
            return false;
        }

        normalized = px.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px";
        return true;
    }

    /// <summary>无单位长度：只接受 1–512 的整数，并按 px 补齐。</summary>
    private static bool TryNormalizeBareLength(string token, out string normalized)
    {
        normalized = string.Empty;
        if (token.Length is 0 or > 3)
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        if (!int.TryParse(token, out var px) || px <= 0 || px > MaxLengthPx)
        {
            return false;
        }

        normalized = px.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px";
        return true;
    }

    // ── 网格词表 ─────────────────────────────────────────────────────
    //
    // 只认可枚举的 track 语法：auto / Nfr（1–12，≤2 位小数）/ Npx（≤512）/
    // minmax(track,track) / repeat(N,track-list) / fit-content(Npx)。
    // calc()、var()、url()、attr()、env() 一律不在白名单内，直接判非法。

    private static bool TryNormalizeTrackList(string value, out string normalized)
    {
        return TryNormalizeTrackList(value, out normalized, out _);
    }

    private static bool TryNormalizeTrackList(string value, out string normalized, out int trackCount)
    {
        normalized = string.Empty;
        trackCount = 0;

        if (!TrySplitTracks(value, out var tracks) || tracks.Count == 0 || tracks.Count > MaxTrackCount)
        {
            return false;
        }

        var parts = new List<string>(tracks.Count);
        var total = 0;
        foreach (var track in tracks)
        {
            if (!TryNormalizeTrack(track, out var part, out var weight))
            {
                return false;
            }

            total += weight;
            if (total > MaxTrackCount)
            {
                return false;
            }

            parts.Add(part);
        }

        normalized = string.Join(' ', parts);
        trackCount = total;
        return true;
    }

    /// <summary>按空格切 track（括号内的空格不切），并校验括号配平。</summary>
    private static bool TrySplitTracks(string value, out List<string> tracks)
    {
        tracks = [];
        var builder = new StringBuilder(value.Length);
        var depth = 0;

        foreach (var ch in value)
        {
            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                if (--depth < 0)
                {
                    return false;
                }
            }
            else if (char.IsWhiteSpace(ch) && depth == 0)
            {
                if (builder.Length > 0)
                {
                    tracks.Add(builder.ToString());
                    builder.Clear();
                }

                continue;
            }

            builder.Append(ch);
        }

        if (depth != 0)
        {
            return false;
        }

        if (builder.Length > 0)
        {
            tracks.Add(builder.ToString());
        }

        return true;
    }

    private static bool TryNormalizeTrack(string token, out string normalized, out int weight)
    {
        normalized = string.Empty;
        weight = 0;
        var lower = token.ToLowerInvariant();

        if (lower == "auto")
        {
            normalized = "auto";
            weight = 1;
            return true;
        }

        if (lower.StartsWith("repeat(", StringComparison.Ordinal) && lower.EndsWith(')'))
        {
            var inner = lower[7..^1];
            if (!TrySplitTopLevel(inner, ',', out var parts)
                || parts.Count != 2
                || !TryParseBoundedInt(parts[0], 1, MaxTrackCount, out var repeat)
                || !TryNormalizeTrackList(parts[1], out var list, out var innerCount))
            {
                return false;
            }

            weight = repeat * innerCount;
            if (weight > MaxTrackCount)
            {
                return false;
            }

            normalized = "repeat(" + repeat.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "," + list + ")";
            return true;
        }

        if (TryNormalizeTrackSize(lower, out var size))
        {
            normalized = size;
            weight = 1;
            return true;
        }

        return false;
    }

    /// <summary>单个 track 尺寸：auto / Nfr / Npx / minmax(track,track) / fit-content(Npx)。</summary>
    private static bool TryNormalizeTrackSize(string token, out string normalized)
    {
        normalized = string.Empty;
        var lower = token.ToLowerInvariant();

        if (lower == "auto")
        {
            normalized = "auto";
            return true;
        }

        // 0 是合法的长度 track（典型写法 minmax(0,1fr)）；其它无单位数字不接受。
        if (lower == "0")
        {
            normalized = "0";
            return true;
        }

        if (lower.StartsWith("minmax(", StringComparison.Ordinal) && lower.EndsWith(')'))
        {
            var inner = lower[7..^1];
            if (!TrySplitTopLevel(inner, ',', out var parts)
                || parts.Count != 2
                || !TryNormalizeTrackSize(parts[0], out var min)
                || !TryNormalizeTrackSize(parts[1], out var max))
            {
                return false;
            }

            normalized = "minmax(" + min + "," + max + ")";
            return true;
        }

        if (lower.StartsWith("fit-content(", StringComparison.Ordinal) && lower.EndsWith(')'))
        {
            var inner = lower[12..^1].Trim();
            if (!inner.EndsWith("px", StringComparison.Ordinal)
                || !TryNormalizeLength(inner, out var length))
            {
                return false;
            }

            normalized = "fit-content(" + length + ")";
            return true;
        }

        if (lower.EndsWith("fr", StringComparison.Ordinal))
        {
            return TryNormalizeFraction(lower[..^2], out normalized);
        }

        if (lower.EndsWith("px", StringComparison.Ordinal))
        {
            return TryNormalizeLength(lower, out normalized);
        }

        return false;
    }

    /// <summary><c>Nfr</c>：1–12，最多两位小数。</summary>
    private static bool TryNormalizeFraction(string digits, out string normalized)
    {
        normalized = string.Empty;
        if (digits.Length is 0 or > 5)
        {
            return false;
        }

        var dot = digits.IndexOf('.');
        if (dot >= 0)
        {
            if (digits.Length - dot - 1 is 0 or > 2)
            {
                return false;
            }
        }

        if (!decimal.TryParse(
                digits,
                System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value)
            || value < 1m
            || value > 12m)
        {
            return false;
        }

        normalized = value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "fr";
        return true;
    }

    /// <summary><c>grid-auto-flow</c>：row / column / dense / row dense / column dense。</summary>
    private static bool TryNormalizeGridAutoFlow(string value, out string normalized)
    {
        normalized = string.Empty;
        var parts = value.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hasRow = false;
        var hasColumn = false;
        var hasDense = false;

        foreach (var part in parts)
        {
            switch (part)
            {
                case "row":
                    if (hasRow || hasColumn) return false;
                    hasRow = true;
                    break;
                case "column":
                    if (hasRow || hasColumn) return false;
                    hasColumn = true;
                    break;
                case "dense":
                    if (hasDense) return false;
                    hasDense = true;
                    break;
                default:
                    return false;
            }
        }

        if (parts.Length == 0)
        {
            return false;
        }

        normalized = (hasRow ? "row" : hasColumn ? "column" : string.Empty) + (hasDense ? " dense" : string.Empty);
        normalized = normalized.Trim();
        return normalized.Length > 0;
    }

    /// <summary><c>grid-column</c>/<c>grid-row</c>：auto、序号（1–99）或 <c>span N</c>，可用 <c>/</c> 给起止。</summary>
    private static bool TryNormalizeGridLine(string value, out string normalized)
    {
        normalized = string.Empty;
        if (!TrySplitTopLevel(value, '/', out var parts) || parts.Count is 0 or > 2)
        {
            return false;
        }

        var tokens = new string[parts.Count];
        for (var index = 0; index < parts.Count; index++)
        {
            if (!TryNormalizeGridLineValue(parts[index], out tokens[index]))
            {
                return false;
            }
        }

        normalized = string.Join(" / ", tokens);
        return true;
    }

    private static bool TryNormalizeGridLineValue(string value, out string normalized)
    {
        normalized = string.Empty;
        var lower = value.Trim().ToLowerInvariant();

        if (lower == "auto")
        {
            normalized = "auto";
            return true;
        }

        if (lower.StartsWith("span ", StringComparison.Ordinal))
        {
            if (!TryParseBoundedInt(lower[5..].Trim(), 1, MaxGridLine, out var span))
            {
                return false;
            }

            normalized = "span " + span.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        if (!TryParseBoundedInt(lower, 1, MaxGridLine, out var line))
        {
            return false;
        }

        normalized = line.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>纯十进制整数，收窄到 [min,max]，输出无前导零的规范串。</summary>
    private static bool TryParseBoundedInt(string value, int min, int max, out int result)
    {
        result = 0;
        if (value.Length is 0 or > 3)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return int.TryParse(value, out result) && result >= min && result <= max;
    }

    /// <summary>按顶层分隔符切分（括号内的分隔符不切）；任一段为空即判非法。</summary>
    private static bool TrySplitTopLevel(string text, char separator, out List<string> parts)
    {
        parts = [];
        var builder = new StringBuilder(text.Length);
        var depth = 0;

        foreach (var ch in text)
        {
            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                if (--depth < 0)
                {
                    return false;
                }
            }
            else if (ch == separator && depth == 0)
            {
                var part = builder.ToString().Trim();
                if (part.Length == 0)
                {
                    return false;
                }

                parts.Add(part);
                builder.Clear();
                continue;
            }

            builder.Append(ch);
        }

        if (depth != 0)
        {
            return false;
        }

        var last = builder.ToString().Trim();
        if (last.Length == 0)
        {
            return false;
        }

        parts.Add(last);
        return true;
    }

    private static bool ContainsUnsafeChar(string value)
    {
        foreach (var c in value)
        {
            if (c is '"' or '\'' or '<' or '>' or '{' or '}' or '\\' or '&' or '\r' or '\n' or '\t')
            {
                return true;
            }
        }

        return false;
    }
}
