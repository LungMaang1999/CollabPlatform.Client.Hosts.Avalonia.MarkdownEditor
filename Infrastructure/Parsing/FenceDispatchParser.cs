using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>围栏块共有的闭合围栏行号，供分派解析器统一回填，避免按块类型分支。</summary>
public interface IFencedBlock
{
    /// <summary>0-based source line index of the closing fence, when matched.</summary>
    int? ClosingFenceLineIndex { get; set; }
}

/// <summary>
/// 一次开围栏的上下文：围栏字符之后的行、命中的关键字与其长度、以及为该目标解析出的条目标记。
/// 块工厂只从这里取值，不自行扫描源码。
/// </summary>
public readonly record struct FenceOpenContext(
    BlockParser Parser,
    BlockProcessor Processor,
    int SpanStart,
    int SpanEnd,
    string SourceText,
    string Rest,
    int KeywordLength,
    FenceKeywordEntry Entry,
    string? ItemMarker);

/// <summary>
/// 三单引号族（<c>'''</c>）的唯一围栏解析器：关键字与目标的对应关系来自运行时
/// <see cref="FenceKeywordRegistry"/>（内置声明 + 定义声明合并），
/// 目标到块类型走块工厂表。本类不含按元素类型的控制流：
/// 新增一种首词形态只加定义数据，不新增解析器类。
/// </summary>
public sealed class FenceDispatchParser : BlockParser
{
    private const char FenceChar = '\'';

    /// <summary>分派目标 → 建块工厂。新增目标才需要在这里登记；新增关键字不需要。</summary>
    private static readonly Dictionary<FenceTarget, Func<FenceOpenContext, LeafBlock>> Factories = new()
    {
        [FenceTarget.Poem] = context => new PoemBlock(context.Parser)
        {
            PoemType = context.Entry.PoemType ?? PoemType.General,
            Alias = context.Entry.Keyword,
            SourceText = context.SourceText
        },
        [FenceTarget.Timeline] = context => new TimelineBlock(context.Parser)
        {
            Info = ReadOptions(context),
            SourceText = context.SourceText,
            ItemMarker = context.ItemMarker
        },
        [FenceTarget.Radar] = context => new RadarBlock(context.Parser)
        {
            Info = ReadOptions(context)
        },
        [FenceTarget.ImageMatrix] = context => new ImageMatrixBlock(context.Parser),
    };

    private readonly FenceKeywordRegistry _keywords;
    private readonly IReadOnlyDictionary<FenceTarget, string>? _itemMarkers;

    /// <param name="keywords">本次运行时使用的关键字表（含定义贡献的首词）。</param>
    /// <param name="itemMarkers">按目标声明的条目标记；缺省时由块自身回落旧行为。</param>
    public FenceDispatchParser(
        FenceKeywordRegistry keywords,
        IReadOnlyDictionary<FenceTarget, string>? itemMarkers = null)
    {
        _keywords = keywords ?? throw new ArgumentNullException(nameof(keywords));
        _itemMarkers = itemMarkers;
        OpeningCharacters = [FenceChar];
    }

    public override bool CanInterrupt(BlockProcessor processor, Block block) => true;

    public override BlockState TryOpen(BlockProcessor processor)
    {
        if (processor.IsCodeIndent)
        {
            return BlockState.None;
        }

        var line = processor.Line;

        var count = 0;
        while (line.CurrentChar == FenceChar)
        {
            count++;
            line.NextChar();
        }

        if (count < 3)
        {
            return BlockState.None;
        }

        var rest = line.ToString().TrimStart();
        if (!_keywords.TryMatchPrefix(FenceFamily.PrimeFence, rest, out var entry, out var keywordLength))
        {
            return BlockState.None;
        }

        if (!Factories.TryGetValue(entry.Target, out var factory))
        {
            return BlockState.None;
        }

        var itemMarker = _itemMarkers is not null && _itemMarkers.TryGetValue(entry.Target, out var marker)
            ? marker
            : null;

        var context = new FenceOpenContext(
            this,
            processor,
            processor.Start,
            line.End,
            line.Text,
            rest,
            keywordLength,
            entry,
            itemMarker);

        var block = factory(context);
        block.Column = processor.Column;
        block.Span = new SourceSpan(processor.Start, line.End);
        block.Lines = new StringLineGroup(4);

        processor.NewBlocks.Push(block);
        return BlockState.ContinueDiscard;
    }

    public override BlockState TryContinue(BlockProcessor processor, Block block)
    {
        var line = processor.Line;

        var indent = 0;
        while (indent < 3 && line.CurrentChar == ' ')
        {
            indent++;
            line.NextChar();
        }

        var closeCount = 0;
        while (line.CurrentChar == FenceChar)
        {
            closeCount++;
            line.NextChar();
        }

        if (closeCount >= 3)
        {
            var tail = line.ToString().Trim();
            if (tail.Length == 0)
            {
                ((IFencedBlock)block).ClosingFenceLineIndex = processor.LineIndex;
                block.UpdateSpanEnd(line.Start);
                return BlockState.BreakDiscard;
            }
        }

        return BlockState.Continue;
    }

    /// <summary>开围栏关键字之后的空白与选项（<c>line=</c>/<c>gap=</c> 等），无则为 null。</summary>
    private static string? ReadOptions(in FenceOpenContext context) =>
        context.Rest.Length > context.KeywordLength
            ? context.Rest[context.KeywordLength..].Trim()
            : null;
}
