using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Exporting.Html;
using ExportingCssStyleBuilder = CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Exporting.Html.CssStyleBuilder;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Html;

internal sealed class HtmlRenderer : IDocumentRenderer, IIncrementalPreviewRenderer
{
    private static readonly HashSet<string> AllowedUriSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "mailto", "tel"
    };

    private static readonly Regex UnsafeUriCharRegex = new(@"[\u0000-\u001F\u007F\s]", RegexOptions.Compiled);

    private static readonly char[] LanguageSeparators = { ' ', '\t' };

    private readonly IStyleResolver _styleResolver;
    private readonly ICssStyleBuilder _cssStyleBuilder;
    private readonly AdmonitionIconOptions _iconOptions;
    private readonly NodeStyle? _defaultStyle;

    public HtmlRenderer(
        IStyleResolver styleResolver,
        ICssStyleBuilder? cssStyleBuilder = null,
        AdmonitionIconOptions? iconOptions = null,
        NodeStyle? defaultStyle = null)
    {
        _styleResolver = styleResolver ?? throw new ArgumentNullException(nameof(styleResolver));
        _cssStyleBuilder = cssStyleBuilder ?? new ExportingCssStyleBuilder();
        _iconOptions = iconOptions ?? new AdmonitionIconOptions();
        _defaultStyle = defaultStyle;
    }

    public RenderResult Render(MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var ctx = RenderCore(document, assembleHtml: true);

        // 任何全量渲染都重建增量基线：补丁必须以一份完整 DOM 为起点。
        lock (_blockCacheGate)
        {
            _baseline = new IncrementalBaseline
            {
                Epoch = ctx.Epoch,
                OrderedIds = ctx.OrderedIds
            };
            _forceFullNext = false;
        }

        LastBlockCacheStats = ctx.Counters;
        return new RenderResult(ctx.Html!.ToString(), ctx.Diagnostics);
    }

    public IncrementalRenderResult RenderIncremental(MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var epoch = new CacheEpoch(document.StyleRevision, _iconOptions.Revision);

        IncrementalBaseline? baseline;
        lock (_blockCacheGate)
        {
            baseline = _baseline;
            if (_forceFullNext || baseline is null || !baseline.Epoch.Equals(epoch))
            {
                baseline = null;
            }
        }

        // 无基线 / epoch 变化（样式、图标修订）/ 显式要求：输出全量并重建基线。
        if (baseline is null)
        {
            var full = Render(document);
            LastIncrementalStats = new IncrementalCounters(0, 0, 0, 0, FullFallback: true);
            return IncrementalRenderResult.Full(full.Html, full.Diagnostics);
        }

        var ctx = RenderCore(document, assembleHtml: false);
        LastBlockCacheStats = ctx.Counters;

        // 出现无法按 id 寻址的块（空/非元素片段）或补丁性价比不足时回退全量。
        if (!ctx.SawNonCacheableBlock)
        {
            var ops = BuildPatchOps(baseline, ctx.OrderedIds, ctx.FrameBlocks, out var payloadChars);
            var fullLengthEstimate = 0L;
            foreach (var fb in ctx.FrameBlocks)
            {
                fullLengthEstimate += fb.Fragment.Length;
            }

            var tooManyOps = ops.Count > Math.Max(1, ctx.OrderedIds.Count) * PatchMaxOpRatio;
            var tooLargePayload =
                payloadChars > (long)Math.Max(16, fullLengthEstimate) * PatchMaxPayloadRatio;

            if (!tooManyOps && !tooLargePayload)
            {
                lock (_blockCacheGate)
                {
                    _baseline = new IncrementalBaseline
                    {
                        Epoch = ctx.Epoch,
                        OrderedIds = ctx.OrderedIds
                    };
                }

                var patch = new PreviewPatch(ops);
                LastIncrementalStats = new IncrementalCounters(
                    ctx.Counters.MemoHits, ctx.Counters.MemoMisses, ops.Count, payloadChars, false);
                return IncrementalRenderResult.Incremental(patch, ctx.Diagnostics);
            }
        }

        var fallback = Render(document);
        LastIncrementalStats = new IncrementalCounters(
            ctx.Counters.MemoHits, ctx.Counters.MemoMisses, 0, 0, FullFallback: true);
        return IncrementalRenderResult.Full(fallback.Html, fallback.Diagnostics);
    }

    public void ResetIncrementalBaseline()
    {
        lock (_blockCacheGate)
        {
            _baseline = null;
            _forceFullNext = true;
        }
    }

    /// <summary>最近一次 Render 的块缓存命中情况（供性能验证/基准测试读取）。</summary>
    internal BlockCounters LastBlockCacheStats { get; private set; }

    /// <summary>最近一次 RenderIncremental 的补丁统计（供基准测试读取）。</summary>
    internal IncrementalCounters LastIncrementalStats { get; private set; }

    internal readonly record struct BlockCounters(int Total, int FromCache, int MemoHits, int MemoMisses)
    {
        public double HitRate => Total == 0 ? 0 : (double)FromCache / Total;
    }

    internal readonly record struct IncrementalCounters(
        int MemoHits, int MemoMisses, int Ops, long PayloadChars, bool FullFallback);

    private const double PatchMaxOpRatio = 0.5;
    private const double PatchMaxPayloadRatio = 0.5;
    private const int EstimatedOpOverhead = 32;

    private readonly record struct CacheEpoch(long StyleRevision, int IconRevision)
    {
        public string Prefix { get; } =
            FormattableString.Invariant($"{StyleRevision}|{IconRevision}|");
    }

    private sealed record BlockEntry(
        string Signature, string Html, MarkdownNode Node, CacheEpoch Epoch);

    private sealed class IncrementalBaseline
    {
        public required CacheEpoch Epoch { get; init; }
        public required List<string> OrderedIds { get; init; }
    }

    private sealed class FrameBlock
    {
        public required string Id { get; init; }
        public required string Fragment { get; init; }
        public required string Signature { get; init; }
        public string? PreviousSignature { get; init; }
        public bool SignatureChanged =>
            PreviousSignature is null || !string.Equals(PreviousSignature, Signature, StringComparison.Ordinal);
    }

    private sealed class RenderContext
    {
        public required CacheEpoch Epoch { get; init; }
        public required List<DiagnosticMessage> Diagnostics { get; init; }
        public required bool AssembleHtml { get; init; }
        public StringBuilder? Html { get; set; }
        public HashSet<string> LiveIds { get; } = new(StringComparer.Ordinal);
        public List<string> OrderedIds { get; } = new();
        public List<FrameBlock> FrameBlocks { get; } = new();
        public BlockCounters Counters { get; set; }
        public bool SawNonCacheableBlock { get; set; }
    }

    private readonly object _blockCacheGate = new();
    private readonly Dictionary<string, BlockEntry> _blockCache = new(StringComparer.Ordinal);
    private IncrementalBaseline? _baseline;
    private bool _forceFullNext;

    private RenderContext RenderCore(MarkdownDocument document, bool assembleHtml)
    {
        var ctx = new RenderContext
        {
            Epoch = new CacheEpoch(document.StyleRevision, _iconOptions.Revision),
            Diagnostics = new List<DiagnosticMessage>(),
            AssembleHtml = assembleHtml,
            Html = assembleHtml
                ? new StringBuilder(Math.Max(1024, (document.SourceMarkdown?.Length ?? 512) * 2))
                : null
        };

        ctx.Html?.AppendLine("<article class=\"markdown-document\">");
        RenderBlockBoundaries(document.Root, ctx);
        ctx.Html?.AppendLine("</article>");

        SweepStaleBlockFragments(ctx.LiveIds);
        return ctx;
    }

    private void RenderBlockBoundaries(MarkdownNode parent, RenderContext ctx)
    {
        for (int i = 0; i < parent.Children.Count; i++)
        {
            var child = parent.Children[i];
            if (child.Type is NodeType.Document or NodeType.Section)
            {
                // 透明容器自身不产生元素，其子节点继续作为缓存边界。
                RenderBlockBoundaries(child, ctx);
            }
            else
            {
                RenderBlockBoundary(child, ctx);
            }
        }
    }

    private void RenderBlockBoundary(MarkdownNode node, RenderContext ctx)
    {
        ctx.Counters = ctx.Counters with { Total = ctx.Counters.Total + 1 };

        BlockEntry? entry;
        lock (_blockCacheGate)
        {
            _blockCache.TryGetValue(node.Id, out entry);
        }

        // 引用级签名备忘：同节点对象 + 同 epoch 时子树内容必然不变，跳过整棵
        // 子树哈希（大文档流式追加帧的主要 O(N) 成本之一）。
        string signature;
        if (entry is not null && entry.Epoch.Equals(ctx.Epoch) && ReferenceEquals(entry.Node, node))
        {
            ctx.Counters = ctx.Counters with { MemoHits = ctx.Counters.MemoHits + 1 };
            signature = entry.Signature;
        }
        else
        {
            ctx.Counters = ctx.Counters with { MemoMisses = ctx.Counters.MemoMisses + 1 };
            signature = ComputeBlockSignature(node, ctx.Epoch);
        }

        if (entry is not null && entry.Signature == signature)
        {
            if (ctx.AssembleHtml)
            {
                ctx.Html!.Append(entry.Html);
            }

            ctx.LiveIds.Add(node.Id);
            ctx.OrderedIds.Add(node.Id);
            ctx.FrameBlocks.Add(new FrameBlock
            {
                Id = node.Id,
                Fragment = entry.Html,
                Signature = signature,
                PreviousSignature = entry.Signature
            });
            ctx.Counters = ctx.Counters with { FromCache = ctx.Counters.FromCache + 1 };
            return;
        }

        var rawLength = node.RawMarkdown?.Length ?? 0;
        var blockBuffer = new StringBuilder(Math.Max(64, rawLength * 2));
        var blockDiagnostics = new List<DiagnosticMessage>();
        RenderNode(node, blockBuffer, blockDiagnostics);
        ctx.Diagnostics.AddRange(blockDiagnostics);

        var rawFragment = blockBuffer.ToString();
        var fragmentHtml = InjectBlockSignature(rawFragment, signature);

        lock (_blockCacheGate)
        {
            if (fragmentHtml is null)
            {
                _blockCache.Remove(node.Id);
            }
            else
            {
                _blockCache[node.Id] = new BlockEntry(signature, fragmentHtml, node, ctx.Epoch);
            }
        }

        if (fragmentHtml is null)
        {
            // 空片段或非元素开头：无法按 id 寻址，标记后由 RenderIncremental 回退全量。
            if (ctx.AssembleHtml)
            {
                ctx.Html!.Append(rawFragment);
            }

            ctx.LiveIds.Add(node.Id);
            ctx.SawNonCacheableBlock = true;
            return;
        }

        if (ctx.AssembleHtml)
        {
            ctx.Html!.Append(fragmentHtml);
        }

        ctx.LiveIds.Add(node.Id);
        ctx.OrderedIds.Add(node.Id);
        ctx.FrameBlocks.Add(new FrameBlock
        {
            Id = node.Id,
            Fragment = fragmentHtml,
            Signature = signature,
            PreviousSignature = entry?.Signature
        });
    }

    // 比较上一帧与本帧的有序块序列，生成最小块级操作：先删除消失块，
    // 再按文档顺序 upsert（新增/签名变化）或 move（仅幸存块相对顺序改变）。
    // 被删块不留空位、新插入块按 after 锚点自然挤位，都不产生 move。
    private static List<PreviewPatchOp> BuildPatchOps(
        IncrementalBaseline baseline,
        List<string> orderedIds,
        List<FrameBlock> blocks,
        out long payloadChars)
    {
        var prevIds = baseline.OrderedIds;
        var prevIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < prevIds.Count; i++)
        {
            prevIndex[prevIds[i]] = i;
        }

        var currentIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < orderedIds.Count; i++)
        {
            currentIndex[orderedIds[i]] = i;
        }

        // 旧序列中每个幸存块的前一个幸存块（跳过本帧将被删除的块）。
        var oldSurvivorPredecessor = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? lastOldSurvivor = null;
        foreach (var id in prevIds)
        {
            if (currentIndex.ContainsKey(id))
            {
                oldSurvivorPredecessor[id] = lastOldSurvivor;
                lastOldSurvivor = id;
            }
        }

        var ops = new List<PreviewPatchOp>();
        payloadChars = 0;

        foreach (var id in prevIds)
        {
            if (!currentIndex.ContainsKey(id))
            {
                ops.Add(new PreviewPatchOp(id, PreviewPatchOpKind.Remove));
                payloadChars += EstimatedOpOverhead;
            }
        }

        string? lastCurrentSurvivor = null;
        for (var i = 0; i < blocks.Count; i++)
        {
            var fb = blocks[i];
            var afterId = i > 0 ? orderedIds[i - 1] : null;
            var existedBefore = prevIndex.ContainsKey(fb.Id);

            if (!existedBefore || fb.SignatureChanged)
            {
                ops.Add(new PreviewPatchOp(fb.Id, PreviewPatchOpKind.Upsert, afterId, fb.Fragment));
                payloadChars += EstimatedOpOverhead + fb.Fragment.Length;
                if (existedBefore)
                {
                    lastCurrentSurvivor = fb.Id;
                }

                continue;
            }

            // 幸存且未变：相对其它幸存块的前驱是否改变。
            var oldPredecessor = oldSurvivorPredecessor.GetValueOrDefault(fb.Id);
            if (!string.Equals(oldPredecessor, lastCurrentSurvivor, StringComparison.Ordinal))
            {
                ops.Add(new PreviewPatchOp(fb.Id, PreviewPatchOpKind.Move, afterId));
                payloadChars += EstimatedOpOverhead;
            }

            lastCurrentSurvivor = fb.Id;
        }

        return ops;
    }

    private void SweepStaleBlockFragments(HashSet<string> liveBlockIds)
    {
        List<string>? stale = null;

        lock (_blockCacheGate)
        {
            foreach (var key in _blockCache.Keys)
            {
                if (!liveBlockIds.Contains(key))
                {
                    (stale ??= new List<string>()).Add(key);
                }
            }

            if (stale is not null)
            {
                foreach (var key in stale)
                {
                    _blockCache.Remove(key);
                }
            }
        }
    }

    /// <summary>
    /// 块签名 = 样式/图标版本 + 整块子树（类型/层级/属性/Text/RawMarkdown，递归子孙）
    /// 的 FNV-1a 64 位哈希。任何可见输入变化都会改签名。
    /// </summary>
    private static string ComputeBlockSignature(MarkdownNode node, in CacheEpoch epoch)
    {
        var hash = HashString(FnvOffsetBasis, epoch.Prefix);
        hash = FoldNodeSignature(hash, node);
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    // 递归折叠整棵子树：未闭合围栏等块节点自身 RawMarkdown 只有开栏行
    // （转换器伪 Range），实际内容在子节点上，不递归会命中陈旧片段。
    private static ulong FoldNodeSignature(ulong hash, MarkdownNode node)
    {
        hash = Mix(hash, (ulong)(int)node.Type);
        hash = Mix(hash, (ulong)(node.Level ?? 0));
        hash = Mix(hash, (ulong)(node.IsSynthetic ? 1 : 0));
        hash = HashString(hash, node.Text);
        hash = HashString(hash, node.RawMarkdown);

        if (node.Attributes.Count > 0)
        {
            var sorted = new List<KeyValuePair<string, string>>(node.Attributes);
            sorted.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));

            foreach (var pair in sorted)
            {
                hash = HashString(hash, pair.Key);
                hash = Mix(hash, '=');
                hash = HashString(hash, pair.Value);
                hash = Mix(hash, ';');
            }
        }

        foreach (var child in node.Children)
        {
            hash = FoldNodeSignature(hash, child);
        }

        return hash;
    }

    private static string? InjectBlockSignature(string fragment, string signature)
    {
        if (fragment.Length == 0 || fragment[0] != '<')
        {
            return null;
        }

        var tagEnd = fragment.IndexOf('>');
        if (tagEnd <= 1)
        {
            return null;
        }

        return string.Concat(
            fragment.AsSpan(0, tagEnd),
            $" data-block-sig=\"{signature}\"",
            fragment.AsSpan(tagEnd));
    }

    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private static ulong Mix(ulong hash, ulong value)
    {
        hash ^= value;
        hash *= FnvPrime;
        return hash;
    }

    private static ulong HashString(ulong hash, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return hash;
        }

        foreach (var c in value)
        {
            hash ^= (ulong)c & 0xFFUL;
            hash *= FnvPrime;
            hash ^= ((ulong)c >> 8) & 0xFFUL;
            hash *= FnvPrime;
        }

        return hash;
    }


    private void RenderNode(MarkdownNode node, StringBuilder html, List<DiagnosticMessage> diagnostics)
    {
        try
        {
            switch (node.Type)
            {
                case NodeType.Document:
                case NodeType.Section:
                    RenderChildren(node, html, diagnostics);
                    break;
                case NodeType.Heading:
                    var level = Math.Clamp(node.Level ?? 1, 1, 6);
                    RenderBlock(node, $"h{level}", html, diagnostics);
                    break;
                case NodeType.Paragraph:
                    RenderBlock(node, "p", html, diagnostics);
                    break;
                case NodeType.UnorderedList:
                    RenderBlock(node, "ul", html, diagnostics);
                    break;
                case NodeType.OrderedList:
                    RenderBlock(node, "ol", html, diagnostics);
                    break;
                case NodeType.ListItem:
                    RenderBlock(node, "li", html, diagnostics);
                    break;
                case NodeType.TaskListItem:
                    {
                        var isChecked = node.Attributes.TryGetValue("checked", out var checkedValue) &&
                                        string.Equals(checkedValue, "true", StringComparison.OrdinalIgnoreCase);

                        html.Append("<li class=\"task-list-item\"");
                        AppendNodeAttributes(node, html);
                        AppendStyle(node, html);
                        html.Append("><input type=\"checkbox\" disabled");
                        if (isChecked) html.Append(" checked");
                        html.Append("> ");

                        RenderTightContainerChildren(node, html, diagnostics);
                        html.Append("</li>");
                        break;
                    }
                case NodeType.FootnoteGroup:
                    html.Append("<section class=\"footnotes\">");
                    RenderChildren(node, html, diagnostics);
                    html.Append("</section>");
                    break;
                case NodeType.Footnote:
                    {
                        var slug = GetAttribute(node, "slug") ?? node.Text;
                        var label = GetAttribute(node, "label") ?? slug;
                        var backrefs = GetAttribute(node, "backrefs") ?? ("fnref-" + slug);

                        html.Append("<div class=\"footnote-item\" id=\"fn-").Append(slug).Append('"');
                        AppendNodeAttributes(node, html);
                        AppendStyle(node, html);
                        html.Append("><span class=\"footnote-index\">");
                        AppendEncoded(html, label);
                        html.Append("</span><div class=\"footnote-content\">");

                        RenderTightContainerChildren(node, html, diagnostics);

                        var anchors = backrefs.Split(',');
                        var backRefCount = 0;
                        for (var anchorIndex = 0; anchorIndex < anchors.Length; anchorIndex++)
                        {
                            var anchor = anchors[anchorIndex].Trim();
                            if (anchor.Length == 0)
                            {
                                continue;
                            }

                            backRefCount++;
                            html.Append(" <a class=\"footnote-backref\" href=\"#").Append(anchor)
                                .Append("\" data-footnote-target=\"").Append(anchor)
                                .Append("\" title=\"返回第").Append(backRefCount)
                                .Append("处引用\" aria-label=\"返回第").Append(backRefCount)
                                .Append("处正文引用\"><span class=\"footnote-backref-icon\">↩</span></a>");
                        }

                        html.Append("</div></div>");
                        break;
                    }
                case NodeType.FootnoteLink:
                    {
                        var slug = GetAttribute(node, "slug") ?? node.Text;
                        var label = GetAttribute(node, "label") ?? slug;
                        var anchor = GetAttribute(node, "anchor") ?? ("fnref-" + slug);

                        html.Append("<sup class=\"footnote-ref\"><a id=\"").Append(anchor)
                            .Append("\" href=\"#fn-").Append(slug)
                            .Append("\" data-footnote-target=\"fn-").Append(slug).Append('"');
                        AppendStyle(node, html);
                        html.Append(">");
                        AppendEncoded(html, label);
                        html.Append("</a></sup>");
                        break;
                    }
                case NodeType.Quote:
                    if (node.Attributes.TryGetValue("admonition", out var admonitionKind))
                    {
                        RenderAdmonition(node, admonitionKind, html, diagnostics);
                    }
                    else
                    {
                        RenderBlock(node, "blockquote", html, diagnostics);
                    }
                    break;
                case NodeType.CodeBlock:
                    RenderCodeBlock(node, html);
                    break;
                case NodeType.MathInline:
                    RenderMath(node, "span", html);
                    break;
                case NodeType.MathBlock:
                    RenderMath(node, "div", html);
                    break;
                case NodeType.Mermaid:
                    RenderMermaid(node, html);
                    return;

                case NodeType.ImageMatrix:
                    RenderImageMatrix(node, html);
                    return;

                case NodeType.QrCode:
                    RenderQrCode(node, html);
                    return;

                case NodeType.PoemBlock:
                    RenderPoemBlock(node, html);
                    return;

                case NodeType.Timeline:
                    RenderTimeline(node, html, diagnostics);
                    return;

                case NodeType.TimelineItem:
                    // 时间线节点只能经 RenderTimeline 统一布局输出，不允许独立成帧。
                    return;

                case NodeType.Custom:
                    RenderCustomBlock(node, html, diagnostics);
                    break;
                case NodeType.ThematicBreak:
                    html.Append("<hr");
                    AppendNodeAttributes(node, html);
                    AppendStyle(node, html);
                    html.Append(" />");
                    break;
                case NodeType.Table:
                    RenderTable(node, html, diagnostics);
                    break;
                case NodeType.TableRow:
                    RenderTableRow(node, html, diagnostics);
                    break;
                case NodeType.TableCell:
                    RenderTableCell(node, html, diagnostics);
                    break;
                case NodeType.Text:
                    RenderText(node, html);
                    break;
                case NodeType.LineBreak:
                    html.Append("<br />");
                    break;
                case NodeType.Strong:
                    RenderInline(node, "strong", html, diagnostics);
                    break;
                case NodeType.Emphasis:
                    RenderInline(node, "em", html, diagnostics);
                    break;
                case NodeType.Delete:
                    RenderInline(node, "del", html, diagnostics);
                    break;
                // EmphasisExtras：下标/上标/插入/标记
                case NodeType.Subscript:
                    RenderInline(node, "sub", html, diagnostics);
                    break;
                case NodeType.Superscript:
                    RenderInline(node, "sup", html, diagnostics);
                    break;
                case NodeType.Inserted:
                    RenderInline(node, "ins", html, diagnostics);
                    break;
                case NodeType.Marked:
                    RenderInline(node, "mark", html, diagnostics);
                    break;
                case NodeType.InlineCode:
                    html.Append("<code");
                    AppendNodeAttributes(node, html);
                    AppendStyle(node, html);
                    html.Append('>');
                    html.Append(WebUtility.HtmlEncode(node.Text));
                    html.Append("</code>");
                    break;
                case NodeType.Link:
                    RenderLink(node, html, diagnostics);
                    break;
                case NodeType.Image:
                    RenderImage(node, html, diagnostics);
                    break;
                case NodeType.HtmlBlock:
                case NodeType.HtmlInline:
                    html.Append(WebUtility.HtmlEncode(node.Text));
                    break;
                default:
                    RenderChildren(node, html, diagnostics);
                    break;
            }
        }
        catch (StyleResolutionException ex)
        {
            diagnostics.Add(new DiagnosticMessage
            {
                Severity = DiagnosticSeverity.Error,
                Code = "STYLE_CYCLE",
                Message = ex.Message,
                Range = node.Range
            });
            RenderChildren(node, html, diagnostics);
        }
    }

    private void RenderTableCell(MarkdownNode node, StringBuilder html, List<DiagnosticMessage> diagnostics)
    {
        var tag = node.IsTableHeader ? "th" : "td";

        html.Append('<').Append(tag);
        AppendNodeAttributes(node, html);
        AppendStyle(node, html);
        html.Append('>');

        RenderTightContainerChildren(node, html, diagnostics);

        html.Append("</").Append(tag).Append('>');
    }

    private void RenderTable(MarkdownNode node, StringBuilder html, List<DiagnosticMessage> diagnostics)
    {
        html.Append("<table");
        AppendNodeAttributes(node, html);
        AppendStyle(node, html);
        html.Append('>');
        RenderChildren(node, html, diagnostics);
        html.Append("</table>");
    }

    private void RenderTableRow(MarkdownNode node, StringBuilder html, List<DiagnosticMessage> diagnostics)
    {
        html.Append("<tr");
        AppendNodeAttributes(node, html);
        // tr 的背景会被 th/td 自带的表头底/斑马纹遮盖：改写为 --row-bg
        // 变量，由预览 CSS 在单元格上消费。
        AppendStyle(node, html, BuildTableRowCascadeVars(node));
        html.Append('>');
        RenderChildren(node, html, diagnostics);
        html.Append("</tr>");
    }

    private string? BuildTableRowCascadeVars(MarkdownNode node)
    {
        var resolved = _styleResolver.Resolve(node);

        var bg = resolved.BackgroundColor?.Trim();
        return string.IsNullOrWhiteSpace(bg) ? null : "--row-bg: " + bg + ';';
    }

    private void RenderBlock(MarkdownNode node, string tag, StringBuilder html, List<DiagnosticMessage> diagnostics)
    {
        html.Append('<').Append(tag);
        AppendNodeAttributes(node, html);
        AppendStyle(node, html);
        html.Append('>');
        RenderChildren(node, html, diagnostics);
        html.Append("</").Append(tag).Append('>');
    }

    private void RenderAdmonition(
        MarkdownNode node,
        string kind,
        StringBuilder html,
        List<DiagnosticMessage> diagnostics)
    {
        var safeKind = AdmonitionCatalog.AdmonitionKinds.Contains(kind)
            ? kind
            : AdmonitionCatalog.DefaultAdmonitionKind;

        var title =
            node.Attributes.TryGetValue("admonition-title", out var titleValue)
            && !string.IsNullOrWhiteSpace(titleValue)
                ? titleValue.Trim()
                : AdmonitionCatalog.GetDefaultTitle(safeKind);

        var icon = TryBuildIconGlyph(node.Attributes.GetValueOrDefault(MarkdownAttributes.Icon))
            ?? _iconOptions.GetAdmonitionIcon(safeKind);

        html.Append("<blockquote class=\"admonition ")
            .Append(safeKind)
            .Append('"');
        AppendNodeAttributes(node, html);
        AppendStyle(node, html, ResolveSemanticAccentCss(node, "--adm-accent"));
        html.Append("><p class=\"admonition-title\">")
            .Append("<span class=\"admonition-icon\"><span class=\"md-icon\" aria-hidden=\"true\">")
            .Append(icon)
            .Append("</span></span>")
            .Append("<span class=\"admonition-title-text\">")
            .Append(WebUtility.HtmlEncode(title))
            .Append("</span></p>");

        RenderChildren(node, html, diagnostics);
        html.Append("</blockquote>");
    }

    private void RenderCustomBlock(
        MarkdownNode node,
        StringBuilder html,
        List<DiagnosticMessage> diagnostics)
    {
        var kind =
            node.Attributes.TryGetValue("custom-kind", out var kindValue)
            && AdmonitionCatalog.CustomBlockKinds.Contains(kindValue)
                ? kindValue
                : AdmonitionCatalog.DefaultCustomBlockKind;

        var icon = TryBuildIconGlyph(node.Attributes.GetValueOrDefault(MarkdownAttributes.Icon))
            ?? _iconOptions.GetCustomBlockIcon(kind);

        html.Append("<div class=\"custom-block custom-")
            .Append(kind)
            .Append('"');
        AppendNodeAttributes(node, html);
        AppendStyle(node, html, ResolveSemanticAccentCss(node, "--cb-accent"));
        html.Append("><span class=\"custom-block-icon\"><span class=\"md-icon\" aria-hidden=\"true\">")
            .Append(icon)
            .Append("</span></span><div class=\"custom-block-body\">");

        RenderChildren(node, html, diagnostics);

        html.Append("</div></div>");
    }

    private void RenderInline(MarkdownNode node, string tag, StringBuilder html, List<DiagnosticMessage> diagnostics)
    {
        html.Append('<').Append(tag);
        AppendNodeAttributes(node, html);
        AppendStyle(node, html);
        html.Append('>');
        if (node.Children.Count == 0)
        {
            html.Append(WebUtility.HtmlEncode(node.Text));
        }
        else
        {
            RenderChildren(node, html, diagnostics);
        }
        html.Append("</").Append(tag).Append('>');
    }

    private void RenderCodeBlock(MarkdownNode node, StringBuilder html)
    {
        if (node.Attributes.TryGetValue("language", out var diagramLanguage)
            && string.Equals(diagramLanguage, "mermaid", StringComparison.OrdinalIgnoreCase))
        {
            RenderMermaid(node, html);
            return;
        }

        var code = new StringBuilder();

        if (node.Children.Count == 0)
        {
            code.Append(node.Text);
        }
        else
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                code.Append(node.Children[i].Text);
            }
        }

        var codeText = code.ToString().TrimEnd('\r', '\n');

        html.Append("<pre");
        AppendNodeAttributes(node, html);
        AppendStyle(node, html);
        html.Append("><code");

        if (node.Attributes.TryGetValue("language", out var lang) && !string.IsNullOrWhiteSpace(lang))
        {
            var languageToken = lang.Trim()
                .Split(LanguageSeparators, StringSplitOptions.RemoveEmptyEntries)[0];
            html.Append(" class=\"language-").Append(WebUtility.HtmlEncode(languageToken)).Append('"');
        }

        html.Append('>');
        html.Append(WebUtility.HtmlEncode(codeText));
        html.Append("</code></pre>");
    }

    private void RenderMath(MarkdownNode node, string tag, StringBuilder html)
    {
        var isBlock = tag == "div";
        var formula = node.Text;

        html.Append('<').Append(tag)
            .Append(" class=\"math ")
            .Append(isBlock ? "math-block" : "math-inline")
            .Append('"');
        AppendNodeAttributes(node, html);
        // 块公式经 KaTeX 渲染后内部 .katex-display/.katex 自带对齐与字体，
        // 直接属性无法靠继承穿透；改写为 CSS 变量由预览专属规则消费。
        AppendStyle(node, html, isBlock ? BuildMathStyleVars(node) : null);
        html.Append(" data-tex=\"")
            .Append(WebUtility.HtmlEncode(formula))
            .Append("\">")
            .Append(WebUtility.HtmlEncode(formula))
            .Append("</").Append(tag).Append('>');
    }

    /// <summary>
    /// KaTeX 注入的 .katex 元素自带 font-family/text-align，容器直接属性无法继承穿透；
    /// 用户设置改写为 --math-* 变量由预览 CSS 消费，未设置时回退 KaTeX 默认。
    /// </summary>
    private string? BuildMathStyleVars(MarkdownNode node)
    {
        var resolved = _styleResolver.Resolve(node);
        StringBuilder? vars = null;

        var align = resolved.TextAlign?.Trim().ToLowerInvariant();
        if (align is "left" or "right" or "center")
        {
            vars = new StringBuilder();
            vars.Append("--math-align: ").Append(align).Append(';');
        }

        // KaTeX 暗色主题规则（html[data-theme="dark"] .katex）特异性高于
        // 普通 .katex 取色规则，会顶掉继承下来的 --node-color；显式色
        // 改写为 --math-color 由 .math-block 专属规则消费。
        var color = resolved.ForegroundColor?.Trim();
        // 与 PreviewCssStyleBuilder 的基线剥离保持一致：生效值等于文档默认
        // 文字色（#333333 等）时只是默认基线而非用户显式设置，不输出变量，
        // 否则暗色主题下公式被默认深灰锁死（灰底灰字）。
        var defaultColor = _defaultStyle?.ForegroundColor?.Trim();
        if (!string.IsNullOrWhiteSpace(color) && !SameHexColor(color, defaultColor))
        {
            vars ??= new StringBuilder();
            vars.Append("--math-color: ").Append(color).Append(';');
        }

        var fontFamily = resolved.FontFamily?.Trim();
        // 与 PreviewCssStyleBuilder 的基线剥离保持一致：生效值等于文档默认
        // 字体（Arial 等）时视为用户未显式设置，不输出变量，交由 KaTeX 默认。
        var defaultFontFamily = _defaultStyle?.FontFamily?.Trim();
        if (!string.IsNullOrWhiteSpace(fontFamily)
            && !string.Equals(fontFamily, defaultFontFamily, StringComparison.OrdinalIgnoreCase))
        {
            vars ??= new StringBuilder();
            vars.Append("--math-font-family: ")
                .Append(fontFamily)
                .Append(';');
        }

        return vars?.ToString();
    }

    // 颜色字面量等价比较：#abc 展开为 #aabbcc，与 PreviewCssStyleBuilder
    // 的归一化规则一致。任一为空视为不等。
    private static bool SameHexColor(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        return string.Equals(NormalizeHexColor(a), NormalizeHexColor(b), StringComparison.Ordinal);
    }

    private static string NormalizeHexColor(string color)
    {
        var c = color.Trim().ToLowerInvariant();
        if (c.Length == 4 && c[0] == '#')
        {
            c = "#" + c[1] + c[1] + c[2] + c[2] + c[3] + c[3];
        }

        return c;
    }

    private void RenderMermaid(MarkdownNode node, StringBuilder html)
    {
        var source = node.Text.TrimEnd('\r', '\n');

        html.Append("<div class=\"mermaid");
        if (node.Attributes.ContainsKey(MarkdownAttributes.Unclosed))
        {
            html.Append(" mermaid-pending");
        }
        html.Append('"');
        AppendNodeAttributes(node, html);
        AppendStyle(node, html, ResolveReplacedAlignmentCss(node, forceBlockDisplay: false));
        html.Append(" data-diagram=\"")
            .Append(WebUtility.HtmlEncode(source))
            .Append("\">")
            .Append(WebUtility.HtmlEncode(source))
            .Append("</div>");
    }

    private void RenderImageMatrix(MarkdownNode node, StringBuilder html)
    {
        var rows = CollectMatrixRows(node);
        var columnCount = CountMatrixColumns(rows);

        html.Append("<div class=\"image-matrix");
        if (node.Attributes.ContainsKey(MarkdownAttributes.Unclosed))
        {
            html.Append(" image-matrix-pending");
        }
        html.Append('"');
        AppendNodeAttributes(node, html);

        // 格间距与对齐经自定义变量/单边外边距注入：--matrix-gap 贯通外层、行与图片格基准，
        // 对齐对替换型容器走 auto 外边距（见 BuildReplacedAlignment）。
        AppendStyle(node, html, BuildMatrixExtraCss(node, columnCount));
        html.Append('>');

        foreach (var row in rows)
        {
            html.Append("<div class=\"image-matrix-row\">");
            foreach (var cell in row)
            {
                AppendMatrixCell(html, cell);
            }

            html.Append("</div>");
        }

        html.Append("</div>");
    }

    private void RenderQrCode(MarkdownNode node, StringBuilder html)
    {
        // 块体文本即二维码承载内容（链接 / 任意文本）。
        var content = (node.Text ?? string.Empty).Trim();
        var isPending = node.Attributes.ContainsKey(MarkdownAttributes.Unclosed);
        var isEmpty = content.Length == 0;

        html.Append("<figure class=\"qrcode-block");
        if (isPending) html.Append(" qrcode-block-pending");
        if (isEmpty) html.Append(" qrcode-block-empty");
        html.Append('"');
        AppendNodeAttributes(node, html);
        // 替换型块定位走 auto 外边距（基础 CSS 已为块级 flex + fit-content），
        // text-align 无法移动 figure 本身。
        AppendStyle(node, html, ResolveReplacedAlignmentCss(node, forceBlockDisplay: false));
        // 完整内容进 data-tip 由预览 CSS 画主题化气泡（原生 title 样式系统级不可控）。
        if (!isEmpty)
            html.Append(" data-tip=\"").Append(WebUtility.HtmlEncode(content)).Append('"');
        html.Append('>');

        if (isEmpty)
        {
            html.Append("<div class=\"qrcode-placeholder\">")
                .Append("<span class=\"qrcode-placeholder-icon\" aria-hidden=\"true\">&#9638;</span>")
                .Append("<span>在围栏内输入二维码内容（链接或文本）</span>")
                .Append("</div>");
        }
        else
        {
            try
            {
                node.Attributes.TryGetValue("qr-ecc", out var eccValue);
                node.Attributes.TryGetValue("qr-border", out var borderValue);

                var ecc = ResolveQrEcc(eccValue);
                var border = ResolveQrBorder(borderValue);

                // 必须关闭 ECC 提升（boostEcl）：默认会把等级向上提升，
                // 短内容选 L/M/Q 最终都被提升到 Q。关闭后严格按所选等级编码。
                var qr = Net.Codecrete.QrCodeGenerator.QrCode.EncodeTextAdvanced(
                    content,
                    ecc,
                    boostEcl: false,
                    minVersion: 1,
                    maxVersion: 40,
                    eci: Net.Codecrete.QrCodeGenerator.ECI.Automatic,
                    encoding: null,
                    kanjiStrategy: Net.Codecrete.QrCodeGenerator.KanjiStrategy.Automatic,
                    encodingInfo: null);

                var svg = qr.ToSvgString(border, "currentColor", "transparent");

                // 去掉 ToSvgString 自带的 <?xml?> 声明与 <!DOCTYPE>，
                // 只保留 <svg>…</svg> 以便内联嵌入。
                var svgStart = svg.IndexOf("<svg", StringComparison.Ordinal);
                if (svgStart > 0)
                    svg = svg.Substring(svgStart);

                html.Append("<div class=\"qrcode-svg\">")
                    .Append(svg)
                    .Append("</div>")
                    // 显示文本由 CSS 两行截断，超长链接不会在卡片下堆成高文字块。
                    .Append("<figcaption>")
                    .Append(WebUtility.HtmlEncode(content))
                    .Append("</figcaption>");
            }
            catch (Net.Codecrete.QrCodeGenerator.DataTooLongException)
            {
                html.Append("<div class=\"qrcode-error\" role=\"alert\">")
                    .Append("内容过长，无法生成二维码，请缩短内容")
                    .Append("</div>");
            }
        }

        html.Append("</figure>");
    }

    private static Net.Codecrete.QrCodeGenerator.QrCode.Ecc ResolveQrEcc(string? value) =>
        value switch
        {
            "L" => Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Low,
            "Q" => Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Quartile,
            "H" => Net.Codecrete.QrCodeGenerator.QrCode.Ecc.High,
            _ => Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Medium
        };

    private static int ResolveQrBorder(string? value) =>
        int.TryParse(value, out var modules) && modules is >= 0 and <= 10
            ? modules
            : 2;

    /// <summary>
    /// 拆分图片矩阵一行中的格子。以空白分隔，但双引号内的空格不参与分隔，
    /// 因此 <c>T:"山居 秋暝"</c> 这样的含空格文本可作为单个文本格。
    /// 引号本身不作为内容保留。
    /// </summary>
    private static List<string> SplitMatrixCells(string line)
    {
        var cells = new List<string>();
        var builder = new StringBuilder();
        var inCell = false;
        var inQuote = false;

        foreach (var ch in line)
        {
            if (inQuote)
            {
                if (ch == '"')
                {
                    inQuote = false;
                }
                else
                {
                    builder.Append(ch);
                }

                continue;
            }

            if (ch == '"')
            {
                inQuote = true;
                inCell = true;
                continue;
            }

            if (ch == ' ' || ch == '\t')
            {
                if (inCell)
                {
                    cells.Add(builder.ToString());
                    builder.Clear();
                    inCell = false;
                }

                continue;
            }

            builder.Append(ch);
            inCell = true;
        }

        // 引号未闭合时也保留已读取的内容。
        if (inCell)
        {
            cells.Add(builder.ToString());
        }

        return cells;
    }

    /// <summary>
    /// 输出图片矩阵中的一个格子：
    /// 显式 <c>T:文字</c>（含空文本占位）或普通纯文本渲染为文本格，
    /// 安全 URL 渲染为图片格；合法但 scheme 不被允许的 URI（如 javascript:）直接忽略。
    /// </summary>
    private static void AppendMatrixCell(
        StringBuilder html,
        string token)
    {
        string? text;

        if (token.StartsWith("T:", StringComparison.OrdinalIgnoreCase))
        {
            // 显式文本标记，冒号后即文本内容（引号已在拆分阶段去除）。
            text = token.Substring(2);
        }
        else if (IsSafeUrl(token))
        {
            html.Append("<figure class=\"image-matrix-cell image-matrix-cell-image\"><img src=\"")
                .Append(WebUtility.HtmlEncode(token))
                .Append("\" loading=\"lazy\" alt=\"\" /></figure>");
            return;
        }
        else if (Uri.TryCreate(token, UriKind.Absolute, out _))
        {
            // 形如绝对 URI 但 scheme 不在允许名单（javascript:/data:/vbscript: 等），
            // 既不作为图片也不作为文本，直接丢弃，防止危险内容注入。
            return;
        }
        else
        {
            // 非 URI 的普通文字，兼容为文本格。
            text = token;
        }

        var isEmpty = string.IsNullOrWhiteSpace(text);

        html.Append(
            "<figure class=\"image-matrix-cell image-matrix-text\"><div class=\"image-matrix-text-inner");

        if (isEmpty)
        {
            html.Append(" image-matrix-text-empty");
        }

        html.Append("\">");

        // 零宽空格保证空文本格仍占据与图片一致的 4:3 盒高度，不塌陷。
        html.Append(
            isEmpty
                ? "&#8203;"
                : WebUtility.HtmlEncode(text));

        html.Append("</div></figure>");
    }

    private void RenderPoemBlock(MarkdownNode node, StringBuilder html)
    {
        // 子部件由转换器解析为虚拟节点，这里按固定布局遍历，
        // 并把各自 LocalStyle 落到对应元素（无样式时 AppendStyle 输出空）。
        var titleNode = node.Children.FirstOrDefault(c => c.Type == NodeType.PoemTitle);
        var authorNode = node.Children.FirstOrDefault(c => c.Type == NodeType.PoemAuthor);
        var dynastyNode = node.Children.FirstOrDefault(c => c.Type == NodeType.PoemDynasty);
        var formNode = node.Children.FirstOrDefault(c => c.Type == NodeType.PoemForm);
        var sourceNode = node.Children.FirstOrDefault(c => c.Type == NodeType.PoemSource);
        var noteNode = node.Children.FirstOrDefault(c => c.Type == NodeType.PoemNote);
        var stanzas = node.Children.Where(c => c.Type == NodeType.PoemStanza).ToList();

        html.Append("<div class=\"poem-block");
        if (node.Attributes.ContainsKey(MarkdownAttributes.Unclosed))
        {
            html.Append(" poem-pending");
        }
        html.Append("\"");
        AppendNodeAttributes(node, html);
        AppendStyle(node, html);
        html.Append('>');

        if (titleNode is not null || authorNode is not null
            || dynastyNode is not null || formNode is not null)
        {
            html.Append("<div class=\"poem-header\">");

            if (titleNode is not null)
            {
                html.Append("<h3 class=\"poem-title\"");
                AppendStyle(titleNode, html);
                html.Append('>')
                    .Append(WebUtility.HtmlEncode(titleNode.Text))
                    .Append("</h3>");
            }

            if (authorNode is not null || dynastyNode is not null)
            {
                html.Append("<div class=\"poem-author-line\">");
                if (authorNode is not null)
                {
                    html.Append("<span class=\"poem-author\"");
                    AppendStyle(authorNode, html);
                    html.Append('>')
                        .Append(WebUtility.HtmlEncode(authorNode.Text))
                        .Append("</span>");
                }
                if (dynastyNode is not null)
                {
                    html.Append("<span class=\"poem-dynasty\"");
                    AppendStyle(dynastyNode, html);
                    html.Append('>')
                        .Append(WebUtility.HtmlEncode(dynastyNode.Text))
                        .Append("</span>");
                }
                html.Append("</div>");
            }

            if (formNode is not null)
            {
                html.Append("<div class=\"poem-form\"");
                AppendStyle(formNode, html);
                html.Append('>')
                    .Append(WebUtility.HtmlEncode(formNode.Text))
                    .Append("</div>");
            }

            html.Append("</div>");
        }

        html.Append("<div class=\"poem-body\">");
        foreach (var stanza in stanzas)
        {
            html.Append("<div class=\"poem-stanza\"");
            AppendStyle(stanza, html);
            html.Append('>');

            foreach (var line in stanza.Children.Where(c => c.Type == NodeType.PoemLine))
            {
                html.Append("<p class=\"poem-line\"");
                AppendStyle(line, html);
                html.Append('>')
                    .Append(WebUtility.HtmlEncode(line.Text))
                    .Append("</p>");
            }

            html.Append("</div>");
        }
        html.Append("</div>");

        if (sourceNode is not null)
        {
            html.Append("<div class=\"poem-source\"");
            AppendStyle(sourceNode, html);
            html.Append('>')
                .Append(WebUtility.HtmlEncode(sourceNode.Text))
                .Append("</div>");
        }

        // 出处与说明均为 inline-block，共存时用 br 断行避免并排
        if (sourceNode is not null && noteNode is not null)
        {
            html.Append("<br class=\"poem-meta-break\"/>");
        }

        if (noteNode is not null)
        {
            html.Append("<div class=\"poem-note\"");
            AppendStyle(noteNode, html);
            html.Append('>')
                .Append(WebUtility.HtmlEncode(noteNode.Text))
                .Append("</div>");
        }

        html.Append("</div>");
    }

    // ── 时间线 ────────────────────────────────────────────────────────

    private void RenderTimeline(
        MarkdownNode node,
        StringBuilder html,
        List<DiagnosticMessage> diagnostics)
    {
        var layout = GetAttribute(node, "timeline-layout") is "axis" ? "axis" : "edge";
        var items = node.Children
            .Where(child => child.Type == NodeType.TimelineItem)
            .ToList();

        html.Append("<div class=\"md-timeline md-timeline-").Append(layout);
        if (GetAttribute(node, "timeline-line") == "dashed")
        {
            html.Append(" md-timeline-line-dashed");
        }

        if (node.Attributes.ContainsKey(MarkdownAttributes.Unclosed))
        {
            html.Append(" md-timeline-pending");
        }
        html.Append('"');
        AppendNodeAttributes(node, html);
        AppendStyle(node, html, BuildNodeExtraInlineCss(node));
        html.Append('>');

        if (items.Count == 0)
        {
            html.Append("<div class=\"md-tl-empty\">在 @@ 行添加时间节点，空行后书写正文</div>");
        }
        else
        {
            foreach (var item in items)
            {
                RenderTimelineItem(item, html, diagnostics);
            }
        }

        html.Append("</div>");
    }

    private void RenderTimelineItem(
        MarkdownNode item,
        StringBuilder html,
        List<DiagnosticMessage> diagnostics)
    {
        var side = GetAttribute(item, "timeline-side-effective") is "left" ? "left" : "right";

        html.Append("<div class=\"md-tl-item\" data-side=\"").Append(side).Append('"');
        AppendNodeAttributes(item, html);
        AppendStyle(item, html);
        html.Append('>');

        // 轴轨与实心圆点；主轴竖线由容器伪元素统一绘制，保证跨节点连续。
        html.Append("<div class=\"md-tl-rail\" aria-hidden=\"true\"><span class=\"md-tl-dot\"></span></div>");

        html.Append("<div class=\"md-tl-meta\">");
        var time = GetAttribute(item, "timeline-time");
        if (string.IsNullOrEmpty(time))
        {
            html.Append("<time class=\"md-tl-time md-tl-time-empty\">未设置时间</time>");
        }
        else
        {
            html.Append("<time class=\"md-tl-time\">");
            AppendEncoded(html, time);
            html.Append("</time>");
        }

        var tags = TimelineMarker.SplitTags(
            item.Attributes.TryGetValue("timeline-tags", out var tagValue) ? tagValue : null);
        if (tags.Count > 0)
        {
            html.Append("<div class=\"md-tl-tags\">");
            foreach (var tag in tags)
            {
                html.Append("<span class=\"md-tl-tag\">");
                AppendEncoded(html, tag);
                html.Append("</span>");
            }
            html.Append("</div>");
        }
        html.Append("</div>");

        html.Append("<div class=\"md-tl-body\">");
        foreach (var child in item.Children)
        {
            RenderNode(child, html, diagnostics);
        }
        html.Append("</div>");

        html.Append("</div>");
    }

    private void RenderLink(MarkdownNode node, StringBuilder html, List<DiagnosticMessage> diagnostics)
    {
        var url = GetAttribute(node, "url") ?? GetAttribute(node, "href");

        html.Append("<a");
        AppendNodeAttributes(node, html);
        AppendStyle(node, html);

        if (IsSafeUrl(url))
        {
            html.Append(" href=\"").Append(WebUtility.HtmlEncode(url!)).Append('"');
        }

        html.Append('>');

        if (!string.IsNullOrEmpty(node.Text))
        {
            html.Append(WebUtility.HtmlEncode(node.Text));
        }
        else
        {
            RenderChildren(node, html, diagnostics);
        }

        html.Append("</a>");
    }

    private void RenderImage(MarkdownNode node, StringBuilder html, List<DiagnosticMessage> diagnostics)
    {
        var source = GetAttribute(node, "url") ?? GetAttribute(node, "src");
        if (!IsSafeUrl(source)) return;

        html.Append("<img");
        AppendNodeAttributes(node, html);
        AppendStyle(node, html, ResolveReplacedAlignmentCss(node, forceBlockDisplay: true));
        html.Append(" src=\"").Append(WebUtility.HtmlEncode(source!)).Append("\" loading=\"lazy\" alt=\"").Append(WebUtility.HtmlEncode(node.Text)).Append("\" />");
    }

    private void RenderChildren(MarkdownNode node, StringBuilder html, List<DiagnosticMessage> diagnostics)
    {
        for (int i = 0; i < node.Children.Count; i++)
            RenderNode(node.Children[i], html, diagnostics);
    }

    private void RenderTightContainerChildren(MarkdownNode node, StringBuilder html, List<DiagnosticMessage> diagnostics)
    {
        foreach (var child in node.Children)
        {
            if (child.Type == NodeType.Paragraph)
            {
                RenderChildren(child, html, diagnostics);
            }
            else
            {
                RenderNode(child, html, diagnostics);
            }
        }
    }

    private void AppendStyle(MarkdownNode node, StringBuilder html, string? extraCss = null)
    {
        var css = BuildNodeInlineCss(node, extraCss);
        if (!string.IsNullOrWhiteSpace(css))
        {
            html.Append(" style=\"").Append(WebUtility.HtmlEncode(css)).Append('"');
        }
    }

    /// <summary>
    /// 节点当前样式对应的完整内联 CSS（含按块类型追加的变量/单边外边距），
    /// 与全量帧写到该节点 style 属性上的内容逐字一致。无任何声明时返回 null。
    /// </summary>
    public string? BuildNodeInlineCss(MarkdownNode node) =>
        BuildNodeInlineCss(node, BuildNodeExtraInlineCss(node));

    private string? BuildNodeInlineCss(MarkdownNode node, string? extraCss)
    {
        var css = _cssStyleBuilder.Build(_styleResolver.Resolve(node));
        if (!string.IsNullOrWhiteSpace(extraCss))
        {
            css = string.IsNullOrWhiteSpace(css) ? extraCss.Trim() : css + " " + extraCss.Trim();
        }

        return string.IsNullOrWhiteSpace(css) ? null : css;
    }

    /// <summary>
    /// 各块类型在基础样式之外追加到同一 style 属性上的补充声明，分发条件必须与
    /// <see cref="RenderNode"/> 的元素分支保持一致：局部 CSS 直写通道据此产出与
    /// 全量帧完全相同的内联声明。
    /// </summary>
    private string? BuildNodeExtraInlineCss(MarkdownNode node)
    {
        switch (node.Type)
        {
            case NodeType.TableRow:
                // tr 背景改写为 --row-bg 变量，由单元格底色规则消费。
                return BuildTableRowCascadeVars(node);
            case NodeType.MathBlock:
                return BuildMathStyleVars(node);
            case NodeType.Mermaid:
            case NodeType.QrCode:
                return ResolveReplacedAlignmentCss(node, forceBlockDisplay: false);
            case NodeType.Image:
                return ResolveReplacedAlignmentCss(node, forceBlockDisplay: true);
            case NodeType.ImageMatrix:
                return BuildMatrixExtraCss(node, CountMatrixColumns(CollectMatrixRows(node)));
            case NodeType.Quote when node.Attributes.ContainsKey("admonition"):
                return ResolveSemanticAccentCss(node, "--adm-accent");
            case NodeType.Custom:
                return ResolveSemanticAccentCss(node, "--cb-accent");
            case NodeType.Timeline:
                return BuildTimelineStyleVars(node);
            default:
                return null;
        }
    }

    // 虚线间隙由属性面板写入围栏 info（gap=），渲染为容器级 CSS 变量，
    // 供主轴与短支线的 repeating-linear-gradient 消费。
    private static string? BuildTimelineStyleVars(MarkdownNode node)
    {
        if (GetAttributeStatic(node, "timeline-line") != "dashed")
        {
            return null;
        }

        var gapText = GetAttributeStatic(node, "timeline-gap");
        if (int.TryParse(gapText, out var gap)
            && gap is >= TimelineMarker.MinLineGap and <= TimelineMarker.MaxLineGap)
        {
            return $"--tl-gap:{gap.ToString(CultureInfo.InvariantCulture)}px;";
        }

        return null;
    }

    private static string? GetAttributeStatic(MarkdownNode node, string key) =>
        node.Attributes.TryGetValue(key, out var value) ? value : null;

    // 先按行收集格子：图片格宽度恒按“单行最多图片数”N 等分；
    // 文本格只以 auto 宽度吸收剩余空间（必要时自动折行），绝不挤压图片尺寸。
    private static List<List<string>> CollectMatrixRows(MarkdownNode node)
    {
        var source = node.Text ?? string.Empty;
        var lines = source.Replace("\r\n", "\n").Split('\n');

        var rows = new List<List<string>>();
        var currentRow = new List<string>();

        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                if (currentRow.Count > 0)
                {
                    rows.Add(currentRow);
                    currentRow = new List<string>();
                }

                continue;
            }

            // 围栏开/关标记行（'''image、''' 等）不是内容格，必须跳过。
            if (trimmed.StartsWith("'''", StringComparison.Ordinal))
            {
                continue;
            }

            currentRow.AddRange(SplitMatrixCells(trimmed));
        }

        if (currentRow.Count > 0)
        {
            rows.Add(currentRow);
        }

        return rows;
    }

    private static int CountMatrixColumns(List<List<string>> rows)
    {
        var columnCount = 1;
        foreach (var row in rows)
        {
            var imageCount = 0;
            foreach (var cell in row)
            {
                if (IsSafeUrl(cell))
                {
                    imageCount++;
                }
            }

            if (imageCount > columnCount)
            {
                columnCount = imageCount;
            }
        }

        return columnCount;
    }

    // 格间距与对齐经自定义变量/单边外边距注入：--matrix-gap 贯通外层、行与图片格基准，
    // 对齐对替换型容器走 auto 外边距（见 BuildReplacedAlignment）。
    private string BuildMatrixExtraCss(MarkdownNode node, int columnCount)
    {
        var resolved = _styleResolver.Resolve(node);
        var matrixExtra = new StringBuilder("--matrix-cols:").Append(columnCount);
        if (resolved.Gap is > 0)
        {
            matrixExtra.Append("; --matrix-gap:")
                .Append(resolved.Gap.Value.ToString("0.##", CultureInfo.InvariantCulture))
                .Append("px");
        }

        var matrixAlign = BuildReplacedAlignment(resolved.TextAlign, forceBlockDisplay: false);
        if (!string.IsNullOrEmpty(matrixAlign))
        {
            matrixExtra.Append("; ").Append(matrixAlign);
        }

        return matrixExtra.ToString();
    }

    private string? ResolveReplacedAlignmentCss(MarkdownNode node, bool forceBlockDisplay)
    {
        var resolved = _styleResolver.Resolve(node);
        return BuildReplacedAlignment(resolved.TextAlign, forceBlockDisplay);
    }

    /// <summary>
    /// 替换型内容（图片 / Mermaid / 公式块 / 图片矩阵）无法用 text-align 定位，
    /// 改为写水平 auto 外边距；图片默认行内级，必须先 display:block 才生效。
    /// 片段经 extraCss 追加在主样式之后，单边 longhand 仅覆盖左右、保留显式上下外边距。
    /// </summary>
    private static string? BuildReplacedAlignment(string? textAlign, bool forceBlockDisplay)
    {
        if (string.IsNullOrWhiteSpace(textAlign))
            return null;

        var prefix = forceBlockDisplay ? "display:block; " : string.Empty;

        return textAlign.Trim().ToLowerInvariant() switch
        {
            "center" => prefix + "margin-left:auto;margin-right:auto",
            "left" => prefix + "margin-left:0;margin-right:auto",
            "right" => prefix + "margin-left:auto;margin-right:0",
            _ => null
        };
    }

    /// <summary>
    /// 渲染链判定：提示块/自定义引用块默认按 kind 用语义色驱动左边条、图标与标题；
    /// 仅当用户（或样式表）显式定义了边框色时，把同色写入语义变量，让细条、图标、
    /// 标题与用户边框保持同色；只改背景色时语义色仍按 kind 保留。
    /// </summary>
    private string? ResolveSemanticAccentCss(MarkdownNode node, string variableName)
    {
        var style = _styleResolver.Resolve(node);
        if (style is null || string.IsNullOrWhiteSpace(style.BorderColor))
        {
            return null;
        }

        return variableName + ": " + style.BorderColor.Trim() + ";";
    }

    /// <summary>
    /// 纯文本节点默认直接输出转义文本；文本运行带局部样式（或继承到非空样式）时
    /// 必须包一层带样式的 span，否则样式在预览中不可见。
    /// </summary>
    private void RenderText(MarkdownNode node, StringBuilder html)
    {
        var style = _styleResolver.Resolve(node);
        var css = _cssStyleBuilder.Build(style);

        if (string.IsNullOrWhiteSpace(css))
        {
            html.Append(WebUtility.HtmlEncode(node.Text));
            return;
        }

        html.Append("<span");
        AppendNodeAttributes(node, html);
        html.Append(" style=\"").Append(WebUtility.HtmlEncode(css)).Append("\">");
        html.Append(WebUtility.HtmlEncode(node.Text));
        html.Append("</span>");
    }

    private static void AppendNodeAttributes(MarkdownNode node, StringBuilder html)
    {
        html.Append(" data-node-id=\"").Append(WebUtility.HtmlEncode(node.Id)).Append('"');

        if (node.Range.StartLine > 0)
        {
            html.Append(" data-line-start=\"").Append(node.Range.StartLine).Append('"');
            html.Append(" data-line-end=\"")
                .Append(Math.Max(node.Range.StartLine, node.Range.EndLine)).Append('"');
        }

        if (node.Attributes.ContainsKey(MarkdownAttributes.Unclosed))
        {
            html.Append(" data-block-unclosed=\"1\"");
        }
    }

    private static string? GetAttribute(MarkdownNode node, string name) =>
        node.Attributes.TryGetValue(name, out var val) && !string.IsNullOrWhiteSpace(val) ? val : null;

    /// <summary>
    /// 属性面板写入的自定义图标为 4~6 位十六进制 Unicode 码点；
    /// 校验通过才输出，非法值回退到 kind 默认图标，避免破坏 HTML。
    /// </summary>
    private static string? TryBuildIconGlyph(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var hex = raw.Trim();
        if (hex.Length is < 4 or > 6
            || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint)
            || codePoint is < 0x20 or > 0x10FFFF
            || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
        {
            return null;
        }

        return $"&#x{hex.ToLowerInvariant()};";
    }

    private static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        var trimmed = url.Trim();

        if (trimmed.StartsWith("//", StringComparison.Ordinal) || UnsafeUriCharRegex.IsMatch(trimmed))
            return false;

        if (trimmed.StartsWith('#') || trimmed.StartsWith('/') ||
            trimmed.StartsWith("./", StringComparison.Ordinal) || trimmed.StartsWith("../", StringComparison.Ordinal))
        {
            return true;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && AllowedUriSchemes.Contains(uri.Scheme);
    }

    private static void AppendEncoded(StringBuilder html, string value) =>
        html.Append(WebUtility.HtmlEncode(value));
}
