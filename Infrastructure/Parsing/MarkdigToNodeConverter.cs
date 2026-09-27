using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
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

    public MarkdigToNodeConverter(NodeFactory? nodeFactory = null)
    {
        _nodeFactory = nodeFactory ?? new NodeFactory();
    }

    public List<MarkdownNode> ConvertBlocks(
        ContainerBlock rootContainer,
        ICollection<DiagnosticMessage> diagnostics,
        string source)
    {
        ArgumentNullException.ThrowIfNull(rootContainer);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(source);

        var resultNodes = ConvertChildren(rootContainer, diagnostics);

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
            && node.Type is NodeType.CodeBlock or NodeType.MathBlock or NodeType.Mermaid)
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
        ICollection<DiagnosticMessage> diagnostics)
    {
        var resultNodes = new List<MarkdownNode>();

        foreach (var block in rootContainer)
        {
            try
            {
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

                    var itemChildren = ConvertChildren(listItem, diagnostics);
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
                var quoteChildren = ConvertChildren(quoteBlock, diagnostics);
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

                        var cellChildren = ConvertChildren(cellBlock, diagnostics);
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

                        var children = ConvertChildren(footnoteBlock, diagnostics);
                        foreach (var child in children)
                        {
                            footnoteNode.AddChild(child);
                        }

                        groupNode.AddChild(footnoteNode);
                        footnoteIndex++;
                    }

                    return groupNode;
                }

            case MathBlock mathBlock:
                return _nodeFactory.Create(
                    NodeType.MathBlock,
                    NodeCategory.Block,
                    text: mathBlock.Lines.ToString().TrimEnd('\r', '\n'),
                    range: range);

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
}
