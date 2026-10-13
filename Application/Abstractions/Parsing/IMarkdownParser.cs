using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Parsing;

public interface IMarkdownParser
{
    ParseResult Parse(string source, MarkdownDocument? previous = null, ParseMode mode = ParseMode.Editing);

    /// <summary>
    /// 纯函数解析：只产新 AST（previousRoot 只读，Id/样式由身份匹配拷到新树），
    /// 可在后台线程调用。提供 previousSource 且为纯追加时仅重解析尾部顶层块，
    /// 不满足安全条件时自动退化为全量解析。
    /// </summary>
    MarkdownNode ParseTree(string source, MarkdownNode? previousRoot, string? previousSource = null);
}
