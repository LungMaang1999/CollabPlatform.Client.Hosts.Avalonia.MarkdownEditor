using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.Composition;

/// <summary>
/// 编辑后 AST 解析的调度方式。
/// </summary>
public enum BackgroundParsingMode
{
    /// <summary>
    /// 按文档字符数阈值动态切换：小文档同步解析（强一致、零异步税），
    /// 大文档后台解析（UI 线程不被 Markdig 阻塞）。
    /// </summary>
    Auto,

    /// <summary>
    /// 始终同步解析（编辑后 AST 立即可见），适用于小工具宿主与单元测试。
    /// </summary>
    Off,

    /// <summary>
    /// 始终后台解析。
    /// </summary>
    On
}

/// <summary>
/// MarkdownEditor 的宿主级配置：解析器能力开关（继承自
/// <see cref="MarkdownParserOptions"/>）加编辑器调度策略。
/// </summary>
public sealed class MarkdownEditorOptions : MarkdownParserOptions
{
    private int _backgroundParsingThresholdChars = 20_000;

    /// <summary>
    /// 编辑后 AST 解析的调度方式，默认 <see cref="BackgroundParsingMode.Auto"/>。
    /// </summary>
    public BackgroundParsingMode BackgroundParsing { get; set; } = BackgroundParsingMode.Auto;

    /// <summary>
    /// <see cref="BackgroundParsingMode.Auto"/> 下启用后台解析的字符数阈值，
    /// 默认 20000（与宿主输入防抖的大文档分档对齐），最小 1。
    /// </summary>
    public int BackgroundParsingThresholdChars
    {
        get => _backgroundParsingThresholdChars;
        set => _backgroundParsingThresholdChars = value < 1 ? 1 : value;
    }
}
