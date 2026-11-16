using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Parsing;

public interface IMarkdownParser
{
    /// <summary>
    /// 解析整篇文档。<paramref name="userBlocks"/> 为空时沿用
    /// <c>previous.EffectiveUserBlockDefinitions</c>，再退回解析器配置里的定义。
    /// <paramref name="sourceUserBlocks"/> 是“来源形态”（保留 ref），只用于写回文档；
    /// <paramref name="composition"/> 是本次生效的组合单元。
    /// </summary>
    ParseResult Parse(
        string source,
        MarkdownDocument? previous = null,
        ParseMode mode = ParseMode.Editing,
        IReadOnlyList<UserBlockDefinition>? userBlocks = null,
        IReadOnlyList<UserBlockDefinition>? sourceUserBlocks = null,
        UserBlockComposition? composition = null);

    /// <summary>
    /// 纯函数解析：只产新 AST（previousRoot 只读，Id/样式由身份匹配拷到新树），
    /// 可在后台线程调用。提供 previousSource 且为纯追加时仅重解析尾部顶层块，
    /// 不满足安全条件时自动退化为全量解析。
    /// 用户块定义必须与构建 previousRoot 时一致；定义变化走
    /// <see cref="ReparseForUserBlockChange"/>。
    /// </summary>
    MarkdownNode ParseTree(
        string source,
        MarkdownNode? previousRoot,
        string? previousSource = null,
        IReadOnlyList<UserBlockDefinition>? userBlocks = null);

    /// <summary>
    /// 用户块定义变化后的定向重解析：只重解析引用了变化定义的块，
    /// 其余块按引用复用。源码未变，因此不需要新旧源码对照。
    /// </summary>
    MarkdownNode ReparseForUserBlockChange(
        string source,
        MarkdownNode previousRoot,
        UserBlockSyntaxChange change);

    /// <summary>
    /// 把新的用户块定义应用到文档：定向重解析后替换文档根，并更新
    /// <see cref="MarkdownDocument.UserBlockDefinitions"/>，后续编辑继续使用新定义。
    /// <paramref name="composition"/> 省略时沿用文档当前的组合单元。
    /// 返回新的文档根。
    /// </summary>
    MarkdownNode UpdateUserBlockDefinitions(
        MarkdownDocument document,
        IReadOnlyList<UserBlockDefinition> definitions,
        UserBlockComposition? composition = null);
}
