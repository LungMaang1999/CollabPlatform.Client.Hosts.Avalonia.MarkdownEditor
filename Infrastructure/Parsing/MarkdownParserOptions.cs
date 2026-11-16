using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

public class MarkdownParserOptions
{
    public bool EnablePipeTables { get; set; } = true;
    public bool EnableTaskLists { get; set; } = true;
    public bool EnableFootnotes { get; set; } = true;
    public bool EnableYamlFrontMatter { get; set; } = true;
    public bool EnableEmphasisExtras { get; set; } = true;
    public bool EnableAutoLinks { get; set; } = true;
    public bool EnableMathematics { get; set; } = true;

    /// <summary>
    /// 行内接管：用 <see cref="InlineCodeSpanParser"/> 原位替换 Markdig 的代码段解析器。
    /// 关闭时保留原生解析器，行为与接管前逐字节一致。
    /// </summary>
    public bool EnableInlineCodeTakeover { get; set; } = true;

    /// <summary>
    /// 用户自定义块定义集合；为空时不注册用户块解析器，行为与旧版完全一致。
    /// 定义在构造解析器时校验，非法定义直接抛出，不进入运行期。
    /// </summary>
    public IReadOnlyList<UserBlockDefinition> UserBlocks { get; set; } = [];
}
