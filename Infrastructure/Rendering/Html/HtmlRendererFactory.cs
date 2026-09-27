using System;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Exporting.Html;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Html;

public sealed class HtmlRendererFactory : IDocumentRendererFactory
{
    private static readonly AdmonitionIconOptions SharedIconOptions = new();

    public static void ConfigureIcons(IReadOnlyDictionary<string, string> icons) =>
        SharedIconOptions.Configure(icons);

    public IDocumentRenderer Create(MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var styleCache = new StyleCache();
        var styleResolver = new StyleResolver(document, styleCache);

        return new HtmlRenderer(styleResolver, iconOptions: SharedIconOptions);
    }

    public IDocumentRenderer CreatePreview(MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var styleCache = new StyleCache();
        var styleResolver = new StyleResolver(document, styleCache);
        var defaultStyle = document.StyleSheet.GetOrCreateDefault().Style;

        return new HtmlRenderer(styleResolver, new PreviewCssStyleBuilder(defaultStyle), SharedIconOptions);
    }
}

internal sealed class PreviewCssStyleBuilder : ICssStyleBuilder
{
    private readonly NodeStyle _defaultStyle;
    private readonly ICssStyleBuilder _inner;

    public PreviewCssStyleBuilder(NodeStyle defaultStyle, ICssStyleBuilder? inner = null)
    {
        _defaultStyle = defaultStyle ?? throw new ArgumentNullException(nameof(defaultStyle));
        _inner = inner ?? new CssStyleBuilder();
    }

    public string Build(ComputedStyle? style)
    {
        if (style is null) return string.Empty;

        var copy = new ComputedStyle(style);
        StripDefaultBaseline(copy);
        return _inner.Build(copy);
    }

    public string Build(NodeStyle? style)
    {
        if (style is null) return string.Empty;

        var copy = style.Clone();
        StripDefaultBaseline(copy);
        return _inner.Build(copy);
    }

    private void StripDefaultBaseline(NodeStyle style)
    {
        if (string.Equals(style.FontFamily?.Trim(), _defaultStyle.FontFamily?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            style.FontFamily = null;
        }

        if (style.FontSize.HasValue
            && _defaultStyle.FontSize.HasValue
            && style.FontSize.Value.Equals(_defaultStyle.FontSize.Value))
        {
            style.FontSize = null;
        }

        if (SameColor(style.ForegroundColor, _defaultStyle.ForegroundColor))
        {
            style.ForegroundColor = null;
        }

        if (SameColor(style.BackgroundColor, _defaultStyle.BackgroundColor))
        {
            style.BackgroundColor = null;
        }
    }

    private static bool SameColor(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(NormalizeColor(a), NormalizeColor(b), StringComparison.Ordinal);
    }

    private static string NormalizeColor(string color)
    {
        var c = color.Trim().ToLowerInvariant();
        if (c.Length == 4 && c[0] == '#')
        {
            c = "#" + c[1] + c[1] + c[2] + c[2] + c[3] + c[3];
        }

        return c;
    }
}
