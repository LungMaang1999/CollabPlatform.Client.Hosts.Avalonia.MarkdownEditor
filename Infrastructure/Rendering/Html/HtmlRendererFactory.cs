using System;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Exporting.Html;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Html;

public sealed class HtmlRendererFactory : IDocumentRendererFactory
{
    private static readonly AdmonitionIconOptions SharedIconOptions = new();

    /// <summary>
    /// 下发进程级图标配置。必须在首次渲染之前一次性完成：渲染器按渲染开始时的
    /// 快照生成签名与字形，中途变更会让缓存纪元失效并触发一帧全量重绘。
    /// </summary>
    public static void ConfigureIcons(IReadOnlyDictionary<string, string> icons) =>
        SharedIconOptions.Configure(icons);

    /// <summary>
    /// 只下发自定义内容块（按 kind 寻址）的图标，不影响提示块字形。
    /// 与 <see cref="ConfigureIcons"/> 共用同一份不可变快照。
    /// </summary>
    public static void ConfigureCustomBlockIcons(IReadOnlyDictionary<string, string> icons) =>
        SharedIconOptions.ConfigureCustomBlockIcons(icons);

    /// <summary>捕获进程级图标配置，供调用方在 finally 中还原。</summary>
    public static AdmonitionIconState CaptureIcons() => SharedIconOptions.Capture();

    /// <summary>还原进程级图标配置（字形表与修订号）。</summary>
    public static void RestoreIcons(AdmonitionIconState state) => SharedIconOptions.Restore(state);

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

        return new HtmlRenderer(styleResolver, new PreviewCssStyleBuilder(defaultStyle), SharedIconOptions, defaultStyle);
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
