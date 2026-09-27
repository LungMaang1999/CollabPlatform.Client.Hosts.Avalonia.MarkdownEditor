using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Editing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing;

public sealed class MarkdownEditApplier : IMarkdownEditApplier
{
    // 命令只负责提交 source（字符串变换 + 修订号）。
    // AST 解析由 EditorViewModel 按配置选择同步或后台调度，
    // 避免命令层把“source 提交”和“解析换树”绑死。
    public void Apply(MarkdownDocument document, string source)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.UpdateSourceMarkdown(source);
    }
}
