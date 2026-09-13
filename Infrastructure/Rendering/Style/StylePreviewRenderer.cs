using System.Collections.Generic;
using System.Linq;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Strategies;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering;

/// <summary>
/// 样式预览HTML渲染器
/// </summary>
internal sealed class StylePreviewRenderer
{
    private readonly CssStyleBuilder _cssBuilder = new();
    private readonly List<IStylePreviewStrategy> _strategies = new()
    {
        new HeadingPreviewStrategy(),
        new ParagraphPreviewStrategy(),
        new CodeBlockPreviewStrategy(),
        new FallbackPreviewStrategy()
    };

    internal string RenderPreviewHtml(NodeStyleDto? styleDto, string? nodeTypeStr = null, string? customContent = null)
    {
        var css = _cssBuilder.Build(styleDto);
        var content = string.IsNullOrWhiteSpace(customContent) ? GetDefaultSampleText(nodeTypeStr) : customContent;

        var strategy = _strategies.First(s => s.CanHandle(nodeTypeStr));
        var innerHtml = strategy.RenderHtml(css, content);

        return $"<div class=\"preview-container\" style=\"box-sizing:border-box; width:100%; font-family:sans-serif;\">{innerHtml}</div>";
    }

    private static string GetDefaultSampleText(string? nodeTypeStr) => nodeTypeStr switch
    {
        "Heading" => "标题样式预览 (Heading Preview)",
        "CodeBlock" => "// 示例代码\npublic class MarkdownEditor { }",
        _ => "这是一段用于预览节点样式的正文文本。"
    };
}