using System;
using System.Net;
using System.Text.RegularExpressions;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Strategies;

public interface IStylePreviewStrategy
{
    bool CanHandle(string? nodeTypeStr);
    string RenderHtml(string cssStyle, string sampleText);
}

internal static class StyleHtmlSanitizer
{
    public static string SanitizeStyle(string? css)
    {
        if (string.IsNullOrWhiteSpace(css)) return string.Empty;

        var sanitized = css.Replace("\"", "'");
        sanitized = Regex.Replace(sanitized, @"javascript\s*:", string.Empty, RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"expression\s*\(", string.Empty, RegexOptions.IgnoreCase);

        return sanitized;
    }
}

public sealed class HeadingPreviewStrategy : IStylePreviewStrategy
{
    public bool CanHandle(string? nodeTypeStr) =>
        string.Equals(nodeTypeStr, nameof(NodeType.Heading), StringComparison.OrdinalIgnoreCase);

    public string RenderHtml(string cssStyle, string sampleText)
    {
        var safeCss = StyleHtmlSanitizer.SanitizeStyle(cssStyle);
        var safeText = WebUtility.HtmlEncode(sampleText ?? string.Empty);
        return $"<h2 style=\"{safeCss}\">{safeText}</h2>";
    }
}

public sealed class ParagraphPreviewStrategy : IStylePreviewStrategy
{
    public bool CanHandle(string? nodeTypeStr) =>
        string.IsNullOrEmpty(nodeTypeStr) ||
        string.Equals(nodeTypeStr, nameof(NodeType.Paragraph), StringComparison.OrdinalIgnoreCase);

    public string RenderHtml(string cssStyle, string sampleText)
    {
        var safeCss = StyleHtmlSanitizer.SanitizeStyle(cssStyle);
        var safeText = WebUtility.HtmlEncode(sampleText ?? string.Empty);
        return $"<p style=\"{safeCss}\">{safeText}</p>";
    }
}

public sealed class CodeBlockPreviewStrategy : IStylePreviewStrategy
{
    public bool CanHandle(string? nodeTypeStr) =>
        string.Equals(nodeTypeStr, nameof(NodeType.CodeBlock), StringComparison.OrdinalIgnoreCase);

    public string RenderHtml(string cssStyle, string sampleText)
    {
        var safeCss = StyleHtmlSanitizer.SanitizeStyle(cssStyle);
        var safeText = WebUtility.HtmlEncode(sampleText ?? string.Empty);
        return $"<pre style=\"background-color: #1e1e1e; color: #d4d4d4; padding: 8px; border-radius: 4px; {safeCss}\"><code>{safeText}</code></pre>";
    }
}

public sealed class FallbackPreviewStrategy : IStylePreviewStrategy
{
    public bool CanHandle(string? nodeTypeStr) => true;

    public string RenderHtml(string cssStyle, string sampleText)
    {
        var safeCss = StyleHtmlSanitizer.SanitizeStyle(cssStyle);
        var safeText = WebUtility.HtmlEncode(sampleText ?? string.Empty);
        return $"<div style=\"{safeCss}\">{safeText}</div>";
    }
}
