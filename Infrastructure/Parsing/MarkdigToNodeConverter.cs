using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using System.Globalization;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax.Factories;
using System.Text;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

public sealed class MarkdigToNodeConverter
{
    private static readonly char[] InfoStringSeparators = { ' ', '\t' };

    private readonly NodeFactory _nodeFactory;
    private readonly Markdig.MarkdownPipeline? _pipeline;

    public MarkdigToNodeConverter(
        NodeFactory? nodeFactory = null,
        Markdig.MarkdownPipeline? pipeline = null)
    {
        _nodeFactory = nodeFactory ?? new NodeFactory();
        _pipeline = pipeline;
    }

    public List<MarkdownNode> ConvertBlocks(
        ContainerBlock rootContainer,
        ICollection<DiagnosticMessage> diagnostics,
        string source)
    {
        ArgumentNullException.ThrowIfNull(rootContainer);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(source);

        var resultNodes = ConvertChildren(rootContainer, diagnostics, source);

        // 先填充 RawMarkdown（未闭合 \[ 公式的判定要用），再标记流式半截块。
        var unclosedFenceStartLine = FenceStateScanner.FindTrailingUnclosedFenceStartLine(source);

        foreach (var node in resultNodes)
        {
            PopulateRawMarkdown(node, source);
            MarkUnclosedStreamingBlocks(node, unclosedFenceStartLine);
        }

        return resultNodes;
    }

    private static void MarkUnclosedStreamingBlocks(MarkdownNode node, int? unclosedFenceStartLine)
    {
        if (unclosedFenceStartLine.HasValue
            && node.Range.StartLine == unclosedFenceStartLine.Value
            && node.Type is NodeType.CodeBlock or NodeType.MathBlock or NodeType.Mermaid
                or NodeType.ImageMatrix or NodeType.QrCode or NodeType.PoemBlock
                or NodeType.Timeline)
        {
            // 文档末尾未闭合围栏对应的代码/math 围栏/mermaid 块。
            node.Attributes[MarkdownAttributes.Unclosed] = "true";
        }
        else if (node.Type == NodeType.MathBlock && IsUnclosedLatexBlock(node.RawMarkdown))
        {
            // \[ 块公式未在源码中出现 \] 闭合（块解析会吞到 EOF）。
            node.Attributes[MarkdownAttributes.Unclosed] = "true";
        }

        foreach (var child in node.Children)
        {
            MarkUnclosedStreamingBlocks(child, unclosedFenceStartLine);
        }
    }

    private static bool IsUnclosedLatexBlock(string rawMarkdown)
    {
        var trimmed = rawMarkdown.AsSpan().TrimStart();
        if (trimmed.Length < 2 || trimmed[0] != '\\' || trimmed[1] != '[')
        {
            return false;
        }

        return rawMarkdown.IndexOf("\\]", StringComparison.Ordinal) < 0;
    }

