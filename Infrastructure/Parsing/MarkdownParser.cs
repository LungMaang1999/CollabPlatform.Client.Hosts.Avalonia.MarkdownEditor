using System.Text.RegularExpressions;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Parsing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax.Factories;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax.Matching;
using Markdig;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

public sealed class MarkdownParser : IMarkdownParser
{
    private readonly MarkdownParserOptions _options;
    private readonly NodeFactory _nodeFactory;
    private readonly SectionBuilder _sectionBuilder;
    private readonly INodeIdentityMatcher _identityMatcher;
    private readonly MarkdigToNodeConverter _converter;
    private readonly MarkdownPipeline _pipeline;

    public MarkdownParser(
        MarkdownParserOptions options,
        NodeFactory? nodeFactory = null,
        SectionBuilder? sectionBuilder = null,
        INodeIdentityMatcher? identityMatcher = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _nodeFactory = nodeFactory ?? new NodeFactory();
        _sectionBuilder = sectionBuilder ?? new SectionBuilder(_nodeFactory);
        _identityMatcher = identityMatcher ?? new NodeIdentityMatcher();

        var pipelineBuilder = new MarkdownPipelineBuilder();
        if (_options.EnablePipeTables) pipelineBuilder.UsePipeTables();
        if (_options.EnableTaskLists) pipelineBuilder.UseTaskLists();
        if (_options.EnableFootnotes) pipelineBuilder.UseFootnotes();
        if (_options.EnableYamlFrontMatter) pipelineBuilder.UseYamlFrontMatter();
        if (_options.EnableEmphasisExtras) pipelineBuilder.UseEmphasisExtras();
        if (_options.EnableAutoLinks) pipelineBuilder.UseAutoLinks();
        if (_options.EnableMathematics)
        {
            pipelineBuilder.UseMathematics();

            pipelineBuilder.InlineParsers.Insert(0, new LatexInlineMathParser());
            pipelineBuilder.BlockParsers.Insert(0, new LatexBlockMathParser());
        }

        pipelineBuilder.BlockParsers.Insert(0, new ImageMatrixFenceParser());
        pipelineBuilder.BlockParsers.Insert(0, new PoemFenceParser());
        pipelineBuilder.BlockParsers.Insert(0, new TimelineFenceParser());

        _pipeline = pipelineBuilder.Build();

        // 转换器需要 pipeline 以递归解析时间线节点正文。
        _converter = new MarkdigToNodeConverter(_nodeFactory, _pipeline);
    }

    public ParseResult Parse(
        string source,
        MarkdownDocument? previous = null,
        ParseMode mode = ParseMode.Editing)
    {
        source ??= string.Empty;

        var diagnostics = new List<DiagnosticMessage>();
        var root = ParseTreeCore(source, previous?.Root, diagnostics, previous?.SourceMarkdown);

        var document = previous ?? new MarkdownDocument();
        document.ReplaceParsedRoot(root, source, markChanged: mode == ParseMode.Editing);

        if (mode == ParseMode.Loading)
        {
            document.MarkSaved();
        }

        return new ParseResult(document, diagnostics);
    }

    public MarkdownNode ParseTree(string source, MarkdownNode? previousRoot, string? previousSource = null)
    {
        source ??= string.Empty;
        var diagnostics = new List<DiagnosticMessage>();
        return ParseTreeCore(source, previousRoot, diagnostics, previousSource);
    }

    private MarkdownNode ParseTreeCore(
        string source,
        MarkdownNode? previousRoot,
        ICollection<DiagnosticMessage> diagnostics,
        string? previousSource = null)
    {
        if (previousRoot is not null
            && TryParseAppendedSuffix(source, previousSource, previousRoot, diagnostics, out var incrementalRoot))
        {
            return incrementalRoot;
        }

        if (previousRoot is not null
            && TryParseModifiedRegion(source, previousSource, previousRoot, diagnostics, out incrementalRoot))
        {
            return incrementalRoot;
        }

        var blocks = ParseBlocks(source, diagnostics);
        var root = _sectionBuilder.Build(blocks);

        if (previousRoot is not null)
        {
            // Matcher 只写新树，previousRoot 保持只读。
            _identityMatcher.Match(previousRoot, root);
        }

        return root;
    }

