namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

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

    /// <summary>垂直对齐：top / middle / bottom（表格单元格为主，可沿表格链继承）。</summary>
    public string? VerticalAlign { get; set; }
    public ThicknessValue? Margin { get; set; }
    public ThicknessValue? Padding { get; set; }
    public string? BorderColor { get; set; }
    public double? BorderWidth { get; set; }
    public double? BorderRadius { get; set; }

    /// <summary>显式宽度（像素）：图片、表格使用。</summary>
    public double? Width { get; set; }

    /// <summary>格间距（像素）：图片矩阵行列间距。</summary>
    public double? Gap { get; set; }

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
        VerticalAlign = VerticalAlign,
        Margin = Margin,
        Padding = Padding,
        BorderColor = BorderColor,
        BorderWidth = BorderWidth,
        BorderRadius = BorderRadius,
        Width = Width,
        Gap = Gap,
        CustomCss = CustomCss
    };
}
