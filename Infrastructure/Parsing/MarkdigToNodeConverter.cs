using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax.Factories;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Helpers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 底层转换器：将 Markdig 语法树转换为系统领域 AST (MarkdownNode)，全面支持 Pipe Tables, TaskLists, Formatting.
/// </summary>
public sealed class MarkdigToNodeConverter
{
    private readonly NodeFactory _nodeFactory;

    public MarkdigToNodeConverter(NodeFactory? nodeFactory = null)
    {
        _nodeFactory = nodeFactory ?? new NodeFactory();
    }

    public List<MarkdownNode> ConvertBlocks(
        ContainerBlock rootContainer,
        ICollection<DiagnosticMessage> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(rootContainer);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var resultNodes = new List<MarkdownNode>();

        foreach (var block in rootContainer)
        {
            try
            {
#if DEBUG
                diagnostics.Add(new DiagnosticMessage
                {
                    Severity = DiagnosticSeverity.Info,
                    Code = "DEBUG_BLOCK_TYPE",
                    Message = $"Block type: {block.GetType().FullName}; Span: {block.Span}",
                    Range = ExtractSourceRange(block)
                });
#endif
                var node = ConvertBlock(block, diagnostics);
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

    private MarkdownNode? ConvertBlock(Block block, ICollection<DiagnosticMessage> diagnostics)
    {
        var range = ExtractSourceRange(block);

        switch (block)
        {
            case HeadingBlock heading:
                var headingNode = _nodeFactory.Create(NodeType.Heading, NodeCategory.Block, range: range);
                headingNode.Level = heading.Level;
                ProcessInlines(heading.Inline, headingNode, diagnostics);
                return headingNode;

            case ParagraphBlock paragraph:
                var paragraphNode = _nodeFactory.Create(NodeType.Paragraph, NodeCategory.Block, range: range);
                ProcessInlines(paragraph.Inline, paragraphNode, diagnostics);
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

                    var itemChildren = ConvertBlocks(listItem, diagnostics);
                    foreach (var child in itemChildren)
                    {
                        itemNode.AddChild(child);
                    }

                    listNode.AddChild(itemNode);
                }

                return listNode;

            case QuoteBlock quoteBlock:
                var quoteNode = _nodeFactory.Create(NodeType.Quote, NodeCategory.Container, range: range);
                var quoteChildren = ConvertBlocks(quoteBlock, diagnostics);
                foreach (var child in quoteChildren)
                {
                    quoteNode.AddChild(child);
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

                        var cellChildren = ConvertBlocks(cellBlock, diagnostics);
                        foreach (var child in cellChildren)
                        {
                            cellNode.AddChild(child);
                        }

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

                    var footnoteIndex = 1;
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

                        footnoteNode.Attributes["index"] = footnoteIndex.ToString();
                        footnoteNode.Attributes["id"] = $"fn-{footnoteIndex}";
                        footnoteNode.Text = footnoteIndex.ToString();

                        var children = ConvertBlocks(footnoteBlock, diagnostics);
                        foreach (var child in children)
                        {
                            footnoteNode.AddChild(child);
                        }

                        groupNode.AddChild(footnoteNode);
                        footnoteIndex++;
                    }

                    return groupNode;
                }

            case FencedCodeBlock codeBlock:
                {
                    var codeText = codeBlock.Lines.ToString();

                    var codeNode = _nodeFactory.Create(
                        NodeType.CodeBlock,
                        NodeCategory.Block,
                        text: codeText,
                        range: range);

                    if (!string.IsNullOrWhiteSpace(codeBlock.Info))
                    {
                        codeNode.Attributes["language"] = codeBlock.Info.Trim();
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
                        var inlineType = emphasis.DelimiterChar switch
                        {
                            '~' => NodeType.Delete,
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
                    {
                        var linkNode = _nodeFactory.Create(
                            NodeType.FootnoteLink,
                            NodeCategory.Inline,
                            range: range);

                        var indexValue = footnoteLink.Index;
                        var indexText = indexValue.ToString();

                        linkNode.Attributes["id"] = $"fnref-{indexText}";
                        linkNode.Attributes["href"] = $"#fn-{indexText}";
                        linkNode.Attributes["index"] = indexText;
                        linkNode.Text = indexText;

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

        // LeafBlock 包含 Lines 信息（如 ParagraphBlock, FencedCodeBlock 等）
        if (obj is LeafBlock leafBlock && leafBlock.Lines.Lines != null && leafBlock.Lines.Count > 0)
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

    private static string GetInlineText(MarkdownNode node)
    {
        if (node.Type == NodeType.Text)
            return node.Text;

        if (!string.IsNullOrEmpty(node.Text))
            return node.Text;

        return string.Concat(node.Children.Select(GetInlineText));
    }
}