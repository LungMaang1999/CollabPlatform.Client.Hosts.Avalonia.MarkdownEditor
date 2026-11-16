using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;

public sealed record DocumentLoadResult(MarkdownDocument Document, DocumentFileSnapshot? Snapshot)
{
    /// <summary>
    /// 打开期的可读诊断（如 <c>.md.elements.xml</c> 损坏或条目被跳过）。
    /// 正常打开为空；有诊断时文档仍按可用定义完整解析，不是空文档。
    /// </summary>
    public IReadOnlyList<DiagnosticMessage> Diagnostics { get; init; } = [];

    /// <summary>聚合后的可读摘要（状态栏用）；无诊断时为 null。明细见 <see cref="Diagnostics"/>。</summary>
    public string? DiagnosticsSummary => DiagnosticSummary.Compose(Diagnostics);
}
