using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>通用用户块；内容由 Markdig 递归解析，因此支持任意标准元素与同类嵌套。</summary>
public sealed class UserBlock : ContainerBlock
{
    public UserBlock(
        BlockParser parser,
        UserBlockDefinition definition,
        int fenceLength,
        char fenceChar,
        string header)
        : base(parser)
    {
        Definition = definition;
        FenceLength = fenceLength;
        FenceChar = fenceChar;
        Header = header;
    }

    public UserBlockDefinition Definition { get; }
    public int FenceLength { get; }

    /// <summary>开围栏与闭围栏使用的字符；闭围栏必须用同一字符。</summary>
    public char FenceChar { get; }

    public string Header { get; }
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);
    public bool Closed { get; set; }

    /// <summary>闭围栏所在行（0-based）；未闭合时为 -1。</summary>
    public int ClosingFenceLineIndex { get; set; } = -1;

    /// <summary>闭围栏行内容的末字符绝对下标；闭围栏行必须计入块范围。</summary>
    public int ClosingFenceEnd { get; set; } = -1;

    /// <summary>
    /// 解析侧产物：开围栏到闭围栏（未闭合时到 EOF）的全文区间。
    /// 由 <see cref="UserBlockRangeResolver"/> 在解析完成后写入，转换器只读。
    /// </summary>
    public SourceRange Range { get; set; } = SourceRange.Empty();
}
