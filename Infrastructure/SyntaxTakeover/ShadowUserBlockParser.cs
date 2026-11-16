using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.SyntaxTakeover;

/// <summary>
/// 影子解析链的定义围栏识别器（<c>:::kind key=value</c>、<c>'''kind</c>、<c>```kind</c>）。
/// 名称解析、围栏长度、优先级与嵌套上限全部在本类内独立实现，
/// 不复用生产 <see cref="UserBlockParser"/> 的判定代码，从而使双跑对比真实有效。
/// 每个实例只认一个围栏字符，与生产管线的“一族一个解析器”保持一致。
/// </summary>
public sealed class ShadowUserBlockParser : BlockParser
{
    /// <summary>影子链实际覆盖的围栏字符；双跑报告据此说明覆盖范围。</summary>
    public static IReadOnlyList<char> SupportedFenceChars { get; } =
    [
        UserBlockRegistry.SupportedFenceChar,
        UserBlockRegistry.PrimeFenceChar,
        UserBlockRegistry.BacktickFenceChar,
        UserBlockRegistry.TildeFenceChar,
    ];

    private const int MinFenceLength = 3;
    private const int MaxNestingDepth = 32;

    private readonly IReadOnlyList<UserBlockDefinition> _definitions;
    private readonly char _fenceChar;

    public ShadowUserBlockParser(UserBlockRegistry registry, char fenceChar)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _definitions = registry.Definitions;
        _fenceChar = fenceChar;
        OpeningCharacters = [fenceChar];
    }

    public override bool CanInterrupt(BlockProcessor processor, Block block) => true;

    public override BlockState TryOpen(BlockProcessor processor)
    {
        if (processor.IsCodeIndent)
        {
            return BlockState.None;
        }

        var line = processor.Line;
        SkipIndent(ref line);

        var fenceLength = CountFence(ref line);
        if (fenceLength < MinFenceLength)
        {
            return BlockState.None;
        }

        var header = line.ToString().Trim();
        if (header.Length == 0)
        {
            return BlockState.None;
        }

        if (!TryResolve(header, fenceLength, out var definition, out var keywordLength))
        {
            return BlockState.None;
        }

        // 超深嵌套不再开新容器：开围栏行降级为正文，避免递归爆栈。
        if (IsInsideTooDeep(processor.CurrentBlock))
        {
            return BlockState.None;
        }

        var block = new UserBlock(this, definition, fenceLength, _fenceChar, header)
        {
            Column = processor.Column,
            Span = new SourceSpan(processor.Start, line.End),
        };

        ReadAttributes(header.AsSpan(keywordLength), block.Attributes);

        processor.NewBlocks.Push(block);
        return BlockState.ContinueDiscard;
    }

    public override BlockState TryContinue(BlockProcessor processor, Block block)
    {
        // 内层用户块未闭合时外层不得截胡闭围栏，否则嵌套会被压平。
        if (block is ContainerBlock container && HasUnclosedInside(container))
        {
            return BlockState.Continue;
        }

        var line = processor.Line;
        SkipIndent(ref line);

        if (line.CurrentChar != _fenceChar)
        {
            return BlockState.Continue;
        }

        var closingLength = CountFence(ref line);

        // 闭围栏不得短于开围栏，且其后只能是空白。
        if (closingLength >= ((UserBlock)block).FenceLength
            && line.ToString().Trim().Length == 0)
        {
            var userBlock = (UserBlock)block;
            userBlock.Closed = true;
            userBlock.ClosingFenceLineIndex = processor.LineIndex;
            userBlock.ClosingFenceEnd = processor.Line.End;
            block.UpdateSpanEnd(line.Start);
            return BlockState.BreakDiscard;
        }

        return BlockState.Continue;
    }

    /// <summary>开围栏首词整词匹配（本名或别名）+ 围栏长度下限 + 优先级/长度双降序择一。</summary>
    private bool TryResolve(
        string header,
        int fenceLength,
        out UserBlockDefinition definition,
        out int keywordLength)
    {
        definition = null!;
        keywordLength = 0;

        var nameEnd = 0;
        while (nameEnd < header.Length && !char.IsWhiteSpace(header[nameEnd]))
        {
            nameEnd++;
        }

        if (nameEnd == 0)
        {
            return false;
        }

        var name = header[..nameEnd];
        UserBlockDefinition? best = null;
        foreach (var candidate in _definitions)
        {
            // 同名跨族互不影响：影子实现同样按声明的围栏字符集合过滤，
            // 否则双跑口径与生产不一致（多字符定义在任一已声明字符下都可命中）。
            if (!candidate.UsesFenceChar(_fenceChar))
            {
                continue;
            }

            // 别名与本名同等参与匹配：命中的关键字长度决定属性起始位置。
            if (!candidate.MatchesKeyword(name))
            {
                continue;
            }

            if (fenceLength < candidate.FenceLength)
            {
                continue;
            }

            if (best is null
                || candidate.Priority > best.Priority
                || (candidate.Priority == best.Priority && candidate.FenceLength > best.FenceLength))
            {
                best = candidate;
            }
        }

        if (best is null)
        {
            return false;
        }

        definition = best;
        keywordLength = nameEnd;
        return true;
    }

    private static void SkipIndent(ref StringSlice line)
    {
        var indent = 0;
        while (indent < 3 && line.CurrentChar == ' ')
        {
            indent++;
            line.NextChar();
        }
    }

    private int CountFence(ref StringSlice line)
    {
        var count = 0;
        while (line.CurrentChar == _fenceChar)
        {
            count++;
            line.NextChar();
        }

        return count;
    }

    private static bool HasUnclosedInside(ContainerBlock container)
    {
        foreach (var child in container)
        {
            if (child is UserBlock { Closed: false })
            {
                return true;
            }

            if (child is ContainerBlock nested && HasUnclosedInside(nested))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInsideTooDeep(Block? current)
    {
        var depth = 0;
        for (var cursor = current; cursor is not null; cursor = cursor.Parent)
        {
            if (cursor is not UserBlock)
            {
                continue;
            }

            depth++;
            if (depth >= MaxNestingDepth)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>行内属性区：key=value、裸 key、双引号含空格值。</summary>
    private static void ReadAttributes(ReadOnlySpan<char> rest, Dictionary<string, string> target)
    {
        rest = rest.TrimStart();
        while (!rest.IsEmpty)
        {
            var tokenLength = 0;
            var inQuotes = false;
            while (tokenLength < rest.Length)
            {
                var ch = rest[tokenLength];
                if (ch == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (!inQuotes && char.IsWhiteSpace(ch))
                {
                    break;
                }

                tokenLength++;
            }

            var token = rest[..tokenLength];
            rest = tokenLength >= rest.Length ? ReadOnlySpan<char>.Empty : rest[tokenLength..].TrimStart();

            if (token.IsEmpty)
            {
                continue;
            }

            var equals = token.IndexOf('=');
            if (equals <= 0)
            {
                target[token.ToString()] = "true";
                continue;
            }

            var key = token[..equals].ToString();
            if (key.Length == 0)
            {
                continue;
            }

            var value = token[(equals + 1)..].ToString();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            target[key] = value;
        }
    }
}
