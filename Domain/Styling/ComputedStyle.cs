namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

/// <summary>
/// Represents the final merged style computed through style resolution cascading.
/// </summary>
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
        if (source.Margin is not null) Margin = source.Margin;
        if (source.Padding is not null) Padding = source.Padding;
        if (source.BorderColor is not null) BorderColor = source.BorderColor;
        if (source.BorderWidth.HasValue) BorderWidth = source.BorderWidth;
        if (source.CustomCss is not null) CustomCss = source.CustomCss;
    }
}