using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using System.Globalization;
using System.Net;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax.Factories;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.SyntaxTakeover;
using System.Text;
using System.Text.RegularExpressions;
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
    private readonly UserBlockRegistry? _userBlocks;
    private readonly FenceKeywordRegistry _fenceKeywords;
    private readonly InlineHtmlPolicy? _inlineHtml;
    private readonly InlineHtmlPolicy? _blockHtml;

    // 本次转换可见的脚注定义（标签 → 定义块），用于展开 [^1,2] 这类多引用。
    private readonly Dictionary<string, Footnote> _footnotesByLabel = new(StringComparer.OrdinalIgnoreCase);

    public MarkdigToNodeConverter(
        NodeFactory? nodeFactory = null,
        UserBlockRegistry? userBlocks = null,
        FenceKeywordRegistry? fenceKeywords = null,
        InlineHtmlDefinition? inlineHtml = null,
        InlineHtmlDefinition? blockHtml = null)
    {
        _nodeFactory = nodeFactory ?? new NodeFactory();
        _userBlocks = userBlocks;
        _fenceKeywords = fenceKeywords ?? FenceKeywordRegistry.BuiltIn;
        _inlineHtml = inlineHtml is null || inlineHtml.IsEmpty
            ? null
            : new InlineHtmlPolicy(inlineHtml);

        // 块级不按 IsEmpty 关通道：空词表 = “一条标签都不放行”，标签就地展开、文字保留；
        // 若这里回落 null，模板是 content="children" 就会连文字一起丢。
        _blockHtml = blockHtml is null ? null : new InlineHtmlPolicy(blockHtml);
    }

    internal List<MarkdownNode> ConvertBlocks(
        ContainerBlock rootContainer,
        ICollection<DiagnosticMessage> diagnostics,
        string source)
    {
        ArgumentNullException.ThrowIfNull(rootContainer);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(source);

        if (!ParsedDocumentPipeline.IsEnriched(rootContainer))
        {
            throw new InvalidOperationException(
                "Markdig 文档未经 ParsedDocumentPipeline 的解析侧标记，禁止直接转换；请改走 ParsedDocumentPipeline.ParseAndConvert。");
        }

        CollectFootnoteDefinitions(rootContainer);
        var resultNodes = ConvertChildren(rootContainer, diagnostics, source);

        // 预处理器为让定义进入 FootnoteGroup 追加的隐藏引用段落只用于解析，
        // 不进文档树，否则会多出可见段落与多余回链。
        resultNodes.RemoveAll(IsHiddenFootnoteCarrier);

        foreach (var node in resultNodes)
        {
            PopulateRawMarkdown(node, source);
        }

        // 脚注角标锚点与回链要覆盖「Markdig 原生引用 + 逗号展开出来的引用」，
        // 统一在整棵转换完成后按文档顺序编号，避免两套编号互相撞号。
        AssignFootnoteAnchors(resultNodes);

        return resultNodes;
    }

    private static bool IsHiddenFootnoteCarrier(MarkdownNode node)
    {
        if (node.Type != NodeType.Paragraph || node.Children.Count == 0)
        {
            return false;
        }

        var first = node.Children[0];
        return first.Type == NodeType.Text
            && first.Text is { Length: > 0 } text
            && text[0] == FootnoteReferencePreprocessor.HiddenMarker;
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
                    if (UnclosedBlockMarker.IsUnclosed(block))
                    {
                        node.SetDerivedAttribute(MarkdownAttributes.Unclosed, "true");
                        if (TryGetUnclosedFenceLabel(block, out var fenceLabel))
                        {
                            diagnostics.Add(new DiagnosticMessage
                            {
                                Severity = DiagnosticSeverity.Warning,
                                Code = "FENCED_BLOCK_UNCLOSED",
                                Message = $"{fenceLabel}围栏未闭合，已按到文档末尾处理；补上闭合引号行后恢复。",
                                Range = node.Range
                            });
                        }
                    }

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
                    Range = BlockRangeProjector.Get(block)
                });
            }
        }

        return resultNodes;
    }

    /// <summary>未闭合围栏块的中文名，用于诊断文案；其余块不额外报诊断。</summary>
    private static bool TryGetUnclosedFenceLabel(Block block, out string label)
    {
        switch (block)
        {
            case CollapseBlock:
                label = "折叠块";
                return true;
            case TocpicBlock:
                label = "目录块";
                return true;
            case ColorBlock:
                label = "颜色块";
                return true;
            default:
                label = string.Empty;
                return false;
        }
    }

    /// <summary>
    /// 嵌套围栏的显式降级提示：内层引号数必须少于外层，否则内层的闭合行会先被外层看到，
    /// 提前结束外层并把后续内容甩到容器之外。只提示，不改变解析结果（内容仍按正文保留）。
    /// </summary>
    private static void ReportNestedFenceMismatch(
        Block block,
        SourceRange range,
        ICollection<DiagnosticMessage> diagnostics)
    {
        var outerCount = EnclosingFenceCount(block);
        if (outerCount < 3)
        {
            return;
        }

        var innerCount = block switch
        {
            CollapseBlock collapse => collapse.FenceCount,
            TocpicBlock tocpic => tocpic.FenceCount,
            ColorBlock color => color.FenceCount,
            _ => 0,
        };
        if (innerCount < 3 || innerCount < outerCount)
        {
            return;
        }

        diagnostics.Add(new DiagnosticMessage
        {
            Severity = DiagnosticSeverity.Warning,
            Code = "FENCED_BLOCK_NESTING_INVALID",
            Message = $"内层围栏用了 {innerCount} 个引号、外层只有 {outerCount} 个：内层闭合行会提前结束外层。" +
                      "嵌套时外层引号数必须多于内层（例如外层 4 个、内层 3 个）。",
            Range = range,
        });
    }

    /// <summary>
    /// 被拒绝的同级/更长开围栏会退化成正文段落（例如折叠块里的 <c>'''collapse</c>）。
    /// 段落首行以 3 个以上引号开头且不少于外层时补一条同样的警告，避免用户以为嵌套生效了。
    /// </summary>
    private static void ReportRejectedNestedFence(
        Block block,
        string text,
        SourceRange range,
        ICollection<DiagnosticMessage> diagnostics)
    {
        var outerCount = EnclosingFenceCount(block);
        if (outerCount < 3)
        {
            return;
        }

        var line = text;
        var newline = line.IndexOfAny(['\r', '\n']);
        if (newline >= 0)
        {
            line = line[..newline];
        }

        line = line.TrimStart();
        var quoteCount = 0;
        while (quoteCount < line.Length && line[quoteCount] == '\'')
        {
            quoteCount++;
        }

        if (quoteCount < 3 || quoteCount < outerCount)
        {
            return;
        }

        var rest = line[quoteCount..].TrimStart();
        if (rest.Length == 0 || (!char.IsLetter(rest[0]) && rest[0] != '-'))
        {
            return;
        }

        diagnostics.Add(new DiagnosticMessage
        {
            Severity = DiagnosticSeverity.Warning,
            Code = "FENCED_BLOCK_NESTING_INVALID",
            Message = $"内层围栏用了 {quoteCount} 个引号、外层只有 {outerCount} 个，无法嵌套：" +
                      "内层开围栏已按正文原样保留，内层闭合行会提前结束外层。" +
                      $"请把外层改成 {quoteCount + 1} 个引号，或把内层减到 {outerCount - 1} 个。",
            Range = range,
        });
    }

    private static int EnclosingFenceCount(Block block) => block.Parent switch
    {
        CollapseBlock collapse => collapse.FenceCount,
        TocpicBlock tocpic => tocpic.FenceCount,
        _ => 0,
    };

    /// <summary>块内列表的最大嵌套层数；无序、有序列表同等计入。</summary>
    private static int ComputeListDepth(IReadOnlyList<MarkdownNode> nodes)
    {
        var depth = 0;
        foreach (var node in nodes)
        {
            // 只沿“列表 → 列表项 → 子列表”这条链数层级；
            // 列表项里嵌的折叠/引用等容器不参与目录深度。
            var current = node.Type switch
            {
                NodeType.UnorderedList or NodeType.OrderedList => 1 + ComputeListDepth(node.Children),
                NodeType.ListItem or NodeType.TaskListItem => ComputeListDepth(node.Children),
                _ => 0,
            };
            if (current > depth)
            {
                depth = current;
            }
        }

        return depth;
    }

    private MarkdownNode? ConvertBlock(
        Block block,
        ICollection<DiagnosticMessage> diagnostics,
        string source)
    {
        var range = BlockRangeProjector.Get(block);

        ReportNestedFenceMismatch(block, range, diagnostics);

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
                ReportRejectedNestedFence(paragraph, paragraphNode.Text, range, diagnostics);
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
                        range: BlockRangeProjector.Get(listItem));

                    if (isTaskItem)
                    {
                        itemNode.SetDerivedAttribute("checked", isChecked ? "true" : "false");
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
                {
                    var quoteChildren = ConvertChildren(quoteBlock, diagnostics, source);

                    // 类型在节点创建前定下，此后不再改 Type/Category。
                    var classification = QuoteLabelClassifier.Classify(ReadFirstLineText(quoteChildren));
                    var quoteType = classification.Kind == QuoteLabelKind.CustomBlock
                        ? NodeType.Custom
                        : NodeType.Quote;
                    var quoteCategory = classification.Kind == QuoteLabelKind.CustomBlock
                        ? NodeCategory.Block
                        : NodeCategory.Container;

                    var quoteNode = _nodeFactory.Create(quoteType, quoteCategory, range: range);
                    foreach (var child in quoteChildren)
                    {
                        quoteNode.AddChild(child);
                    }

                    ApplyQuoteLabel(quoteNode, quoteChildren, classification);
                    return quoteNode;
                }

            case CollapseBlock collapseBlock:
                {
                    var collapseNode = _nodeFactory.Create(
                        NodeType.Collapse,
                        NodeCategory.Container,
                        range: range);

                    var collapseTitle = string.IsNullOrWhiteSpace(collapseBlock.Title)
                        ? "详细信息"
                        : collapseBlock.Title.Trim();
                    collapseNode.SetDerivedAttribute("collapse-title", collapseTitle);
                    if (collapseBlock.Open)
                    {
                        // 只有显式 open 才落属性：缺省即收起，渲染层据此输出裸 open 属性。
                        collapseNode.SetDerivedAttribute("collapse-open", "true");
                    }

                    collapseNode.Text = collapseTitle;

                    var collapseChildren = ConvertChildren(collapseBlock, diagnostics, source);
                    foreach (var child in collapseChildren)
                    {
                        collapseNode.AddChild(child);
                    }

                    return collapseNode;
                }

            case TocpicBlock tocpicBlock:
                {
                    var tocpicNode = _nodeFactory.Create(
                        NodeType.Tocpic,
                        NodeCategory.Container,
                        range: range);

                    var tocpicChildren = ConvertChildren(tocpicBlock, diagnostics, source);
                    foreach (var child in tocpicChildren)
                    {
                        tocpicNode.AddChild(child);
                    }

                    tocpicNode.Text = GetInlineText(tocpicNode);

                    // 目录深度＝块内列表的最大嵌套层数（无序/有序都算），供属性面板只读项展示。
                    var tocDepth = ComputeListDepth(tocpicChildren);
                    if (tocDepth > 0)
                    {
                        tocpicNode.SetDerivedAttribute("toc-depth", tocDepth.ToString(CultureInfo.InvariantCulture));
                    }

                    return tocpicNode;
                }

            case Table tableBlock:
                var tableNode = _nodeFactory.Create(
                    NodeType.Table,
                    NodeCategory.Container,
                    range: range);

                // 列对齐：Markdig 会把分隔行的 :--- / ---: / :---: 解析到 ColumnDefinitions。
                // 渲染层需要每个单元格知道自己的对齐方式，这里在解析期直接下推到 TableCell 节点。
                var columnAligns = tableBlock.ColumnDefinitions;

                foreach (var rowObj in tableBlock)
                {
                    if (rowObj is not TableRow rowBlock)
                    {
                        continue;
                    }

                    var rowNode = _nodeFactory.Create(
                        NodeType.TableRow,
                        NodeCategory.Container,
                        range: BlockRangeProjector.Get(rowBlock));

                    int colIndex = 0;
                    foreach (var cellObj in rowBlock)
                    {
                        if (cellObj is not TableCell cellBlock)
                        {
                            continue;
                        }

                        var cellNode = _nodeFactory.Create(
                            NodeType.TableCell,
                            NodeCategory.Container,
                            range: BlockRangeProjector.Get(cellBlock));

                        cellNode.IsTableHeader = rowBlock.IsHeader;

                        // 把该单元格所在列的对齐方式记下来：渲染时据此加 text-align。
                        if (columnAligns is not null
                            && colIndex < columnAligns.Count)
                        {
                            var align = columnAligns[colIndex].Alignment switch
                            {
                                TableColumnAlign.Left => "left",
                                TableColumnAlign.Right => "right",
                                TableColumnAlign.Center => "center",
                                _ => null
                            };
                            if (align is not null)
                            {
                                cellNode.SetDerivedAttribute("column-align", align);
                            }
                        }

                        var cellChildren = ConvertChildren(cellBlock, diagnostics, source);
                        foreach (var child in cellChildren)
                        {
                            cellNode.AddChild(child);
                        }

                        cellNode.Text = GetInlineText(cellNode);
                        rowNode.AddChild(cellNode);
                        colIndex++;
                    }

                    // 表格行内的裸 `||` 会先被表格切成空单元格，剧透无法成立。
                    // 只看源文本是否真含裸 `||`：合法空单元格（`| a |  | b |`）本来就没有 `||`，
                    // 不能因为单元格文本为空就误报；`\|\|` 是作者显式转义，也不算裸管道。
                    // 这里只提示、不改写原文：改写会改变作者声明的列数。
                    var rowRange = BlockRangeProjector.Get(rowBlock);
                    if (!rowBlock.IsHeader && ContainsBareDoublePipe(ExtractRaw(source, rowRange)))
                    {
                        diagnostics.Add(new DiagnosticMessage
                        {
                            Severity = DiagnosticSeverity.Warning,
                            Code = "SPOILER_PIPE_IN_TABLE",
                            Message = "表格行内的裸 `||` 已按列分隔处理；如需剧透请写成 `\\|\\|内容\\|\\|`。",
                            Range = rowRange
                        });
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
                            range: BlockRangeProjector.Get(footnoteBlock));

                        // 保留用户原始标签作为显示文本（数字 42、字母 alpha 等），
                        // 锚点用确定性 slug；多处引用时 backrefs 记录每个引用的锚点。
                        var label = ExtractFootnoteLabel(footnoteBlock.Label);
                        var slug = BuildFootnoteSlug(label, footnoteBlock.Order);

                        footnoteNode.SetDerivedAttribute("label", label);
                        footnoteNode.SetDerivedAttribute("slug", slug);
                        footnoteNode.SetDerivedAttribute("id", "fn-" + slug);

                        var children = ConvertChildren(footnoteBlock, diagnostics, source);
                        foreach (var child in children)
                        {
                            footnoteNode.AddChild(child);
                        }

                        groupNode.AddChild(footnoteNode);
                    }

                    return groupNode;
                }

            case UserBlock userBlock:
                return BuildUserBlock(userBlock, diagnostics, source);

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
                    poemNode.SetDerivedAttribute("poem-type", poemBlock.PoemType.ToString());
                    poemNode.SetDerivedAttribute("poem-alias", poemBlock.Alias);
                    ApplyPoemLayout(poemNode, poemBlock.Layout);
                    return poemNode;
                }

            case TimelineBlock timelineBlock:
                return BuildTimeline(timelineBlock, range, diagnostics);

            case RadarBlock radarBlock:
                return BuildRadar(radarBlock, range);

            case ColorBlock colorBlock:
                return _nodeFactory.Create(
                    NodeType.ColorBlock,
                    NodeCategory.Block,
                    text: colorBlock.Lines.ToString().TrimEnd('\r', '\n'),
                    range: range);

            case FencedCodeBlock codeBlock:
                {
                    var codeText = codeBlock.Lines.ToString();

                    var infoToken = codeBlock.Info?
                        .Trim()
                        .Split(InfoStringSeparators, StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault();

                    var fenceEntry = _fenceKeywords.ResolveExact(FenceFamily.CodeFence, infoToken);

                    if (fenceEntry?.Target is FenceTarget.Math)
                    {
                        return _nodeFactory.Create(
                            NodeType.MathBlock,
                            NodeCategory.Block,
                            text: codeText.TrimEnd('\r', '\n'),
                            range: range);
                    }

                    if (fenceEntry?.Target is FenceTarget.Mermaid)
                    {
                        return _nodeFactory.Create(
                            NodeType.Mermaid,
                            NodeCategory.Block,
                            text: codeText.TrimEnd('\r', '\n'),
                            range: range);
                    }

                    if (fenceEntry?.Target is FenceTarget.ImageMatrix)
                    {
                        return _nodeFactory.Create(
                            NodeType.ImageMatrix,
                            NodeCategory.Block,
                            text: codeText.TrimEnd('\r', '\n'),
                            range: range);
                    }

                    if (fenceEntry?.Target is FenceTarget.QrCode)
                    {
                        var qrNode = _nodeFactory.Create(
                            NodeType.QrCode,
                            NodeCategory.Block,
                            text: codeText.TrimEnd('\r', '\n'),
                            range: range);

                        // Markdig 把围栏 info 拆为 Info（首词）与 Arguments（其余部分），
                        // 选项解析器负责合并；产物按源码顺序落属性，顺序影响渲染字节。
                        foreach (var option in QrInfoOptionsParser.Parse(codeBlock.Info, codeBlock.Arguments))
                        {
                            qrNode.SetDerivedAttribute(option.Key, option.Value);
                        }

                        return qrNode;
                    }

                    var codeNode = _nodeFactory.Create(
                        NodeType.CodeBlock,
                        NodeCategory.Block,
                        text: codeText,
                        range: range);

                    if (!string.IsNullOrWhiteSpace(infoToken))
                    {
                        codeNode.SetDerivedAttribute("language", infoToken);
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

            case HtmlBlock htmlBlock when htmlBlock.Type == HtmlBlockType.Comment:
                // 注释不是内容：整块丢弃，源码仍保留在编辑区。
                return null;

            case HtmlBlock htmlBlock:
                {
                    var htmlText = htmlBlock.Lines.ToString();
                    var node = _nodeFactory.Create(
                        NodeType.HtmlBlock,
                        NodeCategory.Block,
                        text: htmlText,
                        range: range);

                    PopulateHtmlBlock(node, htmlText, range, diagnostics);

                    return node;
                }

            case DividerBlock dividerBlock:
                {
                    var dividerNode = _nodeFactory.Create(
                        NodeType.Divider,
                        NodeCategory.Leaf,
                        text: dividerBlock.Label,
                        range: range);

                    dividerNode.SetDerivedAttribute(DividerSyntax.TextAttribute, dividerBlock.Label);
                    dividerNode.SetDerivedAttribute(
                        DividerSyntax.LineAttribute,
                        dividerBlock.Dashed ? DividerSyntax.LineDashed : DividerSyntax.LineSolid);
                    return dividerNode;
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
    /// 用户块落到 <see cref="NodeType.Custom"/>：custom-kind 取定义 Id，
    /// 行内 key=value 原样落属性，EOF 未闭合额外落 unclosed。
    /// 定义声明的属性约束（默认值/取值/必填/未知键）与内容模型在此生效。
    /// </summary>
    private MarkdownNode BuildUserBlock(
        UserBlock userBlock,
        ICollection<DiagnosticMessage> diagnostics,
        string source)
    {
        var node = _nodeFactory.Create(
            NodeType.Custom,
            NodeCategory.Container,
            range: userBlock.Range);

        node.Text = userBlock.Header;

        var headerLine = Math.Max(1, userBlock.Line + 1);
        var headerColumn = Math.Max(1, userBlock.Column + 1);
        var headerRange = new SourceRange
        {
            StartOffset = Math.Max(0, userBlock.Span.Start),
            Length = userBlock.Header.Length,
            StartLine = headerLine,
            StartColumn = headerColumn,
            EndLine = headerLine,
            EndColumn = headerColumn + userBlock.Header.Length
        };

        var sourceAttributes = new List<KeyValuePair<string, string>>(userBlock.Attributes.Count);
        foreach (var attribute in userBlock.Attributes)
        {
            // 派生键由解析结果决定，不接受源码覆盖。
            if (attribute.Key is "custom-kind" or MarkdownAttributes.Unclosed)
            {
                continue;
            }
            // 来源标记 Source：这是源码内联值，重解析后优先级最高。
            node.SetSourceAttribute(attribute.Key, attribute.Value);
            sourceAttributes.Add(attribute);
        }

        UserBlockConstraintValidator.ApplyAttributes(
            userBlock.Definition,
            sourceAttributes,
            node,
            diagnostics,
            headerRange);

        node.SetDerivedAttribute("custom-kind", userBlock.Definition.Id);

        if (!userBlock.Closed)
        {
            node.SetDerivedAttribute(MarkdownAttributes.Unclosed, "true");
        }

        foreach (var child in ConvertChildren(userBlock, diagnostics, source))
        {
            node.AddChild(child);
        }

        UserBlockConstraintValidator.ValidateContent(userBlock.Definition, node, diagnostics);

        return node;
    }

    // ── 时间线围栏展开 ────────────────────────────────────────────────

    /// <summary>
    /// 把雷达图围栏转为 Radar 容器节点：表格原文存于 node.Text，开围栏选项落属性，
    /// 渲染时再解析表格并生成内联 SVG（与二维码同思路，不拆子节点）。
    /// </summary>
    private MarkdownNode BuildRadar(RadarBlock radarBlock, SourceRange range)
    {
        var radar = _nodeFactory.Create(
            NodeType.Radar,
            NodeCategory.Block,
            text: radarBlock.Lines.ToString().TrimEnd('\r', '\n'),
            range: range);

        var info = radarBlock.Options;
        if (info.Title.Length > 0)
        {
            radar.SetDerivedAttribute("radar-title", info.Title);
        }
        if (info.ShowLabels)
        {
            radar.SetDerivedAttribute("radar-labels", "show");
        }
        if (info.MaxScale is double maxScale)
        {
            radar.SetDerivedAttribute("radar-scale", "max:" + maxScale.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (!info.ShowLegend)
        {
            radar.SetDerivedAttribute("radar-legend", "hide");
        }
        if (info.FillOpacity != RadarMarker.DefaultFillOpacity)
        {
            radar.SetDerivedAttribute("radar-fill", info.FillOpacity.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (!string.Equals(info.Size, RadarMarker.SizeMedium, StringComparison.Ordinal))
        {
            radar.SetDerivedAttribute("radar-size", info.Size);
        }

        return radar;
    }

    /// <summary>
    /// 把时间线围栏展开为 Timeline + TimelineItem 虚拟节点序列：@@ 行解析为
    /// 时间/标签/侧别属性，节点正文递归走完整 Markdown 管线，局部坐标平移到全文。
    /// </summary>
    private MarkdownNode BuildTimeline(
        TimelineBlock timelineBlock,
        SourceRange range,
        ICollection<DiagnosticMessage> diagnostics)
    {
        var timeline = _nodeFactory.Create(
            NodeType.Timeline,
            NodeCategory.Block,
            text: timelineBlock.Lines.ToString().TrimEnd('\r', '\n'),
            range: range);

        // 条目切分由 parser 侧完成（TimelineBlock.Items），转换器只消费切片。
        var records = timelineBlock.Items.ToList();

        // 默认即中轴双列：侧别缺省的节点按序左右交替，与属性面板“自动”一致。
        timeline.SetDerivedAttribute("timeline-layout", "axis");

        // 开围栏选项：line=solid|dashed、gap=2..24（仅虚线时落属性）、sort=asc|desc
        var fenceInfo = TimelineMarker.ParseFenceInfo(timelineBlock.Info);
        if (fenceInfo.LineStyle == TimelineMarker.LineDashed)
        {
            timeline.SetDerivedAttribute("timeline-line", TimelineMarker.LineDashed);
            if (fenceInfo.Gap is int gap)
            {
                timeline.SetDerivedAttribute("timeline-gap", gap.ToString());
            }
        }

        // 排序：仅当显式设置 sort=asc|desc 时按时间排序；缺省保持源码顺序（向后兼容）。
        // 时间能解析为日期则按日期排序，解析失败的节点排在末尾并保持源码相对顺序（稳定排序）。
        var sortDesc = string.Equals(fenceInfo.Sort, TimelineMarker.SortDesc, StringComparison.OrdinalIgnoreCase);
        var sortAsc = string.Equals(fenceInfo.Sort, TimelineMarker.SortAsc, StringComparison.OrdinalIgnoreCase);
        if (sortDesc)
        {
            timeline.SetDerivedAttribute("timeline-sort", TimelineMarker.SortDesc);
            records = SortTimelineRecords(records, sortDesc: true);
        }
        else if (sortAsc)
        {
            timeline.SetDerivedAttribute("timeline-sort", TimelineMarker.SortAsc);
            records = SortTimelineRecords(records, sortDesc: false);
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
            item.SetDerivedAttribute("timeline-time", record.Info.Time);
            if (record.Info.Tags.Count > 0)
            {
                item.SetDerivedAttribute("timeline-tags", TimelineMarker.JoinTags(record.Info.Tags));
            }
            if (record.Info.Side is not null)
            {
                item.SetDerivedAttribute("timeline-side", record.Info.Side);
            }
            item.SetDerivedAttribute("timeline-side-effective", effectiveSide);

            if (record.BodyLength > 0)
            {
                // 正文切片与再解析是解析侧产物（TimelineBodyResolver），这里只消费。
                var body = timelineBlock.GetBody(record)
                    ?? throw new InvalidOperationException("时间线条目正文未在解析侧展开。");
                if (body.Error is not null)
                {
                    // 保持旧顺序：正文解析失败时所属时间线块整体失败。
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(body.Error).Throw();
                }

                var bodyNodes = ConvertBlocks(body.Document!, diagnostics, body.Text!);

                // 正文子树坐标相对节点正文切片，统一平移到全文。
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
    /// 按时间节点排序：desc 时按日期从晚到早，否则从早到晚。
    /// 时间无法解析为日期的节点统一排到末尾，保持源码相对顺序（LINQ OrderBy 稳定排序）。
    /// </summary>
    private static List<TimelineItemSlice> SortTimelineRecords(List<TimelineItemSlice> records, bool sortDesc)
    {
        if (records.Count <= 1)
        {
            return records;
        }

        // asc：可解析的按日期升序，不可解析的 DateTime.MaxValue 沉底；
        // desc：可解析的按日期降序，不可解析的 DateTime.MinValue 沉底。
        return sortDesc
            ? records
                .OrderByDescending(r => TryParseTimelineDate(r.Info.Time) ?? DateTime.MinValue)
                .ToList()
            : records
                .OrderBy(r => TryParseTimelineDate(r.Info.Time) ?? DateTime.MaxValue)
                .ToList();
    }

    private static DateTime? TryParseTimelineDate(string? time)
    {
        if (string.IsNullOrWhiteSpace(time))
        {
            return null;
        }

        return DateTime.TryParse(time.Trim(),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal,
            out var date)
            ? date
            : null;
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

        // 行内 HTML 的标签栈：放行的标签开一个 HtmlInline 容器节点，后续行内内容挂到容器里；
        // 栈在块（更准确地说是本次容器遍历）结束时自动闭合，不允许跨块闭合。
        var activeParent = parentNode;
        var openTags = new List<OpenInlineHtmlTag>();
        var stripTags = new List<string>();
        var depthReported = false;

        foreach (var inline in container)
        {
            var range = BlockRangeProjector.Get(inline);

            // strip 期间整段内容（含嵌套标签与文字）都不产生节点，只数同名标签的层数。
            if (stripTags.Count > 0)
            {
                if (inline is HtmlInline stripped
                    && InlineHtmlTagParser.TryParse(
                        stripped.Tag,
                        _inlineHtml?.MaxTagNameLength ?? InlineHtmlDefinition.DefaultMaxTagNameLength,
                        out var strippedToken))
                {
                    ConsumeStripToken(strippedToken, stripTags);
                }

                continue;
            }

            switch (inline)
            {
                case LiteralInline literal:
                    activeParent.AddChild(
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
                        activeParent.AddChild(inlineNode);
                        break;
                    }

                case SpoilerInline spoiler:
                    {
                        var spoilerNode = _nodeFactory.Create(
                            NodeType.Spoiler,
                            NodeCategory.Inline,
                            range: range);
                        ProcessInlines(spoiler, spoilerNode, diagnostics);
                        activeParent.AddChild(spoilerNode);
                        break;
                    }

                case CodeInline code:
                    if (ColorValueParser.TryParseDeclaration(code.Content, out var colorValue, out var colorName))
                    {
                        var chipNode = _nodeFactory.Create(
                            NodeType.ColorChip,
                            NodeCategory.Inline,
                            text: colorName,
                            range: range);
                        chipNode.SetDerivedAttribute("color-value", colorValue);
                        if (!string.IsNullOrEmpty(colorName))
                        {
                            chipNode.SetDerivedAttribute("color-name", colorName);
                        }

                        activeParent.AddChild(chipNode);
                        break;
                    }

                    activeParent.AddChild(
                        _nodeFactory.Create(
                            NodeType.InlineCode,
                            NodeCategory.Inline,
                            text: code.Content,
                            range: range));
                    break;

                case MathInline mathInline:
                    activeParent.AddChild(
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
                        activeParent);
                    break;

                case LinkInline link:
                    {
                        var url = (link.Url ?? string.Empty).Trim();

                        if (link.IsAutoLink)
                        {
                            AddAutoLinkNode(
                                url,
                                range,
                                activeParent);
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
                            linkNode.SetDerivedAttribute("src", url);
                            linkNode.SetDerivedAttribute("url", url);

                            ProcessInlines(
                                link,
                                linkNode,
                                diagnostics);
                        }
                        else
                        {
                            linkNode.SetDerivedAttribute("href", url);
                            linkNode.SetDerivedAttribute("url", url);

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

                        activeParent.AddChild(linkNode);
                        break;
                    }

                case LineBreakInline:
                    activeParent.AddChild(
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

                        linkNode.SetDerivedAttribute("href", "#fn-" + slug);
                        linkNode.SetDerivedAttribute("slug", slug);
                        linkNode.Text = label;

                        activeParent.AddChild(linkNode);
                        break;
                    }

                case HtmlInline htmlInline:
                    HandleInlineHtml(
                        htmlInline.Tag,
                        parentNode,
                        ref activeParent,
                        openTags,
                        stripTags,
                        diagnostics,
                        range,
                        ref depthReported,
                        _inlineHtml,
                        InlineScope,
                        InlineSectionName);
                    break;

                default:
                    if (inline is ContainerInline childContainer)
                    {
                        ProcessInlines(childContainer, activeParent, diagnostics);
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

        CloseUnclosedInlineHtml(openTags, diagnostics, InlineScope);

        // Markdig 会把 [^1,2] 里的 '^' 当成上标分隔符切成多个文字片段，
        // 必须在行内序列回落成节点后再合并识别，否则单看一个片段永远匹配不到。
        NormalizeFootnoteReferences(parentNode);
    }

    /// <summary>行内 HTML 放行标签的栈条目：标签名与它对应的容器节点。</summary>
    private sealed record OpenInlineHtmlTag(string Name, MarkdownNode Node);

    internal const string InlineScope = "行内";
    internal const string BlockScope = "块级";
    internal const string InlineSectionName = "inline-html";
    internal const string BlockSectionName = "block-html";

    /// <summary>
    /// 行内/块级 HTML 标签的统一入口：白名单未声明时保持翻案前行为（丢弃标签、保留文字、
    /// 一条 <c>UNSUPPORTED_INLINE_TYPE</c> 警告）；声明后按声明与机制地板的交集处置。
    /// <paramref name="scope"/> 只影响诊断文案，判定完全由 <paramref name="policy"/> 决定。
    /// </summary>
    private void HandleInlineHtml(
        string? rawTag,
        MarkdownNode rootParent,
        ref MarkdownNode activeParent,
        List<OpenInlineHtmlTag> openTags,
        List<string> stripTags,
        ICollection<DiagnosticMessage> diagnostics,
        SourceRange range,
        ref bool depthReported,
        InlineHtmlPolicy? policy,
        string scope,
        string sectionName)
    {
        if (policy is null)
        {
            diagnostics.Add(new DiagnosticMessage
            {
                Severity = DiagnosticSeverity.Warning,
                Code = "UNSUPPORTED_INLINE_TYPE",
                Message = "Unsupported inline element: HtmlInline",
                Range = range
            });
            return;
        }

        // 行内注释整段丢弃（Markdig 把整个 `<!-- ... -->` 作为一个 HtmlInline）。
        // 不能走标签解析：注释体里的 `**`、`<` 等都不应参与渲染。
        var trimmedTag = rawTag?.Trim();
        if (trimmedTag is not null
            && trimmedTag.StartsWith("<!--", StringComparison.Ordinal)
            && trimmedTag.EndsWith("-->", StringComparison.Ordinal))
        {
            return;
        }

        // 注释、声明、处理指令与无法解析的 token：不是元素标签，按未声明处置且不发射内容。
        if (!InlineHtmlTagParser.TryParse(rawTag, policy.MaxTagNameLength, out var token))
        {
            return;
        }

        if (token.IsClosing)
        {
            for (var index = openTags.Count - 1; index >= 0; index--)
            {
                if (!string.Equals(openTags[index].Name, token.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                while (openTags.Count > index)
                {
                    CloseOpenInlineHtml(openTags);
                }

                activeParent = openTags.Count > 0 ? openTags[^1].Node : rootParent;
                return;
            }

            return; // 孤立闭合标签：丢弃
        }

        var tag = policy.FindTag(token.Name);
        switch (policy.ResolveDisposition(tag, token.Name))
        {
            case InlineHtmlDisposition.Strip:
                if (!token.IsSelfClosing)
                {
                    stripTags.Add(token.Name);
                }

                return;

            case InlineHtmlDisposition.Unwrap:
                if (tag is null)
                {
                    diagnostics.Add(new DiagnosticMessage
                    {
                        Severity = DiagnosticSeverity.Warning,
                        Code = "INLINE_HTML_TAG_UNKNOWN",
                        Message = $"{scope} HTML 标签 <{token.Name}> 未在 {sectionName} 声明中列出，已丢弃标签、保留内容。",
                        Range = range
                    });
                }

                return;
        }

        if (openTags.Count >= policy.MaxDepth)
        {
            if (!depthReported)
            {
                depthReported = true;
                diagnostics.Add(new DiagnosticMessage
                {
                    Severity = DiagnosticSeverity.Warning,
                    Code = "INLINE_HTML_DEPTH_EXCEEDED",
                    Message = $"{scope} HTML 嵌套超过上限 {policy.MaxDepth} 层，超出部分已丢弃标签、保留内容。",
                    Range = range
                });
            }

            return;
        }

        var htmlNode = _nodeFactory.Create(NodeType.HtmlInline, NodeCategory.Inline, range: range);
        htmlNode.RawMarkdown = rawTag?.Trim() ?? string.Empty;
        htmlNode.SetDerivedAttribute("html-tag", tag!.Name.ToLowerInvariant());

        ApplyInlineHtmlAttributes(htmlNode, tag, token, diagnostics, range, policy, scope);

        activeParent.AddChild(htmlNode);

        // 空元素与自闭合标签：不压栈、不接子节点。
        if (tag.IsVoid || token.IsSelfClosing)
        {
            htmlNode.SetDerivedAttribute("html-void", "1");
            return;
        }

        openTags.Add(new OpenInlineHtmlTag(token.Name, htmlNode));
        activeParent = htmlNode;
    }

    /// <summary>按声明与机制地板的交集把属性写到节点上；被拒的属性只留诊断，不发射。</summary>
    private void ApplyInlineHtmlAttributes(
        MarkdownNode node,
        InlineHtmlTagDefinition tag,
        InlineHtmlTagToken token,
        ICollection<DiagnosticMessage> diagnostics,
        SourceRange range,
        InlineHtmlPolicy? policy,
        string scope)
    {
        if (policy is null)
        {
            return;
        }

        var kept = 0;
        var hasAbsoluteHttp = false;
        foreach (var attribute in token.Attributes)
        {
            if (kept >= policy.MaxAttributes)
            {
                break; // 超出上限：可预测降级，丢弃剩余属性
            }

            var name = attribute.Name.ToLowerInvariant();
            switch (policy.ResolveAttribute(tag, attribute, out var value))
            {
                case InlineHtmlAttributeVerdict.Accepted:
                    node.SetDerivedAttribute(name, value);
                    kept++;

                    var declaration = tag.FindAttribute(name);
                    if (declaration is not null
                        && declaration.IsUrl
                        && InlineHtmlPolicy.IsAbsoluteHttpUrl(value))
                    {
                        hasAbsoluteHttp = true;
                    }

                    break;

                case InlineHtmlAttributeVerdict.ProtocolRejected:
                    diagnostics.Add(new DiagnosticMessage
                    {
                        Severity = DiagnosticSeverity.Warning,
                        Code = "INLINE_HTML_PROTOCOL_REJECTED",
                        Message = $"{scope} HTML 标签 <{tag.Name}> 的属性 {name} 链接协议被拒绝（原值：{TruncateForDiagnostic(attribute.Value)}）。",
                        Range = range
                    });
                    break;

                case InlineHtmlAttributeVerdict.StyleRejected:
                    diagnostics.Add(new DiagnosticMessage
                    {
                        Severity = DiagnosticSeverity.Warning,
                        Code = "HTML_STYLE_DECLARATION_DROPPED",
                        Message = $"{scope} HTML 标签 <{tag.Name}> 的 style 没有任何声明落在布局子集内，已丢弃该属性（原值：{TruncateForDiagnostic(attribute.Value)}）。",
                        Range = range
                    });
                    break;

                default:
                    diagnostics.Add(new DiagnosticMessage
                    {
                        Severity = DiagnosticSeverity.Warning,
                        Code = "INLINE_HTML_ATTRIBUTE_DROPPED",
                        Message = $"{scope} HTML 标签 <{tag.Name}> 的属性 {name} 未获放行，已丢弃。",
                        Range = range
                    });
                    break;
            }
        }

        // 机制层常量：值来自定义、条件是数据声明的 when。
        // 这里不做字符串拼接，也不给作者留实例入口——常量属性的 ResolveAttribute 永远返回 Dropped。
        foreach (var declaration in tag.Attributes)
        {
            if (!declaration.IsConstant)
            {
                continue;
            }

            // 条件来自数据（when），但 target/rel 这两个机制保留名由机制层强制要求
            // 绝对 http/https：数据漏写 when 也不能让它们出现在相对/mailto/tel 链接上。
            var requiresAbsoluteHttp = string.Equals(
                    declaration.ConstantWhen,
                    InlineHtmlAttributeDefinition.AbsoluteHttpCondition,
                    StringComparison.Ordinal)
                || InlineHtmlRules.IsMechanismConstantAttribute(declaration.Name);

            if (requiresAbsoluteHttp && !hasAbsoluteHttp)
            {
                continue;
            }

            if (node.Attributes.ContainsKey(declaration.Name))
            {
                continue;
            }

            node.SetDerivedAttribute(declaration.Name, declaration.Constant!);
        }
    }

    /// <summary>strip 期间只数同名标签的层数：同名开标签入栈、同名闭标签出栈。</summary>
    private static void ConsumeStripToken(InlineHtmlTagToken token, List<string> stripTags)
    {
        if (stripTags.Count == 0
            || !string.Equals(stripTags[^1], token.Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (token.IsClosing)
        {
            stripTags.RemoveAt(stripTags.Count - 1);
        }
        else if (!token.IsSelfClosing)
        {
            stripTags.Add(token.Name);
        }
    }

    private static void CloseOpenInlineHtml(List<OpenInlineHtmlTag> openTags)
    {
        var last = openTags[^1];
        openTags.RemoveAt(openTags.Count - 1);
        last.Node.Text = GetInlineText(last.Node);
    }

    /// <summary>块结束时仍在栈上的标签自动闭合（不跨块），并逐条留诊断。</summary>
    private static void CloseUnclosedInlineHtml(
        List<OpenInlineHtmlTag> openTags,
        ICollection<DiagnosticMessage> diagnostics,
        string scope,
        SourceRange? fallbackRange = null)
    {
        for (var index = openTags.Count - 1; index >= 0; index--)
        {
            var open = openTags[index];
            open.Node.Text = GetInlineText(open.Node);

            diagnostics.Add(new DiagnosticMessage
            {
                Severity = DiagnosticSeverity.Warning,
                Code = "INLINE_HTML_UNCLOSED_TAG",
                Message = $"{scope} HTML 标签 <{open.Name}> 未闭合，已在块结束时自动闭合。",
                Range = fallbackRange ?? open.Node.Range
            });
        }

        openTags.Clear();
    }

    /// <summary>
    /// 块级 HTML 的解析期净化：把 <c>HtmlBlock</c> 原文按与行内同一套标签解析器、
    /// 同一份策略收敛成子树。<b>只认 <c>&lt;block-html&gt;</c> 声明</b>，不与
    /// <c>&lt;inline-html&gt;</c> 取并集；块内 markdown 不解析（CommonMark 语义）。
    /// 未声明 <c>&lt;block-html&gt;</c> 时保持翻案前行为：原文留在 <c>Text</c> 上整段转义。
    /// </summary>
    private void PopulateHtmlBlock(
        MarkdownNode node,
        string htmlText,
        SourceRange blockRange,
        ICollection<DiagnosticMessage> diagnostics)
    {
        var policy = _blockHtml;
        if (policy is null || htmlText.Length == 0)
        {
            return;
        }

        var activeParent = node;
        var openTags = new List<OpenInlineHtmlTag>();
        var stripTags = new List<string>();
        var depthReported = false;
        var text = new StringBuilder();

        var index = 0;
        while (index < htmlText.Length)
        {
            if (htmlText[index] != '<' || !TryReadTagSpan(htmlText, index, out var tagEnd))
            {
                if (stripTags.Count == 0)
                {
                    text.Append(htmlText[index]);
                }

                index++;
                continue;
            }

            AddBlockHtmlText(activeParent, text);

            var rawTag = htmlText[index..tagEnd];
            if (!InlineHtmlTagParser.TryParse(rawTag, policy.MaxTagNameLength, out var token))
            {
                // 注释、声明、处理指令：不是元素标签，整段丢弃。
                index = tagEnd;
                continue;
            }

            if (stripTags.Count > 0)
            {
                ConsumeStripToken(token, stripTags);
                index = tagEnd;
                continue;
            }

            HandleInlineHtml(
                rawTag,
                node,
                ref activeParent,
                openTags,
                stripTags,
                diagnostics,
                blockRange,
                ref depthReported,
                policy,
                BlockScope,
                BlockSectionName);

            index = tagEnd;
        }

        AddBlockHtmlText(activeParent, text);
        CloseUnclosedInlineHtml(openTags, diagnostics, BlockScope, blockRange);
    }

    /// <summary>
    /// 把一段块内文本落成 <c>Text</c> 节点：先做一次实体解码，交给渲染器时再统一转义，
    /// 避免 <c>&amp;amp;</c> 被二次转义。<c>strip</c> 段的文字不会走到这里。
    /// </summary>
    private void AddBlockHtmlText(MarkdownNode parent, StringBuilder text)
    {
        if (text.Length == 0)
        {
            return;
        }

        var value = WebUtility.HtmlDecode(text.ToString());
        text.Clear();

        if (value.Length == 0)
        {
            return;
        }

        parent.AddChild(_nodeFactory.Create(NodeType.Text, NodeCategory.Inline, text: value));
    }

    /// <summary>
    /// 从 <paramref name="start"/> 起读出一个标签跨度（含结尾 <c>&gt;</c>）。
    /// 引号内的 <c>&gt;</c> 不算结束；注释按 <c>--&gt;</c> 收口；
    /// 不是标签起点的 <c>&lt;</c>（如 <c>a &lt; b</c>）返回 <c>false</c>，按普通文字处理。
    /// </summary>
    private static bool TryReadTagSpan(string text, int start, out int end)
    {
        end = start;
        if (start < 0 || start + 1 >= text.Length || text[start] != '<')
        {
            return false;
        }

        var next = text[start + 1];
        if (next is '!' or '?')
        {
            if (text.AsSpan(start).StartsWith("<!--", StringComparison.Ordinal))
            {
                var commentEnd = text.IndexOf("-->", start + 4, StringComparison.Ordinal);
                if (commentEnd < 0)
                {
                    return false;
                }

                end = commentEnd + 3;
                return true;
            }

            var declarationEnd = text.IndexOf('>', start + 2);
            if (declarationEnd < 0)
            {
                return false;
            }

            end = declarationEnd + 1;
            return true;
        }

        var nameIndex = next == '/' ? start + 2 : start + 1;
        if (nameIndex >= text.Length || !IsAsciiLetter(text[nameIndex]))
        {
            return false;
        }

        var quote = '\0';
        for (var index = start + 1; index < text.Length; index++)
        {
            var ch = text[index];
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (ch == '>')
            {
                end = index + 1;
                return true;
            }

            if (ch == '<')
            {
                return false; // 前一个标签没有闭合：把这整段按普通文字处理
            }
        }

        return false;
    }

    private static bool IsAsciiLetter(char ch) => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static string TruncateForDiagnostic(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= 64 ? value : value[..64] + "…";
    }

    /// <summary>
    /// 把首行标签的识别结果落到已创建好的节点上。识别本身在
    /// <see cref="QuoteLabelClassifier"/>，这里只做结构性消费。
    /// </summary>
    private static void ApplyQuoteLabel(
        MarkdownNode quoteNode,
        List<MarkdownNode> quoteChildren,
        QuoteLabelClassification classification)
    {
        if (classification.Kind == QuoteLabelKind.None)
        {
            return;
        }

        var firstParagraph = quoteChildren
            .FirstOrDefault(child => child.Type == NodeType.Paragraph);
        if (firstParagraph is null)
        {
            return;
        }

        if (classification.Kind == QuoteLabelKind.CustomBlock)
        {
            quoteNode.SetDerivedAttribute("custom-kind", classification.MappedKind);
        }
        else
        {
            quoteNode.SetDerivedAttribute("admonition", classification.MappedKind);
            quoteNode.SetDerivedAttribute("admonition-title", classification.Title);
        }

        ConsumeInlinePrefix(firstParagraph, classification.PrefixLength);
        RemoveLeadingLineBreaks(firstParagraph);

        if (classification.Kind == QuoteLabelKind.CustomBlock)
        {
            firstParagraph.Text = BuildInlineText(firstParagraph.Children);
            quoteNode.Text = GetInlineText(quoteNode);
        }
    }

    private static string? ReadFirstLineText(List<MarkdownNode> children)
    {
        var firstParagraph = children.FirstOrDefault(child => child.Type == NodeType.Paragraph);
        return firstParagraph is null ? null : ReadFirstLineText(firstParagraph);
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

    /// <summary>源文本里是否存在未被反斜杠转义的裸 <c>||</c>。</summary>
    private static bool ContainsBareDoublePipe(string text)
    {
        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] != '|' || text[i + 1] != '|')
            {
                continue;
            }

            if (i == 0 || text[i - 1] != '\\')
            {
                return true;
            }
        }

        return false;
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

        autoLinkNode.SetDerivedAttribute("href", hrefValue);
        autoLinkNode.SetDerivedAttribute("url", hrefValue);
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
    /// 按 parser 侧给出的诗体切片建虚拟子部件（标题/作者/朝代/体裁 +
    /// 诗句组/诗句 + 出处/说明）。子节点 IsSynthetic、共享古诗块源码区间，
    /// 仅承载各自 LocalStyle；切分规则已移出转换器。
    /// </summary>
    private void ApplyPoemLayout(MarkdownNode poemNode, PoemLayout layout)
    {
        MarkdownNode CreatePart(NodeType type, NodeCategory category, string text, SourceRange range)
        {
            var part = _nodeFactory.Create(type, category, text: text, range: range);
            part.IsSynthetic = true;
            return part;
        }

        foreach (var field in layout.Header)
        {
            poemNode.AddChild(CreatePart(MapPoemField(field.Kind), NodeCategory.Inline, field.Text, field.Range));
        }

        foreach (var stanza in layout.Stanzas)
        {
            var stanzaNode = CreatePart(NodeType.PoemStanza, NodeCategory.Container, string.Empty, stanza.Range);
            poemNode.AddChild(stanzaNode);
            foreach (var line in stanza.Lines)
            {
                stanzaNode.AddChild(CreatePart(NodeType.PoemLine, NodeCategory.Inline, line.Text, line.Range));
            }
        }

        foreach (var field in layout.Footer)
        {
            poemNode.AddChild(CreatePart(MapPoemField(field.Kind), NodeCategory.Inline, field.Text, field.Range));
        }
    }

    private static NodeType MapPoemField(PoemFieldKind kind) => kind switch
    {
        PoemFieldKind.Title => NodeType.PoemTitle,
        PoemFieldKind.Author => NodeType.PoemAuthor,
        PoemFieldKind.Dynasty => NodeType.PoemDynasty,
        PoemFieldKind.Form => NodeType.PoemForm,
        PoemFieldKind.Source => NodeType.PoemSource,
        _ => NodeType.PoemNote
    };

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

    // [^1,2] / [^1][^2] 经预处理器遮蔽成占位符后按这里还原；
    // 转义写法 \[\^ 也被换成占位符，命中后还原成字面量 "["，不生成引用。
    private static readonly Regex FootnoteSegmentPattern = new(
        "(?<escape>\uE004\uE005)|\\[\\^(?<label>[^\\[\\]\\r\\n]+)\\]",
        RegexOptions.Compiled);

    private void CollectFootnoteDefinitions(ContainerBlock container)
    {
        _footnotesByLabel.Clear();
        Walk(container);

        void Walk(ContainerBlock current)
        {
            foreach (var block in current)
            {
                if (block is FootnoteGroup group)
                {
                    foreach (var item in group)
                    {
                        if (item is Footnote footnote)
                        {
                            _footnotesByLabel[ExtractFootnoteLabel(footnote.Label)] = footnote;
                        }
                    }

                    // 脚注区内部是脚注正文，不再当作容器继续找脚注定义。
                    continue;
                }

                if (block is ContainerBlock nested)
                {
                    Walk(nested);
                }
            }
        }
    }

    /// <summary>
    /// 合并相邻文字节点后识别脚注引用：Markdig 只认单个已定义标签，
    /// 逗号写法 <c>[^1,2]</c> 会按 '^' 切成多段文字；这里合并回原样再拆分。
    /// </summary>
    private void NormalizeFootnoteReferences(MarkdownNode parent)
    {
        var index = 0;
        while (index < parent.Children.Count)
        {
            if (parent.Children[index].Type != NodeType.Text)
            {
                index++;
                continue;
            }

            var end = index;
            var text = new StringBuilder();
            while (end < parent.Children.Count && parent.Children[end].Type == NodeType.Text)
            {
                text.Append(parent.Children[end].Text);
                end++;
            }

            var merged = text.ToString();
            if (!NeedsFootnoteNormalization(merged))
            {
                index = end;
                continue;
            }

            var firstRange = parent.Children[index].Range;
            var lastRange = parent.Children[end - 1].Range;
            var range = firstRange with
            {
                Length = Math.Max(0, lastRange.EndOffset - firstRange.StartOffset),
                EndLine = lastRange.EndLine,
                EndColumn = lastRange.EndColumn
            };

            var replacement = new List<MarkdownNode>();
            BuildFootnoteSegment(merged, range, replacement);

            for (var k = end - 1; k >= index; k--)
            {
                parent.RemoveChild(parent.Children[k]);
            }

            for (var k = 0; k < replacement.Count; k++)
            {
                parent.InsertChild(index + k, replacement[k]);
            }

            index += replacement.Count;
        }
    }

    private static bool NeedsFootnoteNormalization(string text) =>
        text.Contains("[^", StringComparison.Ordinal)
        || text.Contains(FootnoteReferencePreprocessor.OpenMarker)
        || text.Contains(FootnoteReferencePreprocessor.EscapedBackslashMarker)
        || text.Contains(FootnoteReferencePreprocessor.HiddenMarker);

    private void BuildFootnoteSegment(string text, SourceRange range, List<MarkdownNode> output)
    {
        // 占位符还原成原始定界符；转义占位符保留到匹配阶段输出字面量。
        var normalized = text
            .Replace(
                string.Concat(
                    FootnoteReferencePreprocessor.OpenMarker,
                    FootnoteReferencePreprocessor.CaretMarker),
                "[^",
                StringComparison.Ordinal)
            .Replace(
                FootnoteReferencePreprocessor.CloseMarker.ToString(),
                "]",
                StringComparison.Ordinal);

        var index = 0;
        foreach (Match match in FootnoteSegmentPattern.Matches(normalized))
        {
            AppendText(normalized[index..match.Index]);

            if (match.Groups["escape"].Success)
            {
                // \[^x] 是转义写法：只还原方括号，后面 ^x] 原样当文字。
                AppendText("[");
                index = match.Index + match.Length;
                continue;
            }

            foreach (var rawLabel in match.Groups["label"].Value.Split(','))
            {
                var label = rawLabel.Trim();
                if (label.Length == 0)
                {
                    continue;
                }

                if (_footnotesByLabel.TryGetValue(label, out var footnote))
                {
                    // 显示文本统一取定义块的原始标签，大小写与书写不一致时以定义为准。
                    var display = ExtractFootnoteLabel(footnote.Label);
                    var slug = BuildFootnoteSlug(display, footnote.Order);
                    var linkNode = _nodeFactory.Create(
                        NodeType.FootnoteLink,
                        NodeCategory.Inline,
                        range: range);

                    linkNode.SetDerivedAttribute("href", "#fn-" + slug);
                    linkNode.SetDerivedAttribute("slug", slug);
                    linkNode.Text = display;
                    output.Add(linkNode);
                }
                else
                {
                    // 定义不存在：不露出 [^x] 原样，退成普通上标文字（不可点击）。
                    var fallback = _nodeFactory.Create(
                        NodeType.Superscript,
                        NodeCategory.Inline,
                        range: range);
                    fallback.AddChild(
                        _nodeFactory.Create(
                            NodeType.Text,
                            NodeCategory.Inline,
                            text: label,
                            range: range));
                    output.Add(fallback);
                }
            }

            index = match.Index + match.Length;
        }

        AppendText(normalized[index..]);

        void AppendText(string segment)
        {
            if (segment.Length == 0)
            {
                return;
            }

            output.Add(
                _nodeFactory.Create(
                    NodeType.Text,
                    NodeCategory.Inline,
                    text: segment,
                    range: range));
        }
    }

    // 统一给所有正文脚注引用编号（含逗号展开出来的引用），并把每处锚点写回
    // 对应脚注条目的 backrefs；按文档顺序编号，返回链接与正文顺序一致。
    private static void AssignFootnoteAnchors(List<MarkdownNode> nodes)
    {
        var anchorsBySlug = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var footnotes = new List<(MarkdownNode Node, string Slug)>();

        Walk(nodes);

        foreach (var (node, slug) in footnotes)
        {
            node.SetDerivedAttribute(
                "backrefs",
                anchorsBySlug.TryGetValue(slug, out var anchors) ? string.Join(',', anchors) : string.Empty);
        }

        void Walk(IEnumerable<MarkdownNode> children)
        {
            foreach (var node in children)
            {
                if (node.Type == NodeType.FootnoteLink
                    && node.Attributes.TryGetValue("slug", out var slug))
                {
                    occurrences.TryGetValue(slug, out var occurrence);
                    occurrences[slug] = occurrence + 1;

                    var anchor = BuildFootnoteRefAnchor(slug, occurrence);
                    node.SetDerivedAttribute("id", anchor);
                    node.SetDerivedAttribute("anchor", anchor);

                    if (!anchorsBySlug.TryGetValue(slug, out var anchors))
                    {
                        anchors = new List<string>();
                        anchorsBySlug[slug] = anchors;
                    }

                    anchors.Add(anchor);
                }
                else if (node.Type == NodeType.Footnote
                    && node.Attributes.TryGetValue("slug", out var footnoteSlug))
                {
                    footnotes.Add((node, footnoteSlug));
                }

                Walk(node.Children);
            }
        }
    }

    // 锚点 slug：Unicode 单词字符（含数字、CJK）与 '-' 保留，其余字符统一
    // 替换为 '-'；发生过替换时追加脚注 Order 后缀以防不同标签撞 slug。
    // 纯单词标签（绝大多数场景）无损直用。
    private static string BuildFootnoteSlug(string label, int order)    {
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
