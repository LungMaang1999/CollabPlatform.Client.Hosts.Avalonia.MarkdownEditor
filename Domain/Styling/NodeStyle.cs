namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

/// <summary>
/// Represents visual styling attributes applicable to document nodes.
/// </summary>
public class NodeStyle
{
    public string? FontFamily { get; set; }
    public double? FontSize { get; set; }
    public string? ForegroundColor { get; set; }
    public string? BackgroundColor { get; set; }
    public bool? Bold { get; set; }
    public bool? Italic { get; set; }
    public double? LineHeight { get; set; }
    public string? TextAlign { get; set; }
    public ThicknessValue? Margin { get; set; }
    public ThicknessValue? Padding { get; set; }
    public string? BorderColor { get; set; }
    public double? BorderWidth { get; set; }
    public string? CustomCss { get; set; }

    public NodeStyle Clone() => new()
    {
        FontFamily = FontFamily,
        FontSize = FontSize,
        ForegroundColor = ForegroundColor,
        BackgroundColor = BackgroundColor,
        Bold = Bold,
        Italic = Italic,
        LineHeight = LineHeight,
        TextAlign = TextAlign,
        Margin = Margin,
        Padding = Padding,
        BorderColor = BorderColor,
        BorderWidth = BorderWidth,
        CustomCss = CustomCss
    };
}