namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 首词分派的可声明目标：定义里的 <c>opens</c> 取值限定现有 <c>NodeType</c> 白名单，
/// 并声明哪种围栏字符可以开启该目标。新增一种关键字只需要在定义里加数据；
/// 只有新增目标时才需要同时补一个块工厂。
/// </summary>
public static class FenceOpenTargets
{
    /// <summary>自定义元素通道：不进入首词分派表，仍由 <see cref="UserBlockParser"/> 处理。</summary>
    public const string Custom = "Custom";

    private static readonly (string Name, FenceTarget Target, char[] FenceChars)[] Declared =
    {
        // 三单引号族：与内置诗词/时间线/雷达图/图片矩阵共用围栏符。
        ("PoemBlock", FenceTarget.Poem, [UserBlockRegistry.PrimeFenceChar]),
        ("Timeline", FenceTarget.Timeline, [UserBlockRegistry.PrimeFenceChar]),
        ("Radar", FenceTarget.Radar, [UserBlockRegistry.PrimeFenceChar]),
        ("ImageMatrix", FenceTarget.ImageMatrix, [UserBlockRegistry.PrimeFenceChar, UserBlockRegistry.BacktickFenceChar, UserBlockRegistry.TildeFenceChar]),

        // 代码围栏族：info 首词经 Markdig FencedCodeBlock 交给转换器。
        ("MathBlock", FenceTarget.Math, [UserBlockRegistry.BacktickFenceChar, UserBlockRegistry.TildeFenceChar]),
        ("Mermaid", FenceTarget.Mermaid, [UserBlockRegistry.BacktickFenceChar, UserBlockRegistry.TildeFenceChar]),
        ("QrCode", FenceTarget.QrCode, [UserBlockRegistry.BacktickFenceChar, UserBlockRegistry.TildeFenceChar]),
    };

    /// <summary>全部可声明的目标名（含 <see cref="Custom"/>），供错误消息与测试使用。</summary>
    public static IReadOnlyList<string> Names { get; } = [.. Declared.Select(item => item.Name), Custom];

    /// <summary>把 <c>opens</c> 原文规范成白名单里的规范名（忽略大小写）。</summary>
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, Custom, StringComparison.OrdinalIgnoreCase))
        {
            normalized = Custom;
            return true;
        }

        foreach (var item in Declared)
        {
            if (string.Equals(item.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                normalized = item.Name;
                return true;
            }
        }

        return false;
    }

    /// <summary>是否自定义元素通道的缺省语义（不贡献首词分派）。</summary>
    public static bool IsCustom(string? value) =>
        string.Equals(value, Custom, StringComparison.OrdinalIgnoreCase);

    /// <summary>规范名 → 分派目标；<see cref="Custom"/> 返回 false。</summary>
    public static bool TryGetTarget(string? value, out FenceTarget target)
    {
        target = default;
        if (!TryNormalize(value, out var normalized) || IsCustom(normalized))
        {
            return false;
        }

        foreach (var item in Declared)
        {
            if (string.Equals(item.Name, normalized, StringComparison.Ordinal))
            {
                target = item.Target;
                return true;
            }
        }

        return false;
    }

    /// <summary>该目标是否允许用这个围栏字符开启。</summary>
    public static bool SupportsFenceChar(string? opensName, char fenceChar)
    {
        if (!TryNormalize(opensName, out var normalized) || IsCustom(normalized))
        {
            return false;
        }

        foreach (var item in Declared)
        {
            if (string.Equals(item.Name, normalized, StringComparison.Ordinal))
            {
                return Array.IndexOf(item.FenceChars, fenceChar) >= 0;
            }
        }

        return false;
    }
}