    // 链接引用定义 [id]: 与脚注定义 [^id]:（含冒号前可选空白）。
    private static readonly Regex ReferenceDefinitionLineRegex = new(
        @"^[ \t]{0,3}\[[^\]\n]+\][ \t]*:",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// 严格前缀追加的增量解析：前缀块直接复用上一棵树的对象，只解析追加部分，
    /// 平移 Range 后重建章节。任何不确定场景返回 false 走全量解析。
    /// </summary>
    private bool TryParseAppendedSuffix(
        string source,
        string? previousSource,
        MarkdownNode previousRoot,
        ICollection<DiagnosticMessage> diagnostics,
        out MarkdownNode root)
    {
        root = null!;

        if (string.IsNullOrEmpty(previousSource)
            || source.Length <= previousSource.Length
            || !source.StartsWith(previousSource, StringComparison.Ordinal))
        {
            return false;
        }

        var appendOffset = previousSource.Length;

        var previousBlocks = FlattenConcreteBlocks(previousRoot);
        if (previousBlocks.Count == 0)
        {
            return false;
        }

        var cutBlockIndex = -1;
        for (var i = 0; i < previousBlocks.Count; i++)
        {
            var range = previousBlocks[i].Range;
            if (range.StartOffset >= 0
                && range.StartOffset <= appendOffset
                && appendOffset <= range.EndOffset)
            {
                cutBlockIndex = i;
                break;
            }
        }

        // 追加点落在尾部空行：从最后一个块开始重解析。
        if (cutBlockIndex < 0)
        {
            cutBlockIndex = previousBlocks.Count - 1;
        }

        // 从受影响块向前回退寻找“密封边界”：探测首块与锚点类型/RawMarkdown
        // 一致则前方前缀可安全复用；否则追加与锚点发生回溯性合并，继续扩大重解析区。
        const int maxWalkBack = 8;
        var lowest = Math.Max(1, cutBlockIndex - maxWalkBack + 1);

        for (var k = cutBlockIndex; k >= lowest; k--)
        {
            var anchor = previousBlocks[k];
            if (anchor.Range.StartOffset < 0 || anchor.Range.Length <= 0)
            {
                return false;
            }

            var start = GetLineStart(source, anchor.Range.StartOffset);
            if (start <= 0)
            {
                return false;
            }

            var prefixOrdered = true;
            for (var i = 0; i < k; i++)
            {
                if (previousBlocks[i].Range.EndOffset > start)
                {
                    prefixOrdered = false;
                    break;
                }
            }
            if (!prefixOrdered)
            {
                // Range 与切点交错说明结构假设不成立，保守全量。
                return false;
            }

            var probeText = source.Substring(start);

            // 引用定义是文档级解析：重解析区出现 [ 或区内新定义可能影响区外，
            // 区域无法自给自足时扩大区域，最终走全量。
            if (ReferenceDefinitionLineRegex.IsMatch(previousSource)
                && probeText.IndexOf('[') >= 0)
            {
                continue;
            }
            if (ReferenceDefinitionLineRegex.IsMatch(probeText)
                && source.AsSpan(0, start).Contains('['))
            {
                continue;
            }

            Markdig.Syntax.MarkdownDocument probeDocument;
            try
            {
                probeDocument = Markdig.Markdown.Parse(probeText, _pipeline);
            }
            catch
            {
                // 探测解析异常不致命：交回全量路径记录诊断。
                return false;
            }

            List<MarkdownNode> probeBlocks;
            try
            {
                // 探测块 Range 为局部坐标，选中边界后统一平移。
                probeBlocks = _converter.ConvertBlocks(probeDocument, new List<DiagnosticMessage>(), probeText);
            }
            catch
            {
                return false;
            }

            if (probeBlocks.Count == 0)
            {
                continue;
            }

            var first = probeBlocks[0];
            var sealedBoundary = first.Type == anchor.Type
                && string.Equals(first.RawMarkdown, anchor.RawMarkdown, StringComparison.Ordinal);

            if (!sealedBoundary)
            {
                continue;
            }

            // 优先在旧 Section 树上做尾部拼接；失败再退回拍平全建。
            if (TrySpliceAppendedTail(
                    source, previousRoot, previousBlocks, k, probeBlocks, start, out root))
            {
                return true;
            }

            // splice 不修改共享前缀；拍平重建抛异常则返回 false，不发布半成品树。
            try
            {
                var prefixBlocks = new List<MarkdownNode>(k);
                for (var i = 0; i < k; i++)
                {
                    prefixBlocks.Add(previousBlocks[i]);
                }

                var lineDelta = CountLineBreaks(source.AsSpan(0, start));
                foreach (var block in probeBlocks)
                {
                    ShiftBlockRange(block, start, lineDelta);
                }

                var mergedBlocks = new List<MarkdownNode>(prefixBlocks.Count + probeBlocks.Count);
                mergedBlocks.AddRange(prefixBlocks);
                mergedBlocks.AddRange(probeBlocks);

                // 前缀块是旧树共享对象，Parent 延迟到提交换根时切换。
                root = _sectionBuilder.Build(mergedBlocks, new HashSet<MarkdownNode>(prefixBlocks));
                _identityMatcher.Match(previousRoot, root);
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// 严格追加的“尾部 Section 拼接”：复用锚点 Section 链之前的对象（共享节点
    /// Parent 延迟切换，见 MarkdownNode.PendingParentLinks），探测块挂到复制的
    /// Section 外壳，身份匹配只在各层尾部子序列间进行。结构不成立返回 false。
    /// </summary>
    private bool TrySpliceAppendedTail(
        string source,
        MarkdownNode previousRoot,
        IReadOnlyList<MarkdownNode> previousBlocks,
        int anchorIndex,
        List<MarkdownNode> probeBlocks,
        int probeStart,
        out MarkdownNode root)
    {
        root = null!;

        try
        {
            var anchor = previousBlocks[anchorIndex];
            var anchorSection = anchor.Parent;
            if (anchorSection is null || anchorSection.Type != NodeType.Section)
            {
                return false;
            }

            // 自合成根 Section（level 0）到锚点直接父 Section 的链。
            var chain = new List<MarkdownNode>();
            for (var cur = anchorSection; cur is not null; cur = cur.Parent)
            {
                if (cur.Type != NodeType.Section) return false;
                chain.Add(cur);
                if (cur.Level == 0) break;
            }

            chain.Reverse();
            if (chain.Count == 0 || chain[0].Level != 0 || chain[0].Parent != previousRoot)
            {
                return false;
            }

            var depth = chain.Count;
            var childIndex = new int[depth - 1];
            for (var f = 0; f < depth - 1; f++)
            {
                childIndex[f] = chain[f].Children.IndexOf(chain[f + 1]);
                if (childIndex[f] < 0) return false;
            }

            var anchorIndexInSection = chain[^1].Children.IndexOf(anchor);
            if (anchorIndexInSection < 0) return false;

            // 探测块 Range 平移必须推迟到全部结构校验之后：提前 false 返回
            // 会走拍平回退，回退自己负责平移，先平移会导致二次平移。
            var lineDelta = CountLineBreaks(source.AsSpan(0, probeStart));

            // 逐层复制 Section 外壳，锚点之前的子节点共享旧引用；共享节点
            // 构建期不覆写 Parent，边记入延迟账本，提交换根时统一切换。
            var pendingParentLinks = new List<(MarkdownNode Child, MarkdownNode NewParent)>();
            var document = _nodeFactory.CreateDocument();
            document.Id = previousRoot.Id;

            var shells = new MarkdownNode[depth];
            var extras = new List<MarkdownNode>[depth];
            for (var f = 0; f < depth; f++)
            {
                shells[f] = _nodeFactory.CreateSection(chain[f].Level ?? 0);
                shells[f].Text = chain[f].Text;
                extras[f] = new List<MarkdownNode>();

                var prefixCount = f == depth - 1 ? anchorIndexInSection : childIndex[f];
                for (var ci = 0; ci < prefixCount; ci++)
                {
                    shells[f].AttachSharedChildDeferred(chain[f].Children[ci], pendingParentLinks);
                }
            }

            for (var f = 0; f < depth - 1; f++)
            {
                shells[f].AttachChildFromPriorTree(shells[f + 1]);
            }

            document.AttachChildFromPriorTree(shells[0]);

            // 与 SectionBuilder 完全相同的栈逻辑处理探测块（含新标题弹栈）。
            var shellSet = new HashSet<MarkdownNode>(shells);
            var frameOf = new Dictionary<MarkdownNode, int>(depth);
            for (var f = 0; f < depth; f++) frameOf[shells[f]] = f;

            // Stack 按枚举顺序压栈：正序传入使最深壳位于栈顶。
            var stack = new Stack<MarkdownNode>(shells);
            var poppedShells = new HashSet<MarkdownNode>();

            foreach (var node in probeBlocks)
            {
                if (node.Type != NodeType.Heading)
                {
                    var top = stack.Peek();
                    top.AttachChildFromPriorTree(node);
                    if (frameOf.TryGetValue(top, out var frame)) extras[frame].Add(node);
                    continue;
                }

                var level = Math.Max(1, node.Level ?? 1);
                while (stack.Count > 1 && (stack.Peek().Level ?? 0) >= level)
                {
                    var leaving = stack.Pop();
                    if (shellSet.Contains(leaving)) poppedShells.Add(leaving);
                }

                var section = _nodeFactory.CreateSection(level);
                section.Text = node.Text;
                section.AttachChildFromPriorTree(node);
                var parent = stack.Peek();
                parent.AttachChildFromPriorTree(section);
                if (frameOf.TryGetValue(parent, out var parentFrame))
                {
                    extras[parentFrame].Add(section);
                }

                stack.Push(section);
            }

            // 弹栈后为空的种子壳（如锚点本身是标题）必须移除：全量 Build
            // 不产出空 Section，且空壳不能承接旧 Section Id。
            var removedShells = new HashSet<MarkdownNode>();
            foreach (var dead in poppedShells)
            {
                if (dead.Children.Count == 0 && dead.Parent is not null && shellSet.Contains(dead.Parent))
                {
                    dead.Parent.Children.Remove(dead);
                    removedShells.Add(dead);
                }
            }

            // 保留下来的种子壳（含非空闭合壳）与旧链 Section 一一对应，承接其 Id。
            for (var f = 0; f < depth; f++)
            {
                if (!removedShells.Contains(shells[f])) shells[f].Id = chain[f].Id;
            }

            // 逐层只匹配旧/新尾部子序列：最深层旧尾部从锚点起；上层从路径
            // 子节点之后起，路径壳被空弹移除时则把它纳入旧尾部承接身份。
            for (var f = 0; f < depth; f++)
            {
                if (removedShells.Contains(shells[f])) continue;

                int oldStart;
                if (f == depth - 1)
                {
                    oldStart = anchorIndexInSection;
                }
                else
                {
                    oldStart = childIndex[f] + (removedShells.Contains(shells[f + 1]) ? 0 : 1);
                }

                var oldChildren = chain[f].Children;
                if (oldStart >= oldChildren.Count || extras[f].Count == 0) continue;

                var oldTail = oldChildren.GetRange(oldStart, oldChildren.Count - oldStart);
                _identityMatcher.MatchSiblingLists(oldTail, extras[f]);
            }

            // 结构前提成立：探测块局部坐标平移到全文坐标。
            foreach (var block in probeBlocks)
            {
                ShiftBlockRange(block, probeStart, lineDelta);
            }

            if (pendingParentLinks.Count > 0)
            {
                document.PendingParentLinks = pendingParentLinks;
            }

            root = document;
            return true;
        }
        catch
        {
            // 交回调用方走拍平全建路径。
            root = null!;
            return false;
        }
    }

    /// <summary>
    /// 中间编辑（插入/删除/替换）的增量解析：前缀块复用，改动区间顶层块重解析，
    /// 后缀块复用或深克隆平移 Range；两侧做密封探测，不密封则扩区（最多各 8 块）。
    /// 不确定场景返回 false 走全量。
    /// </summary>
    private bool TryParseModifiedRegion(
        string source,
        string? previousSource,
        MarkdownNode previousRoot,
        ICollection<DiagnosticMessage> diagnostics,
        out MarkdownNode root)
    {
        root = null!;

        if (string.IsNullOrEmpty(previousSource) || ReferenceEquals(source, previousSource))
        {
            return false;
        }

        var oldLen = previousSource.Length;
        var newLen = source.Length;

        var p = 0;
        var maxPrefix = Math.Min(oldLen, newLen);
        while (p < maxPrefix && previousSource[p] == source[p])
        {
            p++;
        }

        var cs = 0;
        while (cs < oldLen - p
            && cs < newLen - p
            && previousSource[oldLen - 1 - cs] == source[newLen - 1 - cs])
        {
            cs++;
        }

        // 纯追加由 TryParseAppendedSuffix 负责；前后缀都为空（整文档替换）不值得增量。
        if (p == oldLen || (p == 0 && cs == 0))
        {
            return false;
        }

        var oldBlocks = FlattenConcreteBlocks(previousRoot);
        if (oldBlocks.Count == 0)
        {
            return false;
        }

        // 改动区间（旧坐标 [a,bOld)，新坐标 [a,bNew)），先按行边界对齐。
        var a = GetLineStart(source, Math.Min(p, newLen));

        // 公共后缀必须锁步推进到同一个行边界，否则两侧各自对齐会落到不同行，
        // bNew-bOld 不再等于整体 delta。
        var bOld = oldLen - cs;
        var bNew = newLen - cs;
        while (true)
        {
            var oldAtLineStart = bOld == 0 || previousSource[bOld - 1] == '\n';
            var newAtLineStart = bNew == 0 || source[bNew - 1] == '\n';
            if (oldAtLineStart && newAtLineStart) break;
            if (bOld >= oldLen || bNew >= newLen)
            {
                // 公共后缀内找不到共同行边界，保守全量。
                return false;
            }
            bOld++;
            bNew++;
        }

        // 边界停在空行行首时吞掉所有连续空行：容器 span 与未闭合围栏会吞并
        // 尾部换行，空行必须放进中间重解析区。
        bOld = ConsumeBlankLines(previousSource, bOld);
        bNew = ConsumeBlankLines(source, bNew);
        var delta = bNew - bOld;

        var i0 = -1;
        var i1 = -1;
        for (var i = 0; i < oldBlocks.Count; i++)
        {
            var range = oldBlocks[i].Range;
            if (range.StartOffset < 0)
            {
                return false;
            }
            if (range.StartOffset < bOld && range.EndOffset > a)
            {
                if (i0 < 0) i0 = i;
                i1 = i;
            }
        }

        int leftCount;
        int suffixStart;

        if (i0 >= 0)
        {
            // 切点落在多行块内部时向左扩到整块行首；a 不能越过分歧点，
            // 否则新源码行首字符会被漏掉。
            leftCount = i0;
            suffixStart = i1 + 1;
            a = Math.Min(a, GetLineStart(previousSource, oldBlocks[i0].Range.StartOffset));
            var alignedBOld = bOld;
            var blockEnd = GetLineStartAfter(previousSource, oldBlocks[i1].Range.EndOffset);
            bOld = Math.Max(alignedBOld, blockEnd);
            bOld = ConsumeBlankLines(previousSource, bOld);
            bNew = bOld + delta;
        }
        else
        {
            // 编辑完全落在块间空行缝隙：中间块数为 0，前缀/后缀直接拼接。
            leftCount = 0;
            foreach (var block in oldBlocks)
            {
                if (block.Range.EndOffset <= a) leftCount++;
            }
            suffixStart = leftCount;
        }

        for (var i = 0; i < leftCount; i++)
        {
            if (oldBlocks[i].Range.EndOffset > a) return false;
        }
        for (var i = suffixStart; i < oldBlocks.Count; i++)
        {
            if (oldBlocks[i].Range.StartOffset < bOld) return false;
        }

        // 未闭合围栏一路吃到 EOF，其后任何编辑都可能改变跨度，直接全量。
        foreach (var block in oldBlocks)
        {
            if ((block.Type == NodeType.CodeBlock || block.Type == NodeType.ImageMatrix
                    || block.Type == NodeType.QrCode || block.Type == NodeType.PoemBlock
                    || block.Type == NodeType.Timeline)
                && block.Attributes.ContainsKey(MarkdownAttributes.Unclosed))
            {
                return false;
            }
        }

        const int maxExpand = 8;
        List<MarkdownNode>? middleBlocks = null;

        for (var attempt = 0; attempt <= maxExpand; attempt++)
        {
            if (a < 0 || bNew < a || bNew > newLen || bOld > oldLen)
            {
                return false;
            }

            // 后缀锚点必须全部位于边界之后；扩区后该不变量被破坏则全量。
            if (bOld >= oldLen && suffixStart < oldBlocks.Count)
            {
                return false;
            }
            for (var i = suffixStart; i < oldBlocks.Count; i++)
            {
                if (oldBlocks[i].Range.StartOffset < bOld) return false;
            }

            middleBlocks = TryConvertSlice(source.Substring(a, bNew - a));
            if (middleBlocks is null)
            {
                return false;
            }

            // 中间切片未闭合且其后还有正文时，闭合行可能在后缀探测窗口之外，全量。
            if (bNew < newLen && ContainsUnclosedFence(middleBlocks))
            {
                return false;
            }

            var currentSuffixOffsetDelta = bNew - bOld;
            var currentSuffixLineDelta = CountLineBreaks(source.AsSpan(0, bNew))
                - CountLineBreaks(previousSource.AsSpan(0, bOld));
            var middleStartLine = CountLineBreaks(source.AsSpan(0, a)) + 1;

            // 左密封：探测切片从锚点前一块开始覆盖中间区（中间 0 块时带上
            // 右侧首块），发现块吞并、内部结构变化或 span 伸缩。
            var leftSealed = true;
            if (a > 0 && leftCount > 0)
            {
                var expected = new List<ExpectedBlock>();
                var firstExpected = leftCount - 1;
                if (leftCount >= 2) firstExpected = leftCount - 2;
                for (var i = firstExpected; i < leftCount; i++)
                {
                    var n = oldBlocks[i];
                    expected.Add(new ExpectedBlock(n, n.Range.StartOffset, n.Range.StartLine, n.Range.EndLine));
                }

                var probeStart = GetLineStart(previousSource, oldBlocks[firstExpected].Range.StartOffset);
                var probeEnd = bNew;
                if (middleBlocks.Count == 0 && suffixStart < oldBlocks.Count)
                {
                    probeEnd = GetLineStartAfter(
                        source, oldBlocks[suffixStart].Range.EndOffset + delta);
                    expected.Add(new ExpectedBlock(
                        oldBlocks[suffixStart],
                        oldBlocks[suffixStart].Range.StartOffset + currentSuffixOffsetDelta,
                        oldBlocks[suffixStart].Range.StartLine + currentSuffixLineDelta,
                        oldBlocks[suffixStart].Range.EndLine + currentSuffixLineDelta));
                }

                var probe = TryConvertSlice(source.Substring(probeStart, probeEnd - probeStart));
                if (probe is null)
                {
                    leftSealed = false;
                }
                else
                {
                    var probeStartLine = CountLineBreaks(source.AsSpan(0, probeStart)) + 1;
                    leftSealed = BlocksPrefixEqual(probe, expected, probeStart, probeStartLine);

                    // 探测切片带外侧上下文重解析了整个中间区：中间块还须与
                    // 独立切片逐块一致，防止末块 span 吞/漏尾换行。
                    if (leftSealed && middleBlocks.Count > 0)
                    {
                        leftSealed = MiddleMatchesProbe(
                            probe, middleBlocks, expected.Count,
                            probe.Count - expected.Count,
                            probeStart, probeStartLine, a, middleStartLine);
                    }
                }
            }

            // 右密封对称：探测切片延伸到锚点后一块；中间 0 块时起点回退到左侧末块。
            var rightSealed = true;
            if (bOld < oldLen && suffixStart < oldBlocks.Count)
            {
                var expected = new List<ExpectedBlock>();
                var anchorEndOld = GetLineStartAfter(previousSource, oldBlocks[suffixStart].Range.EndOffset);
                var anchorEndNew = anchorEndOld + delta;
                var probeStart = a;
                var probeEnd = anchorEndNew;

                if (middleBlocks.Count == 0 && leftCount > 0)
                {
                    probeStart = GetLineStart(source, oldBlocks[leftCount - 1].Range.StartOffset);
                    var pre = oldBlocks[leftCount - 1];
                    expected.Add(new ExpectedBlock(pre, pre.Range.StartOffset, pre.Range.StartLine, pre.Range.EndLine));
                }

                var suffixEntryCount = 0;
                for (var i = suffixStart; i <= Math.Min(suffixStart + 1, oldBlocks.Count - 1); i++)
                {
                    var n = oldBlocks[i];
                    expected.Add(new ExpectedBlock(
                        n,
                        n.Range.StartOffset + currentSuffixOffsetDelta,
                        n.Range.StartLine + currentSuffixLineDelta,
                        n.Range.EndLine + currentSuffixLineDelta));
                    suffixEntryCount++;
                    if (i == suffixStart + 1)
                    {
                        probeEnd = GetLineStartAfter(source, n.Range.EndOffset + delta);
                    }
                }

                var probe = TryConvertSlice(source.Substring(probeStart, probeEnd - probeStart));
                if (probe is null)
                {
                    rightSealed = false;
                }
                else
                {
                    var probeStartLine = CountLineBreaks(source.AsSpan(0, probeStart)) + 1;
                    rightSealed = BlocksSuffixEqual(probe, expected, probeStart, probeStartLine);

                    if (rightSealed && middleBlocks.Count > 0)
                    {
                        rightSealed = MiddleMatchesProbe(
                            probe, middleBlocks, 0,
                            probe.Count - suffixEntryCount,
                            probeStart, probeStartLine, a, middleStartLine);
                    }
                }
            }

            if (leftSealed && rightSealed)
            {
                break;
            }

            // 中间 0 块时须联合扩区，否则同一块会既在中间体里又留在后缀里。
            var expandLeft = false;
            var expandRight = false;
            if (!leftSealed)
            {
                if (leftCount == 0) return false;
                expandLeft = true;
                if (middleBlocks.Count == 0 && suffixStart < oldBlocks.Count)
                {
                    expandRight = true;
                }
            }
            else if (!rightSealed)
            {
                if (suffixStart >= oldBlocks.Count) return false;
                expandRight = true;
                if (middleBlocks.Count == 0 && leftCount > 0)
                {
                    expandLeft = true;
                }
            }

            if (expandLeft)
            {
                leftCount--;
                a = GetLineStart(previousSource, oldBlocks[leftCount].Range.StartOffset);
            }
            if (expandRight)
            {
                bOld = GetLineStartAfter(previousSource, oldBlocks[suffixStart].Range.EndOffset);
                bOld = ConsumeBlankLines(previousSource, bOld);
                bNew = bOld + delta;
                suffixStart++;
            }
            if (attempt == maxExpand)
            {
                return false;
            }
        }

        if (middleBlocks is null)
        {
            return false;
        }

        var middleText = source.Substring(a, bNew - a);

        // 引用定义是文档级解析：改动区内外交叉出现定义与 [ 用法时放弃增量。
        var outsidePrefix = source.AsSpan(0, a);
        var outsideSuffix = source.AsSpan(bNew);
        var defInMiddle = ReferenceDefinitionLineRegex.IsMatch(middleText);
        var defOutside = ReferenceDefinitionLineRegex.IsMatch(outsidePrefix)
            || ReferenceDefinitionLineRegex.IsMatch(outsideSuffix);
        var bracketInMiddle = middleText.IndexOf('[') >= 0;
        var bracketOutside = outsidePrefix.Contains('[') || outsideSuffix.Contains('[');
        if ((defInMiddle && bracketOutside) || (defOutside && bracketInMiddle))
        {
            return false;
        }

        var merged = new List<MarkdownNode>(oldBlocks.Count + middleBlocks.Count + 1);

        for (var i = 0; i < leftCount; i++)
        {
            merged.Add(oldBlocks[i]);
        }

        var middleLineDelta = CountLineBreaks(source.AsSpan(0, a));
        foreach (var block in middleBlocks)
        {
            ShiftBlockRange(block, a, middleLineDelta);
            merged.Add(block);
        }

        // 后缀偏移/行号增量均为 0（等长等行数替换）时直接复用旧对象；
        // 否则单趟完成深克隆 + 坐标平移（旧树只读，不能原地改坐标）。
        var suffixOffsetDelta = bNew - bOld;
        var suffixLineDelta = CountLineBreaks(source.AsSpan(0, bNew))
            - CountLineBreaks(previousSource.AsSpan(0, bOld));
        var suffixUnchanged = suffixOffsetDelta == 0 && suffixLineDelta == 0;
        for (var i = suffixStart; i < oldBlocks.Count; i++)
        {
            merged.Add(suffixUnchanged
                ? oldBlocks[i]
                : CloneAndShiftBlock(oldBlocks[i], suffixOffsetDelta, suffixLineDelta));
        }

        // 前缀块与等长后缀块都是旧树共享对象，Parent 延迟到提交时切换。
        var priorTreeNodes = new HashSet<MarkdownNode>();
        for (var i = 0; i < leftCount; i++)
        {
            priorTreeNodes.Add(oldBlocks[i]);
        }

        if (suffixUnchanged)
        {
            for (var i = suffixStart; i < oldBlocks.Count; i++)
            {
                priorTreeNodes.Add(oldBlocks[i]);
            }
        }

        root = _sectionBuilder.Build(merged, priorTreeNodes);
        _identityMatcher.Match(previousRoot, root);
        return true;
    }

    private List<MarkdownNode>? TryConvertSlice(string text)
    {
        try
        {
            var markdigDoc = Markdig.Markdown.Parse(text, _pipeline);
            return _converter.ConvertBlocks(markdigDoc, new List<DiagnosticMessage>(), text);
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct ExpectedBlock(
        MarkdownNode Node, int NewStart, int NewStartLine, int NewEndLine);

    // 探测切片中的“中间部分”必须与独立中间切片逐块结构/全局坐标一致。
    private static bool MiddleMatchesProbe(
        IReadOnlyList<MarkdownNode> probe,
        IReadOnlyList<MarkdownNode> middle,
        int probeIndex,
        int count,
        int probeStart,
        int probeStartLine,
        int middleStart,
        int middleStartLine)
    {
        if (count != middle.Count || probeIndex + count > probe.Count) return false;

        for (var i = 0; i < count; i++)
        {
            var m = middle[i];
            var expected = new ExpectedBlock(
                m,
                middleStart + m.Range.StartOffset,
                middleStartLine + m.Range.StartLine - 1,
                middleStartLine + m.Range.EndLine - 1);
            if (!BlockSubtreeEqual(expected, probe[probeIndex + i], probeStart, probeStartLine))
            {
                return false;
            }
        }

        return true;
    }

    // 探测切片的开头是否依次出现期望的旧块（结构与相对坐标完全一致，忽略 Id/样式）。
    private static bool BlocksPrefixEqual(
        IReadOnlyList<MarkdownNode> probe,
        IReadOnlyList<ExpectedBlock> expected,
        int sliceStart,
        int sliceStartLine)
    {
        if (probe.Count < expected.Count) return false;
        for (var i = 0; i < expected.Count; i++)
        {
            if (!BlockSubtreeEqual(expected[i], probe[i], sliceStart, sliceStartLine)) return false;
        }
        return true;
    }

    // 探测切片的结尾是否依次出现期望的旧块。
    private static bool BlocksSuffixEqual(
        IReadOnlyList<MarkdownNode> probe,
        IReadOnlyList<ExpectedBlock> expected,
        int sliceStart,
        int sliceStartLine)
    {
        if (probe.Count < expected.Count) return false;
        var offset = probe.Count - expected.Count;
        for (var i = 0; i < expected.Count; i++)
        {
            if (!BlockSubtreeEqual(expected[i], probe[offset + i], sliceStart, sliceStartLine)) return false;
        }
        return true;
    }

    // 块结构等价：类型/层级/文本/Raw/属性/全部后代一致，且切片局部坐标与
    // 期望的新文档坐标一致（不含 Id/样式）。
    private static bool BlockSubtreeEqual(
        ExpectedBlock expected,
        MarkdownNode actual,
        int sliceStart,
        int sliceStartLine)
    {
        var node = expected.Node;
        var range = actual.Range;
        var rangeMatches = range.StartOffset == expected.NewStart - sliceStart
            && range.Length == node.Range.Length
            && range.StartLine == expected.NewStartLine - sliceStartLine + 1
            && range.EndLine == expected.NewEndLine - sliceStartLine + 1
            && range.StartColumn == node.Range.StartColumn
            && range.EndColumn == node.Range.EndColumn;

        if (!rangeMatches
            || node.Type != actual.Type
            || node.Category != actual.Category
            || node.Level != actual.Level
            || !string.Equals(node.Text, actual.Text, StringComparison.Ordinal)
            || !string.Equals(node.RawMarkdown, actual.RawMarkdown, StringComparison.Ordinal)
            || node.Attributes.Count != actual.Attributes.Count
            || node.Children.Count != actual.Children.Count)
        {
            return false;
        }

        foreach (var pair in node.Attributes)
        {
            if (!actual.Attributes.TryGetValue(pair.Key, out var value)
                || !string.Equals(value, pair.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        // 后代期望坐标 = 旧坐标叠加与块首相同的整体平移量，递归沿用。
        var offsetShift = expected.NewStart - node.Range.StartOffset;
        var lineShift = expected.NewStartLine - node.Range.StartLine;
        for (var i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            var expectedChild = new ExpectedBlock(
                child,
                child.Range.StartOffset + offsetShift,
                child.Range.StartLine + lineShift,
                child.Range.EndLine + lineShift);
            if (!BlockSubtreeEqual(expectedChild, actual.Children[i], sliceStart, sliceStartLine))
            {
                return false;
            }
        }

        return true;
    }

    // 递归检测块子树内是否存在未闭合围栏（含嵌套在列表/引用中的围栏）。
    private static bool ContainsUnclosedFence(IEnumerable<MarkdownNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Type == NodeType.CodeBlock
                || node.Type == NodeType.ImageMatrix
                || node.Type == NodeType.QrCode
                || node.Type == NodeType.PoemBlock
                || node.Type == NodeType.Timeline)
            {
                if (node.Attributes.ContainsKey(MarkdownAttributes.Unclosed))
                {
                    return true;
                }
            }
            if (ContainsUnclosedFence(node.Children)) return true;
        }
        return false;
    }

    // 从行首 pos 起跨过所有连续的纯空白空行，返回第一个非空行行首（或 EOF）。
    private static int ConsumeBlankLines(string text, int pos)
    {
        while (pos < text.Length && IsBlankLineAt(text, pos))
        {
            pos = SkipLine(text, pos);
        }
        return pos;
    }

    // pos 是否位于一条纯空白（空格/制表符）空行的行首（含文档末尾）。
    private static bool IsBlankLineAt(string text, int pos)
    {
        if (pos >= text.Length) return true;
        var p = pos;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) p++;
        return p >= text.Length || text[p] == '\n' || text[p] == '\r';
    }

    // 返回从行首 pos 开始的下一非空行行首（跨过整行含换行符）。
    private static int SkipLine(string text, int pos)
    {
        var p = pos;
        while (p < text.Length && text[p] != '\n') p++;
        return p >= text.Length ? text.Length : p + 1;
    }

    private static int GetLineStartAfter(string text, int offset)
    {
        var pos = Math.Clamp(offset, 0, text.Length);
        while (pos < text.Length && text[pos] != '\n')
        {
            pos++;
        }
        return pos >= text.Length ? text.Length : pos + 1;
    }

    // 深克隆块子树并在同一趟递归里平移坐标，规则与 ShiftBlockRange 一致：
    // 默认空 Range 不平移；子节点“起点在父块之前”的伪坐标按轴跳过。
    private static MarkdownNode CloneAndShiftBlock(MarkdownNode node, int offsetDelta, int lineDelta)
    {
        var range = node.Range;
        SourceRange shifted = range;
        var hasRange = range.StartOffset != 0 || range.Length != 0 || range.StartLine > 1;
        if (range.StartOffset >= 0 && hasRange)
        {
            shifted = new SourceRange
            {
                StartOffset = range.StartOffset + offsetDelta,
                Length = range.Length,
                StartLine = range.StartLine + lineDelta,
                StartColumn = range.StartColumn,
                EndLine = range.EndLine + lineDelta,
                EndColumn = range.EndColumn
            };
        }

        var clone = new MarkdownNode(node.Id, node.LocalStyle)
        {
            Type = node.Type,
            Category = node.Category,
            Text = node.Text,
            RawMarkdown = node.RawMarkdown,
            Level = node.Level,
            Range = shifted,
            StyleId = node.StyleId,
            IsTableHeader = node.IsTableHeader,
            IsSynthetic = node.IsSynthetic
        };

        foreach (var pair in node.Attributes)
        {
            clone.Attributes[pair.Key] = pair.Value;
        }

        // 手动挂接：AddChild 会把孩子从旧树摘除。包含判定用原始坐标。
        foreach (var child in node.Children)
        {
            var cr = child.Range;
            var childOffsetDelta = cr.StartOffset < range.StartOffset ? 0 : offsetDelta;
            var childLineDelta = cr.StartLine < range.StartLine ? 0 : lineDelta;
            var childClone = CloneAndShiftBlock(child, childOffsetDelta, childLineDelta);
            childClone.Parent = clone;
            clone.Children.Add(childClone);
        }

        return clone;
    }

    private static List<MarkdownNode> FlattenConcreteBlocks(MarkdownNode node)
    {
        var result = new List<MarkdownNode>();
        CollectConcreteBlocks(node, result);
        return result;
    }

    private static void CollectConcreteBlocks(MarkdownNode node, List<MarkdownNode> result)
    {
        foreach (var child in node.Children)
        {
            if (child.Type is NodeType.Document or NodeType.Section)
            {
                CollectConcreteBlocks(child, result);
            }
            else
            {
                result.Add(child);
            }
        }
    }

    private static int GetLineStart(string source, int offset)
    {
        var start = Math.Clamp(offset, 0, source.Length);
        while (start > 0 && source[start - 1] != '\n')
        {
            start--;
        }
        return start;
    }

    private static int CountLineBreaks(ReadOnlySpan<char> text)
    {
        var count = 0;
        foreach (var c in text)
        {
            if (c == '\n') count++;
        }
        return count;
    }

    private static void ShiftBlockRange(MarkdownNode node, int offsetDelta, int lineDelta)
    {
        var range = node.Range;
        // 默认空 Range（0:0/ln1，如空表格单元格）不平移，与全量解析保持一致。
        var hasRange = range.StartOffset != 0 || range.Length != 0 || range.StartLine > 1;
        if (range.StartOffset >= 0 && hasRange)
        {
            node.Range = new SourceRange
            {
                StartOffset = range.StartOffset + offsetDelta,
                Length = range.Length,
                StartLine = range.StartLine + lineDelta,
                StartColumn = range.StartColumn,
                EndLine = range.EndLine + lineDelta,
                EndColumn = range.EndColumn
            };
        }

        foreach (var child in node.Children)
        {
            // 跨行未闭合行内结构可能产生“起点在父块之前”的伪坐标（行号仍
            // 有效），偏移与行号分别判定；子节点超出只覆盖首行的父块是正常的。
            var cr = child.Range;
            var offsetStartsOutside = cr.StartOffset < range.StartOffset;
            var lineStartsOutside = cr.StartLine < range.StartLine;
            ShiftBlockRange(
                child,
                offsetStartsOutside ? 0 : offsetDelta,
                lineStartsOutside ? 0 : lineDelta);
        }
    }

    private IReadOnlyList<MarkdownNode> ParseBlocks(
        string source,
        ICollection<DiagnosticMessage> diagnostics)
    {
        Markdig.Syntax.MarkdownDocument markdigDoc;

        try
        {
            markdigDoc = Markdig.Markdown.Parse(source, _pipeline);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new DiagnosticMessage
            {
                Severity = DiagnosticSeverity.Error,
                Code = "MARKDOWN_PARSE_FAILED",
                Message = ex.Message,
                Range = SourceRange.Empty()
            });

            return Array.Empty<MarkdownNode>();
        }

        try
        {
            return _converter.ConvertBlocks(markdigDoc, diagnostics, source);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new DiagnosticMessage
            {
                Severity = DiagnosticSeverity.Error,
                Code = "MARKDOWN_CONVERSION_FAILED",
                Message = ex.Message,
                Range = SourceRange.Empty()
            });

            return Array.Empty<MarkdownNode>();
        }
    }
}