    private List<MarkdownNode> ConvertChildren(
        ContainerBlock rootContainer,
        ICollection<DiagnosticMessage> diagnostics,
        string source)
    {
        var resultNodes = new List<MarkdownNode>();

        foreach (var block in rootContainer)
        {
            try
            {
                var node = ConvertBlock(block, diagnostics, source);
                if (node is not null)
                {
                    resultNodes.Add(node);
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add(new DiagnosticMessage
                {
                    Severity = DiagnosticSeverity.Warning,
                    Code = "BLOCK_CONVERSION_FAILED",
                    Message = $"Failed to convert block {block.GetType().FullName}: {ex.Message}",
                    Range = ExtractSourceRange(block)
                });
            }
        }

        return resultNodes;
    }

    private MarkdownNode? ConvertBlock(
        Block block,
        ICollection<DiagnosticMessage> diagnostics,
        string source)
    {
        var range = ExtractSourceRange(block);

        switch (block)
        {
            case HeadingBlock heading:
                var headingNode = _nodeFactory.Create(NodeType.Heading, NodeCategory.Block, range: range);
                headingNode.Level = heading.Level;
                ProcessInlines(heading.Inline, headingNode, diagnostics);
                headingNode.Text = GetInlineText(headingNode);
                return headingNode;

            case ParagraphBlock paragraph:
                var paragraphNode = _nodeFactory.Create(NodeType.Paragraph, NodeCategory.Block, range: range);
                ProcessInlines(paragraph.Inline, paragraphNode, diagnostics);
                paragraphNode.Text = GetInlineText(paragraphNode);
                return paragraphNode;

            case ListBlock listBlock:
                var listType = listBlock.IsOrdered
                     ? NodeType.OrderedList
                     : NodeType.UnorderedList;

                var listNode = _nodeFactory.Create(
                    listType,
                    NodeCategory.Container,
                    range: range);

                foreach (var item in listBlock)
                {
                    if (item is not ListItemBlock listItem)
                    {
                        continue;
                    }

                    var isTaskItem = TryGetTaskState(listItem, out var isChecked);
                    var itemNode = _nodeFactory.Create(
                        isTaskItem ? NodeType.TaskListItem : NodeType.ListItem,
                        NodeCategory.Container,
                        range: ExtractSourceRange(listItem));

                    if (isTaskItem)
                    {
                        itemNode.Attributes["checked"] = isChecked ? "true" : "false";
                    }

                    var itemChildren = ConvertChildren(listItem, diagnostics, source);
                    foreach (var child in itemChildren)
                    {
                        itemNode.AddChild(child);
                    }

                    itemNode.Text = GetInlineText(itemNode);
                    listNode.AddChild(itemNode);
                }

                return listNode;

            case QuoteBlock quoteBlock:
                var quoteNode = _nodeFactory.Create(NodeType.Quote, NodeCategory.Container, range: range);
                var quoteChildren = ConvertChildren(quoteBlock, diagnostics, source);
                foreach (var child in quoteChildren)
                {
                    quoteNode.AddChild(child);
                }

                if (TryClassifyCustomBlock(quoteNode))
                {
                    quoteNode.Text = GetInlineText(quoteNode);
                }
                else
                {
                    TryClassifyAdmonition(quoteNode);
                }
                return quoteNode;

            case Table tableBlock:
                var tableNode = _nodeFactory.Create(
                    NodeType.Table,
                    NodeCategory.Container,
                    range: range);

                foreach (var rowObj in tableBlock)
                {
                    if (rowObj is not TableRow rowBlock)
                    {
                        continue;
                    }

                    var rowNode = _nodeFactory.Create(
                        NodeType.TableRow,
                        NodeCategory.Container,
                        range: ExtractSourceRange(rowBlock));

                    foreach (var cellObj in rowBlock)
                    {
                        if (cellObj is not TableCell cellBlock)
                        {
                            continue;
                        }

                        var cellNode = _nodeFactory.Create(
                            NodeType.TableCell,
                            NodeCategory.Container,
                            range: ExtractSourceRange(cellBlock));

                        cellNode.IsTableHeader = rowBlock.IsHeader;

                        var cellChildren = ConvertChildren(cellBlock, diagnostics, source);
                        foreach (var child in cellChildren)
                        {
                            cellNode.AddChild(child);
                        }

                        cellNode.Text = GetInlineText(cellNode);
                        rowNode.AddChild(cellNode);
                    }

                    tableNode.AddChild(rowNode);
                }

                return tableNode;

            case FootnoteGroup footnoteGroup:
                {
                    var groupNode = _nodeFactory.Create(
                        NodeType.FootnoteGroup,
                        NodeCategory.Container,
                        range: range);

                    foreach (var blockObj in footnoteGroup)
                    {
                        if (blockObj is not Footnote footnoteBlock)
                        {
                            continue;
                        }

                        var footnoteNode = _nodeFactory.Create(
                            NodeType.Footnote,
                            NodeCategory.Container,
                            range: ExtractSourceRange(footnoteBlock));

                        // 保留用户原始标签作为显示文本（数字 42、字母 alpha 等），
                        // 锚点用确定性 slug；多处引用时 backrefs 记录每个引用的锚点。
                        var label = ExtractFootnoteLabel(footnoteBlock.Label);
                        var slug = BuildFootnoteSlug(label, footnoteBlock.Order);

                        footnoteNode.Attributes["label"] = label;
                        footnoteNode.Attributes["slug"] = slug;
                        footnoteNode.Attributes["id"] = "fn-" + slug;

                        var backrefAnchors = new StringBuilder();
                        for (var linkIndex = 0; linkIndex < footnoteBlock.Links.Count; linkIndex++)
                        {
                            if (linkIndex > 0)
                            {
                                backrefAnchors.Append(',');
                            }

                            backrefAnchors.Append(BuildFootnoteRefAnchor(slug, linkIndex));
                        }

                        footnoteNode.Attributes["backrefs"] = backrefAnchors.ToString();

                        var children = ConvertChildren(footnoteBlock, diagnostics, source);
                        foreach (var child in children)
                        {
                            footnoteNode.AddChild(child);
                        }

                        groupNode.AddChild(footnoteNode);
                    }

                    return groupNode;
                }

            case MathBlock mathBlock:
                return _nodeFactory.Create(
                    NodeType.MathBlock,
                    NodeCategory.Block,
                    text: mathBlock.Lines.ToString().TrimEnd('\r', '\n'),
                    range: range);

            case ImageMatrixBlock matrixBlock:
                return _nodeFactory.Create(
                    NodeType.ImageMatrix,
                    NodeCategory.Block,
                    text: matrixBlock.Lines.ToString().TrimEnd('\r', '\n'),
                    range: range);

            case PoemBlock poemBlock:
                {
                    var poemNode = _nodeFactory.Create(
                        NodeType.PoemBlock,
                        NodeCategory.Block,
                        text: poemBlock.Lines.ToString().TrimEnd('\r', '\n'),
                        range: range);
                    poemNode.Attributes["poem-type"] = poemBlock.PoemType.ToString();
                    poemNode.Attributes["poem-alias"] = poemBlock.Alias;
                    BuildPoemParts(poemNode);
                    return poemNode;
                }

            case TimelineBlock timelineBlock:
                return BuildTimeline(timelineBlock, range, diagnostics, source);

            case FencedCodeBlock codeBlock:
                {
                    var codeText = codeBlock.Lines.ToString();

                    var infoToken = codeBlock.Info?
                        .Trim()
                        .Split(InfoStringSeparators, StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault();

                    if (string.Equals(infoToken, "math", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(infoToken, "latex", StringComparison.OrdinalIgnoreCase))
                    {
                        return _nodeFactory.Create(
                            NodeType.MathBlock,
                            NodeCategory.Block,
                            text: codeText.TrimEnd('\r', '\n'),
                            range: range);
                    }

                    if (string.Equals(infoToken, "mermaid", StringComparison.OrdinalIgnoreCase))
                    {
                        return _nodeFactory.Create(
                            NodeType.Mermaid,
                            NodeCategory.Block,
                            text: codeText.TrimEnd('\r', '\n'),
                            range: range);
                    }

                    if (string.Equals(infoToken, "image", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(infoToken, "img", StringComparison.OrdinalIgnoreCase))
                    {
                        return _nodeFactory.Create(
                            NodeType.ImageMatrix,
                            NodeCategory.Block,
                            text: codeText.TrimEnd('\r', '\n'),
                            range: range);
                    }

                    if (string.Equals(infoToken, "qrcode", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(infoToken, "qr", StringComparison.OrdinalIgnoreCase))
                    {
                        var qrNode = _nodeFactory.Create(
                            NodeType.QrCode,
                            NodeCategory.Block,
                            text: codeText.TrimEnd('\r', '\n'),
                            range: range);

                        // Markdig 把围栏 info 拆为 Info（首词）与 Arguments（其余部分），
                        // 选项在 Arguments 中，两者合并后解析。
                        var fullInfo = string.IsNullOrWhiteSpace(codeBlock.Arguments)
                            ? codeBlock.Info
                            : codeBlock.Info + " " + codeBlock.Arguments;

                        ParseQrInfoOptions(fullInfo, qrNode);
                        return qrNode;
                    }

                    var codeNode = _nodeFactory.Create(
                        NodeType.CodeBlock,
                        NodeCategory.Block,
                        text: codeText,
                        range: range);

                    if (!string.IsNullOrWhiteSpace(infoToken))
                    {
                        codeNode.Attributes["language"] = infoToken;
                    }

                    return codeNode;
                }

            case YamlFrontMatterBlock yamlBlock:
                {
                    var yamlText = yamlBlock.Lines.ToString();

                    return _nodeFactory.Create(
                        NodeType.YamlFrontMatter,
                        NodeCategory.Block,
                        text: yamlText,
                        range: range);
                }

            case HtmlBlock htmlBlock:
                {
                    var htmlText = htmlBlock.Lines.ToString();

                    return _nodeFactory.Create(
                        NodeType.HtmlBlock,
                        NodeCategory.Block,
                        text: htmlText,
                        range: range);
                }

            case ThematicBreakBlock:
                return _nodeFactory.Create(NodeType.ThematicBreak, NodeCategory.Leaf, range: range);

            case LinkReferenceDefinitionGroup:
                return null;

            default:
                diagnostics.Add(new DiagnosticMessage
                {
                    Severity = DiagnosticSeverity.Warning,
                    Code = "UNSUPPORTED_BLOCK_TYPE",
                    Message = $"Unsupported block element: {block.GetType().FullName}",
                    Range = range
                });

                return null;
        }
    }

    /// <summary>
    /// 解析二维码围栏 info string 可选项（<c>```qrcode ecc=H border=4</c>）：
    /// ecc（别名 level）仅 L/M/Q/H；border（别名 quiet/margin）仅 0–10。非法值忽略。
    /// </summary>
    private static void ParseQrInfoOptions(string? info, MarkdownNode qrNode)
    {
        if (string.IsNullOrWhiteSpace(info))
            return;

        var tokens = info.Trim()
            .Split(InfoStringSeparators, StringSplitOptions.RemoveEmptyEntries);

        // tokens[0] 是关键字本身（qrcode/qr），从其后解析
        for (var i = 1; i < tokens.Length; i++)
        {
            var token = tokens[i];
            var eqIndex = token.IndexOf('=');
            if (eqIndex <= 0 || eqIndex >= token.Length - 1)
                continue;

            var key = token[..eqIndex];
            var val = token[(eqIndex + 1)..];

            if (key.Equals("ecc", StringComparison.OrdinalIgnoreCase)
                || key.Equals("level", StringComparison.OrdinalIgnoreCase))
            {
                if (val is "L" or "M" or "Q" or "H")
                    qrNode.Attributes["qr-ecc"] = val.ToUpperInvariant();
            }
            else if (key.Equals("border", StringComparison.OrdinalIgnoreCase)
                || key.Equals("quiet", StringComparison.OrdinalIgnoreCase)
                || key.Equals("margin", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(val, out var modules) && modules is >= 0 and <= 10)
                    qrNode.Attributes["qr-border"] = modules.ToString();
            }
        }
    }

    // ── 时间线围栏展开 ────────────────────────────────────────────────

    private sealed record TimelineItemScan(
        TimelineMarkerInfo Info,
        SourceRange Range,
        string RawMarkdown,
        int BodyStart,
        int BodyLength,
        int BodyStartLine);

    /// <summary>
    /// 把时间线围栏展开为 Timeline + TimelineItem 虚拟节点序列：@@ 行解析为
    /// 时间/标签/侧别属性，节点正文递归走完整 Markdown 管线，局部坐标平移到全文。
    /// </summary>
    private MarkdownNode BuildTimeline(
        TimelineBlock timelineBlock,
        SourceRange range,
        ICollection<DiagnosticMessage> diagnostics,
        string source)
    {
        var timeline = _nodeFactory.Create(
            NodeType.Timeline,
            NodeCategory.Block,
            text: timelineBlock.Lines.ToString().TrimEnd('\r', '\n'),
            range: range);

        var records = ScanTimelineItems(source, timelineBlock, range);

        // 默认即中轴双列：侧别缺省的节点按序左右交替，与属性面板“自动”一致。
        timeline.Attributes["timeline-layout"] = "axis";

        // 开围栏选项：line=solid|dashed、gap=2..24（仅虚线时落属性）
        var fenceInfo = TimelineMarker.ParseFenceInfo(timelineBlock.Info);
        if (fenceInfo.LineStyle == TimelineMarker.LineDashed)
        {
            timeline.Attributes["timeline-line"] = TimelineMarker.LineDashed;
            if (fenceInfo.Gap is int gap)
            {
                timeline.Attributes["timeline-gap"] = gap.ToString();
            }
        }

        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            var effectiveSide = record.Info.Side ?? (index % 2 == 0 ? "right" : "left");

            var item = _nodeFactory.Create(
                NodeType.TimelineItem,
                NodeCategory.Container,
                text: record.Info.Time,
                range: record.Range);
            item.IsSynthetic = true;
            item.RawMarkdown = record.RawMarkdown;
            item.Attributes["timeline-time"] = record.Info.Time;
            if (record.Info.Tags.Count > 0)
            {
                item.Attributes["timeline-tags"] = TimelineMarker.JoinTags(record.Info.Tags);
            }
            if (record.Info.Side is not null)
            {
                item.Attributes["timeline-side"] = record.Info.Side;
            }
            item.Attributes["timeline-side-effective"] = effectiveSide;

            if (record.BodyLength > 0)
            {
                var bodyText = source.Substring(record.BodyStart, record.BodyLength);
                var bodyDocument = _pipeline is null
                    ? Markdig.Markdown.Parse(bodyText)
                    : Markdig.Markdown.Parse(bodyText, _pipeline);

                // 正文子树坐标相对节点正文切片，统一平移到全文。
                var bodyNodes = ConvertBlocks(bodyDocument, diagnostics, bodyText);
                var bodyLineDelta = record.BodyStartLine - 1;
                foreach (var bodyNode in bodyNodes)
                {
                    ShiftTimelineBodyRange(bodyNode, record.BodyStart, bodyLineDelta);
                    item.AddChild(bodyNode);
                }
            }

            timeline.AddChild(item);
        }

        return timeline;
    }

    /// <summary>
    /// 逐行扫描围栏体，定位每个 @@ 节点在全文源码中的区间以及正文切片坐标。
    /// 未闭合围栏扫描到 EOF；首个 @@ 之前的杂散文本不产生节点。
    /// 行级语法限制：正文代码块内若出现行首 "@@ " 也会被当作节点边界。
    /// </summary>
    private static List<TimelineItemScan> ScanTimelineItems(
        string source,
        TimelineBlock block,
        SourceRange range)
    {
        var records = new List<TimelineItemScan>();

        var pos = MoveToNextLine(source, range.StartOffset);
        var lineIndex = block.Line + 1;

        var markerStart = -1;
        var markerLine = -1;
        TimelineMarkerInfo? markerInfo = null;

        void Finish(int itemEnd, int lastBodyLineIndex)
        {
            if (markerInfo is null || markerStart < 0)
            {
                return;
            }

            var rawEnd = itemEnd;
            while (rawEnd > markerStart
                && (source[rawEnd - 1] == '\n' || source[rawEnd - 1] == '\r'))
            {
                rawEnd--;
            }

            var rawMarkdown = source.Substring(markerStart, rawEnd - markerStart);

            var bodyStart = 0;
            var bodyLength = 0;
            var bodyStartLine = markerLine + 2; // 1-based：标记行下一行为正文首行
            var markerNewline = source.IndexOf('\n', markerStart);
            if (markerNewline >= 0 && markerNewline + 1 < itemEnd)
            {
                bodyStart = markerNewline + 1;
                bodyLength = itemEnd - bodyStart;
                while (bodyLength > 0
                    && (source[bodyStart + bodyLength - 1] == '\n'
                        || source[bodyStart + bodyLength - 1] == '\r'))
                {
                    bodyLength--;
                }
            }

            var column = 1;
            for (var probe = markerStart; probe < rawEnd && source[probe] == ' '; probe++)
            {
                column++;
            }

            var itemRange = new SourceRange
            {
                StartOffset = markerStart,
                Length = itemEnd - markerStart,
                StartLine = markerLine + 1,
                StartColumn = column,
                EndLine = lastBodyLineIndex + 1,
                EndColumn = 1
            };

            records.Add(new TimelineItemScan(
                markerInfo, itemRange, rawMarkdown,
                bodyStart, bodyLength, bodyStartLine));
        }

        while (pos <= source.Length)
        {
            var newlineIndex = source.IndexOf('\n', pos);
            var lineEnd = newlineIndex < 0 ? source.Length : newlineIndex;
            var contentEnd = lineEnd > pos && source[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
            var lineText = source.Substring(pos, contentEnd - pos);

            var isClosing = block.ClosingFenceLineIndex.HasValue
                && lineIndex == block.ClosingFenceLineIndex.Value;

            if (isClosing)
            {
                Finish(pos, lineIndex - 1);
                break;
            }

            if (TimelineMarker.IsMarkerLine(lineText))
            {
                // 上一节点正文止于本节点行首。
                Finish(pos, lineIndex - 1);
                markerStart = pos;
                markerLine = lineIndex;
                markerInfo = TimelineMarker.TryParse(lineText);
            }

            if (newlineIndex < 0)
            {
                Finish(source.Length, lineIndex);
                break;
            }

            pos = newlineIndex + 1;
            lineIndex++;
        }

        return records;
    }

    // 返回 fromOffset 所在行的下一行行首偏移。
    private static int MoveToNextLine(string source, int fromOffset)
    {
        var newlineIndex = source.IndexOf('\n', fromOffset);
        return newlineIndex < 0 ? source.Length : newlineIndex + 1;
    }

    // 时间线节点正文子树在正文切片内是局部坐标，统一平移为全文坐标。
    private static void ShiftTimelineBodyRange(MarkdownNode node, int offsetDelta, int lineDelta)
    {
        var current = node.Range;
        var hasRange = current.StartOffset != 0 || current.Length != 0 || current.StartLine > 1;
        if (hasRange)
        {
            node.Range = new SourceRange
            {
                StartOffset = current.StartOffset + offsetDelta,
                Length = current.Length,
                StartLine = current.StartLine + lineDelta,
                StartColumn = current.StartColumn,
                EndLine = current.EndLine + lineDelta,
                EndColumn = current.EndColumn
            };
        }

        foreach (var child in node.Children)
        {
            ShiftTimelineBodyRange(child, offsetDelta, lineDelta);
        }
    }

    private void ProcessInlines(
        ContainerInline? container,
        MarkdownNode parentNode,
        ICollection<DiagnosticMessage> diagnostics)
    {
        if (container is null)
        {
            return;
        }

        foreach (var inline in container)
        {
            var range = ExtractSourceRange(inline);

            switch (inline)
            {
                case LiteralInline literal:
                    parentNode.AddChild(
                        _nodeFactory.Create(
                            NodeType.Text,
                            NodeCategory.Inline,
                            text: literal.Content.ToString(),
                            range: range));
                    break;

                case TaskList:
                    break;

                case EmphasisInline emphasis:
                    {
                        // EmphasisExtras 行内扩展（与 MarkdownParser.UseEmphasisExtras 对应）：
                        //   ~text~  下标 <sub>，~~text~~ 删除线 <del>
                        //   ^text^  上标 <sup>，==text== 标记 <mark>，++text++ 插入 <ins>
                        var inlineType = emphasis.DelimiterChar switch
                        {
                            '~' when emphasis.DelimiterCount >= 2 => NodeType.Delete,
                            '~' => NodeType.Subscript,
                            '^' => NodeType.Superscript,
                            '=' => NodeType.Marked,
                            '+' => NodeType.Inserted,
                            '*' or '_' when emphasis.DelimiterCount >= 2 => NodeType.Strong,
                            '*' or '_' => NodeType.Emphasis,
                            _ => NodeType.Emphasis
                        };

                        var inlineNode = _nodeFactory.Create(
                            inlineType,
                            NodeCategory.Inline,
                            range: range);

                        ProcessInlines(emphasis, inlineNode, diagnostics);
                        parentNode.AddChild(inlineNode);
                        break;
                    }

                case CodeInline code:
                    parentNode.AddChild(
                        _nodeFactory.Create(
                            NodeType.InlineCode,
                            NodeCategory.Inline,
                            text: code.Content,
                            range: range));
                    break;

                case MathInline mathInline:
                    parentNode.AddChild(
                        _nodeFactory.Create(
                            NodeType.MathInline,
                            NodeCategory.Inline,
                            text: mathInline.Content.ToString(),
                            range: range));
                    break;

                case AutolinkInline autoLink:
                    AddAutoLinkNode(
                        autoLink.Url ?? string.Empty,
                        range,
                        parentNode);
                    break;

                case LinkInline link:
                    {
                        var url = (link.Url ?? string.Empty).Trim();

                        if (link.IsAutoLink)
                        {
                            AddAutoLinkNode(
                                url,
                                range,
                                parentNode);
                            break;
                        }

                        var linkType = link.IsImage
                            ? NodeType.Image
                            : NodeType.Link;

                        var linkNode = _nodeFactory.Create(
                            linkType,
                            NodeCategory.Inline,
                            range: range);

                        if (link.IsImage)
                        {
                            linkNode.Attributes["src"] = url;
                            linkNode.Attributes["url"] = url;

                            ProcessInlines(
                                link,
                                linkNode,
                                diagnostics);
                        }
                        else
                        {
                            linkNode.Attributes["href"] = url;
                            linkNode.Attributes["url"] = url;

                            ProcessInlines(
                                link,
                                linkNode,
                                diagnostics);

                            linkNode.Text = GetInlineText(linkNode);

                            if (string.IsNullOrEmpty(linkNode.Text))
                            {
                                linkNode.Text = url;
                            }
                        }

                        parentNode.AddChild(linkNode);
                        break;
                    }

                case LineBreakInline:
                    parentNode.AddChild(
                        _nodeFactory.Create(
                            NodeType.LineBreak,
                            NodeCategory.Inline,
                            range: range));
                    break;

                case FootnoteLink footnoteLink:
                    // Markdig 会在脚注内容尾部自带返回链接（IsBackLink），
                    // 锚点规则与本系统不一致；统一跳过，由脚注渲染按
                    // backrefs 逐处追加，保证每处引用都有正确返回入口。
                    if (footnoteLink.IsBackLink)
                    {
                        break;
                    }

                    {
                        var linkNode = _nodeFactory.Create(
                            NodeType.FootnoteLink,
                            NodeCategory.Inline,
                            range: range);

                        // 显示文本用脚注的原始标签；同一脚注的多次引用各自
                        // 带唯一锚点（第 2 处起加序号后缀），href 指向同一脚注。
                        var footnote = footnoteLink.Footnote;
                        var label = ExtractFootnoteLabel(footnote.Label);
                        var slug = BuildFootnoteSlug(label, footnote.Order);
                        var occurrence = footnote.Links.IndexOf(footnoteLink);
                        if (occurrence < 0)
                        {
                            occurrence = 0;
                        }

                        var anchor = BuildFootnoteRefAnchor(slug, occurrence);

                        linkNode.Attributes["id"] = anchor;
                        linkNode.Attributes["href"] = "#fn-" + slug;
                        linkNode.Attributes["anchor"] = anchor;
                        linkNode.Attributes["slug"] = slug;
                        linkNode.Text = label;

                        parentNode.AddChild(linkNode);
                        break;
                    }

                default:
                    if (inline is ContainerInline childContainer)
                    {
                        ProcessInlines(childContainer, parentNode, diagnostics);
                    }
                    else
                    {
                        diagnostics.Add(new DiagnosticMessage
                        {
                            Severity = DiagnosticSeverity.Warning,
                            Code = "UNSUPPORTED_INLINE_TYPE",
                            Message = $"Unsupported inline element: {inline.GetType().Name}",
                            Range = range
                        });
                    }
                    break;
            }
        }
    }

    private static readonly System.Text.RegularExpressions.Regex CustomBlockLabelRegex =
        new(
            @"^(?<kind>[A-Za-z][A-Za-z0-9_-]{0,20})[ \t]*:[ \t]*(?<body>.*)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool TryClassifyCustomBlock(MarkdownNode quoteNode)
    {
        var firstParagraph = quoteNode.Children
            .FirstOrDefault(child => child.Type == NodeType.Paragraph);
        if (firstParagraph is null)
        {
            return false;
        }

        var firstLine = ReadFirstLineText(firstParagraph);
        if (firstLine is null)
        {
            return false;
        }

        var match = CustomBlockLabelRegex.Match(firstLine);
        if (!match.Success)
        {
            return false;
        }

        if (!AdmonitionCatalog.TryMapCustomBlockLabel(match.Groups["kind"].Value, out var kind))
        {
            return false;
        }

        quoteNode.Type = NodeType.Custom;
        quoteNode.Category = NodeCategory.Block;
        quoteNode.Attributes["custom-kind"] = kind;

        ConsumeInlinePrefix(firstParagraph, match.Groups["body"].Index);
        RemoveLeadingLineBreaks(firstParagraph);

        firstParagraph.Text = BuildInlineText(firstParagraph.Children);

        return true;
    }

    private static void ConsumeInlinePrefix(MarkdownNode firstParagraph, int remaining)
    {
        while (remaining > 0 && firstParagraph.Children.Count > 0)
        {
            var child = firstParagraph.Children[0];

            if (child.Type == NodeType.LineBreak || child.Type != NodeType.Text)
            {
                break;
            }

            if (child.Text.Length <= remaining)
            {
                remaining -= child.Text.Length;
                firstParagraph.RemoveChild(child);
            }
            else
            {
                child.Text = child.Text.Substring(remaining);
                remaining = 0;
            }
        }
    }

    private static void RemoveLeadingLineBreaks(MarkdownNode firstParagraph)
    {
        while (firstParagraph.Children.Count > 0 &&
               firstParagraph.Children[0].Type == NodeType.LineBreak)
        {
            firstParagraph.RemoveChild(firstParagraph.Children[0]);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex AdmonitionLabelRegex =
        new(
            @"^\[!(?<label>[A-Za-z][A-Za-z0-9_-]{0,20})\](?:[ \t]+(?<title>.+?))?[ \t]*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool TryClassifyAdmonition(MarkdownNode quoteNode)
    {
        var firstParagraph = quoteNode.Children
            .FirstOrDefault(child => child.Type == NodeType.Paragraph);
        if (firstParagraph is null)
        {
            return false;
        }

        var firstLine = ReadFirstLineText(firstParagraph);
        if (firstLine is null)
        {
            return false;
        }

        var match = AdmonitionLabelRegex.Match(firstLine);
        if (!match.Success)
        {
            return false;
        }

        if (!AdmonitionCatalog.TryMapAdmonitionLabel(match.Groups["label"].Value, out var kind))
        {
            return false;
        }

        var title = match.Groups["title"].Success
            ? match.Groups["title"].Value.Trim()
            : AdmonitionCatalog.GetDefaultTitle(kind);

        quoteNode.Attributes["admonition"] = kind;
        quoteNode.Attributes["admonition-title"] = title;

        ConsumeInlinePrefix(firstParagraph, match.Length);
        RemoveLeadingLineBreaks(firstParagraph);

        return true;
    }

    private static string? ReadFirstLineText(MarkdownNode paragraph)
    {
        var builder = new System.Text.StringBuilder();

        foreach (var child in paragraph.Children)
        {
            if (child.Type == NodeType.LineBreak)
            {
                break;
            }

            if (child.Type == NodeType.Text)
            {
                builder.Append(child.Text);
            }
        }

        var line = builder.ToString();
        return line.Length == 0 ? null : line;
    }

    private static SourceRange ExtractSourceRange(MarkdownObject? obj)
    {
        if (obj == null || obj.Span.Start < 0)
        {
            return SourceRange.Empty();
        }

        var startOffset = Math.Max(0, obj.Span.Start);
        var length = Math.Max(0, obj.Span.Length);
        var startLine = Math.Max(1, obj.Line + 1);
        var startColumn = Math.Max(1, obj.Column + 1);

        int endLine = startLine;
        int endColumn = startColumn + length;

        if (obj is ImageMatrixBlock { ClosingFenceLineIndex: >= 0 } matrixFence)
        {
            endLine = matrixFence.ClosingFenceLineIndex.Value + 1;
        }
        else if (obj is PoemBlock { ClosingFenceLineIndex: >= 0 } poemFence)
        {
            endLine = poemFence.ClosingFenceLineIndex.Value + 1;
        }
        else if (obj is TimelineBlock { ClosingFenceLineIndex: >= 0 } timelineFence)
        {
            endLine = timelineFence.ClosingFenceLineIndex.Value + 1;
        }
        else if (obj is LeafBlock leafBlock && leafBlock.Lines.Lines != null && leafBlock.Lines.Count > 0)
        {
            endLine = startLine + leafBlock.Lines.Count - 1;
            var lastSlice = leafBlock.Lines.Lines[leafBlock.Lines.Count - 1];
            endColumn = lastSlice.Slice.Length + 1;
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

    private static void PopulateRawMarkdown(MarkdownNode node, string source)
    {
        if (string.IsNullOrEmpty(node.RawMarkdown))
        {
            var raw = ExtractRaw(source, node.Range);
            if (!string.IsNullOrEmpty(raw))
            {
                node.RawMarkdown = raw;
            }
        }

        foreach (var child in node.Children)
        {
            PopulateRawMarkdown(child, source);
        }
    }

    private static string ExtractRaw(string source, SourceRange range)
    {
        if (range.StartOffset < 0 || range.Length <= 0 || range.StartOffset >= source.Length)
        {
            return string.Empty;
        }

        var length = Math.Min(range.Length, source.Length - range.StartOffset);
        return source.AsSpan(range.StartOffset, length).TrimEnd('\n').TrimEnd('\r').ToString();
    }

    private static bool TryGetTaskState(
        ListItemBlock listItem,
        out bool isChecked)
    {
        isChecked = false;

        foreach (var block in listItem)
        {
            if (block is not ParagraphBlock paragraph || paragraph.Inline is null)
            {
                continue;
            }

            foreach (var inline in paragraph.Inline)
            {
                if (inline is TaskList taskList)
                {
                    isChecked = taskList.Checked;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool LooksLikeEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var at = value.LastIndexOf('@');
        return at > 0
            && at < value.Length - 1
            && value.IndexOf(' ') < 0
            && value.IndexOf('/') < 0
            && value[(at + 1)..].Contains('.');
    }

    private void AddAutoLinkNode(
        string rawUrl,
        SourceRange range,
        MarkdownNode parentNode)
    {
        var normalizedUrl = (rawUrl ?? string.Empty).Trim();
        var hrefValue = normalizedUrl;
        var displayValue = normalizedUrl;

        if (normalizedUrl.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            displayValue = normalizedUrl["mailto:".Length..];
        }
        else if (LooksLikeEmail(normalizedUrl))
        {
            hrefValue = $"mailto:{normalizedUrl}";
            displayValue = normalizedUrl;
        }

        var autoLinkNode = _nodeFactory.Create(
            NodeType.Link,
            NodeCategory.Inline,
            range: range);

        autoLinkNode.Attributes["href"] = hrefValue;
        autoLinkNode.Attributes["url"] = hrefValue;
        autoLinkNode.Text = displayValue;

        parentNode.AddChild(autoLinkNode);
    }

    private static string GetInlineText(MarkdownNode node) =>
        node.Type == NodeType.Text || !string.IsNullOrEmpty(node.Text)
            ? node.Text
            : BuildInlineText(node.Children);

    private static string BuildInlineText(IList<MarkdownNode> roots)
    {
        if (roots.Count == 0)
            return string.Empty;

        var stack = new List<MarkdownNode>(roots.Count);
        for (int i = roots.Count - 1; i >= 0; i--)
            stack.Add(roots[i]);

        var builder = new StringBuilder();
        while (stack.Count > 0)
        {
            int lastIndex = stack.Count - 1;
            var current = stack[lastIndex];
            stack.RemoveAt(lastIndex);

            if (current.Type == NodeType.Text || !string.IsNullOrEmpty(current.Text))
            {
                builder.Append(current.Text);
                continue;
            }

            for (int i = current.Children.Count - 1; i >= 0; i--)
                stack.Add(current.Children[i]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 把古诗块正文解析为可展开的虚拟子部件（标题/作者/朝代/体裁 +
    /// 诗句组/诗句 + 出处/说明）。子节点 IsSynthetic、共享古诗块源码区间，
    /// 仅承载各自 LocalStyle；解析规则与预览渲染保持一致。
    /// </summary>
    private void BuildPoemParts(MarkdownNode poemNode)
    {
        var normalized = (poemNode.Text ?? string.Empty).Replace("\r\n", "\n");

        SplitPoemSections(normalized, out var metadataText, out var bodyText);
        var metadata = ParsePoemMetadata(metadataText);
        var partRange = poemNode.Range;

        MarkdownNode CreatePart(NodeType type, NodeCategory category, string text)
        {
            var part = _nodeFactory.Create(type, category, text: text, range: partRange);
            part.IsSynthetic = true;
            return part;
        }

        void AddField(NodeType type, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                poemNode.AddChild(CreatePart(type, NodeCategory.Inline, value!));
        }

        AddField(NodeType.PoemTitle, GetMeta(metadata, "title"));
        AddField(NodeType.PoemAuthor, GetMeta(metadata, "author"));
        AddField(NodeType.PoemDynasty, GetMeta(metadata, "dynasty"));
        AddField(NodeType.PoemForm, GetMeta(metadata, "form"));

        var stanzaStarted = false;
        MarkdownNode? stanza = null;
        foreach (var raw in bodyText.Split('\n'))
        {
            if (raw.Length == 0)
            {
                stanzaStarted = false;
                stanza = null;
                continue;
            }

            if (!stanzaStarted)
            {
                stanza = CreatePart(NodeType.PoemStanza, NodeCategory.Container, string.Empty);
                poemNode.AddChild(stanza);
                stanzaStarted = true;
            }

            stanza!.AddChild(CreatePart(NodeType.PoemLine, NodeCategory.Inline, raw));
        }

        AddField(NodeType.PoemSource, GetMeta(metadata, "source"));
        AddField(NodeType.PoemNote, GetMeta(metadata, "note"));
    }

    private static string? GetMeta(Dictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// 与预览渲染一致的元数据/正文切分：以首个单独的 "---" 行为界。
    /// </summary>
    private static void SplitPoemSections(
        string normalized,
        out string metadataText,
        out string bodyText)
    {
        metadataText = string.Empty;
        bodyText = string.Empty;

        if (normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            bodyText = normalized.Substring(4);
            return;
        }

        if (normalized == "---")
            return;

        var sepIdx = normalized.IndexOf("\n---\n", StringComparison.Ordinal);
        if (sepIdx >= 0)
        {
            metadataText = normalized.Substring(0, sepIdx);
            bodyText = normalized.Substring(sepIdx + 5);
            return;
        }

        sepIdx = normalized.IndexOf("\n---", StringComparison.Ordinal);
        if (sepIdx >= 0)
        {
            metadataText = normalized.Substring(0, sepIdx);
            bodyText = sepIdx + 4 < normalized.Length
                ? normalized.Substring(sepIdx + 4)
                : string.Empty;
            return;
        }

        bodyText = normalized;
    }

    private static Dictionary<string, string> ParsePoemMetadata(string metadataText)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(metadataText))
            return result;

        foreach (var raw in metadataText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0)
                continue;

            var key = line.Substring(0, colonIdx).Trim();
            var value = line.Substring(colonIdx + 1).Trim();
            if (key.Length > 0)
                result[key] = value;
        }

        return result;
    }

    // ── 脚注标签 / slug ──────────────────────────────────────────────

    // Markdig 的 Footnote.Label 带前导 '^'（如 "^42"），去掉它得到用户
    // 原始标签，作为正文角标与脚注列表共同显示的文本。
    private static string ExtractFootnoteLabel(string? markdigLabel)
    {
        var label = (markdigLabel ?? string.Empty).Trim();
        if (label.Length > 0 && label[0] == '^')
        {
            label = label[1..];
        }

        return label.Length > 0 ? label : "note";
    }

    // 锚点 slug：Unicode 单词字符（含数字、CJK）与 '-' 保留，其余字符统一
    // 替换为 '-'；发生过替换时追加脚注 Order 后缀以防不同标签撞 slug。
    // 纯单词标签（绝大多数场景）无损直用。
    private static string BuildFootnoteSlug(string label, int order)
    {
        var slug = new StringBuilder(label.Length);
        var replaced = false;
        foreach (var ch in label)
        {
            if (IsSlugChar(ch))
            {
                slug.Append(ch);
            }
            else
            {
                slug.Append('-');
                replaced = true;
            }
        }

        if (!replaced)
        {
            return slug.ToString();
        }

        slug.Append("-o").Append(order);
        return slug.ToString();
    }

    private static bool IsSlugChar(char ch)
    {
        if (ch == '-')
        {
            return true;
        }

        var category = char.GetUnicodeCategory(ch);
        return category
            is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.ConnectorPunctuation;
    }

    // 正文引用锚点：第 1 处 fnref-{slug}，第 2 处起 fnref-{slug}-{n+1}，
    // 保证同一脚注多处引用时 HTML id 唯一、脚注返回链接各自可寻址。
    private static string BuildFootnoteRefAnchor(string slug, int occurrence) =>
        occurrence == 0 ? "fnref-" + slug : "fnref-" + slug + "-" + (occurrence + 1);
}
