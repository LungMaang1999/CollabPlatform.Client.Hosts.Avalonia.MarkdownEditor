namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

/// <summary>用户块属性声明的取值类型；决定编辑面板控件的形态。</summary>
public enum UserBlockAttributeKind
{
    Text,
    Number,
    Boolean,
    Enum,
    Url
}

/// <summary>
/// 用户块实例属性声明（<c>:::note level=info</c> 中的 <c>level</c>）。
/// 只描述允许的键、取值约束与默认值，不改变围栏识别规则。
/// </summary>
public sealed record UserBlockAttributeDeclaration
{
    /// <summary>属性键，必须与开围栏行内 <c>key=value</c> 的键一致。</summary>
    public required string Key { get; init; }

    /// <summary>实例未显式赋值时采用的默认值；null 表示无默认值。</summary>
    public string? DefaultValue { get; init; }

    public UserBlockAttributeKind Kind { get; init; } = UserBlockAttributeKind.Text;

    /// <summary>true 表示该属性缺失时应报告诊断。</summary>
    public bool Required { get; init; }

    /// <summary>
    /// <see cref="UserBlockAttributeKind.Enum"/> 的允许值集合；空集合表示未给出取值约束
    /// （加载期会报一条 Warning，运行期按 Text 处理，不阻断解析）。
    /// </summary>
    public IReadOnlyList<string> AllowedValues { get; init; } = [];
}
