namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;

/// <summary>
/// 打开期诊断的聚合摘要。一次打开可能同时出现多个元素、多个属性与多处内容违规，
/// 状态栏应先给计数摘要，明细仍保留在 <c>CurrentLoadDiagnostics</c> 里可查。
/// </summary>
public static class DiagnosticSummary
{
    /// <summary>无诊断时返回 null；其余情况返回一条可直接展示的文本。</summary>
    public static string? Compose(IReadOnlyList<DiagnosticMessage>? diagnostics)
    {
        if (diagnostics is null || diagnostics.Count == 0)
        {
            return null;
        }

        var elementProblems = 0;
        var attributeProblems = 0;
        var contentViolations = 0;
        var others = 0;

        foreach (var message in diagnostics)
        {
            var code = message.Code ?? string.Empty;
            if (code.StartsWith("USER_ELEMENTS_", StringComparison.Ordinal))
            {
                elementProblems++;
            }
            else if (code.StartsWith("USER_BLOCK_ATTRIBUTE_", StringComparison.Ordinal))
            {
                attributeProblems++;
            }
            else if (code.StartsWith("USER_BLOCK_CONTENT_", StringComparison.Ordinal))
            {
                contentViolations++;
            }
            else
            {
                others++;
            }
        }

        // 没有自定义元素类诊断时保持旧的逐条展示，不做无意义的重新归类。
        if (elementProblems + attributeProblems + contentViolations == 0)
        {
            return string.Join(" | ", diagnostics.Select(message => message.Message));
        }

        // 单条诊断直接给原文，摘要只在会“刷屏”时启用。
        if (diagnostics.Count == 1)
        {
            return diagnostics[0].Message;
        }

        var parts = new List<string>();
        if (elementProblems > 0) parts.Add($"{elementProblems} 个元素定义问题");
        if (attributeProblems > 0) parts.Add($"{attributeProblems} 处属性问题");
        if (contentViolations > 0) parts.Add($"{contentViolations} 处内容违规");
        if (others > 0) parts.Add($"其它 {others} 条");
        return $"自定义元素：{string.Join("、", parts)}（共 {diagnostics.Count} 条明细）";
    }
}
