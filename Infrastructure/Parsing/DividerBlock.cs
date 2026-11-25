using System.Text.RegularExpressions;
using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 中间带文字的分割线：<c>--- 文本 ---</c>；虚线写作 <c>--- 文本 --- {line=dashed}</c>。
/// 与普通 <c>---</c> 分割线互不抢占：没有中间文字时仍由 Markdig 的 ThematicBreakParser 处理。
/// </summary>
public sealed class DividerBlock : LeafBlock
{
    public DividerBlock(BlockParser parser)
        : base(parser)
    {
    }

    /// <summary>两侧横线之间的文字/表情/符号。</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>线条是否为虚线（尾部 <c>{line=dashed}</c>）。</summary>
    public bool Dashed { get; set; }
}

/// <summary>
/// 分割线语法解析的唯一入口：解析器与属性面板改写共用同一份规则，
/// 避免面板重建出的行与解析器识别的行结构不一致。
/// </summary>
public static partial class DividerSyntax
{
    public const string LineSolid = "solid";
    public const string LineDashed = "dashed";

    /// <summary>解析器写到节点上的标签派生属性（只读）。</summary>
    public const string TextAttribute = "divider-text";

    /// <summary>解析器写到节点上的线型派生属性：solid / dashed。</summary>
    public const string LineAttribute = "divider-line";

    /// <summary>一行分割线的解析结果；下标均相对于传入的整行。</summary>
    public readonly record struct LineInfo(
        string Label,
        bool Dashed,
        int LabelStart,
        int LabelLength,
        string Prefix);

    [GeneratedRegex(
        @"^(?<lead>[ \t]{0,3})(?<left>-{3,})[ \t]+(?<label>.+?)[ \t]+(?<right>-{3,})(?<suffix>[ \t]*(?:\{(?<attrs>[^{}\r\n]*)\})?)$",
        RegexOptions.Compiled)]
    private static partial Regex LinePattern();

    public static bool TryParse(string? line, out LineInfo info)
    {
        info = default;
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        // 调用方可能带入 Windows 换行里的 \r。
        line = line.TrimEnd('\r');

        var match = LinePattern().Match(line);
        if (!match.Success)
        {
            return false;
        }

        var labelGroup = match.Groups["label"];
        var label = labelGroup.Value.Trim();
        if (label.Length == 0)
        {
            return false;
        }

        // 正则的 label 组可能带首尾空白，写回时必须只替换真正的文字区间。
        var leading = labelGroup.Value.Length - labelGroup.Value.TrimStart().Length;
        var suffix = match.Groups["suffix"];

        info = new LineInfo(
            label,
            IsDashed(match.Groups["attrs"].Value),
            labelGroup.Index + leading,
            label.Length,
            line[..suffix.Index]);
        return true;
    }

    /// <summary>尾部属性区支持的写法：<c>line=dashed</c>、<c>line:dashed</c> 或裸 <c>dashed</c>。</summary>
    private static bool IsDashed(string attrsBody)
    {
        var dashed = false;
        foreach (var token in attrsBody.Split([' ', '\t', ','], System.StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals(LineDashed, System.StringComparison.OrdinalIgnoreCase)
                || token.Equals("line=" + LineDashed, System.StringComparison.OrdinalIgnoreCase)
                || token.Equals("line:" + LineDashed, System.StringComparison.OrdinalIgnoreCase))
            {
                dashed = true;
            }
            else if (token.Equals(LineSolid, System.StringComparison.OrdinalIgnoreCase)
                     || token.Equals("line=" + LineSolid, System.StringComparison.OrdinalIgnoreCase)
                     || token.Equals("line:" + LineSolid, System.StringComparison.OrdinalIgnoreCase))
            {
                dashed = false;
            }
        }

        return dashed;
    }
}

/// <summary>
/// 只认 <c>--- 文本 ---</c> 这一种带中间文字的分割线；
/// 普通 <c>---</c>、<c>* * *</c> 等仍交给 Markdig 原生分割线。
/// </summary>
public sealed class DividerParser : BlockParser
{
    public DividerParser()
    {
        OpeningCharacters = ['-'];
    }

    public override bool CanInterrupt(BlockProcessor processor, Block block) => true;

    public override BlockState TryOpen(BlockProcessor processor)
    {
        if (processor.IsCodeIndent)
        {
            return BlockState.None;
        }

        if (!DividerSyntax.TryParse(processor.Line.ToString(), out var info))
        {
            return BlockState.None;
        }

        var block = new DividerBlock(this)
        {
            Label = info.Label,
            Dashed = info.Dashed,
            Column = processor.Column,
            Span = new SourceSpan(processor.Start, processor.Line.End),
        };

        processor.NewBlocks.Push(block);
        return BlockState.BreakDiscard;
    }
}
