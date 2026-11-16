using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 数据驱动的块解析器：不写死元素名，只查注册表。
/// 开围栏为 3 个以上 <see cref="_fenceChar"/> + 已注册名称 + 可选行内 key=value；闭围栏为裸围栏行。
/// 冒号族与单引号族各注册一个实例，两族互不抢行。
/// </summary>
public sealed class UserBlockParser : BlockParser
{
    private readonly UserBlockRegistry _registry;
    private readonly char _fenceChar;

    public UserBlockParser(UserBlockRegistry registry, char fenceChar)
    {
        _registry = registry;
        _fenceChar = fenceChar;
        OpeningCharacters = [fenceChar];
    }

    public override bool CanInterrupt(BlockProcessor processor, Block block) => true;

    public override BlockState TryOpen(BlockProcessor processor)
    {
        if (processor.IsCodeIndent) return BlockState.None;

        var line = processor.Line;
        SkipUpToThreeSpaces(ref line);

        var fenceLength = CountFenceChars(ref line, _fenceChar);
        if (fenceLength < UserBlockRegistry.MinFenceLength) return BlockState.None;

        var header = line.ToString().Trim();
        if (header.Length == 0) return BlockState.None;

        if (!_registry.TryResolve(header, fenceLength, _fenceChar, out var definition, out var keywordLength))
        {
            return BlockState.None;
        }

        // 超深嵌套：不再开新容器，开围栏行降级为正文。
        if (IsInsideUserBlockTooDeep(processor.CurrentBlock, UserBlockRegistry.MaxNestingDepth))
        {
            return BlockState.None;
        }

        var block = new UserBlock(this, definition, fenceLength, _fenceChar, header)
        {
            Column = processor.Column,
            Span = new SourceSpan(processor.Start, line.End),
        };

        ParseAttributes(header.AsSpan(keywordLength), block.Attributes);

        processor.NewBlocks.Push(block);
        return BlockState.ContinueDiscard;
    }

    public override BlockState TryContinue(BlockProcessor processor, Block block)
    {
        // Markdig 先问外层容器；子块还开着时外层不能截胡闭围栏，否则嵌套层次会被压平。
        if (block is ContainerBlock container && HasUnclosedUserBlockInside(container))
        {
            return BlockState.Continue;
        }

        var line = processor.Line;
        SkipUpToThreeSpaces(ref line);

        var userBlock = (UserBlock)block;
        if (line.CurrentChar != userBlock.FenceChar)
        {
            return BlockState.Continue;
        }

        var closeLength = CountFenceChars(ref line, userBlock.FenceChar);

        // 闭围栏长度不得短于开围栏，且其后只能是空白。
        if (closeLength >= userBlock.FenceLength
            && line.ToString().Trim().Length == 0)
        {
            userBlock.Closed = true;
            userBlock.ClosingFenceLineIndex = processor.LineIndex;
            userBlock.ClosingFenceEnd = processor.Line.End;
            block.UpdateSpanEnd(line.Start);
            return BlockState.BreakDiscard;
        }

        return BlockState.Continue;
    }

    private static void SkipUpToThreeSpaces(ref StringSlice line)
    {
        var indent = 0;
        while (indent < 3 && line.CurrentChar == ' ')
        {
            indent++;
            line.NextChar();
        }
    }

    private static int CountFenceChars(ref StringSlice line, char fenceChar)
    {
        var count = 0;
        while (line.CurrentChar == fenceChar)
        {
            count++;
            line.NextChar();
        }

        return count;
    }

    private static bool HasUnclosedUserBlockInside(ContainerBlock block)
    {
        foreach (var child in block)
        {
            if (child is UserBlock { Closed: false }) return true;
            if (child is ContainerBlock container && HasUnclosedUserBlockInside(container)) return true;
        }

        return false;
    }

    private static bool IsInsideUserBlockTooDeep(Block? current, int maxDepth)
    {
        var depth = 0;
        for (var cur = current; cur is not null; cur = cur.Parent)
        {
            if (cur is not UserBlock) continue;
            depth++;
            if (depth >= maxDepth) return true;
        }

        return false;
    }

    /// <summary>解析行内属性区：支持 key=value、裸 key，以及双引号包裹的含空格值。</summary>
    private static void ParseAttributes(ReadOnlySpan<char> rest, Dictionary<string, string> target)
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

            if (token.IsEmpty) continue;

            var equals = token.IndexOf('=');
            if (equals <= 0)
            {
                target[token.ToString()] = "true";
                continue;
            }

            var key = token[..equals].ToString();
            var value = token[(equals + 1)..].ToString();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            if (key.Length > 0)
            {
                target[key] = value;
            }
        }
    }
}
