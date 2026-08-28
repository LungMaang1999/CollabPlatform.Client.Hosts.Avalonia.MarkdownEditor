using System.Net;
using System.Text;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Html;

public sealed class HtmlRenderer : IDocumentRenderer
{
    private static readonly HashSet<string> AllowedUriSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "mailto", "tel"
    };

    private readonly IStyleResolver _styleResolver;
    private readonly CssStyleBuilder _cssStyleBuilder;

    public HtmlRenderer(IStyleResolver styleResolver, CssStyleBuilder? cssStyleBuilder = null)
    {
        _styleResolver = styleResolver ?? throw new ArgumentNullException(nameof(styleResolver));
        _cssStyleBuilder = cssStyleBuilder ?? new CssStyleBuilder();
    }

    public RenderResult Render(MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var estimatedCapacity = Math.Max(1024, (document.SourceMarkdown?.Length ?? 512) * 2);
        var html = new StringBuilder(estimatedCapacity);
        var diagnostics = new List<DiagnosticMessage>();

        html.AppendLine("<article class=\"markdown-document\">");
        var children = document.Root.Children;
        for (int i = 0; i < children.Count; i++)
        {
            RenderNode(children[i], html, diagnostics);
        }
        html.AppendLine("</article>");

        return new RenderResult(html.ToString(), diagnostics);
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
                        var isChecked =
                            node.Attributes.TryGetValue("checked", out var checkedValue)
                            && string.Equals(
                                checkedValue,
                                "true",
                                StringComparison.OrdinalIgnoreCase);

                        html.Append("<li class=\"task-list-item\">");
                        html.Append("<input type=\"checkbox\" disabled");

                        if (isChecked)
                        {
                            html.Append(" checked");
                        }

                        html.Append("> ");
                        RenderListItemChildren(node, html, diagnostics);
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
                        var index = node.Attributes.TryGetValue("index", out var value)
                            ? value
                            : node.Text;

                        html.Append("<li id=\"fn-");
                        AppendEncoded(html, index);
                        html.Append("\">");

                        RenderFootnoteChildren(node, html, diagnostics);

                        html.Append(" <a class=\"footnote-backref\" href=\"#fnref-");
                        AppendEncoded(html, index);
                        html.Append("\" data-footnote-target=\"fnref-");
                        AppendEncoded(html, index);
                        html.Append("\" aria-label=\"返回正文中的脚注引用 ");
                        AppendEncoded(html, index);
                        html.Append("\">↩</a>");

                        html.Append("</li>");
                        break;
                    }
                case NodeType.FootnoteLink:
                    {
                        var index = node.Attributes.TryGetValue("index", out var value)
                            ? value
                            : node.Text;

                        html.Append("<sup class=\"footnote-ref\">");
                        html.Append("<a id=\"fnref-");
                        AppendEncoded(html, index);
                        html.Append("\" href=\"#fn-");
                        AppendEncoded(html, index);
                        html.Append("\" data-footnote-target=\"fn-");
                        AppendEncoded(html, index);
                        html.Append("\" aria-label=\"跳转到脚注 ");
                        AppendEncoded(html, index);
                        html.Append("\">");
                        AppendEncoded(html, index);
                        html.Append("</a></sup>");
                        break;
                    }
                case NodeType.Quote:
                    RenderBlock(node, "blockquote", html, diagnostics);
                    break;
                case NodeType.CodeBlock:
                    RenderCodeBlock(node, html);
                    break;
                case NodeType.ThematicBreak:
                    html.Append("<hr");
                    AppendNodeAttributes(node, html);
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

    private void RenderTableCell(
        MarkdownNode node,
        StringBuilder html,
        List<DiagnosticMessage> diagnostics)
    {
        var tag = node.IsTableHeader ? "th" : "td";

        html.Append('<').Append(tag);
        AppendNodeAttributes(node, html);
        AppendStyle(node, html);
        html.Append('>');

        for (int i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];

            if (child.Type == NodeType.Paragraph)
            {
                RenderChildren(child, html, diagnostics);
            }
            else
            {
                RenderNode(child, html, diagnostics);
            }
        }

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

    private static void RenderCodeBlock(MarkdownNode node, StringBuilder html)
    {
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
        html.Append("><code");

        if (node.Attributes.TryGetValue("language", out var lang) && !string.IsNullOrWhiteSpace(lang))
        {
            html.Append(" class=\"language-").Append(WebUtility.HtmlEncode(lang.Trim())).Append('"');
        }

        html.Append('>');
        html.Append(WebUtility.HtmlEncode(codeText));
        html.Append("</code></pre>");
    }

    private void RenderLink(
        MarkdownNode node,
        StringBuilder html,
        List<DiagnosticMessage> diagnostics)
    {
        var url = GetAttribute(node, "url") ?? GetAttribute(node, "href");

        html.Append("<a");
        AppendNodeAttributes(node, html);
        AppendStyle(node, html);

        if (IsSafeUrl(url))
        {
            html.Append(" href=\"")
                .Append(WebUtility.HtmlEncode(url!))
                .Append('"');
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
        html.Append(" src=\"").Append(WebUtility.HtmlEncode(source)).Append("\" alt=\"").Append(WebUtility.HtmlEncode(node.Text)).Append("\" />");
    }

    private void RenderChildren(MarkdownNode node, StringBuilder html, List<DiagnosticMessage> diagnostics)
    {
        for (int i = 0; i < node.Children.Count; i++)
            RenderNode(node.Children[i], html, diagnostics);
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
    }

    private static string? GetAttribute(MarkdownNode node, string name) =>
        node.Attributes.TryGetValue(name, out var val) && !string.IsNullOrWhiteSpace(val) ? val : null;

    private static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        var trimmed = url.Trim();

        // 严密拦截协议相对链接 "//"
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
            return false;

        // 允许相对路径与锚点
        if (trimmed.StartsWith('#') || trimmed.StartsWith('/') || trimmed.StartsWith("./", StringComparison.Ordinal) || trimmed.StartsWith("../", StringComparison.Ordinal))
            return true;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return AllowedUriSchemes.Contains(uri.Scheme);
        }

        return false;
    }

    private static void AppendEncoded(StringBuilder html, string value)
    {
        html.Append(WebUtility.HtmlEncode(value));
    }

    private void RenderListItemChildren(
        MarkdownNode node,
        StringBuilder html,
        List<DiagnosticMessage> diagnostics)
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

    private void RenderFootnoteChildren(
        MarkdownNode node,
        StringBuilder html,
        List<DiagnosticMessage> diagnostics)
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
}