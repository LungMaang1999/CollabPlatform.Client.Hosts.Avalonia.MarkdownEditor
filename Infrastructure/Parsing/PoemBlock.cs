using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.SyntaxTakeover;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

public enum PoemType
{
    General,
    AncientPoem,
    Shi,
    Ci,
    Qu
}

public sealed class PoemBlock : LeafBlock, IFencedBlock
{
    private PoemLayout? _layout;

    public PoemBlock(BlockParser parser) : base(parser)
    {
    }

    public PoemType PoemType { get; set; } = PoemType.General;

    public string Alias { get; set; } = "poem";

    /// <summary>预扫描计划中的围栏层级（从 1 开始）；0 表示退回旧的闭合规则。</summary>
    public int NestingDepth { get; set; }

    /// <summary>0-based source line index of the closing fence, when matched.</summary>
    public int? ClosingFenceLineIndex { get; set; }

    /// <summary>解析器在开围栏时捕获的全文源码，供围栏体切分使用。</summary>
    public string? SourceText { get; set; }

    /// <summary>围栏体原文（不含开闭围栏行与尾部换行）。</summary>
    public string BodyText
    {
        get
        {
            if (SourceText is null || Span.Start < 0)
            {
                return Lines.ToString().TrimEnd('\r', '\n');
            }

            var bodyStart = NextLineStart(SourceText, Span.Start);
            var bodyEnd = SourceText.Length;
            if (ClosingFenceLineIndex is int closing)
            {
                var cursor = bodyStart;
                var lineIndex = Line + 1;
                while (lineIndex < closing && cursor < SourceText.Length)
                {
                    cursor = NextLineStart(SourceText, cursor);
                    lineIndex++;
                }

                bodyEnd = Math.Min(cursor, SourceText.Length);
            }

            if (bodyStart > bodyEnd)
            {
                return string.Empty;
            }

            return SourceText[bodyStart..bodyEnd].TrimEnd('\r', '\n');
        }
    }

    /// <summary>
    /// 诗体切片（元数据字段 + 诗句组）。切分由 parser 侧完成（<see cref="PoemLayoutBuilder"/>），
    /// 首次读取时计算一次；转换器只按切片建节点。
    /// </summary>
    public PoemLayout Layout => _layout ??= PoemLayoutBuilder.Build(BodyText, PartRange);

    /// <summary>
    /// 诗体节点及其子节点共享的源码区间（与转换器对围栏块的取值一致）：
    /// 闭合围栏存在时按闭围栏行收尾，否则按叶块行组收尾。
    /// </summary>
    public SourceRange PartRange
    {
        get
        {
            var startOffset = Math.Max(0, Span.Start);
            var length = Math.Max(0, Span.Length);
            var startLine = Math.Max(1, Line + 1);
            var startColumn = Math.Max(1, Column + 1);

            var endLine = startLine;
            var endColumn = startColumn + length;

            if (ClosingFenceLineIndex is >= 0)
            {
                endLine = ClosingFenceLineIndex.Value + 1;
            }
            else if (Lines.Lines != null && Lines.Count > 0)
            {
                endLine = startLine + Lines.Count - 1;
                var lastLine = Lines.Lines[Lines.Count - 1];
                endColumn = lastLine.Slice.Length + 1;
            }

            return new SourceRange
            {
                StartOffset = startOffset,
                Length = length,
                StartLine = startLine,
                StartColumn = startColumn,
                EndLine = endLine,
                EndColumn = endColumn
            };
        }
    }

    private static int NextLineStart(string source, int fromOffset)
    {
        var newlineIndex = source.IndexOf('\n', fromOffset);
        return newlineIndex < 0 ? source.Length : newlineIndex + 1;
    }
}
