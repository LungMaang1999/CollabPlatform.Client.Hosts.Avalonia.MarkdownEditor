using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using System.Globalization;
using System.Text;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Html;

/// <summary>
/// Converts ComputedStyle value objects into inline CSS string declarations.
/// </summary>
public sealed class CssStyleBuilder
{
    public string Build(ComputedStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        var css = new StringBuilder();

        Append(css, "font-family", style.FontFamily);
        Append(css, "font-size", style.FontSize, "px");
        Append(css, "color", style.ForegroundColor);
        Append(css, "background-color", style.BackgroundColor);

        if (style.Bold == true) Append(css, "font-weight", "bold");
        if (style.Italic == true) Append(css, "font-style", "italic");

        Append(css, "line-height", style.LineHeight);
        Append(css, "text-align", style.TextAlign);

        AppendThickness(css, "margin", style.Margin);
        AppendThickness(css, "padding", style.Padding);

        Append(css, "border-color", style.BorderColor);
        Append(css, "border-width", style.BorderWidth, "px");
        if (style.BorderWidth.HasValue && style.BorderWidth > 0 && string.IsNullOrEmpty(style.BorderColor))
            Append(css, "border-style", "solid");

        if (!string.IsNullOrWhiteSpace(style.CustomCss))
            css.Append(style.CustomCss.Trim().TrimEnd(';')).Append(';');

        return css.ToString();
    }

    private static void Append(StringBuilder css, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            css.Append(name).Append(':').Append(value).Append(';');
    }

    private static void Append(StringBuilder css, string name, double? value, string unit = "")
    {
        if (value.HasValue)
            css.Append(name).Append(':').Append(value.Value.ToString("R", CultureInfo.InvariantCulture)).Append(unit).Append(';');
    }

    private static void AppendThickness(StringBuilder css, string name, ThicknessValue? thickness)
    {
        if (thickness is null) return;
        css.Append(name).Append(':')
           .Append(thickness.Top.ToString("R", CultureInfo.InvariantCulture)).Append("px ")
           .Append(thickness.Right.ToString("R", CultureInfo.InvariantCulture)).Append("px ")
           .Append(thickness.Bottom.ToString("R", CultureInfo.InvariantCulture)).Append("px ")
           .Append(thickness.Left.ToString("R", CultureInfo.InvariantCulture)).Append("px;");
    }
}