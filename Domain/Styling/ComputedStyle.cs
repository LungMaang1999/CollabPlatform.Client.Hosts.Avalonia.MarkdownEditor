namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

public sealed class ComputedStyle : NodeStyle
{
    public ComputedStyle() { }

    public ComputedStyle(NodeStyle source)
    {
        Apply(source);
    }

    public void Apply(NodeStyle source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.FontFamily is not null) FontFamily = source.FontFamily;
        if (source.FontSize.HasValue) FontSize = source.FontSize;
        if (source.ForegroundColor is not null) ForegroundColor = source.ForegroundColor;
        if (source.BackgroundColor is not null) BackgroundColor = source.BackgroundColor;
        if (source.Bold.HasValue) Bold = source.Bold;
        if (source.Italic.HasValue) Italic = source.Italic;
        if (source.LineHeight.HasValue) LineHeight = source.LineHeight;
        if (source.TextAlign is not null) TextAlign = source.TextAlign;
        if (source.VerticalAlign is not null) VerticalAlign = source.VerticalAlign;
        if (source.Margin is not null) Margin = source.Margin;
        if (source.Padding is not null) Padding = source.Padding;
        if (source.BorderColor is not null) BorderColor = source.BorderColor;
        if (source.BorderWidth.HasValue) BorderWidth = source.BorderWidth;
        if (source.BorderRadius.HasValue) BorderRadius = source.BorderRadius;
        if (source.Width.HasValue) Width = source.Width;
        if (source.Gap.HasValue) Gap = source.Gap;
        if (source.CustomCss is not null) CustomCss = source.CustomCss;
    }

    /// <summary>
    /// 仅合并 CSS 意义上的"可继承属性"（排版与墨色），用于祖先 → 后代的继承回退；
    /// 背景、外边距、内边距、边框、宽度、格间距、自定义 CSS 不随祖先继承。
    /// </summary>
    public void ApplyInheritable(NodeStyle source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.FontFamily is not null) FontFamily = source.FontFamily;
        if (source.FontSize.HasValue) FontSize = source.FontSize;
        if (source.ForegroundColor is not null) ForegroundColor = source.ForegroundColor;
        if (source.Bold.HasValue) Bold = source.Bold;
        if (source.Italic.HasValue) Italic = source.Italic;
        if (source.LineHeight.HasValue) LineHeight = source.LineHeight;
        if (source.TextAlign is not null) TextAlign = source.TextAlign;
        if (source.VerticalAlign is not null) VerticalAlign = source.VerticalAlign;
    }
}
