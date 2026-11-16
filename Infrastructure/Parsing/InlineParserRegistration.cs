using Markdig;
using Markdig.Parsers;
using Markdig.Parsers.Inlines;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 行内接管的唯一注册入口：生产管线（<see cref="MarkdownParser.CreateRuntime"/>）与影子管线
/// （<c>ShadowSyntaxTakeover.BuildPipeline</c>）都只从这里登记行内 parser。
/// <para>
/// 下标不是自由变量：<c>InlineParsers</c> 的次序决定同一触发符上的相对优先级，
/// 两端的次序必须由同一份代码给出，并在注册后立即自检；位移一旦被破坏，
/// 直接抛异常，而不是让优先级静默漂移。
/// </para>
/// </summary>
internal static class InlineParserRegistration
{
    public static void Apply(MarkdownPipelineBuilder builder, MarkdownParserOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        // 先登记行内公式：与既有行为一致，仍在列表首位（优先于同为 '\' 触发的转义解析）。
        if (options.EnableMathematics)
        {
            builder.InlineParsers.Insert(0, new LatexInlineMathParser());
        }

        if (options.EnableInlineCodeTakeover)
        {
            // 原位替换，不前置影子：同一个 '`' 触发符只能有一个所有者。
            if (!builder.InlineParsers.Replace<CodeInlineParser>(new InlineCodeSpanParser()))
            {
                throw new InvalidOperationException(
                    "未找到 Markdig CodeInlineParser，行内代码接管无法按原位替换。");
            }
        }

        VerifyOrder(builder.InlineParsers, options);
    }

    private static void VerifyOrder(IList<InlineParser> parsers, MarkdownParserOptions options)
    {
        // 触发符唯一所有权：'`' 这一族只能有一个解析器，且必须是接管实现。
        var backtickOwners = parsers
            .Select((parser, index) => (parser, index))
            .Where(item => item.parser.OpeningCharacters?.Contains('`') == true)
            .ToList();

        if (backtickOwners.Count != 1)
        {
            throw new InvalidOperationException(
                $"反引号触发符必须且只能有一个解析器，实际 {backtickOwners.Count} 个。");
        }

        if (options.EnableInlineCodeTakeover)
        {
            var owner = backtickOwners[0];
            if (owner.parser is not InlineCodeSpanParser)
            {
                throw new InvalidOperationException(
                    $"反引号触发符的所有者应为 {nameof(InlineCodeSpanParser)}，实际 {owner.parser.GetType().Name}。");
            }

            // 相对位置钉死：晚于强调、早于自动链接，与 Markdig 原生代码段解析器同位。
            var emphasisIndex = IndexOf(parsers, typeof(EmphasisInlineParser));
            var autolinkIndex = IndexOf(parsers, typeof(AutolinkInlineParser));
            if (emphasisIndex < 0 || autolinkIndex < 0 || owner.index != emphasisIndex + 1 || owner.index >= autolinkIndex)
            {
                throw new InvalidOperationException(
                    "行内代码接管解析器下标漂移："
                    + $"owner={owner.index}, emphasis={emphasisIndex}, autolink={autolinkIndex}。");
            }
        }

        if (options.EnableMathematics)
        {
            var mathIndex = IndexOf(parsers, typeof(LatexInlineMathParser));
            var escapeIndex = IndexOf(parsers, typeof(EscapeInlineParser));
            if (mathIndex != 0 || escapeIndex < 0 || mathIndex >= escapeIndex)
            {
                throw new InvalidOperationException(
                    "行内公式解析器必须先于转义解析器且位于首位："
                    + $"math={mathIndex}, escape={escapeIndex}。");
            }
        }
    }

    private static int IndexOf(IList<InlineParser> parsers, Type parserType)
    {
        for (var i = 0; i < parsers.Count; i++)
        {
            if (parsers[i].GetType() == parserType)
            {
                return i;
            }
        }

        return -1;
    }
}
