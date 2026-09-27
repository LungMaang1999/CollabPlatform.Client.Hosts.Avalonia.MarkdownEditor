using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Parsing;

public interface IMarkdownParser
{
    ParseResult Parse(string source, MarkdownDocument? previous = null, ParseMode mode = ParseMode.Editing);

    /// <summary>
    /// 纯函数解析：只产出新 AST（previousRoot 不被修改，身份匹配把 Id/样式
    /// 从 previousRoot 拷贝到新树），不读写任何 <see cref="MarkdownDocument"/>，
    /// 可在后台线程调用。诊断信息在编辑路径上原本即被丢弃。
    /// 提供 previousSource 时，若新源码是纯追加（流式场景），仅重解析受影响的
    /// 尾部顶层块，前缀块对象直接复用；不满足安全条件时自动退化为全量解析。
    /// </summary>
    MarkdownNode ParseTree(string source, MarkdownNode? previousRoot, string? previousSource = null);
}
