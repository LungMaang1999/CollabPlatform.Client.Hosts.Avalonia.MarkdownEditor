using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

/// <summary>
/// 一次 <c>.md.elements.xml</c> 读取的结果：可用的定义（已逐条校验、坏条目被跳过）
/// 加组合单元（属性组 / 结构片段）加可读诊断。
/// 读取永不抛异常——损坏文件只产生诊断，文档照常打开。
/// </summary>
public sealed record UserBlockElementsFile(
    IReadOnlyList<UserBlockDefinition> Definitions,
    IReadOnlyList<DiagnosticMessage> Diagnostics,
    UserBlockComposition? Composition = null)
{
    public static UserBlockElementsFile Empty { get; } = new([], []);

    /// <summary>组合单元，永不为 null（没有单元时取 <see cref="UserBlockComposition.Empty"/>）。</summary>
    public UserBlockComposition EffectiveComposition => Composition ?? UserBlockComposition.Empty;
}
