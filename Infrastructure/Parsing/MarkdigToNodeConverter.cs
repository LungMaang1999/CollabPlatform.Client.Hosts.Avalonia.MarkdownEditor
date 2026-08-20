using System;
using System.Collections.Generic;
using System.Linq;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax.Factories;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
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

    public List<MarkdownNode> ConvertBlocks(ContainerBlock rootContainer, ICollection<DiagnosticMessage> diagnostics)
    {
        var resultNodes = new List<MarkdownNode>();

        foreach (var block in rootContainer)
        {
            var node = ConvertBlock(block, diagnostics);
            if (node is not null)
            {
                resultNodes.Add(node);
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
                var listType = listBlock.IsOrdered ? NodeType.OrderedList : NodeType.UnorderedList;
                var listNode = _nodeFactory.Create(listType, NodeCategory.Container, range: range);

                foreach (var item in listBlock)
                {
                    if (item is ListItemBlock listItem)
                    {
                        var listItemNode = _nodeFactory.Create(NodeType.ListItem, NodeCategory.Container, range: ExtractSourceRange(listItem));
                        var itemChildren = ConvertBlocks(listItem, diagnostics);
                        foreach (var child in itemChildren)
                        {
                            listItemNode.AddChild(child);
                        }
                        listNode.AddChild(listItemNode);
                    }
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
                var tableNode = _nodeFactory.Create(NodeType.Table, NodeCategory.Container, range: range);
                foreach (var rowObj in tableBlock)
                {
                    if (rowObj is TableRow rowBlock)
                    {
                        var rowNode = _nodeFactory.Create(NodeType.TableRow, NodeCategory.Container, range: ExtractSourceRange(rowBlock));
                        foreach (var cellObj in rowBlock)
                        {
                            if (cellObj is TableCell cellBlock)
                            {
                                var cellNode = _nodeFactory.Create(NodeType.TableCell, NodeCategory.Container, range: ExtractSourceRange(cellBlock));
                                cellNode.IsTableHeader = rowBlock.IsHeader;
                                var cellChildren = ConvertBlocks(cellBlock, diagnostics);
                                foreach (var cc in cellChildren) cellNode.AddChild(cc);
                                rowNode.AddChild(cellNode);
                            }
                        }
                        tableNode.AddChild(rowNode);
                    }
                }
                return tableNode;

            case FencedCodeBlock codeBlock:
                var codeText = string.Join("\n", codeBlock.Lines.Lines.Select(l => l.ToString()));
                var codeNode = _nodeFactory.Create(NodeType.CodeBlock, NodeCategory.Block, text: codeText, range: range);
                if (!string.IsNullOrWhiteSpace(codeBlock.Info))
                {
                    codeNode.Attributes["language"] = codeBlock.Info;
                }
                return codeNode;

            case ThematicBreakBlock:
                return _nodeFactory.Create(NodeType.ThematicBreak, NodeCategory.Leaf, range: range);

            case HtmlBlock htmlBlock:
                var htmlText = string.Join("\n", htmlBlock.Lines.Lines.Select(l => l.ToString()));
                return _nodeFactory.Create(NodeType.HtmlBlock, NodeCategory.Block, text: htmlText, range: range);

            default:
                diagnostics.Add(new DiagnosticMessage
                {
                    Severity = DiagnosticSeverity.Warning,
                    Code = "UNSUPPORTED_BLOCK_TYPE",
                    Message = $"Unsupported block element: {block.GetType().Name}",
                    Range = range
                });
                return null;
        }
    }

    private void ProcessInlines(ContainerInline? container, MarkdownNode parentNode, ICollection<DiagnosticMessage> diagnostics)
    {
        if (container is null) return;

        foreach (var inline in container)
        {
            var range = ExtractSourceRange(inline);
            switch (inline)
            {
                case LiteralInline literal:
                    parentNode.AddChild(_nodeFactory.Create(NodeType.Text, NodeCategory.Inline, text: literal.Content.ToString(), range: range));
                    break;

                case TaskList taskList:
                    var taskText = taskList.Checked ? "[x] " : "[ ] ";
                    parentNode.AddChild(_nodeFactory.Create(NodeType.Text, NodeCategory.Inline, text: taskText, range: range));
                    break;

                case EmphasisInline emphasis:
                    NodeType inlineType;
                    if (emphasis.DelimiterChar == '~')
                    {
                        inlineType = NodeType.Delete;
                    }
                    else
                    {
                        inlineType = (emphasis.DelimiterCount == 2) ? NodeType.Strong : NodeType.Emphasis;
                    }

                    var inlineNode = _nodeFactory.Create(inlineType, NodeCategory.Inline, range: range);
                    ProcessInlines(emphasis, inlineNode, diagnostics);
                    parentNode.AddChild(inlineNode);
                    break;

                case CodeInline code:
                    parentNode.AddChild(_nodeFactory.Create(NodeType.InlineCode, NodeCategory.Inline, text: code.Content, range: range));
                    break;

                case LinkInline link:
                    var linkType = link.IsImage ? NodeType.Image : NodeType.Link;
                    var linkNode = _nodeFactory.Create(linkType, NodeCategory.Inline, range: range);

                    var url = link.Url ?? string.Empty;
                    if (link.IsImage)
                    {
                        linkNode.Attributes["src"] = url;
                        linkNode.Attributes["url"] = url;
                    }
                    else
                    {
                        linkNode.Attributes["href"] = url;
                        linkNode.Attributes["url"] = url;
                    }

                    ProcessInlines(link, linkNode, diagnostics);
                    parentNode.AddChild(linkNode);
                    break;

                case LineBreakInline:
                    parentNode.AddChild(_nodeFactory.Create(NodeType.Text, NodeCategory.Inline, text: "\n", range: range));
                    break;

                default:
                    if (inline is ContainerInline childContainer)
                    {
                        ProcessInlines(childContainer, parentNode, diagnostics);
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

        return new SourceRange
        {
            StartOffset = startOffset,
            Length = length,
            StartLine = startLine,
            StartColumn = startColumn,
            EndLine = startLine,
            EndColumn = startColumn + length
        };
    }
}