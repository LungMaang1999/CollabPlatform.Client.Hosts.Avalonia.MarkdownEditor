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

    public HtmlRenderer(
        IStyleResolver styleResolver,
        ICssStyleBuilder? cssStyleBuilder = null,
        AdmonitionIconOptions? iconOptions = null)
    {
        _styleResolver = styleResolver ?? throw new ArgumentNullException(nameof(styleResolver));
        _cssStyleBuilder = cssStyleBuilder ?? new ExportingCssStyleBuilder();
        _iconOptions = iconOptions ?? new AdmonitionIconOptions();
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
                    html.Append("<section class=\"footnotes\"><ol>");
                    RenderChildren(node, html, diagnostics);
                    html.Append("</ol></section>");
                    break;
                case NodeType.Footnote:
                    {
                        var index = node.Attributes.TryGetValue("index", out var value) ? value : node.Text;

                        html.Append("<li id=\"fn-");
                        AppendEncoded(html, index);
                        html.Append("\"");
                        AppendNodeAttributes(node, html);
                        html.Append('>');

                        RenderTightContainerChildren(node, html, diagnostics);

                        html.Append(" <a class=\"footnote-backref\" href=\"#fnref-");
                        AppendEncoded(html, index);
                        html.Append("\" data-footnote-target=\"fnref-");
                        AppendEncoded(html, index);
                        html.Append("\" aria-label=\"返回正文引用\">↩</a></li>");
                        break;
                    }
                case NodeType.FootnoteLink:
                    {
                        var index = node.Attributes.TryGetValue("index", out var value) ? value : node.Text;

                        html.Append("<sup class=\"footnote-ref\"><a id=\"fnref-");
                        AppendEncoded(html, index);
                        html.Append("\" href=\"#fn-");
                        AppendEncoded(html, index);
                        html.Append("\" data-footnote-target=\"fn-");
                        AppendEncoded(html, index);
                        html.Append("\">");
                        AppendEncoded(html, index);
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
                    break;
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
                    RenderBlock(node, "table", html, diagnostics);
                    break;
                case NodeType.TableRow:
                    RenderBlock(node, "tr", html, diagnostics);
                    break;
                case NodeType.TableCell:
                    RenderTableCell(node, html, diagnostics);
                    break;
                case NodeType.Text:
                    html.Append(WebUtility.HtmlEncode(node.Text));
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

        html.Append("<blockquote class=\"admonition ")
            .Append(safeKind)
            .Append('"');
        AppendNodeAttributes(node, html);
        AppendStyle(node, html);
        html.Append("><p class=\"admonition-title\">")
            .Append("<span class=\"admonition-icon\"><span class=\"md-icon\" aria-hidden=\"true\">")
            .Append(_iconOptions.GetIcon(safeKind))
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

        html.Append("<div class=\"custom-block custom-")
            .Append(kind)
            .Append('"');
        AppendNodeAttributes(node, html);
        AppendStyle(node, html);
        html.Append("><span class=\"custom-block-icon\"><span class=\"md-icon\" aria-hidden=\"true\">")
            .Append(_iconOptions.GetIcon(kind))
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
        AppendStyle(node, html);
        html.Append(" data-tex=\"")
            .Append(WebUtility.HtmlEncode(formula))
            .Append("\">")
            .Append(WebUtility.HtmlEncode(formula))
            .Append("</").Append(tag).Append('>');
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
        AppendStyle(node, html);
        html.Append(" data-diagram=\"")
            .Append(WebUtility.HtmlEncode(source))
            .Append("\">")
            .Append(WebUtility.HtmlEncode(source))
            .Append("</div>");
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
        AppendStyle(node, html);
        html.Append(" src=\"").Append(WebUtility.HtmlEncode(source!)).Append("\" alt=\"").Append(WebUtility.HtmlEncode(node.Text)).Append("\" />");
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

    private void AppendStyle(MarkdownNode node, StringBuilder html)
    {
        var style = _styleResolver.Resolve(node);
        var css = _cssStyleBuilder.Build(style);
        if (!string.IsNullOrWhiteSpace(css))
        {
            html.Append(" style=\"").Append(WebUtility.HtmlEncode(css)).Append('"');
        }
    }

    private static void AppendNodeAttributes(MarkdownNode node, StringBuilder html)
    {
        html.Append(" data-node-id=\"").Append(WebUtility.HtmlEncode(node.Id)).Append('"');

        // 流式半截块：前端据此跳过高亮/mermaid/公式渲染，闭合后该属性随新 HTML 消失。
        if (node.Attributes.ContainsKey(MarkdownAttributes.Unclosed))
        {
            html.Append(" data-block-unclosed=\"1\"");
        }
    }

    private static string? GetAttribute(MarkdownNode node, string name) =>
        node.Attributes.TryGetValue(name, out var val) && !string.IsNullOrWhiteSpace(val) ? val : null;

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
