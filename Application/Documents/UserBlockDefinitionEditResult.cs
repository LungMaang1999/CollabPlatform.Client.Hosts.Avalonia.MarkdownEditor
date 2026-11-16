using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Documents;

/// <summary>
/// 一次元素定义编辑的结果分类。界面据此决定提示形态，不需要解析异常文本：
/// <list type="bullet">
/// <item><see cref="ValidationFailed"/>：提交内容不合法，磁盘与内存均未改变；</item>
/// <item><see cref="NotFound"/>：要修改/删除的定义已不存在（可能被其它操作移除）；</item>
/// <item><see cref="NoDocument"/>：没有打开文档，或当前文档没有磁盘路径；</item>
/// <item><see cref="Conflict"/>：磁盘上的定义文件在打开后被外部改过，拒绝覆盖；</item>
/// <item><see cref="SaveFailed"/>：落盘失败（只读、被占用、IO），内存态未生效。</item>
/// </list>
/// </summary>
public enum UserBlockDefinitionEditOutcome
{
    Succeeded,
    ValidationFailed,
    NotFound,
    NoDocument,
    Conflict,
    SaveFailed
}

/// <summary>
/// 一条可展示的校验问题。诊断码与打开期加载器完全一致（<c>USER_ELEMENTS_*</c>），
/// 不另造一套；编辑路径上这些码统一提升为 Error（提交被阻断），
/// 加载路径仍保持“告警 + 降级”语义。
/// </summary>
public sealed record UserBlockDefinitionEditIssue
{
    public required DiagnosticSeverity Severity { get; init; }

    /// <summary>既有诊断码，如 <c>USER_ELEMENTS_ID_DUPLICATE</c>。</summary>
    public required string Code { get; init; }

    /// <summary>可直接展示的中文原因。</summary>
    public required string Message { get; init; }

    /// <summary>出问题的定义 Id；无法确定时为 null。</summary>
    public string? DefinitionId { get; init; }

    /// <summary>出问题的字段：id / name / fence-char / fence-length / priority / attributes / content。</summary>
    public string? Field { get; init; }
}

/// <summary>
/// 被本次定义变更波及、已定向重解析的块。未被波及的块按引用复用，
/// 不会出现在这里，界面可据此只提示“这一个块需要刷新”。
/// </summary>
public sealed record InvalidatedUserBlock
{
    /// <summary>在整篇文档顶层块序列中的位置（从 0 开始）。</summary>
    public required int Index { get; init; }

    public required NodeType Type { get; init; }

    /// <summary>自定义块的 <c>custom-kind</c>；普通块为 null。</summary>
    public string? CustomKind { get; init; }

    /// <summary>截断后的内容预览，供界面提示使用。</summary>
    public string Preview { get; init; } = string.Empty;

    public SourceRange Range { get; init; } = SourceRange.Empty();

    public int StartLine => Range.StartLine;

    public int EndLine => Range.EndLine;
}

/// <summary>
/// 元素定义编辑的结构化结果。所有可预期的失败都以结果返回，不向界面抛异常。
/// </summary>
public sealed record UserBlockDefinitionEditResult
{
    public required UserBlockDefinitionEditOutcome Outcome { get; init; }

    public bool Succeeded => Outcome == UserBlockDefinitionEditOutcome.Succeeded;

    /// <summary>一句话中文结论，可直接放进状态栏或对话框标题。</summary>
    public required string Message { get; init; }

    /// <summary>全部校验问题（不是只报第一条）；非校验失败时为空。</summary>
    public IReadOnlyList<UserBlockDefinitionEditIssue> Issues { get; init; } = [];

    /// <summary>本次被定向重解析的块；只在成功时有意义。</summary>
    public IReadOnlyList<InvalidatedUserBlock> InvalidatedBlocks { get; init; } = [];

    /// <summary>本次新增/修改的定义；删除或无定义时为 null。</summary>
    public UserBlockDefinition? Definition { get; init; }

    /// <summary>提交后的全量定义（成功时为磁盘上的实际内容）。</summary>
    public IReadOnlyList<UserBlockDefinition> Definitions { get; init; } = [];

    /// <summary>定义文件路径；无法确定时为 null。</summary>
    public string? ElementsFilePath { get; init; }

    /// <summary>本次操作是否已进入命令栈（可撤销）。</summary>
    public bool UndoRecorded { get; init; }

    internal static UserBlockDefinitionEditResult Failure(
        UserBlockDefinitionEditOutcome outcome,
        string message,
        IReadOnlyList<UserBlockDefinitionEditIssue>? issues = null,
        string? elementsFilePath = null,
        IReadOnlyList<UserBlockDefinition>? definitions = null,
        UserBlockDefinition? definition = null) => new()
    {
        Outcome = outcome,
        Message = message,
        Issues = issues ?? [],
        ElementsFilePath = elementsFilePath,
        Definitions = definitions ?? [],
        Definition = definition
    };
}
