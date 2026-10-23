using System.Text;
using System.Text.RegularExpressions;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

/// <summary>时间线节点标记行（<c>@@ 时间 [tag] [tag] side=left|right</c>）的解析结果。</summary>
/// <param name="Time">时间文本（最核心字段，可为空字符串）。</param>
/// <param name="Tags">标签数组（按源码顺序，已去空白）。</param>
/// <param name="Side">显式侧别 left/right；缺省或非法为 null（自动）。</param>
public sealed record TimelineMarkerInfo(
    string Time,
    IReadOnlyList<string> Tags,
    string? Side);

/// <summary>开围栏选项（<c>line=solid|dashed gap=2..24</c>）的解析结果。</summary>
/// <param name="LineStyle">轴线线型：solid（缺省）或 dashed。</param>
/// <param name="Gap">虚线段间隙像素；仅 dashed 时有意义，缺省由 CSS 取默认值。</param>
public sealed record TimelineFenceInfo(string LineStyle, int? Gap);

/// <summary>
/// 时间线 @@ 标记行的解析与规范化，解析器与宿主属性改写计划器共用同一套语法。
/// </summary>
public static class TimelineMarker
{
    /// <summary>节点属性 timeline-tags 多值连接符（单元分隔符，不会出现在用户输入中）。</summary>
    public const char TagSeparator = '\u001F';

    /// <summary>轴线线型：实线（缺省）。</summary>
    public const string LineSolid = "solid";

    /// <summary>轴线线型：虚线。</summary>
    public const string LineDashed = "dashed";

    /// <summary>虚线间隙允许范围（像素）与缺省值。</summary>
    public const int MinLineGap = 2;
    public const int MaxLineGap = 24;
    public const int DefaultLineGap = 6;

    private static readonly Regex MarkerLineRegex =
        new(@"^[ \t]{0,3}@@(?:[ \t]|$)", RegexOptions.Compiled);

    private static readonly Regex MarkerContentRegex =
        new(@"^(?<lead>[ \t]*)@@(?<rest>(?:[ \t].*)?)$", RegexOptions.Compiled);

    private static readonly Regex TagRegex =
        new(@"\[([^\]\n]*)\]", RegexOptions.Compiled);

    private static readonly Regex SideRegex =
        new(@"(?:^|\s)side=([^\s\[\]]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>原始行是否为节点标记行（允许 ≤3 空格缩进，@@ 后须为空白或行尾）。</summary>
    public static bool IsMarkerLine(string line) =>
        !string.IsNullOrEmpty(line) && MarkerLineRegex.IsMatch(line);

    /// <summary>解析标记行；非 @@ 行返回 null。</summary>
    public static TimelineMarkerInfo? TryParse(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        var match = MarkerContentRegex.Match(line);
        if (!match.Success)
        {
            return null;
        }

        var content = match.Groups["rest"].Value;
        if (content.Length > 0 && content[0] is ' ' or '\t')
        {
            content = content.Substring(1);
        }

        var timeEnd = content.Length;

        // 收集标签区间并掩码，避免标签文本里的 "side=xxx" 被当成侧别片段
        var sideScratch = new StringBuilder(content);
        var tags = new List<string>();
        foreach (Match tagMatch in TagRegex.Matches(content))
        {
            if (tagMatch.Index < timeEnd)
            {
                timeEnd = tagMatch.Index;
            }

            var tag = tagMatch.Groups[1].Value.Trim();
            if (tag.Length > 0)
            {
                tags.Add(tag);
            }

            for (var i = tagMatch.Index; i < tagMatch.Index + tagMatch.Length; i++)
            {
                sideScratch[i] = ' ';
            }
        }

        string? side = null;
        foreach (Match sideMatch in SideRegex.Matches(sideScratch.ToString()))
        {
            var candidate = NormalizeSide(sideMatch.Groups[1].Value);
            if (candidate is not null)
            {
                side = candidate;
                if (sideMatch.Index < timeEnd)
                {
                    timeEnd = sideMatch.Index;
                }
            }
        }

        var time = content.Substring(0, timeEnd).Trim();

        return new TimelineMarkerInfo(time, tags, side);
    }

    /// <summary>把标签数组规范化为节点属性存储串。</summary>
    public static string JoinTags(IEnumerable<string>? tags) =>
        tags is null ? string.Empty : string.Join(TagSeparator, tags);

    /// <summary>把属性存储串拆回标签数组。</summary>
    public static IReadOnlyList<string> SplitTags(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        return value.Split(TagSeparator, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>规范化侧别值：left/right 返回小写；auto、空、非法值返回 null。</summary>
    public static string? NormalizeSide(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "left" => "left",
            "right" => "right",
            _ => null
        };
    }

    /// <summary>
    /// 解析开围栏 <c>timeline</c> 之后的选项串：<c>line=solid|dashed</c>、<c>gap=2..24</c>。
    /// 未知选项忽略；非法值回退缺省。
    /// </summary>
    public static TimelineFenceInfo ParseFenceInfo(string? info)
    {
        var lineStyle = LineSolid;
        int? gap = null;

        if (!string.IsNullOrWhiteSpace(info))
        {
            foreach (var token in info.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = token.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                var key = token[..eq].Trim().ToLowerInvariant();
                var val = token[(eq + 1)..].Trim().ToLowerInvariant();

                switch (key)
                {
                    case "line":
                        lineStyle = val == LineDashed ? LineDashed : LineSolid;
                        break;
                    case "gap" when int.TryParse(val, out var parsed)
                        && parsed is >= MinLineGap and <= MaxLineGap:
                        gap = parsed;
                        break;
                }
            }
        }

        return new TimelineFenceInfo(lineStyle, gap);
    }

    /// <summary>
    /// 按线型/间隙重建开围栏选项串（不含前导空白）：实线或全缺省返回空串；
    /// 虚线且间隙为缺省值时省略 gap。
    /// </summary>
    public static string BuildFenceInfo(string? lineStyle, int? gap)
    {
        if (!string.Equals(lineStyle?.Trim(), LineDashed, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (gap is int parsed && parsed is >= MinLineGap and <= MaxLineGap && parsed != DefaultLineGap)
        {
            return $"line={LineDashed} gap={parsed}";
        }

        return $"line={LineDashed}";
    }

    /// <summary>
    /// 按当前时间/标签/侧别重建整行标记文本（列 0 缩进）：
    /// <c>@@ 时间 [tag] [tag] side=left</c>，侧别为 null 时省略 side 片段。
    /// </summary>
    public static string Build(string? time, IEnumerable<string?>? tags, string? side)
    {
        var body = new StringBuilder();
        body.Append((time ?? string.Empty).Trim());

        if (tags is not null)
        {
            foreach (var rawTag in tags)
            {
                var tag = (rawTag ?? string.Empty).Trim();
                if (tag.Length == 0)
                {
                    continue;
                }

                // 标签内的方括号会破坏解析，统一剔除。
                tag = tag.Replace("[", string.Empty).Replace("]", string.Empty).Trim();
                if (tag.Length == 0)
                {
                    continue;
                }

                body.Append(" [").Append(tag).Append(']');
            }
        }

        var normalizedSide = NormalizeSide(side);
        if (normalizedSide is not null)
        {
            body.Append(" side=").Append(normalizedSide);
        }

        var content = body.ToString().Trim();
        return content.Length == 0 ? "@@" : "@@ " + content;
    }
}
